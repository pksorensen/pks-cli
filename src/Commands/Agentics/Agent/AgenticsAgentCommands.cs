using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PKS.Infrastructure.Services.Agentics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Agentics.Agent;

/// <summary>
/// `pks agentics agent …` and `pks agentics fetch` — an AI agent's own identity on
/// agentics.dk. The person shares a page with an invite link; their agent joins that
/// invite with a key it creates here, and from then on reads what is shared with it
/// as itself (see <see cref="AgentIdentity"/>).
///
/// The audience is an agent reading stdout, so output is plain text: no colours, no
/// panels, the content on stdout and the commentary on stderr.
/// </summary>
internal static class AgentHttp
{
    public static HttpClient Create()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"pks-cli/{AgentClientInfo.Collect()["pksVersion"] ?? "dev"} (agent)");
        return http;
    }

    public static async Task<(HttpStatusCode Status, JsonNode? Json, string Raw)> SendAsync(
        HttpClient http, HttpRequestMessage request, CancellationToken ct = default)
    {
        using var response = await http.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = JsonNode.Parse(raw); } catch { /* not JSON */ }
        return (response.StatusCode, json, raw);
    }

    /// <summary>The innermost exception — on Windows inside a sandbox the useful part is three levels down.</summary>
    public static string Explain(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        var text = inner == ex ? ex.Message : $"{ex.Message} ({inner.GetType().Name}: {inner.Message})";
        if (inner is System.ComponentModel.Win32Exception w && (uint)w.NativeErrorCode == 0x8009030E
            || text.Contains("SEC_E_NO_CREDENTIALS", StringComparison.OrdinalIgnoreCase)
            || inner is System.Security.Authentication.AuthenticationException)
        {
            text += Environment.NewLine +
                    "Windows' HTTPS (Schannel) is unavailable here — typically inside the Codex sandbox. " +
                    "Run this command outside the sandbox (ask your user to approve it).";
        }
        return text;
    }
}

public class AgenticsAgentSettings : AgenticsSettings
{
    [CommandOption("--key-dir <DIR>")]
    [Description("Where the agent key is stored (default: ~/.pks-cli/agents, or PKS_AGENT_KEY_DIR)")]
    public string? KeyDir { get; set; }
}

public sealed class AgenticsAgentJoinCommand : AsyncCommand<AgenticsAgentJoinCommand.Settings>
{
    public sealed class Settings : AgenticsAgentSettings
    {
        [CommandArgument(0, "<INVITE_URL>")]
        [Description("The invite link the person pasted (…/artifacts/<owner>/<project>/<id>/invite/<secret>)")]
        public string InviteUrl { get; set; } = "";

