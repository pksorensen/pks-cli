using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Agentics.Runner;

/// <summary>
/// Selector shared by <c>status</c>, <c>logs</c> and <c>stop</c>. One positional argument covers
/// both worlds because they cannot collide: an owner/project always contains a slash, an SSH target
/// label never does. So <c>stop pksorensen/museliving</c> reaches the runner on this machine and
/// <c>stop hetzner</c> reaches the one handed off to that target, without two command families.
/// </summary>
public class AgenticsRunnerSelectorSettings : AgenticsRunnerSettings
{
    [CommandArgument(0, "[SELECTOR]")]
    [Description("owner/project for a runner on this machine, or an SSH target label for one handed off (interactive picker if omitted)")]
    public string? Selector { get; set; }

    [CommandOption("--project <owner-project>")]
    [Description("Disambiguate when more than one project has been handed off to the same SSH target")]
    public string? Project { get; set; }

    [CommandOption("--lines <N>")]
    [Description("How many lines of output to show (default: 20 for status, 200 for logs)")]
    public int? Lines { get; set; }

    /// <summary>A selector naming a local runner, or null when this should go down the SSH path.</summary>
    public string? LocalOwnerProject =>
        Selector != null && Selector.Contains('/') ? Selector
        : Selector == null && Project != null && Project.Contains('/') ? Project
        : null;
}

/// <summary>
/// Start a runner that outlives the shell that started it.
///
/// The split is <c>run</c> = foreground, <c>start</c> = background, matching the Aspire CLI
/// convention this workspace already lives with (<c>aspire run</c> vs <c>aspire start</c>). It is
/// not a <c>--persist</c> flag because whether a command returns is a mode, not an option:
/// <c>list</c>, <c>logs</c> and <c>stop</c> all need that mode to be a first-class thing they can
/// enumerate, and a flag leaves them guessing.
///
/// Anything interactive happens *here*, in the foreground, before detaching -- registration,
/// GitHub device-code login -- because a prompt inside a detached tmux pane is a prompt nobody
/// will ever answer. Same ordering as the SSH handoff, which ships the registration before it
/// launches anything remote.
/// </summary>
[Description("Start a runner in the background (survives logout); use 'run' to stay in the foreground")]
public class AgenticsRunnerStartCommand : AsyncCommand<AgenticsRunnerStartCommand.Settings>
{
    private readonly IAgenticsRunnerConfigurationService _configService;
    private readonly ILocalRunnerStore _store;
    private readonly ILocalRunnerSupervisor _supervisor;
    private readonly IGitHubAuthenticationService _githubAuth;
    private readonly IAnsiConsole _console;

    public AgenticsRunnerStartCommand(
        IAgenticsRunnerConfigurationService configService,
        ILocalRunnerStore store,
        ILocalRunnerSupervisor supervisor,
        IGitHubAuthenticationService githubAuth,
        IAnsiConsole console)
    {
        _configService = configService;
        _store = store;
        _supervisor = supervisor;
        _githubAuth = githubAuth;
        _console = console;
    }

    public class Settings : AgenticsRunnerSettings
    {
        [CommandOption("--project <owner-project>")]
        [Description("Project to run for in owner/project format. Auto-registers if not already registered. Without this, uses the only saved registration.")]
        public string? Project { get; set; }

        [CommandOption("--server <SERVER>")]
        [Description("Agentics server URL (falls back to AGENTIC_SERVER env, then agentics.dk)")]
        public string? Server { get; set; }

        [CommandOption("--work-dir <PATH>")]
        [Description("Base work directory (default: ~/.pks-cli/agentics-work/<owner>-<project>)")]
        public string? WorkDir { get; set; }

        [CommandOption("--polling-interval <SECONDS>")]
        [Description("Polling interval in seconds (default: 10)")]
        [DefaultValue(10)]
        public int PollingInterval { get; set; } = 10;

