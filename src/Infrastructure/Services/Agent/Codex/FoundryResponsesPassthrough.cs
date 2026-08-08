using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PKS.Infrastructure.Services.Agent.Anthropic;
using PKS.Infrastructure.Services.Agent.Foundry;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Agent.Codex;

/// <summary>
/// A thin loopback proxy that lets the genuine <c>codex</c> CLI run natively against an Azure AI
/// Foundry Responses deployment. Unlike <c>pks claude codex</c> (which translates Anthropic ⇄
/// Responses), this forwards the Responses request/response <b>verbatim</b> — its only job is to
/// inject fresh Foundry bearer auth on every request so long sessions never hit the ~1h AAD token
/// expiry that an env-var-once CLI would.
///
/// Codex points <c>base_url</c> at <c>http://127.0.0.1:{Port}/openai/v1</c> and authenticates to the
/// proxy with the per-run token in <c>PKS_CODEX_TOKEN</c>.
/// </summary>
public sealed class FoundryResponsesPassthrough
{
    internal const long MaxRequestBodySize = 256L * 1024 * 1024;
    internal const int DefaultTransparentRetries = 4;
    internal const int DefaultRetryBaseDelayMs = 2_000;
    internal const int DefaultRetryMaxDelayMs = 30_000;
    internal const int MaxBufferedSseBytes = 4 * 1024 * 1024;
    internal const bool DefaultBufferFullResponse = true;
    internal const bool DefaultCacheBustOnServerError = true;
    internal const int DefaultCacheBustAfterErrors = 2;
    internal const int DefaultCacheBustMaxRotations = 3;

    private readonly FoundryStoredCredentials _creds;
    private readonly IAzureFoundryAuthService _authService;
    private readonly string _foundryScope;
    private readonly string _proxyToken;
    private readonly string _upstreamUrl;
    private static readonly string PksDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli");
    private static readonly string FailureLogPath = Path.Combine(PksDir, "codex-passthrough-failures.log");
    private static readonly string FailedRequestBodyDir = Path.Combine(PksDir, "codex-passthrough-failed-requests");
    private static readonly string AttemptLogPath = Path.Combine(PksDir, "codex-passthrough-attempts.jsonl");
    private static readonly SemaphoreSlim AttemptLogLock = new(1, 1);

    private readonly int _transparentRetries;
    private readonly int _retryBaseDelayMs;
    private readonly int _retryMaxDelayMs;
    private readonly bool _bufferFullResponse;
    private readonly bool _cacheBustOnServerError;
    private readonly int _cacheBustAfterErrors;
    private readonly int _cacheBustMaxRotations;
    private readonly object _cacheKeyRecoveryLock = new();
    private readonly Dictionary<string, CacheKeyOverride> _cacheKeyOverrides = new(StringComparer.Ordinal);

    /// <summary>
    /// The Generic Host defaults to a <see cref="Microsoft.Extensions.FileProviders.PhysicalFileProvider"/>
    /// watch (<c>FileSystemWatcher</c>, <c>IncludeSubdirectories = true</c>) rooted at the current
    /// directory for appsettings.json hot-reload — regardless of whether appsettings.json exists. This
    /// passthrough never reads config at runtime, so on a large project tree (e.g. a JS repo with
    /// node_modules) that watcher alone can burn 100k+ inotify watches per `pks codex` session for no
    /// benefit. Passing this disables it. See HostDefaults.ReloadConfigOnChangeKey.
    /// </summary>
    private static readonly string[] NoConfigReloadArgs = ["--hostBuilder:reloadConfigOnChange=false"];

    private WebApplication? _app;

    public int Port { get; }

    public FoundryResponsesPassthrough(
        FoundryStoredCredentials creds,
        IAzureFoundryAuthService authService,
        string foundryScope,
        string proxyToken,
        int port)
    {
        _creds = creds;
        _authService = authService;
        _foundryScope = foundryScope;
        _proxyToken = proxyToken;
        Port = port;
        _upstreamUrl = FoundryResponsesEndpoint.BuildResponsesUrl(creds.SelectedResourceEndpoint);
        _transparentRetries = ReadBoundedIntEnvironment("PKS_CODEX_PROXY_RETRIES", DefaultTransparentRetries, 0, 10);
        _retryBaseDelayMs = ReadBoundedIntEnvironment("PKS_CODEX_PROXY_RETRY_BASE_MS", DefaultRetryBaseDelayMs, 250, 60_000);
        _retryMaxDelayMs = ReadBoundedIntEnvironment("PKS_CODEX_PROXY_RETRY_MAX_MS", DefaultRetryMaxDelayMs, 1_000, 120_000);
        _bufferFullResponse = ReadBooleanEnvironment("PKS_CODEX_PROXY_BUFFER_FULL_RESPONSE", DefaultBufferFullResponse);
        _cacheBustOnServerError = ReadBooleanEnvironment(
            "PKS_CODEX_PROXY_CACHE_BUST_ON_SERVER_ERROR", DefaultCacheBustOnServerError);
        _cacheBustAfterErrors = ReadBoundedIntEnvironment(
            "PKS_CODEX_PROXY_CACHE_BUST_AFTER_ERRORS", DefaultCacheBustAfterErrors, 1, 10);
        _cacheBustMaxRotations = ReadBoundedIntEnvironment(
            "PKS_CODEX_PROXY_CACHE_BUST_MAX_ROTATIONS", DefaultCacheBustMaxRotations, 1, 10);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder(NoConfigReloadArgs);
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        builder.WebHost.ConfigureKestrel(ConfigureKestrel);
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.Services.AddHttpClient("codex-passthrough")
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);

