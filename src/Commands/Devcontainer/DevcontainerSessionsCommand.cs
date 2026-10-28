using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Devcontainer;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Lists the coding sessions (Claude Code + tmux) running inside every running devcontainer on a host.
/// Built for a desktop tool that polls it read-only: <c>--remote &lt;target&gt;</c> never prompts, and
/// <c>--format json</c> puts nothing but the document on stdout — an unreachable host is reported as
/// data with exit code 0, and a failing probe is reported per container without aborting the list.
/// </summary>
[Description("List the coding sessions running inside each devcontainer (local or remote)")]
public class DevcontainerSessionsCommand : DevcontainerCommand<DevcontainerSessionsCommand.Settings>
{
    private const string DockerPsCommand = "docker ps --filter " + DevcontainerLabels.Filter + " --format json";

    private readonly ISshTargetConfigurationService? _sshConfigService;
    private readonly ISshCommandRunner? _sshCommandRunner;
    private readonly ISshKeyStore? _keyStore;
    private readonly IActionGuard? _guard;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;

    public DevcontainerSessionsCommand(
        IAnsiConsole console,
        ISshTargetConfigurationService? sshConfigService = null,
        ISshCommandRunner? sshCommandRunner = null,
        ISshKeyStore? keyStore = null,
        IActionGuard? guard = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null)
        : base(console)
    {
        _sshConfigService = sshConfigService;
        _sshCommandRunner = sshCommandRunner;
        _keyStore = keyStore;
        _guard = guard;
        _stdout = stdout ?? System.Console.Out;
        _stderr = stderr ?? System.Console.Error;
    }

    /// <summary>
    /// Runs a shell command against the local docker daemon (<c>sh -c</c>). Replaceable so tests never
    /// touch a real shell; production uses <see cref="RunLocalShellAsync"/>.
    /// </summary>
    internal Func<string, CancellationToken, Task<SshCommandResult>> LocalShellRunner { get; set; } = RunLocalShellAsync;

    public class Settings : DevcontainerSettings
    {
        [CommandOption("--remote <TARGET>")]
        [Description("Registered SSH target (label or host) to inspect, or 'local'. Never prompts.")]
        public string? Remote { get; set; }

        [CommandOption("--format <FORMAT>")]
        [Description("Output format: table or json (default: table)")]
        [DefaultValue("table")]
        public string Format { get; set; } = "table";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var json = settings.Format.Equals("json", StringComparison.OrdinalIgnoreCase);
        if (!json && !settings.Format.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            Fail(json: false, $"Invalid format '{settings.Format}'. Valid formats are: table, json");
            return 1;
        }

        if (!json) DisplayBanner("Sessions");

        DevcontainerRemoteHost? host = null;
        try
        {
            string? error;
            (host, error) = await ResolveHostAsync(settings.Remote, json);
            if (host == null)
            {
                Fail(json, error ?? "Could not resolve host");
                return 1;
            }

            if (!host.IsLocal && _sshCommandRunner == null)
            {
                Fail(json, "SSH command runner not available");
                return 1;
            }

            var fetchedAt = DateTimeOffset.UtcNow;

            // 1. Running devcontainers (no -a: a stopped container has no sessions).
            var ps = await RunAsync(host, DockerPsCommand, json ? null : $"Listing devcontainers on {host.Label}...");
            if (!ps.Success)
            {
                var message = string.IsNullOrWhiteSpace(ps.StdErr) ? $"docker ps exited {ps.ExitCode}" : ps.StdErr.Trim();
                if (json)
                {
                    WriteJson(new SessionsReport { Host = host.Label, Reachable = false, Error = message, FetchedAt = fetchedAt });
                    return 0;
                }
                DisplayError($"Could not reach {host.Label}: {message}");
                return 1;
            }

            var containers = ParseDockerPs(ps.StdOut);

            // 2. One probe run for every container, framed BEGIN/END so a failure stays with its container.
            if (containers.Count > 0)
            {
                var script = SessionProbeScript.BuildHostScript(
                    containers.Select(c => new ProbeTarget(c.Id, c.RemoteUser)));
                var probe = await RunAsync(host, SessionProbeScript.BuildShellCommand(script),
                    json ? null : $"Probing {containers.Count} container(s) for sessions...");

                var parsed = probe.Success || !string.IsNullOrEmpty(probe.StdOut)
                    ? SessionProbeParser.ParseHostOutput(probe.StdOut)
                    : new Dictionary<string, ContainerProbeResult>();

                foreach (var c in containers)
                {
                    if (parsed.TryGetValue(c.Id, out var result))
                    {
                        c.Sessions = result.Sessions;
                        c.Tmux = result.Tmux;
                        c.StaleSessionFiles = result.StaleSessionFiles;
                        c.ProbeError = result.ProbeError;
                    }
                    else
                    {
                        var detail = string.IsNullOrWhiteSpace(probe.StdErr) ? $"probe exited {probe.ExitCode}" : probe.StdErr.Trim();
                        c.ProbeError = probe.Success ? "no probe output for this container" : detail;
                    }
                }
            }

            var report = new SessionsReport { Host = host.Label, Reachable = true, FetchedAt = fetchedAt, Containers = containers };
            if (json) WriteJson(report);
            else DisplayTable(report);
            return 0;
        }
        catch (Exception ex)
        {
            if (json)
            {
                _stderr.WriteLine($"Unexpected error: {ex.Message}");
            }
            else
            {
                DisplayError("Unexpected error");
                Console.WriteException(ex);
            }
            return 1;
        }
        finally
        {
            host?.Dispose();
        }
    }