        [CommandOption("--restart")]
        [Description("Stop an already-running runner for this project and start a fresh one")]
        public bool Restart { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        AgenticsRunnerRegistration registration;
        try
        {
            registration = await ResolveRegistrationAsync(settings);
        }
        catch (InvalidOperationException ex)
        {
            _console.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        var existing = await _store.FindAsync(registration.Owner, registration.Project);
        if (existing != null && await _supervisor.IsAliveAsync(existing))
        {
            if (!settings.Restart)
            {
                _console.MarkupLine(
                    $"[yellow]A runner for {registration.Owner}/{registration.Project} is already running[/] " +
                    $"[dim]({Describe(existing)})[/]");
                _console.MarkupLine("[dim]Use --restart to replace it, or 'pks agentics runner stop' to stop it.[/]");
                return 0;
            }

            _console.MarkupLine($"[dim]Stopping the existing runner ({Describe(existing)})...[/]");
            await _supervisor.StopAsync(existing);
            await _store.RemoveAsync(registration.Owner, registration.Project);
        }
        else if (existing != null)
        {
            // Recorded but dead -- a reboot, an OOM kill, or somebody closed the tmux session.
            await _store.RemoveAsync(registration.Owner, registration.Project);
        }

        // GitHub device-code login, in the foreground. The detached process would hit the same
        // preflight and print a verification URL into a pane nobody is watching.
        if (!await _githubAuth.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[yellow]Not logged in to GitHub.[/] " +
                "[dim]Jobs that clone a github.com repo will fail until you are.[/]");
            _console.MarkupLine("[dim]Run 'pks agentics runner run' once to complete the device-code login, then start again.[/]");
        }

        if (registration.Profile == null)
        {
            _console.MarkupLine("[dim]No capability profile saved — the runner will auto-detect. " +
                "Run 'pks agentics runner run --configure' once to choose explicitly.[/]");
        }

        var workDir = string.IsNullOrWhiteSpace(settings.WorkDir)
            ? LocalRunnerSupervisor.DefaultWorkDir(registration.Owner, registration.Project)
            : Path.GetFullPath(settings.WorkDir);

        var launcher = RunnerLauncher.ResolveSelf();
        LocalRunnerRecord record;
        try
        {
            record = await _supervisor.StartDetachedAsync(new LocalRunnerStartRequest(
                registration.Owner,
                registration.Project,
                registration.Server,
                workDir,
                settings.PollingInterval,
                launcher));
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Could not start the runner: {ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        await _store.UpsertAsync(record);

        _console.MarkupLine($"[green]Runner started for [cyan]{registration.Owner}/{registration.Project}[/][/] " +
                            $"[dim]({Describe(record)})[/]");
        _console.MarkupLine($"[dim]server:   {registration.Server.EscapeMarkup()}[/]");
        _console.MarkupLine($"[dim]work dir: {workDir.EscapeMarkup()}[/]");
        _console.WriteLine();
        _console.MarkupLine($"[dim]  pks agentics runner logs {registration.Owner}/{registration.Project}[/]");
        _console.MarkupLine($"[dim]  pks agentics runner stop {registration.Owner}/{registration.Project}[/]");
        return 0;
    }

    private async Task<AgenticsRunnerRegistration> ResolveRegistrationAsync(Settings settings)
    {
        if (!string.IsNullOrEmpty(settings.Project))
        {
            return await RunnerRegistrar.ResolveOrRegisterAsync(
                _configService, settings.Project, settings.Server,
                msg => _console.MarkupLine($"[dim]{msg.EscapeMarkup()}[/]"));
        }

        var registrations = await _configService.ListRegistrationsAsync();
        if (registrations.Count == 0)
            throw new InvalidOperationException("No runner registrations found. Pass --project owner/project to register one.");
        if (registrations.Count > 1)
        {
            throw new InvalidOperationException(
                "More than one registration is saved — pass --project owner/project to say which one to start. " +
                "('pks agentics runner list' shows them.)");
        }

        return registrations[0];
    }

    internal static string Describe(LocalRunnerRecord record) => record.Mode == LocalRunnerMode.Tmux
        ? $"tmux session {record.TmuxSession}"
        : $"pid {record.Pid}";
}

/// <summary>
/// Everything this machine knows about: every registration, whether it is running here, and where.
/// A registration handed off over SSH shows its target rather than pretending to be local.
/// </summary>
[Description("List this machine's runners and whether they are running")]
public class AgenticsRunnerListCommand : AsyncCommand<AgenticsRunnerListCommand.Settings>
{
    private readonly IAgenticsRunnerConfigurationService _configService;
    private readonly ILocalRunnerStore _store;
    private readonly ILocalRunnerSupervisor _supervisor;
    private readonly IAnsiConsole _console;

    public AgenticsRunnerListCommand(
        IAgenticsRunnerConfigurationService configService,
        ILocalRunnerStore store,
        ILocalRunnerSupervisor supervisor,
        IAnsiConsole console)
    {
        _configService = configService;
        _store = store;
        _supervisor = supervisor;
        _console = console;
    }

    public class Settings : AgenticsRunnerSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var registrations = await _configService.ListRegistrationsAsync();
        if (registrations.Count == 0)
        {
            _console.MarkupLine("[yellow]No runners registered on this machine.[/]");
            _console.MarkupLine("[dim]  pks agentics runner start --project owner/project[/]");
            return 0;
        }

        var locals = await _store.ListAsync();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Project");
        table.AddColumn("Where");
        table.AddColumn("State");
        table.AddColumn("Server");

        foreach (var registration in registrations.OrderBy(r => r.Owner).ThenBy(r => r.Project))
        {
            var local = locals.FirstOrDefault(l => l.Matches(registration.Owner, registration.Project));
            var handedOff = registration.Profile?.SshTargetLabel;

            string where;
            string state;

            if (local != null)
            {
                where = $"local ({AgenticsRunnerStartCommand.Describe(local)})";
                state = await _supervisor.IsAliveAsync(local)
                    ? "[green]running[/]"
                    : "[red]stopped[/]";
            }
            else if (!string.IsNullOrEmpty(handedOff))
            {
                where = $"ssh:{handedOff}";
                // Deliberately not probed: reaching every SSH target to render one table would make
                // `list` as slow as the slowest unreachable host. `runner status <target>` asks.
                state = "[dim]unknown[/]";
            }
            else
            {
                where = "[dim]—[/]";
                state = "[dim]not started[/]";
            }

            table.AddRow(
                $"[cyan]{registration.Owner.EscapeMarkup()}/{registration.Project.EscapeMarkup()}[/]",
                where,
                state,
                $"[dim]{registration.Server.EscapeMarkup()}[/]");
        }

        _console.Write(table);
        return 0;
    }
}

/// <summary>Shared plumbing for the three selector commands: local first, SSH target second.</summary>
internal static class LocalRunnerCommandHelpers
{
    public static async Task<LocalRunnerRecord?> ResolveLocalAsync(
        ILocalRunnerStore store, AgenticsRunnerSelectorSettings settings)
    {
        var ownerProject = settings.LocalOwnerProject;

        if (ownerProject != null)
        {
            var (owner, project) = RunnerRegistrar.ParseOwnerProject(ownerProject);
            return await store.FindAsync(owner, project);
        }

        // No selector at all: if exactly one local runner is tracked, it is unambiguous.
        if (settings.Selector == null && settings.Project == null)
        {
            var all = await store.ListAsync();
            if (all.Count == 1) return all[0];
        }

        return null;
    }
}

[Description("Show a runner's state — local, or the remote tmux session for one handed off to an SSH target")]
public class AgenticsRunnerStatusCommand : AsyncCommand<AgenticsRunnerSelectorSettings>
{
    private readonly ILocalRunnerStore _store;
    private readonly ILocalRunnerSupervisor _supervisor;
    private readonly AgenticsRunnerSshStatusCommand _ssh;
    private readonly IAnsiConsole _console;