        [CommandOption("-n|--name <NAME>")]
        [Description("A name the person will recognise, e.g. \"Codex on Jakob's laptop\"")]
        public string? Name { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!Uri.TryCreate(settings.InviteUrl.Trim(), UriKind.Absolute, out var invite)
            || !invite.AbsolutePath.Contains("/invite/", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("That is not an invite link. Expected https://<server>/artifacts/<owner>/<project>/<id>/invite/<secret>.");
            return 2;
        }

        var server = AgentIdentity.NormalizeServer(invite.GetLeftPart(UriPartial.Authority));
        AgentIdentity identity;
        bool created;
        try
        {
            var existing = AgentIdentity.Load(server, settings.KeyDir);
            created = existing is null;
            identity = existing ?? AgentIdentity.Create(server);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read the agent key: {ex.Message}");
            return 1;
        }

        var name = settings.Name?.Trim();
        if (string.IsNullOrEmpty(name)) name = identity.Name ?? $"Agent på {Environment.MachineName}";

        var body = new JsonObject
        {
            ["invite"] = invite.ToString(),
            ["publicKey"] = JsonSerializer.SerializeToNode(identity.PublicJwk()),
            ["name"] = name,
            ["client"] = JsonSerializer.SerializeToNode(AgentClientInfo.Collect()),
        };

        // Persist the key BEFORE the server learns it: the invite is single-use, so a key
        // lost after a successful join (save failed, response dropped) would strand the
        // invite. Saved first, a retry presents the same key and lands on already-yours.
        string path;
        if (created)
        {
            identity.Name = name;
            try
            {
                path = identity.Save(settings.KeyDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not save the agent key to {AgentIdentity.KeyPath(server, settings.KeyDir)}: {ex.Message}");
                Console.Error.WriteLine("Nothing was sent to the server. Run join from a place that can write there, or set PKS_AGENT_KEY_DIR.");
                return 1;
            }
        }

        using var http = AgentHttp.Create();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{server}/api/artifact-agents/join")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.MintJwt(identity.Audience));

        (HttpStatusCode Status, JsonNode? Json, string Raw) result;
        try
        {
            result = await AgentHttp.SendAsync(http, request);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not reach {server}: {AgentHttp.Explain(ex)}");
            return 1;
        }

        if (result.Status != HttpStatusCode.OK)
        {
            var message = result.Json?["message"]?.GetValue<string>() ?? result.Json?["error"]?.GetValue<string>() ?? result.Raw.Trim();
            Console.Error.WriteLine($"Join refused ({(int)result.Status}): {message}");
            return 1;
        }

        identity.Name = result.Json?["agent"]?["name"]?.GetValue<string>() ?? name;
        identity.OnBehalfOf = result.Json?["agent"]?["onBehalfOf"]?.GetValue<string>();
        try
        {
            path = identity.Save(settings.KeyDir);
        }
        catch (Exception ex)
        {
            // The key itself is already on disk; only the name/on-behalf-of labels are stale.
            Console.Error.WriteLine($"Joined, but could not update the key file's labels: {ex.Message}");
            path = AgentIdentity.KeyPath(server, settings.KeyDir);
        }

        var art = result.Json?["artifact"];
        Console.Out.WriteLine(created ? "Agent registered." : "Agent already registered; invite joined.");
        Console.Out.WriteLine($"  name:          {identity.Name}");
        Console.Out.WriteLine($"  on behalf of:  {identity.OnBehalfOf} (not confirmed until that person signs in)");
        Console.Out.WriteLine($"  agent id:      {identity.Id}");
        Console.Out.WriteLine($"  key file:      {path}");
        Console.Out.WriteLine($"Shared with you: \"{art?["title"]?.GetValue<string>()}\" from {art?["from"]?.GetValue<string>()}");
        Console.Out.WriteLine($"Read it with:    pks agentics fetch \"{art?["url"]?.GetValue<string>()}\"");
        return 0;
    }
}

public sealed class AgenticsAgentStatusCommand : AsyncCommand<AgenticsAgentStatusCommand.Settings>
{
    public sealed class Settings : AgenticsAgentSettings
    {
        [CommandOption("--server <SERVER>")]
        [Description("Only this server (default: every server this machine has an agent key for)")]
        public string? Server { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        IReadOnlyList<AgentIdentity> identities;
        try
        {
            identities = settings.Server is { Length: > 0 } s
                ? (AgentIdentity.Load(s, settings.KeyDir) is { } one ? new[] { one } : Array.Empty<AgentIdentity>())
                : AgentIdentity.LoadAll(settings.KeyDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read the agent key: {ex.Message}");
            return 1;
        }

        if (identities.Count == 0)
        {
            Console.Out.WriteLine($"No agent key on this machine ({settings.KeyDir ?? AgentIdentity.KeyDirectory()}).");
            Console.Out.WriteLine("Ask your person for an invite link and run: pks agentics agent join \"<link>\" --name \"<your name>\"");
            return 1;
        }

        var failures = 0;
        using var http = AgentHttp.Create();
        foreach (var identity in identities)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{identity.Server}/api/artifact-agents/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.MintJwt(identity.Audience));
            Console.Out.WriteLine($"{identity.Server} — {identity.Name} (on behalf of {identity.OnBehalfOf}), id {identity.Id}");
            try
            {
                var result = await AgentHttp.SendAsync(http, request);
                if (result.Status != HttpStatusCode.OK)
                {
                    Console.Out.WriteLine(result.Status == HttpStatusCode.Unauthorized
                        ? "  The server does not accept this key (revoked or unknown)."
                        : $"  Server answered {(int)result.Status}.");
                    failures++;
                    continue;
                }
                var shared = result.Json?["shared"]?.AsArray() ?? new JsonArray();
                if (shared.Count == 0) Console.Out.WriteLine("  Nothing is shared with this agent right now.");
                foreach (var item in shared)
                {
                    Console.Out.WriteLine($"  - \"{item?["title"]?.GetValue<string>()}\" from {item?["from"]?.GetValue<string>()}");
                    Console.Out.WriteLine($"    pks agentics fetch \"{item?["url"]?.GetValue<string>()}\"");
                }
            }
            catch (Exception ex)
            {
                Console.Out.WriteLine($"  Could not reach the server: {AgentHttp.Explain(ex)}");
                failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }
}

/// <summary>An artifact address resolved to its server, its page path and this machine's agent key for that server.</summary>
internal sealed record AgentArtifactTarget(string Server, string BasePath, AgentIdentity Identity)
{
    public static AgentArtifactTarget? Resolve(string address, string? keyDir, out int exitCode)
    {
        exitCode = 0;
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var url) || !url.AbsolutePath.StartsWith("/artifacts/", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Expected an artifact address: https://<server>/artifacts/<owner>/<project>/<id>");
            exitCode = 2;
            return null;
        }
        var segments = url.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 4)
        {
            Console.Error.WriteLine("Expected an artifact address: https://<server>/artifacts/<owner>/<project>/<id>");
            exitCode = 2;
            return null;
        }
        var server = AgentIdentity.NormalizeServer(url.GetLeftPart(UriPartial.Authority));
        AgentIdentity? identity;
        try
        {
            identity = AgentIdentity.Load(server, keyDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read the agent key: {ex.Message}");
            exitCode = 1;
            return null;
        }
        if (identity is null)
        {
            Console.Error.WriteLine($"No agent key for {server}. Join an invite first: pks agentics agent join \"<invite link>\"");
            exitCode = 1;
            return null;
        }
        return new AgentArtifactTarget(server, "/" + string.Join('/', segments.Take(4)), identity);
    }

