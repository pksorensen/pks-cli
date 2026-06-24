using Spectre.Console;
using PKS.Infrastructure.Services;

namespace PKS.Infrastructure.Services.Foundry;

/// <summary>
/// Resolved Azure AI Foundry environment for a spawned `claude` process. Applied to a
/// process's environment so the claude CLI talks to Foundry (via the local MSI token
/// server) instead of api.anthropic.com. Immutable; safe to share across many workers
/// (they all read tokens from the single backing <see cref="LocalMsiTokenServer"/>).
/// </summary>
public sealed record FoundryEnvVars(
    string Resource,
    string? ApiKey,
    string IdentityEndpoint,
    string IdentityHeader,
    IReadOnlyDictionary<string, string> ModelTier)
{
    /// <summary>Set the CLAUDE_CODE_USE_FOUNDRY env vars on a process's environment.</summary>
    public void Apply(IDictionary<string, string?> env)
    {
        env["CLAUDE_CODE_USE_FOUNDRY"] = "1";
        env["ANTHROPIC_FOUNDRY_RESOURCE"] = Resource;
        env["IDENTITY_ENDPOINT"] = IdentityEndpoint;
        env["IDENTITY_HEADER"] = IdentityHeader;
        if (!string.IsNullOrEmpty(ApiKey))
            env["ANTHROPIC_FOUNDRY_API_KEY"] = ApiKey;
        foreach (var (k, v) in ModelTier)
            env[k] = v;
    }
}

/// <summary>A started Foundry env session: the env vars plus the token server that backs them.</summary>
public sealed class FoundryExtractSession : IAsyncDisposable
{
    public required FoundryEnvVars EnvVars { get; init; }
    public required LocalMsiTokenServer Server { get; init; }
    public ValueTask DisposeAsync() => Server.DisposeAsync();
}

public interface IFoundryExtractEnv
{
    /// <summary>
    /// Start a single shared MSI token server and build the Foundry env for batch
    /// `claude` spawns. Returns null (with a console note) when Foundry isn't logged in
    /// or has no stored credentials — callers should then fall back / abort.
    /// Non-interactive (no model prompt) — suited to batch jobs like brain extract.
    /// </summary>
    Task<FoundryExtractSession?> StartAsync(CancellationToken ct = default);
}

public sealed class FoundryExtractEnv : IFoundryExtractEnv
{
    private readonly IAzureFoundryAuthService _auth;
    private readonly IAnsiConsole _console;

    public FoundryExtractEnv(IAzureFoundryAuthService auth, IAnsiConsole console)
    {
        _auth = auth;
        _console = console;
    }

    public async Task<FoundryExtractSession?> StartAsync(CancellationToken ct = default)
    {
        if (!await _auth.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[yellow]--foundry:[/] not logged in to Azure AI Foundry. Run [bold]pks foundry[/] first.");
            return null;
        }

        var creds = await _auth.GetStoredCredentialsAsync();
        if (creds == null)
        {
            _console.MarkupLine("[yellow]--foundry:[/] no stored Foundry credentials.");
            return null;
        }

        var server = await LocalMsiTokenServer.StartAsync(_auth, _console);

        var enabled = creds.EnabledModels.Count > 0
            ? creds.EnabledModels
            : new List<string> { creds.DefaultModel };

        // Map enabled Claude deployments to their tiers so --model haiku/sonnet/opus resolve.
        // Only Claude chat deployments map to a tier — never let a non-Claude deployment clobber it.
        var tier = new Dictionary<string, string>();
        foreach (var model in enabled)
        {
            var lower = model.ToLowerInvariant();
            if (lower.Contains("sonnet")) tier["ANTHROPIC_DEFAULT_SONNET_MODEL"] = model;
            else if (lower.Contains("opus")) tier["ANTHROPIC_DEFAULT_OPUS_MODEL"] = model;
            else if (lower.Contains("haiku")) tier["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = model;
        }

        return new FoundryExtractSession
        {
            EnvVars = new FoundryEnvVars(
                Resource: creds.SelectedResourceName,
                ApiKey: creds.ApiKey,
                IdentityEndpoint: server.Endpoint,
                IdentityHeader: server.Secret,
                ModelTier: tier),
            Server = server,
        };
    }
}
