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
        [Description("Project to run for in owner/project format; repeat for several. Auto-registers if not already registered. Without this, every saved registration is started — 'pks agentics runner register' is the opt-in list and 'unregister' the opt-out.")]
        public string[] Projects { get; set; } = [];

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
        if (settings.Projects.Length == 0 && InstallationContext.FromEnvironment() is { } installation)
            return await InstallationRunnerMode.StartServiceAsync(installation, _console);

        List<AgenticsRunnerRegistration> registrations;
        try
        {
            registrations = await ResolveRegistrationsAsync(settings);
        }
        catch (InvalidOperationException ex)
        {
            _console.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        // Once, not per project: the device-code login is a single operator credential for the
        // whole machine, and asking twice in one command would print a second verification URL
        // that invalidates the first. Same reason it happens here rather than in the detached
        // child -- see the class comment.
        if (!await _githubAuth.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[yellow]Not logged in to GitHub.[/] " +
                "[dim]Jobs that clone a github.com repo will fail until you are.[/]");
            _console.MarkupLine("[dim]Run 'pks agentics runner run' once to complete the device-code login, then start again.[/]");
        }

        // One project's failure must not strand the rest: a stale token on project three is not a
        // reason to leave projects four and five unserved. Every project is attempted, the exit
        // code reports whether any failed, and `runner list` is the authority on what ended up
        // running.
        var failed = new List<string>();
        foreach (var registration in registrations)
        {
            if (!await StartOneAsync(registration, settings))
                failed.Add($"{registration.Owner}/{registration.Project}");
        }

        if (registrations.Count > 1)
        {
            _console.WriteLine();
            _console.MarkupLine(failed.Count == 0
                ? $"[green]{registrations.Count} runner(s) running.[/] [dim]pks agentics runner list[/]"
                : $"[yellow]{registrations.Count - failed.Count} of {registrations.Count} started[/] " +
                  $"[dim]— failed: {string.Join(", ", failed).EscapeMarkup()}[/]");
        }

        return failed.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// One project's share of <c>start</c>: reconcile whatever is already recorded for it, then
    /// detach a runner. Returns false when this project could not be started, so the caller can
    /// carry on with the others and still report a non-zero exit.
    /// </summary>
    private async Task<bool> StartOneAsync(AgenticsRunnerRegistration registration, Settings settings)
    {
        var existing = await _store.FindAsync(registration.Owner, registration.Project);
        if (existing != null && await _supervisor.IsAliveAsync(existing))
        {
            if (!settings.Restart)
            {
                _console.MarkupLine(
                    $"[yellow]A runner for {registration.Owner}/{registration.Project} is already running[/] " +
                    $"[dim]({Describe(existing)})[/]");
                _console.MarkupLine("[dim]Use --restart to replace it, or 'pks agentics runner stop' to stop it.[/]");
                // Already running is success, not failure: `start` is how you make sure the
                // runners are up, so running it twice must be a no-op rather than an error.
                return true;
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
            _console.MarkupLine(
                $"[red]{registration.Owner}/{registration.Project}: could not start the runner: " +
                $"{ex.Message.EscapeMarkup()}[/]");
            return false;
        }

        await _store.UpsertAsync(record);

        _console.MarkupLine($"[green]Runner started for [cyan]{registration.Owner}/{registration.Project}[/][/] " +
                            $"[dim]({Describe(record)})[/]");
        _console.MarkupLine($"[dim]server:   {registration.Server.EscapeMarkup()}[/]");
        _console.MarkupLine($"[dim]work dir: {workDir.EscapeMarkup()}[/]");
        _console.WriteLine();
        _console.MarkupLine($"[dim]  pks agentics runner logs {registration.Owner}/{registration.Project}[/]");
        _console.MarkupLine($"[dim]  pks agentics runner stop {registration.Owner}/{registration.Project}[/]");
        return true;
    }

    /// <summary>
    /// Which projects this invocation serves. No <c>--project</c> means **every saved
    /// registration** — the GitHub-runner shape Poul asked for: <c>register</c> the projects once,
    /// then one <c>start</c> serves them all, and each gets its own detached runner so a long job
    /// in one project does not hold up another.
    ///
    /// This used to refuse more than one registration ("pass --project to say which one"), which
    /// made the saved list unusable as what it already was: an opt-in list. Six registrations on a
    /// box served one project.
    /// </summary>
    private async Task<List<AgenticsRunnerRegistration>> ResolveRegistrationsAsync(Settings settings)
    {
        if (settings.Projects.Length > 0)
        {
            var resolved = new List<AgenticsRunnerRegistration>();
            foreach (var project in settings.Projects.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Registration happens here, in the parent, before StartDetachedAsync — so a
                // first-run `runner start` can still sign the operator in even though the
                // runner it launches has no terminal of its own.
                resolved.Add(await RunnerRegistrar.ResolveOrRegisterAsync(
                    _configService, project, settings.Server,
                    msg => _console.MarkupLine($"[dim]{msg.EscapeMarkup()}[/]"),
                    canPrompt: _console.Profile.Capabilities.Interactive));
            }

            return resolved;
        }

        var registrations = await _configService.ListRegistrationsAsync();
        if (registrations.Count == 0)
            throw new InvalidOperationException("No runner registrations found. Pass --project owner/project to register one.");

        // `--work-dir` names one directory. Two runners sharing it would check out two projects
        // over each other, so it is refused rather than silently applied to the first.
        if (registrations.Count > 1 && !string.IsNullOrWhiteSpace(settings.WorkDir))
        {
            throw new InvalidOperationException(
                $"--work-dir names a single directory, but {registrations.Count} registrations are saved. " +
                "Pass --project owner/project to start one, or drop --work-dir to give each project its own.");
        }

        return registrations;
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

/// <summary>
/// Drop a project from this machine's saved registrations — the opt-out half of what
/// <c>register</c> opts in to. It matters because a project-less <c>runner start</c> serves
/// *every* saved registration: without a way to take one out, the only way to stop serving a
/// project would be to edit the config file by hand or name every other project on the command
/// line.
///
/// Local only. The runner record on the platform is left alone deliberately: this machine
/// forgetting its token is not the same as the project being told the runner is gone, and the
/// platform page is where that is managed.
/// </summary>
[Description("Stop serving a project from this machine (removes its saved registration)")]
public class AgenticsRunnerUnregisterCommand : AsyncCommand<AgenticsRunnerUnregisterCommand.Settings>
{
    private readonly IAgenticsRunnerConfigurationService _configService;
    private readonly ILocalRunnerStore _store;
    private readonly ILocalRunnerSupervisor _supervisor;
    private readonly IAnsiConsole _console;

    public AgenticsRunnerUnregisterCommand(
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
        [CommandArgument(0, "<OWNER_PROJECT>")]
        [Description("Owner/project to stop serving; name several to remove them all")]
        public string[] OwnerProjects { get; set; } = [];

        [CommandOption("--keep-running")]
        [Description("Leave an already-running runner for the project alone (it keeps its own copy of the token until it exits)")]
        public bool KeepRunning { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (settings.OwnerProjects.Length == 0)
        {
            _console.MarkupLine("[red]Name at least one project: pks agentics runner unregister owner/project[/]");
            return 1;
        }

        var configuration = await _configService.LoadAsync();
        var removed = new List<string>();
        var missing = new List<string>();

        foreach (var ownerProject in settings.OwnerProjects.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parts = ownerProject.Split('/', 2);
            if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            {
                _console.MarkupLine($"[red]'{ownerProject.EscapeMarkup()}' is not in owner/project format[/]");
                return 1;
            }

            var (owner, project) = (parts[0], parts[1]);
            var matches = configuration.Registrations
                .Where(r => string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                missing.Add($"{owner}/{project}");
                continue;
            }

            // A detached runner already holds its own copy of the token, so removing the
            // registration does not stop it polling. Stopping it is the point of unregistering.
            var record = await _store.FindAsync(owner, project);
            if (record != null)
            {
                if (settings.KeepRunning)
                {
                    _console.MarkupLine($"[yellow]{owner}/{project}: leaving the running runner alone[/] " +
                                        "[dim](it keeps polling until it exits)[/]");
                }
                else
                {
                    if (await _supervisor.IsAliveAsync(record))
                    {
                        _console.MarkupLine($"[dim]{owner}/{project}: stopping its runner ({AgenticsRunnerStartCommand.Describe(record)})...[/]");
                        await _supervisor.StopAsync(record);
                    }

                    await _store.RemoveAsync(owner, project);
                }
            }

            foreach (var match in matches)
                configuration.Registrations.Remove(match);

            removed.Add($"{owner}/{project}");
        }

        if (removed.Count > 0)
            await _configService.SaveAsync(configuration);

        foreach (var name in removed)
            _console.MarkupLine($"[green]No longer serving [cyan]{name.EscapeMarkup()}[/] from this machine[/]");
        foreach (var name in missing)
            _console.MarkupLine($"[yellow]{name.EscapeMarkup()} was not registered here[/]");

        if (removed.Count > 0)
        {
            _console.WriteLine();
            _console.MarkupLine($"[dim]{configuration.Registrations.Count} registration(s) left — " +
                                "'pks agentics runner start' serves those.[/]");
            _console.MarkupLine("[dim]The runner record on the platform is untouched; remove it there if you want it gone.[/]");
        }

        return missing.Count > 0 && removed.Count == 0 ? 1 : 0;
    }
}