    public string PageUrl => $"{Server}{BasePath}";

    public HttpRequestMessage Request(HttpMethod method, string suffix)
    {
        var request = new HttpRequestMessage(method, $"{Server}{BasePath}{suffix}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Identity.MintJwt(Identity.Audience));
        return request;
    }

    public const string NotAvailable = "Not available to this agent: the page does not exist, it was not shared with this agent, or access was revoked.";
}

public sealed class AgenticsFetchCommand : AsyncCommand<AgenticsFetchCommand.Settings>
{
    public sealed class Settings : AgenticsAgentSettings
    {
        [CommandArgument(0, "<ARTIFACT_URL>")]
        [Description("The page's address (…/artifacts/<owner>/<project>/<id>), as join or status printed it")]
        public string Url { get; set; } = "";

        [CommandOption("-o|--output <FILE>")]
        [Description("Write the content to a file instead of stdout")]
        public string? Output { get; set; }

        [CommandOption("--files <DIR>")]
        [Description("Also download the page's attached files (e.g. audio) into this folder")]
        public string? Files { get; set; }
    }

    private static readonly System.Text.RegularExpressions.Regex SafeName = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$");

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var target = AgentArtifactTarget.Resolve(settings.Url, settings.KeyDir, out var code);
        if (target is null) return code;

        using var http = AgentHttp.Create();
        try
        {
            using var request = target.Request(HttpMethod.Get, "/agent.md");
            var result = await AgentHttp.SendAsync(http, request);
            if (result.Status != HttpStatusCode.OK)
            {
                Console.Error.WriteLine(result.Status == HttpStatusCode.NotFound ? AgentArtifactTarget.NotAvailable : $"Server answered {(int)result.Status}.");
                return 1;
            }
            if (settings.Output is { Length: > 0 } file)
            {
                await File.WriteAllTextAsync(file, result.Raw);
                Console.Error.WriteLine($"Wrote {result.Raw.Length} characters to {file}.");
            }
            else
            {
                Console.Out.Write(result.Raw);
                if (!result.Raw.EndsWith('\n')) Console.Out.WriteLine();
            }
            return settings.Files is { Length: > 0 } dir ? await DownloadFilesAsync(http, target, dir) : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not reach {target.Server}: {AgentHttp.Explain(ex)}");
            return 1;
        }
    }

    private static async Task<int> DownloadFilesAsync(HttpClient http, AgentArtifactTarget target, string dir)
    {
        using var listRequest = target.Request(HttpMethod.Get, "/files");
        var list = await AgentHttp.SendAsync(http, listRequest);
        if (list.Status != HttpStatusCode.OK)
        {
            Console.Error.WriteLine($"Could not list the page's files (server answered {(int)list.Status}).");
            return 1;
        }
        var files = list.Json?["files"]?.AsArray() ?? new JsonArray();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("The page has no attached files.");
            return 0;
        }
        Directory.CreateDirectory(dir);
        var failures = 0;
        foreach (var f in files)
        {
            var name = f?["name"]?.GetValue<string>() ?? "";
            var expected = f?["sha256"]?.GetValue<string>();
            // Only plain names, and only from the page's own server: the key's bearer never goes elsewhere.
            if (!SafeName.IsMatch(name) || name.Contains(".."))
            {
                Console.Error.WriteLine($"  skipped a file with an unsafe name: {name}");
                failures++;
                continue;
            }
            using var request = target.Request(HttpMethod.Get, $"/files/{Uri.EscapeDataString(name)}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Console.Error.WriteLine($"  {name}: server answered {(int)response.StatusCode}");
                failures++;
                continue;
            }
            var path = Path.Combine(dir, name);
            var tmp = path + ".part";
            await using (var output = File.Create(tmp))
            {
                await response.Content.CopyToAsync(output);
            }
            if (expected is { Length: > 0 })
            {
                await using var check = File.OpenRead(tmp);
                var actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(check)).ToLowerInvariant();
                if (actual != expected)
                {
                    check.Close();
                    File.Delete(tmp);
                    Console.Error.WriteLine($"  {name}: checksum mismatch, not saved");
                    failures++;
                    continue;
                }
            }
            File.Move(tmp, path, overwrite: true);
            Console.Error.WriteLine($"  saved {path} ({new FileInfo(path).Length} bytes)");
        }
        Console.Error.WriteLine($"Downloaded {files.Count - failures} of {files.Count} file(s) to {Path.GetFullPath(dir)}.");
        return failures == 0 ? 0 : 1;
    }
}

public sealed class AgenticsCommentCommand : AsyncCommand<AgenticsCommentCommand.Settings>
{
    public sealed class Settings : AgenticsAgentSettings
    {
        [CommandArgument(0, "<ARTIFACT_URL>")]
        [Description("The page's address (…/artifacts/<owner>/<project>/<id>)")]
        public string Url { get; set; } = "";