    private async Task<(DevcontainerRemoteHost? Host, string? Error)> ResolveHostAsync(string? remoteSpec, bool json)
    {
        if (!string.IsNullOrWhiteSpace(remoteSpec))
            return await DevcontainerRemoteHost.ResolveAsync(remoteSpec, _sshConfigService, _keyStore, _guard);

        // No --remote: local unless targets are registered, in which case ask (table mode only —
        // a json consumer cannot answer, so it gets local).
        if (json || _sshConfigService == null)
            return (DevcontainerRemoteHost.Local, null);

        var targets = await _sshConfigService.ListTargetsAsync();
        if (targets.Count == 0)
            return (DevcontainerRemoteHost.Local, null);

        var choices = new List<string> { "Local (this machine)" };
        choices.AddRange(targets.Select(t => $"{t.Username}@{t.Host}" + (string.IsNullOrEmpty(t.Label) ? "" : $" ({t.Label})")));
        var selected = Console.Prompt(new SelectionPrompt<string>().Title("[cyan]Which host?[/]").AddChoices(choices));
        var index = choices.IndexOf(selected) - 1;
        if (index < 0) return (DevcontainerRemoteHost.Local, null);
        return await DevcontainerRemoteHost.FromTargetAsync(targets[index], _keyStore, _guard);
    }

    private async Task<SshCommandResult> RunAsync(DevcontainerRemoteHost host, string command, string? spinner)
    {
        Func<Task<SshCommandResult>> run = host.IsLocal
            ? () => LocalShellRunner(command, CancellationToken.None)
            : () => _sshCommandRunner!.RunAsync(host.Host!, command);

        return spinner == null ? await run() : await WithSpinnerAsync(spinner, run);
    }