    public AgenticsRunnerStatusCommand(
        ILocalRunnerStore store,
        ILocalRunnerSupervisor supervisor,
        AgenticsRunnerSshStatusCommand ssh,
        IAnsiConsole console)
    {
        _store = store;
        _supervisor = supervisor;
        _ssh = ssh;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, AgenticsRunnerSelectorSettings settings)
    {
        var local = await LocalRunnerCommandHelpers.ResolveLocalAsync(_store, settings);
        if (local == null) return await _ssh.ExecuteAsync(context, ToSshSettings(settings));

        var alive = await _supervisor.IsAliveAsync(local);
        _console.MarkupLine($"[cyan]{local.OwnerProject.EscapeMarkup()}[/] — " +
                            (alive ? "[green]running[/]" : "[red]not running[/]") +
                            $" [dim]({AgenticsRunnerStartCommand.Describe(local)}, started {local.StartedAt:u})[/]");
        _console.MarkupLine($"[dim]work dir: {local.WorkDir.EscapeMarkup()}[/]");
        if (!string.IsNullOrEmpty(local.LogPath))
            _console.MarkupLine($"[dim]log:      {local.LogPath.EscapeMarkup()}[/]");

        // status shows a short tail by default -- `logs` is the command for reading properly.
        var output = await _supervisor.CaptureOutputAsync(local, settings.Lines ?? 20);
        if (!string.IsNullOrWhiteSpace(output))
        {
            _console.WriteLine();
            _console.Write(new Panel(output.TrimEnd().EscapeMarkup())
                .Header("[dim]last output[/]").Border(BoxBorder.Rounded).BorderColor(Color.Grey));
        }

        return alive ? 0 : 1;
    }