        var app = builder.Build();
        var factory = app.Services.GetRequiredService<IHttpClientFactory>();

        // codex (wire_api=responses) POSTs to {base_url}/responses. Accept the path under the
        // configured base_url plus a couple of tolerant shapes; the query string (api-version) is ignored.
        Task Handle(HttpContext ctx) => ForwardAsync(ctx, factory);
        app.MapPost("/openai/v1/responses", Handle);
        app.MapPost("/v1/responses", Handle);
        app.MapPost("/responses", Handle);

        _app = app;
        await app.StartAsync(ct);
    }

    internal static void ConfigureKestrel(KestrelServerOptions options)
    {
        // Codex sends the complete local conversation on each Responses request. Browser
        // screenshots are embedded as base64 and can push a healthy session beyond Kestrel's
        // 30,000,000-byte default. Keep a generous but bounded loopback-only ceiling so those
        // sessions can compact or resume without allowing unbounded buffering below.
        options.Limits.MaxRequestBodySize = MaxRequestBodySize;
    }

    private async Task ForwardAsync(HttpContext ctx, IHttpClientFactory factory)
    {
        if (!AnthropicProxyUtil.ValidateToken(ctx, _proxyToken)) return;

        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        var requestBytes = FilterFoundryIncompatibleAdditionalTools(ms.ToArray(), out var filterSummary);
        requestBytes = FixLeadingReasoningItem(requestBytes, out var reasoningFixSummary);
        var requestSummary = BuildRequestSummary(requestBytes);
        if (filterSummary is not null)
        {
            await WriteLocalFailureAsync("request.filtered", requestSummary, filterSummary, ctx.RequestAborted);
        }
        if (reasoningFixSummary is not null)
        {
            await WriteLocalFailureAsync("request.reasoning_fix", requestSummary, reasoningFixSummary, ctx.RequestAborted);
        }

        var client = factory.CreateClient("codex-passthrough");
        var requestHash = Convert.ToHexString(SHA256.HashData(requestBytes)).ToLowerInvariant()[..16];
        var responseConfigured = false;
        var originalCacheKey = GetPromptCacheKey(requestBytes);
        var activeCacheKey = originalCacheKey;
        var attemptRequestBytes = requestBytes;
        var consecutiveServerErrors = 0;
        var cacheBustsThisRequest = 0;

        if (_cacheBustOnServerError && originalCacheKey is not null
            && TryGetCacheKeyOverride(originalCacheKey, out var existingOverride))
        {
            activeCacheKey = existingOverride.Replacement;
            attemptRequestBytes = ReplacePromptCacheKey(requestBytes, activeCacheKey);
        }

        for (var attempt = 0; attempt <= _transparentRetries; attempt++)
        {
            var cacheKeyForAttempt = activeCacheKey;
            var cacheKeyWasOverridden = originalCacheKey is not null
                && !string.Equals(cacheKeyForAttempt, originalCacheKey, StringComparison.Ordinal);
            using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, _upstreamUrl)
            {
                Content = new ByteArrayContent(attemptRequestBytes),
            };
            upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            try
            {
                await FoundryResponsesEndpoint.ApplyUpstreamAuthAsync(
                    upstreamReq, _creds, _authService, _foundryScope, ctx.RequestAborted, forceBearer: true);
            }
            catch (Exception ex)
            {
                await WriteLocalFailureAsync("auth", requestSummary, ex.ToString(), ctx.RequestAborted);
                if (!responseConfigured)
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsync(
                        "Could not obtain Foundry access token. Run `pks foundry init` or `pks foundry select` and retry.",
                        ctx.RequestAborted);
                }
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            using var upstream = await client.SendAsync(
                upstreamReq, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

            if (!upstream.IsSuccessStatusCode)
            {
                var body = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                await WriteLocalFailureAsync("http", requestSummary, $"HTTP {(int)upstream.StatusCode}: {body}", ctx.RequestAborted);
                await WriteAttemptTelemetryAsync(requestHash, attempt, _transparentRetries + 1, "http_error",
                    stopwatch.ElapsedMilliseconds, upstream, null, null, false, 0, 0, null, ctx.RequestAborted);
                if (!responseConfigured)
                    await AnthropicProxyUtil.RelayUpstreamErrorAsync(ctx, upstream);
                return;
            }

            var contentType = upstream.Content.Headers.ContentType?.ToString();
            if (!IsEventStream(contentType))
            {
                if (!responseConfigured)
                {
                    ConfigureDownstreamResponse(ctx, upstream, contentType);
                    responseConfigured = true;
                }
                await RelayRawResponseAsync(ctx, upstream, ctx.RequestAborted);
                await WriteAttemptTelemetryAsync(requestHash, attempt, _transparentRetries + 1, "non_sse_completed",
                    stopwatch.ElapsedMilliseconds, upstream, null, null, true, 0, 0, null, ctx.RequestAborted);
                return;
            }

            if (!responseConfigured)
            {
                ConfigureDownstreamResponse(ctx, upstream, contentType);
                responseConfigured = true;
            }

            var result = await RelaySseAttemptAsync(
                ctx, upstream, requestSummary, attemptRequestBytes, persistFailedBody: attempt == 0,
                _bufferFullResponse, ctx.RequestAborted);
            stopwatch.Stop();
            if (result.RetryableBeforeCommit)
            {
                consecutiveServerErrors = CountsTowardPromptCacheRecovery(
                        result.RetryableBeforeCommit, result.TerminalEventType, result.ErrorCode)
                    ? consecutiveServerErrors + 1
                    : 0;
            }

            if (result.RetryableBeforeCommit && attempt < _transparentRetries)
            {
                var serverErrorsOnCacheKey = consecutiveServerErrors;
                var cacheBustTriggered = false;
                var cacheBustLimitReached = false;
                string? nextCacheKeyHash = null;

                if (ShouldBustPromptCache(
                        _cacheBustOnServerError,
                        originalCacheKey is not null,
                        consecutiveServerErrors,
                        _cacheBustAfterErrors,
                        cacheBustsThisRequest))
                {
                    if (TryRotateCacheKey(originalCacheKey!, out var rotated))
                    {
                        activeCacheKey = rotated.Replacement;
                        attemptRequestBytes = ReplacePromptCacheKey(requestBytes, activeCacheKey);
                        cacheBustsThisRequest++;
                        consecutiveServerErrors = 0;
                        cacheBustTriggered = true;
                        nextCacheKeyHash = HashCacheKey(activeCacheKey);
                        await WriteLocalFailureAsync(
                            "cache.bust",
                            requestSummary,
                            $"Rotated prompt cache key after {_cacheBustAfterErrors} consecutive cache-eligible response.failed events. " +
                            $"old_hash={HashCacheKey(cacheKeyForAttempt)} new_hash={nextCacheKeyHash} " +
                            $"rotation={rotated.Rotations}/{_cacheBustMaxRotations}",
                            ctx.RequestAborted);
                    }
                    else
                    {
                        cacheBustLimitReached = true;
                    }
                }

                var delay = CalculateRetryDelay(attempt + 1, _retryBaseDelayMs, _retryMaxDelayMs, Random.Shared.NextDouble());
                var retryOutcome = cacheBustTriggered ? "response_failed_cache_busting" : "response_failed_retrying";
                var cacheDiagnostics = new CacheAttemptDiagnostics(
                    CacheKeyHash: HashCacheKey(cacheKeyForAttempt),
                    CacheKeyOverridden: cacheKeyWasOverridden,
                    ConsecutiveServerErrors: serverErrorsOnCacheKey,
                    CacheBustTriggered: cacheBustTriggered,
                    NextCacheKeyHash: nextCacheKeyHash,
                    CacheBustsThisRequest: cacheBustsThisRequest,
                    CacheBustLimitReached: cacheBustLimitReached);
                await WriteAttemptTelemetryAsync(requestHash, attempt, _transparentRetries + 1, retryOutcome,
                    stopwatch.ElapsedMilliseconds, upstream, result.ResponseId, result.ErrorCode, false,
                    result.EventCount, result.BufferedBytes, delay.TotalMilliseconds, ctx.RequestAborted, result,
                    cacheDiagnostics);
                await ctx.Response.WriteAsync($": pks-foundry retry {attempt + 1}/{_transparentRetries} in {(int)delay.TotalMilliseconds}ms\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                await Task.Delay(delay, ctx.RequestAborted);
                continue;
            }

            if (result.BufferedPayload.Length > 0)
            {
                await ctx.Response.WriteAsync(result.BufferedPayload, ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            var outcome = result.RetryableBeforeCommit ? "response_failed_exhausted" : result.Outcome;
            var finalCacheDiagnostics = new CacheAttemptDiagnostics(
                CacheKeyHash: HashCacheKey(cacheKeyForAttempt),
                CacheKeyOverridden: cacheKeyWasOverridden,
                ConsecutiveServerErrors: consecutiveServerErrors,
                CacheBustTriggered: false,
                NextCacheKeyHash: null,
                CacheBustsThisRequest: cacheBustsThisRequest,
                CacheBustLimitReached: false);
            await WriteAttemptTelemetryAsync(requestHash, attempt, _transparentRetries + 1, outcome,
                stopwatch.ElapsedMilliseconds, upstream, result.ResponseId, result.ErrorCode, result.OutputCommitted,
                result.EventCount, result.BufferedBytes, null, ctx.RequestAborted, result, finalCacheDiagnostics);
            return;
        }
    }

    private static void ConfigureDownstreamResponse(HttpContext ctx, HttpResponseMessage upstream, string? contentType)
    {
        ctx.Response.StatusCode = (int)upstream.StatusCode;
        if (!string.IsNullOrEmpty(contentType)) ctx.Response.ContentType = contentType;
        ctx.Response.Headers["Cache-Control"] = "no-cache";
    }

    private static async Task RelayRawResponseAsync(HttpContext ctx, HttpResponseMessage upstream, CancellationToken ct)
    {
        await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        int read;
        while ((read = await upstreamStream.ReadAsync(buffer, ct)) > 0)
        {
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }

    internal static byte[] FilterFoundryIncompatibleAdditionalTools(byte[] requestBytes, out string? summary)
    {
        summary = null;
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(requestBytes) as JsonObject;
        }
        catch (JsonException)
        {
            return requestBytes;
        }

        if (root?["input"] is not JsonArray input)
        {
            return requestBytes;
        }

        var removed = 0;
        foreach (var item in input.OfType<JsonObject>())
        {
            var type = item["type"]?.GetValue<string>();
            if (!string.Equals(type, "additional_tools", StringComparison.OrdinalIgnoreCase)
                || item["tools"] is not JsonArray tools)
            {
                continue;
            }

            for (var i = tools.Count - 1; i >= 0; i--)
            {
                if (tools[i] is not JsonObject tool || !IsReservedCollaborationTool(tool))
                {
                    continue;
                }

                tools.RemoveAt(i);
                removed++;
            }
        }

        if (removed == 0)
        {
            return requestBytes;
        }

        summary = $"Removed {removed} `collaboration` additional_tools entr{(removed == 1 ? "y" : "ies")} for Azure AI Foundry compatibility.";
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    /// <summary>
    /// Observed on a live gpt-5.6-sol session: Foundry masked a real request with a generic 400
    /// (<c>invalid_request_error</c>, "There was an issue with your request...") whose <c>input[0]</c>
    /// was a <c>reasoning</c> item (opaque <c>encrypted_content</c> from a prior turn) followed by a
    /// tool-call round trip — the shape Codex's own compaction/resume produces whenever a kept slice
    /// happens to start on a reasoning item. Bisected replay against the passthrough reproduced this
    /// reliably for over an hour, isolated down to a 4-item request, and confirmed a placeholder ahead
    /// of the reasoning item avoided it every time (content-preserving, unlike dropping the item).
    /// However, a later retest found the *exact same bytes* (including the original 62-item capture)
    /// now succeed unmodified — so the trigger is not a stable, purely structural rule; it looks like
    /// a transient Azure-side condition (backend replica/capacity/routing) that has since cleared,
    /// not something client-side bytes alone determine. This prepend is kept as a cheap, harmless
    /// hedge (a no-op placeholder ahead of a reasoning item cannot make a request worse) rather than
    /// a confirmed fix — if the failure recurs, check <see cref="FailureLogPath"/> for its Azure
    /// correlation headers (already captured on every response.failed) to file a proper support case.
    /// </summary>
    internal static byte[] FixLeadingReasoningItem(byte[] requestBytes, out string? summary)
    {
        summary = null;
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(requestBytes) as JsonObject;
        }
        catch (JsonException)
        {
            return requestBytes;
        }

        if (root?["input"] is not JsonArray { Count: > 0 } input)
        {
            return requestBytes;
        }

        if (input[0] is not JsonObject first
            || !string.Equals(first["type"]?.GetValue<string>(), "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            return requestBytes;
        }

        var placeholder = new JsonObject
        {
            ["type"] = "message",
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "output_text",
                ["text"] = "",
            }),
        };
        input.Insert(0, placeholder);

        summary = "Prepended a no-op placeholder message ahead of a leading `reasoning` item " +
                   "(Foundry sol rejects requests whose input[0] is a reasoning item).";
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static bool IsReservedCollaborationTool(JsonObject tool)
    {
        var name = tool["name"]?.GetValue<string>();
        if (string.Equals(name, "collaboration", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ns = tool["namespace"]?.GetValue<string>();
        return string.Equals(ns, "collaboration", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEventStream(string? contentType)
    {
        return !string.IsNullOrEmpty(contentType)
               && contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<SseAttemptResult> RelaySseAttemptAsync(
        HttpContext ctx,
        HttpResponseMessage upstream,
        string requestSummary,
        byte[] requestBytes,
        bool persistFailedBody,
        bool bufferFullResponse,
        CancellationToken ct)
    {
        var upstreamHeaders = FormatUpstreamHeaders(upstream);
        await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(upstreamStream, Encoding.UTF8);
        var buffered = new StringBuilder();
        var outputCommitted = false;
        var eventCount = 0;
        var streamStopwatch = Stopwatch.StartNew();
        var eventTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var recentEventTypes = new Queue<string>();
        long receivedSseBytes = 0;
        long forwardedSseBytes = 0;
        long? firstEventAfterMs = null;
        string? commitEventType = null;
        string? commitReason = null;
        int? commitEventIndex = null;
        long? commitAfterMs = null;
        string? terminalEventType = null;
        string? responseId = null;
        string? errorCode = null;
        string? errorMessage = null;

        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadSseFrameAsync(reader, ct);
            if (frame is null) break;
            eventCount++;
            var analysis = AnalyzeSseEvent(frame.EventName, frame.Data);
            var eventType = analysis.EventType ?? "<unknown>";
            var frameBytes = Encoding.UTF8.GetByteCount(frame.Raw);
            receivedSseBytes += frameBytes;
            firstEventAfterMs ??= streamStopwatch.ElapsedMilliseconds;
            eventTypeCounts[eventType] = eventTypeCounts.GetValueOrDefault(eventType) + 1;
            recentEventTypes.Enqueue(eventType);
            if (recentEventTypes.Count > 12) recentEventTypes.Dequeue();
            responseId ??= analysis.ResponseId;
            errorCode ??= analysis.ErrorCode;
            errorMessage ??= analysis.ErrorMessage;

            if (!outputCommitted)
            {
                if (buffered.Length + frame.Raw.Length <= MaxBufferedSseBytes)
                {
                    buffered.Append(frame.Raw);
                }
                else
                {
                    // Preserve streaming and bound memory. Once any attempt bytes are visible to
                    // Codex, a transparent retry could duplicate response/tool ids and is disabled.
                    outputCommitted = true;
                    commitEventType = eventType;
                    commitReason = "buffer_limit";
                    commitEventIndex = eventCount;
                    commitAfterMs = streamStopwatch.ElapsedMilliseconds;
                    var bufferedPayload = buffered.ToString();
                    forwardedSseBytes += Encoding.UTF8.GetByteCount(bufferedPayload) + frameBytes;
                    await ctx.Response.WriteAsync(bufferedPayload, ct);
                    buffered.Clear();
                    await ctx.Response.WriteAsync(frame.Raw, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            else
            {
                forwardedSseBytes += frameBytes;
                await ctx.Response.WriteAsync(frame.Raw, ct);
                await ctx.Response.Body.FlushAsync(ct);
            }

            if (!outputCommitted && ShouldCommitSseEvent(analysis, bufferFullResponse))
            {
                outputCommitted = true;
                commitEventType = eventType;
                commitReason = "semantic_delta";
                commitEventIndex = eventCount;
                commitAfterMs = streamStopwatch.ElapsedMilliseconds;
                var bufferedPayload = buffered.ToString();
                forwardedSseBytes += Encoding.UTF8.GetByteCount(bufferedPayload);
                await ctx.Response.WriteAsync(bufferedPayload, ct);
                buffered.Clear();
                await ctx.Response.Body.FlushAsync(ct);
            }

            if (analysis.IsFailure)
            {
                terminalEventType = eventType;
                await LogResponseFailedIfPresentAsync(
                    frame.EventName, frame.Data, requestSummary, upstreamHeaders, requestBytes, persistFailedBody, ct);
                return new SseAttemptResult(
                    RetryableBeforeCommit: !outputCommitted && analysis.IsRetryableFailure,
                    OutputCommitted: outputCommitted,
                    Outcome: outputCommitted ? "response_failed_after_commit" : "response_failed",
                    BufferedPayload: outputCommitted ? "" : buffered.ToString(),
                    BufferedBytes: Encoding.UTF8.GetByteCount(buffered.ToString()),
                    EventCount: eventCount,
                    ResponseId: responseId,
                    ErrorCode: errorCode,
                    ErrorMessage: errorMessage,
                    FirstEventAfterMs: firstEventAfterMs,
                    CommitEventType: commitEventType,
                    CommitReason: commitReason,
                    CommitEventIndex: commitEventIndex,
                    CommitAfterMs: commitAfterMs,
                    TerminalEventType: terminalEventType,
                    StreamEndedAfterMs: streamStopwatch.ElapsedMilliseconds,
                    ReceivedSseBytes: receivedSseBytes,
                    ForwardedSseBytes: forwardedSseBytes,
                    EventTypeCounts: eventTypeCounts,
                    RecentEventTypes: recentEventTypes.ToArray(),
                    BufferedFullResponse: bufferFullResponse);
            }

            if (analysis.IsCompleted)
            {
                terminalEventType = eventType;
                if (!outputCommitted)
                {
                    outputCommitted = true;
                    commitEventType = eventType;
                    commitReason = bufferFullResponse ? "completed_response" : "terminal_event";
                    commitEventIndex = eventCount;
                    commitAfterMs = streamStopwatch.ElapsedMilliseconds;
                    var bufferedPayload = buffered.ToString();
                    forwardedSseBytes += Encoding.UTF8.GetByteCount(bufferedPayload);
                    await ctx.Response.WriteAsync(bufferedPayload, ct);
                    buffered.Clear();
                    await ctx.Response.Body.FlushAsync(ct);
                }
                return new SseAttemptResult(false, outputCommitted, "response_completed", "", 0,
                    eventCount, responseId, errorCode, errorMessage, firstEventAfterMs, commitEventType,
                    commitReason, commitEventIndex, commitAfterMs, terminalEventType,
                    streamStopwatch.ElapsedMilliseconds, receivedSseBytes, forwardedSseBytes,
                    eventTypeCounts, recentEventTypes.ToArray(), bufferFullResponse);
            }
        }

        // A clean SSE response has a response.completed/failed terminal event. EOF before either
        // is safe to replay only while no semantic output has escaped the gate.
        return new SseAttemptResult(
            RetryableBeforeCommit: !outputCommitted,
            OutputCommitted: outputCommitted,
            Outcome: outputCommitted ? "stream_disconnected_after_commit" : "stream_disconnected",
            BufferedPayload: outputCommitted ? "" : buffered.ToString(),
            BufferedBytes: Encoding.UTF8.GetByteCount(buffered.ToString()),
            EventCount: eventCount,
            ResponseId: responseId,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage,
            FirstEventAfterMs: firstEventAfterMs,
            CommitEventType: commitEventType,
            CommitReason: commitReason,
            CommitEventIndex: commitEventIndex,
            CommitAfterMs: commitAfterMs,
            TerminalEventType: terminalEventType,
            StreamEndedAfterMs: streamStopwatch.ElapsedMilliseconds,
            ReceivedSseBytes: receivedSseBytes,
            ForwardedSseBytes: forwardedSseBytes,
            EventTypeCounts: eventTypeCounts,
            RecentEventTypes: recentEventTypes.ToArray(),
            BufferedFullResponse: bufferFullResponse);
    }

    private static async Task<SseFrame?> ReadSseFrameAsync(StreamReader reader, CancellationToken ct)
    {
        var raw = new StringBuilder();
        var data = new StringBuilder();
        string? eventName = null;
        var readAny = false;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            readAny = true;
            raw.Append(line).Append('\n');
            if (line.Length == 0) break;

            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line.Length > 6 && line[6] == ' ' ? line[7..] : line[6..];
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..]);
            }
        }

        return readAny ? new SseFrame(raw.ToString(), eventName, data.ToString()) : null;
    }

    internal static SseEventAnalysis AnalyzeSseEvent(string? eventName, string payload)
    {
        if (payload == "[DONE]")
            return new SseEventAnalysis(false, false, false, false, eventName ?? "[DONE]", null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var payloadType = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            var type = eventName ?? payloadType;
            var isFailure = string.Equals(type, "response.failed", StringComparison.Ordinal)
                            || string.Equals(payloadType, "response.failed", StringComparison.Ordinal);
            var isCompleted = string.Equals(type, "response.completed", StringComparison.Ordinal)
                              || string.Equals(payloadType, "response.completed", StringComparison.Ordinal);
            var commitsOutput = type?.EndsWith(".delta", StringComparison.Ordinal) == true;

            string? responseId = null;
            string? errorCode = null;
            string? errorMessage = null;
            if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
            {
                if (response.TryGetProperty("id", out var idProp)) responseId = idProp.GetString();
                if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("code", out var codeProp)) errorCode = codeProp.GetString();
                    if (error.TryGetProperty("message", out var messageProp))
                        errorMessage = AnthropicProxyUtil.Truncate(messageProp.GetString() ?? "", 500);
                }
            }

            var retryableFailure = isFailure && (errorCode is null
                || errorCode is "unknown" or "server_error" or "no_capacity" or "rate_limit_exceeded");
            return new SseEventAnalysis(isFailure, retryableFailure, isCompleted, commitsOutput, type,
                responseId, errorCode, errorMessage);
        }
        catch (JsonException)
        {
            return new SseEventAnalysis(false, false, false, false, eventName, null, null, null);
        }
    }

    internal static TimeSpan CalculateRetryDelay(int retryNumber, int baseDelayMs, int maxDelayMs, double jitterSample)
    {
        var exponent = Math.Max(0, retryNumber - 1);
        var exponential = Math.Min(maxDelayMs, baseDelayMs * Math.Pow(2, exponent));
        var boundedJitter = Math.Clamp(jitterSample, 0, 1) * 0.25;
        return TimeSpan.FromMilliseconds(Math.Min(maxDelayMs, exponential * (1 + boundedJitter)));
    }

    internal static bool ShouldCommitSseEvent(SseEventAnalysis analysis, bool bufferFullResponse)
    {
        return !bufferFullResponse && analysis.CommitsOutput;
    }

    internal static bool CountsTowardPromptCacheRecovery(
        bool retryableBeforeCommit,
        string? terminalEventType,
        string? errorCode)
    {
        // A poisoned Foundry prompt-cache entry has been observed with both an explicit
        // server_error and a null error object. Restrict cache rotation to terminal
        // response.failed events so an ordinary transport EOF or rate limit does not
        // unnecessarily discard an otherwise healthy cache identity.
        return retryableBeforeCommit
               && string.Equals(terminalEventType, "response.failed", StringComparison.Ordinal)
               && errorCode is null or "unknown" or "server_error";
    }

    internal static bool ShouldBustPromptCache(
        bool enabled,
        bool hasCacheKey,
        int consecutiveServerErrors,
        int threshold,
        int cacheBustsThisRequest)
    {
        return enabled
               && hasCacheKey
               && consecutiveServerErrors >= threshold
               && cacheBustsThisRequest == 0;
    }

    private static int ReadBoundedIntEnvironment(string name, int fallback, int min, int max)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    internal static bool ReadBooleanEnvironment(string name, bool fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (bool.TryParse(value, out var parsed)) return parsed;
        return value switch
        {
            "1" => true,
            "0" => false,
            _ => fallback,
        };
    }

    internal static string? GetPromptCacheKey(byte[] requestBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(requestBytes);
            return document.RootElement.TryGetProperty("prompt_cache_key", out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static byte[] ReplacePromptCacheKey(byte[] requestBytes, string replacement)
    {
        try
        {
            if (JsonNode.Parse(requestBytes) is not JsonObject root) return requestBytes;
            root["prompt_cache_key"] = replacement;
            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }
        catch (JsonException)
        {
            return requestBytes;
        }
    }

    internal static string? HashCacheKey(string? cacheKey)
    {
        return cacheKey is null
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))).ToLowerInvariant()[..12];
    }

    private bool TryGetCacheKeyOverride(string original, out CacheKeyOverride cacheOverride)
    {
        lock (_cacheKeyRecoveryLock)
        {
            return _cacheKeyOverrides.TryGetValue(original, out cacheOverride!);
        }
    }

    private bool TryRotateCacheKey(string original, out CacheKeyOverride cacheOverride)
    {
        lock (_cacheKeyRecoveryLock)
        {
            var rotations = _cacheKeyOverrides.TryGetValue(original, out var current)
                ? current.Rotations
                : 0;
            if (rotations >= _cacheBustMaxRotations)
            {
                cacheOverride = current!;
                return false;
            }

            var replacement = $"pks-recovery-{HashCacheKey(original)}-{Guid.NewGuid():N}";
            cacheOverride = new CacheKeyOverride(replacement, rotations + 1);
            _cacheKeyOverrides[original] = cacheOverride;
            return true;
        }
    }

    internal readonly record struct SseEventAnalysis(
        bool IsFailure,
        bool IsRetryableFailure,
        bool IsCompleted,
        bool CommitsOutput,
        string? EventType,
        string? ResponseId,
        string? ErrorCode,
        string? ErrorMessage);

    private sealed record SseFrame(string Raw, string? EventName, string Data);

    private sealed record CacheKeyOverride(string Replacement, int Rotations);

    private sealed record CacheAttemptDiagnostics(
        string? CacheKeyHash,
        bool CacheKeyOverridden,
        int ConsecutiveServerErrors,
        bool CacheBustTriggered,
        string? NextCacheKeyHash,
        int CacheBustsThisRequest,
        bool CacheBustLimitReached);

    private sealed record SseAttemptResult(
        bool RetryableBeforeCommit,
        bool OutputCommitted,
        string Outcome,
        string BufferedPayload,
        int BufferedBytes,
        int EventCount,
        string? ResponseId,
        string? ErrorCode,
        string? ErrorMessage,
        long? FirstEventAfterMs,
        string? CommitEventType,
        string? CommitReason,
        int? CommitEventIndex,
        long? CommitAfterMs,
        string? TerminalEventType,
        long StreamEndedAfterMs,
        long ReceivedSseBytes,
        long ForwardedSseBytes,
        IReadOnlyDictionary<string, int> EventTypeCounts,
        IReadOnlyList<string> RecentEventTypes,
        bool BufferedFullResponse);

    /// <summary>
    /// Azure's SDK-facing error body for many backend failure classes is a deliberately generic
    /// catch-all ("There was an issue with your request..."). The only way to correlate a specific
    /// failure with Azure-side diagnostics (Log Analytics, a support case) is the correlation
    /// headers on the HTTP response itself, so capture those verbatim alongside the body.
    /// </summary>
    private static string FormatUpstreamHeaders(HttpResponseMessage upstream)
    {
        var parts = new List<string>();
        foreach (var header in upstream.Headers)
        {
            parts.Add($"{header.Key}={string.Join(",", header.Value)}");
        }
        foreach (var header in upstream.Content.Headers)
        {
            parts.Add($"{header.Key}={string.Join(",", header.Value)}");
        }
        return parts.Count == 0 ? "<none>" : string.Join(" | ", parts);
    }

    private static async Task LogResponseFailedIfPresentAsync(
        string? eventName,
        string payload,
        string requestSummary,
        string upstreamHeaders,
        byte[] requestBytes,
        bool persistRequestBody,
        CancellationToken ct)
    {
        if (payload == "[DONE]") return;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var payloadType = root.TryGetProperty("type", out var type) ? type.GetString() : null;
            var isFailure =
                string.Equals(eventName, "response.failed", StringComparison.Ordinal)
                || string.Equals(payloadType, "response.failed", StringComparison.Ordinal);
            if (!isFailure)
            {
                return;
            }

            var responseId = root.TryGetProperty("response", out var responseProp)
                && responseProp.ValueKind == JsonValueKind.Object
                && responseProp.TryGetProperty("id", out var idProp)
                ? idProp.GetString()
                : null;
            var requestBodyPath = persistRequestBody
                ? await PersistFailedRequestBodyAsync(requestBytes, responseId, ct)
                : null;

            var summary = BuildFailureSummary(root);
            var details = new StringBuilder()
                .AppendLine(summary)
                .AppendLine($"upstream_headers={upstreamHeaders}")
                .AppendLine($"full_request_body={requestBodyPath ?? (persistRequestBody ? "<not saved>" : "<duplicate retry not saved>")}")
                .ToString();
            await WriteLocalFailureAsync("response.failed", requestSummary, details, ct);
        }
        catch (JsonException)
        {
            // Invalid partial/debug SSE payloads are relayed unchanged; they just are not diagnosable here.
        }
    }

    /// <summary>
    /// The in-memory failure log truncates request markers to a short summary for readability.
    /// On an actual response.failed, persist the exact bytes Codex sent (post `collaboration`-tool
    /// filtering) so the request can be replayed or bisected to find what Foundry rejected.
    /// </summary>
    private static async Task<string?> PersistFailedRequestBodyAsync(byte[] requestBytes, string? responseId, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(FailedRequestBodyDir);
            var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fffffff}-{responseId ?? "no-id"}.request.json";
            var path = Path.Combine(FailedRequestBodyDir, fileName);
            await File.WriteAllBytesAsync(path, requestBytes, ct);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildFailureSummary(JsonElement root)
    {
        try
        {
            var response = root.TryGetProperty("response", out var responseProp) ? responseProp : default;
            var id = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("id", out var idProp)
                ? idProp.GetString()
                : null;
            var status = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString()
                : null;
            var error = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("error", out var errorProp)
                ? errorProp.ToString()
                : null;
            var incomplete = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("incomplete_details", out var incompleteProp)
                ? incompleteProp.ToString()
                : null;
            var usage = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("usage", out var usageProp)
                ? usageProp.ToString()
                : null;

            return new StringBuilder()
                .AppendLine($"id={id ?? "<none>"} status={status ?? "<none>"}")
                .AppendLine($"error={NullIfBlank(error) ?? "<null>"}")
                .AppendLine($"incomplete_details={NullIfBlank(incomplete) ?? "<null>"}")
                .AppendLine($"usage={NullIfBlank(usage) ?? "<null>"}")
                .AppendLine("payload_prefix=")
                .AppendLine(AnthropicProxyUtil.Truncate(root.ToString(), 2000))
                .ToString();
        }
        catch
        {
            return AnthropicProxyUtil.Truncate(root.ToString(), 4000);
        }
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || value == "null" ? null : value;
    }

    private static string BuildRequestSummary(byte[] requestBytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBytes);
            var root = doc.RootElement;
            var model = root.TryGetProperty("model", out var modelProp) ? modelProp.GetString() : null;
            var stream = root.TryGetProperty("stream", out var streamProp) && streamProp.ValueKind == JsonValueKind.True;
            var previousResponseId = root.TryGetProperty("previous_response_id", out var prevProp) ? prevProp.GetString() : null;
            var inputKind = root.TryGetProperty("input", out var inputProp) ? inputProp.ValueKind.ToString() : "missing";
            var truncation = root.TryGetProperty("truncation", out var truncationProp) ? truncationProp.GetString() : null;
            var tools = SummarizeTools(root);
            var markers = SummarizeRequestMarkers(root);
            return $"model={model ?? "<unset>"} stream={stream} truncation={truncation ?? "<unset>"} previous_response_id={(previousResponseId is null ? "<none>" : "<set>")} input_kind={inputKind} tools={tools} markers={markers} bytes={requestBytes.Length}";
        }
        catch (JsonException)
        {
            return $"invalid_json bytes={requestBytes.Length}";
        }
    }

    private static string SummarizeTools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return "<none>";
        }

        var summaries = new List<string>();
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                summaries.Add(tool.ValueKind.ToString());
                continue;
            }

            var type = tool.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            var name = tool.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var ns = tool.TryGetProperty("namespace", out var nsProp) ? nsProp.GetString() : null;
            summaries.Add($"type={type ?? "<unset>"},namespace={ns ?? "<unset>"},name={name ?? "<unset>"}");
        }

        return summaries.Count == 0
            ? "[]"
            : AnthropicProxyUtil.Truncate(string.Join(" | ", summaries), 2000);
    }

    private static string SummarizeRequestMarkers(JsonElement root)
    {
        var markers = new List<string>();
        CollectRequestMarkers(root, "$", markers);
        return markers.Count == 0
            ? "<none>"
            : AnthropicProxyUtil.Truncate(string.Join(" | ", markers), 3000);
    }

    private static void CollectRequestMarkers(JsonElement element, string path, ICollection<string> markers)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = $"{path}.{property.Name}";
                    if (property.Name.Contains("tool", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("collaboration", StringComparison.OrdinalIgnoreCase))
                    {
                        markers.Add($"{propertyPath}={SummarizeMarkerValue(property.Value)}");
                    }

                    CollectRequestMarkers(property.Value, propertyPath, markers);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    CollectRequestMarkers(item, $"{path}[{index}]", markers);
                    index++;
                }
                break;

            case JsonValueKind.String:
                var value = element.GetString();
                if (value is not null &&
                    (value.Contains("tool", StringComparison.OrdinalIgnoreCase)
                     || value.Contains("collaboration", StringComparison.OrdinalIgnoreCase)))
                {
                    markers.Add($"{path}={AnthropicProxyUtil.Truncate(value, 240)}");
                }
                break;
        }
    }

    private static string SummarizeMarkerValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => AnthropicProxyUtil.Truncate(value.GetString() ?? "", 240),
            JsonValueKind.Array => $"Array({value.GetArrayLength()})",
            JsonValueKind.Object => AnthropicProxyUtil.Truncate(value.ToString(), 240),
            _ => value.ToString(),
        };
    }

    private static async Task WriteLocalFailureAsync(
        string kind,
        string requestSummary,
        string details,
        CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(PksDir);
            var entry = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.UtcNow:O}] {kind}")
                .AppendLine(requestSummary)
                .AppendLine(AnthropicProxyUtil.Truncate(details, 12000))
                .AppendLine()
                .ToString();
            await File.AppendAllTextAsync(FailureLogPath, entry, ct);
        }
        catch
        {
            // Diagnostics should never break the proxy response path.
        }
    }

    private static async Task WriteAttemptTelemetryAsync(
        string requestHash,
        int zeroBasedAttempt,
        int maxAttempts,
        string outcome,
        long durationMs,
        HttpResponseMessage upstream,
        string? responseId,
        string? errorCode,
        bool outputCommitted,
        int eventCount,
        int bufferedBytes,
        double? retryDelayMs,
        CancellationToken ct,
        SseAttemptResult? sse = null,
        CacheAttemptDiagnostics? cache = null)
    {
        try
        {
            var record = new
            {
                timestamp = DateTimeOffset.UtcNow,
                request_hash = requestHash,
                attempt = zeroBasedAttempt + 1,
                max_attempts = maxAttempts,
                outcome,
                duration_ms = durationMs,
                http_status = (int)upstream.StatusCode,
                response_id = responseId,
                error_code = errorCode,
                error_message = sse?.ErrorMessage,
                output_committed = outputCommitted,
                buffer_full_response = sse?.BufferedFullResponse,
                event_count = eventCount,
                buffered_bytes = bufferedBytes,
                first_event_after_ms = sse?.FirstEventAfterMs,
                commit_event_type = sse?.CommitEventType,
                commit_reason = sse?.CommitReason,
                commit_event_index = sse?.CommitEventIndex,
                commit_after_ms = sse?.CommitAfterMs,
                terminal_event_type = sse?.TerminalEventType,
                stream_ended_after_ms = sse?.StreamEndedAfterMs,
                received_sse_bytes = sse?.ReceivedSseBytes,
                forwarded_sse_bytes = sse?.ForwardedSseBytes,
                event_type_counts = sse?.EventTypeCounts,
                recent_event_types = sse?.RecentEventTypes,
                prompt_cache_key_hash = cache?.CacheKeyHash,
                cache_key_overridden = cache?.CacheKeyOverridden,
                consecutive_server_errors = cache?.ConsecutiveServerErrors,
                cache_bust_triggered = cache?.CacheBustTriggered,
                next_cache_key_hash = cache?.NextCacheKeyHash,
                cache_busts_this_request = cache?.CacheBustsThisRequest,
                cache_bust_limit_reached = cache?.CacheBustLimitReached,
                retry_delay_ms = retryDelayMs is null ? null : (long?)Math.Round(retryDelayMs.Value),
                azure_request_id = GetHeader(upstream, "X-Request-ID") ?? GetHeader(upstream, "apim-request-id"),
                served_model = GetHeader(upstream, "x-ms-served-model"),
                region = GetHeader(upstream, "x-ms-region"),
                remaining_requests = GetHeader(upstream, "x-ratelimit-remaining-requests"),
                remaining_tokens = GetHeader(upstream, "x-ratelimit-remaining-tokens"),
            };
            var line = JsonSerializer.Serialize(record) + Environment.NewLine;
            Directory.CreateDirectory(PksDir);
            await AttemptLogLock.WaitAsync(ct);
            try { await File.AppendAllTextAsync(AttemptLogPath, line, ct); }
            finally { AttemptLogLock.Release(); }
        }
        catch
        {
            // Attempt telemetry is diagnostic only and must not affect the response path.
        }
    }

    private static string? GetHeader(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values)) return values.FirstOrDefault();
        return response.Content.Headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        try { await _app.StopAsync(); }
        catch { /* never started (e.g. bind failure) — dispose is enough */ }
        await _app.DisposeAsync();
        _app = null;
    }
}