    private static async Task<SshCommandResult> RunLocalShellAsync(string command, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new SshCommandResult { ExitCode = process.ExitCode, StdOut = await stdout, StdErr = await stderr };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SshCommandResult { ExitCode = 127, StdErr = $"could not run sh: {ex.Message}" };
        }
    }

    private void Fail(bool json, string message)
    {
        if (json) _stderr.WriteLine(message);
        else DisplayError(message);
    }

    /// <summary>Turns <c>docker ps --format json</c> lines into containers (unparseable lines are skipped).</summary>
    internal static List<ContainerSessions> ParseDockerPs(string stdout)
    {
        var containers = new List<ContainerSessions>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line.Trim());
                var root = doc.RootElement;
                string Get(string name) => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

                var labels = Get("Labels");
                var name = Get("Names");
                var image = Get("Image");
                var (project, localFolder) = DevcontainerLabels.Describe(labels, name, image);

                containers.Add(new ContainerSessions
                {
                    Id = Get("ID"),
                    Name = name,
                    Project = project,
                    LocalFolder = localFolder,
                    Status = Get("Status"),
                    Image = image,
                    CreatedAt = DockerTimestamps.ToIso(Get("CreatedAt")),
                    RemoteUser = SessionProbeScript.ExtractRemoteUser(labels),
                });
            }
            catch (JsonException)
            {
                // not a docker ps line
            }
        }
        return containers;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep '+' and '/' readable
    };

    private void WriteJson(SessionsReport report)
    {
        _stdout.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        _stdout.Flush();
    }

    private void DisplayTable(SessionsReport report)
    {
        Console.WriteLine();
        if (report.Containers.Count == 0)
        {
            DisplayInfo($"No running devcontainers on {report.Host.EscapeMarkup()}");
            Console.WriteLine();
            return;
        }

        foreach (var c in report.Containers)
        {
            var table = new Table()
                .Title($"[cyan]{c.Name.EscapeMarkup()}[/] [dim]{c.Project.EscapeMarkup()} · {c.Status.EscapeMarkup()}[/]")
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Cyan1);
            table.AddColumn(new TableColumn("[yellow]Source[/]"));
            table.AddColumn(new TableColumn("[yellow]Session[/]"));
            table.AddColumn(new TableColumn("[yellow]Status[/]"));
            table.AddColumn(new TableColumn("[yellow]Working directory[/]"));
            table.AddColumn(new TableColumn("[yellow]Started[/]").RightAligned());

            foreach (var s in c.Sessions)
            {
                var status = s.Status switch
                {
                    "busy" => "[green]busy[/]",
                    null => "[dim]-[/]",
                    var other => $"[yellow]{other.EscapeMarkup()}[/]",
                };
                table.AddRow(
                    "claude",
                    $"{(s.Name ?? s.SessionId ?? "?").EscapeMarkup()} [dim]({s.Kind ?? "?"}, pid {s.Pid})[/]",
                    status,
                    $"[dim]{(s.Cwd ?? "").EscapeMarkup()}[/]",
                    $"[dim]{s.StartedAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") ?? "-"}[/]");
            }
            foreach (var t in c.Tmux)
            {
                table.AddRow(
                    "tmux",
                    $"{t.Name.EscapeMarkup()} [dim]({t.Windows} window{(t.Windows == 1 ? "" : "s")})[/]",
                    t.Attached ? "[green]attached[/]" : "[dim]detached[/]",
                    "",
                    $"[dim]{t.CreatedAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") ?? "-"}[/]");
            }
            if (c.Sessions.Count == 0 && c.Tmux.Count == 0)
            {
                table.AddRow("[dim]-[/]", "[dim]no sessions[/]", "", "", "");
            }

            var footer = $"[dim]{c.StaleSessionFiles} stale session file(s)[/]";
            if (c.ProbeError != null) footer += $"  [red]probe: {c.ProbeError.EscapeMarkup()}[/]";
            table.Caption(footer);

            Console.Write(table);
            Console.WriteLine();
        }
    }

    /// <summary>The JSON document: <c>{host, reachable, error, fetchedAt, containers[]}</c>.</summary>
    public sealed class SessionsReport
    {
        public string Host { get; init; } = "local";
        public bool Reachable { get; init; }
        public string? Error { get; init; }
        [JsonConverter(typeof(UtcSecondsDateTimeOffsetConverter))]
        public DateTimeOffset? FetchedAt { get; init; }
        public List<ContainerSessions> Containers { get; init; } = new();
    }

    /// <summary>One running devcontainer and what the probe found inside it.</summary>
    public sealed class ContainerSessions
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Project { get; init; } = "";
        public string LocalFolder { get; init; } = "";
        public string Status { get; init; } = "";
        public string Image { get; init; } = "";
        public string CreatedAt { get; init; } = "";
        public string? RemoteUser { get; init; }
        public List<ClaudeSessionInfo> Sessions { get; set; } = new();
        public List<TmuxSessionInfo> Tmux { get; set; } = new();
        public int StaleSessionFiles { get; set; }
        public string? ProbeError { get; set; }
    }
}