    internal static AgenticsRunnerSshTargetSettings ToSshSettings(AgenticsRunnerSelectorSettings settings) => new()
    {
        Target = settings.Selector,
        Project = settings.Project,
        Verbose = settings.Verbose,
    };
}

[Description("Show a runner's output — local, or the remote tmux pane for one handed off to an SSH target")]
public class AgenticsRunnerLogsCommand : AsyncCommand<AgenticsRunnerSelectorSettings>
{
    private readonly ILocalRunnerStore _store;
    private readonly ILocalRunnerSupervisor _supervisor;
    private readonly AgenticsRunnerSshLogsCommand _ssh;
    private readonly IAnsiConsole _console;

    public AgenticsRunnerLogsCommand(
        ILocalRunnerStore store,
        ILocalRunnerSupervisor supervisor,
        AgenticsRunnerSshLogsCommand ssh,
        IAnsiConsole console)
    {
        _store = store;
        _supervisor = supervisor;
        _ssh = ssh;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, AgenticsRunnerSelectorSettings settings)
    {
        var local = await LocalRunnerCommandHelpers.ResolveLocalAsync(_store, settings);
        if (local == null) return await _ssh.ExecuteAsync(context, AgenticsRunnerStatusCommand.ToSshSettings(settings));

        var output = await _supervisor.CaptureOutputAsync(local, settings.Lines ?? 200);
        if (output == null)
        {
            _console.MarkupLine($"[yellow]No output available for {local.OwnerProject.EscapeMarkup()}[/] " +
                                $"[dim]({AgenticsRunnerStartCommand.Describe(local)} — is it still running?)[/]");
            return 1;
        }

        _console.WriteLine(output.TrimEnd());
        return 0;
    }
}

[Description("Stop a runner — local, or the remote tmux session for one handed off to an SSH target")]
public class AgenticsRunnerStopLocalCommand : AsyncCommand<AgenticsRunnerSelectorSettings>
{
    private readonly ILocalRunnerStore _store;
    private readonly ILocalRunnerSupervisor _supervisor;
    private readonly AgenticsRunnerSshStopCommand _ssh;
    private readonly IAnsiConsole _console;

    public AgenticsRunnerStopLocalCommand(
        ILocalRunnerStore store,
        ILocalRunnerSupervisor supervisor,
        AgenticsRunnerSshStopCommand ssh,
        IAnsiConsole console)
    {
        _store = store;
        _supervisor = supervisor;
        _ssh = ssh;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, AgenticsRunnerSelectorSettings settings)
    {
        var local = await LocalRunnerCommandHelpers.ResolveLocalAsync(_store, settings);
        if (local == null) return await _ssh.ExecuteAsync(context, AgenticsRunnerStatusCommand.ToSshSettings(settings));

        if (!await _supervisor.IsAliveAsync(local))
        {
            _console.MarkupLine($"[yellow]{local.OwnerProject.EscapeMarkup()} is not running.[/]");
            await _store.RemoveAsync(local.Owner, local.Project);
            return 0;
        }

        var stopped = await _supervisor.StopAsync(local);
        if (!stopped)
        {
            _console.MarkupLine($"[red]Could not stop {local.OwnerProject.EscapeMarkup()} " +
                                $"({AgenticsRunnerStartCommand.Describe(local)}).[/]");
            return 1;
        }

        await _store.RemoveAsync(local.Owner, local.Project);
        _console.MarkupLine($"[green]Stopped {local.OwnerProject.EscapeMarkup()}[/] " +
                            $"[dim]({AgenticsRunnerStartCommand.Describe(local)})[/]");
        return 0;
    }
}