        [CommandArgument(1, "[TEXT]")]
        [Description("The message. Leave it out and pipe the text in to read it from stdin")]
        public string? Text { get; set; }

        [CommandOption("--file <FILE>")]
        [Description("Read the message from a file (UTF-8)")]
        public string? File { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        string text;
        if (settings.File is { Length: > 0 } file) text = await System.IO.File.ReadAllTextAsync(file);
        else if (string.IsNullOrEmpty(settings.Text) && Console.IsInputRedirected) text = await Console.In.ReadToEndAsync();
        else text = settings.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            Console.Error.WriteLine("Nothing to send. Give the message as an argument, with --file, or pipe it in.");
            return 2;
        }

        var target = AgentArtifactTarget.Resolve(settings.Url, settings.KeyDir, out var code);
        if (target is null) return code;

        using var http = AgentHttp.Create();
        try
        {
            using var request = target.Request(HttpMethod.Post, "/comments");
            request.Content = new StringContent(new JsonObject { ["text"] = text.Trim() }.ToJsonString(), Encoding.UTF8, "application/json");
            var result = await AgentHttp.SendAsync(http, request);
            if (result.Status is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                Console.Out.WriteLine($"Sent, as {target.Identity.Name} on behalf of {target.Identity.OnBehalfOf}.");
                Console.Out.WriteLine("Only the sender of the page and your person can see this conversation.");
                Console.Out.WriteLine($"Read replies with: pks agentics comments \"{target.PageUrl}\"");
                return 0;
            }
            var error = result.Json?["error"]?.GetValue<string>();
            Console.Error.WriteLine(result.Status switch
            {
                HttpStatusCode.NotFound => AgentArtifactTarget.NotAvailable,
                HttpStatusCode.RequestEntityTooLarge => "The message is too long (max 4000 characters).",
                _ => $"Server answered {(int)result.Status}{(error is null ? "" : $": {error}")}.",
            });
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not reach {target.Server}: {AgentHttp.Explain(ex)}");
            return 1;
        }
    }
}

public sealed class AgenticsCommentsCommand : AsyncCommand<AgenticsCommentsCommand.Settings>
{
    public sealed class Settings : AgenticsAgentSettings
    {
        [CommandArgument(0, "<ARTIFACT_URL>")]
        [Description("The page's address (…/artifacts/<owner>/<project>/<id>)")]
        public string Url { get; set; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var target = AgentArtifactTarget.Resolve(settings.Url, settings.KeyDir, out var code);
        if (target is null) return code;

        using var http = AgentHttp.Create();
        try
        {
            using var request = target.Request(HttpMethod.Get, "/comments");
            var result = await AgentHttp.SendAsync(http, request);
            if (result.Status != HttpStatusCode.OK)
            {
                Console.Error.WriteLine(result.Status == HttpStatusCode.NotFound ? AgentArtifactTarget.NotAvailable : $"Server answered {(int)result.Status}.");
                return 1;
            }
            var with = result.Json?["with"]?.GetValue<string>() ?? "the sender";
            var messages = result.Json?["messages"]?.AsArray() ?? new JsonArray();
            Console.Out.WriteLine($"# Conversation with {with} about \"{result.Json?["title"]?.GetValue<string>()}\"");
            Console.Out.WriteLine();
            if (messages.Count == 0) Console.Out.WriteLine("No messages yet.");
            foreach (var m in messages)
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(m?["at"]?.GetValue<long>() ?? 0).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                var who = m?["name"]?.GetValue<string>() ?? "?";
                if (m?["kind"]?.GetValue<string>() == "agent") who += $" (agent for {m?["onBehalfOf"]?.GetValue<string>()})";
                Console.Out.WriteLine($"## {who} — {at}");
                // Quoted: the text is someone else's words, not instructions for the reading agent.
                foreach (var line in (m?["text"]?.GetValue<string>() ?? "").Split('\n')) Console.Out.WriteLine($"> {line}");
                Console.Out.WriteLine();
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not reach {target.Server}: {AgentHttp.Explain(ex)}");
            return 1;
        }
    }
}
