using System.ComponentModel;
using PKS.Commands.Ssh;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Agentics.Runner;

/// <summary>
/// Shared settings + target/registration resolution for the SSH-handoff status/logs/stop commands
/// (docs/remote-runner-targets-plan.md Phase 4, work item 6). All three read the remote tmux session
/// via <c>tmux capture-pane -p</c> / <c>kill-session</c> -- the same idiom as
/// <c>Commands/Claude/UsageTmuxDriver.cs</c> -- never systemd/systemctl.
/// </summary>
public class AgenticsRunnerSshTargetSettings : AgenticsRunnerSettings
{
    [CommandArgument(0, "[TARGET]")]
    [Description("SSH target label or host that a project was handed off to (interactive picker if omitted)")]
    public string? Target { get; set; }

    [CommandOption("--project <owner-project>")]
    [Description("Disambiguate when more than one project has been handed off to this target, in owner/project format")]
    public string? Project { get; set; }
}

internal static class SshHandoffCommandHelpers
{
    /// <summary>
    /// Resolves the SSH target (via <see cref="SshTargetSelection"/>) and the single local
    /// registration handed off to it (matched by <see cref="RunnerProfile.SshTargetLabel"/>).
    /// Returns null and has already printed an actionable message when resolution isn't possible:
    /// no targets, target not found, no project handed off to it, or an ambiguous match that needs
    /// <c>--project</c>.
    /// </summary>
    public static async Task<(SshTarget Target, AgenticsRunnerRegistration Registration)?> ResolveAsync(
        IAnsiConsole console,
        ISshTargetConfigurationService sshTargets,
        IAgenticsRunnerConfigurationService runnerConfig,
        AgenticsRunnerSshTargetSettings settings)
    {
        var targets = await sshTargets.ListTargetsAsync();
        var target = await SshTargetSelection.PickAsync(console, targets, settings.Target, "[cyan]Select SSH target:[/]");
        if (target == null) return null;

        var targetLabel = target.Label ?? target.Host;
        var registrations = await runnerConfig.ListRegistrationsAsync();
        var matches = registrations
            .Where(r => r.Profile?.SshTargetLabel != null &&
                        string.Equals(r.Profile.SshTargetLabel, targetLabel, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            console.MarkupLine($"[yellow]No project has been handed off to '{Markup.Escape(targetLabel)}'.[/]");
            return null;
        }

        if (matches.Count == 1)
            return (target, matches[0]);

        if (!string.IsNullOrWhiteSpace(settings.Project))
        {
            var parts = settings.Project.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var byProject = matches.FirstOrDefault(r =>
                    string.Equals(r.Owner, parts[0], StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Project, parts[1], StringComparison.OrdinalIgnoreCase));
                if (byProject != null) return (target, byProject);
            }
            console.MarkupLine($"[red]No project '{Markup.Escape(settings.Project)}' handed off to '{Markup.Escape(targetLabel)}'.[/]");
            return null;
        }

        console.MarkupLine($"[yellow]Multiple projects have been handed off to '{Markup.Escape(targetLabel)}' — pass --project to pick one:[/]");
        foreach (var m in matches)
            console.MarkupLine($"  [cyan]{Markup.Escape(m.Owner)}/{Markup.Escape(m.Project)}[/]");
        return null;
    }
}

[Description("Show the remote tmux session status for a project handed off to an SSH target")]
public class AgenticsRunnerSshStatusCommand : AsyncCommand<AgenticsRunnerSshTargetSettings>
{
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly IAgenticsRunnerConfigurationService _runnerConfig;
    private readonly IAgenticsRunnerSshHandoffService _handoff;
    private readonly IAnsiConsole _console;

    public AgenticsRunnerSshStatusCommand(
        ISshTargetConfigurationService sshTargets,
        IAgenticsRunnerConfigurationService runnerConfig,
        IAgenticsRunnerSshHandoffService handoff,
        IAnsiConsole console)
    {
        _sshTargets = sshTargets;
        _runnerConfig = runnerConfig;
        _handoff = handoff;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, AgenticsRunnerSshTargetSettings settings)
    {
        var resolved = await SshHandoffCommandHelpers.ResolveAsync(_console, _sshTargets, _runnerConfig, settings);
        if (resolved == null) return 1;
        var (target, registration) = resolved.Value;

        var session = _handoff.BuildTmuxSessionName(registration.Owner, registration.Project);
        var pane = await _handoff.CapturePaneAsync(target, session);

        if (pane == null)
        {
            _console.MarkupLine($"[red]Could not reach {Markup.Escape(target.Username)}@{Markup.Escape(target.Host)} to check session '{session}'.[/]");
            return 1;
        }

        var trimmed = pane.TrimEnd();
        var alive = trimmed.Length > 0;
        _console.MarkupLine($"[cyan1]Target:[/]  {Markup.Escape(target.Label ?? target.Host)}");
        _console.MarkupLine($"[cyan1]Project:[/] {Markup.Escape(registration.Owner)}/{Markup.Escape(registration.Project)}");
        _console.MarkupLine($"[cyan1]Session:[/] {session}");
        _console.MarkupLine(alive ? "[green]Session is present.[/]" : "[yellow]Session reported no output (may have exited).[/]");

        if (trimmed.Length > 0)
        {
            var lastLines = string.Join('\n', trimmed.Split('\n').TakeLast(10));
            _console.WriteLine();
            _console.MarkupLine("[dim]Last output:[/]");
            _console.WriteLine(lastLines);
        }

        await WarnIfClaudeCredentialVolumeMissingAsync(target, registration.Owner, registration.Project);

        return 0;
    }

    /// <summary>Same pre-flight warning shown right after a successful handoff
    /// (<c>AgenticsRunnerStartCommand.WarnIfClaudeCredentialVolumeMissingAsync</c>), surfaced again
    /// any time an operator checks status later -- a job dispatched after the credential volume was
    /// removed or never populated would otherwise stall silently on an interactive OAuth login it
    /// can never complete headless. A <c>null</c> (host unreachable / probe failed) degrades to
    /// silence rather than a false "missing" claim -- the pane-capture failure above already covers
    /// the unreachable case.</summary>
    private async Task WarnIfClaudeCredentialVolumeMissingAsync(SshTarget target, string owner, string project)
    {
        bool? present;
        try { present = await _handoff.DetectClaudeCredentialVolumeAsync(target, owner, project); }
        catch { return; }

        if (present != false) return;

        _console.WriteLine();
        _console.MarkupLine(
            $"[yellow]No Claude credentials volume found on {Markup.Escape(target.Host)} for {Markup.Escape(owner)}/{Markup.Escape(project)}.[/]");
        _console.MarkupLine(
            "[dim]A headless devcontainer spawn there will stall waiting for an interactive OAuth login. " +
            $"Populate it first: [bold]pks agentics runner claude-login {Markup.Escape(target.Label ?? target.Host)}[/][/]");
    }
}

[Description("Show the full remote tmux pane output for a project handed off to an SSH target")]
public class AgenticsRunnerSshLogsCommand : AsyncCommand<AgenticsRunnerSshTargetSettings>
{
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly IAgenticsRunnerConfigurationService _runnerConfig;
    private readonly IAgenticsRunnerSshHandoffService _handoff;
    private readonly IAnsiConsole _console;

    public AgenticsRunnerSshLogsCommand(
        ISshTargetConfigurationService sshTargets,
        IAgenticsRunnerConfigurationService runnerConfig,
        IAgenticsRunnerSshHandoffService handoff,
        IAnsiConsole console)
    {
        _sshTargets = sshTargets;
        _runnerConfig = runnerConfig;
        _handoff = handoff;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, AgenticsRunnerSshTargetSettings settings)
    {
        var resolved = await SshHandoffCommandHelpers.ResolveAsync(_console, _sshTargets, _runnerConfig, settings);
        if (resolved == null) return 1;
        var (target, registration) = resolved.Value;

        var session = _handoff.BuildTmuxSessionName(registration.Owner, registration.Project);
        var pane = await _handoff.CapturePaneAsync(target, session);

        if (pane == null)
        {
            _console.MarkupLine($"[red]Could not reach {Markup.Escape(target.Username)}@{Markup.Escape(target.Host)} to read session '{session}'.[/]");
            return 1;
        }

        _console.WriteLine(pane);
        return 0;
    }
}

[Description("Stop the remote tmux session for a project handed off to an SSH target")]
public class AgenticsRunnerSshStopCommand : AsyncCommand<AgenticsRunnerSshTargetSettings>
{
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly IAgenticsRunnerConfigurationService _runnerConfig;
    private readonly IAgenticsRunnerSshHandoffService _handoff;
    private readonly IAnsiConsole _console;

    public AgenticsRunnerSshStopCommand(
        ISshTargetConfigurationService sshTargets,
        IAgenticsRunnerConfigurationService runnerConfig,
        IAgenticsRunnerSshHandoffService handoff,
        IAnsiConsole console)
    {
        _sshTargets = sshTargets;
        _runnerConfig = runnerConfig;
        _handoff = handoff;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, AgenticsRunnerSshTargetSettings settings)
    {
        var resolved = await SshHandoffCommandHelpers.ResolveAsync(_console, _sshTargets, _runnerConfig, settings);
        if (resolved == null) return 1;
        var (target, registration) = resolved.Value;

        var session = _handoff.BuildTmuxSessionName(registration.Owner, registration.Project);
        var stopped = await _handoff.StopAsync(target, session);

        if (!stopped)
        {
            _console.MarkupLine($"[red]Could not stop session '{session}' on {Markup.Escape(target.Username)}@{Markup.Escape(target.Host)} (unreachable or already stopped).[/]");
            return 1;
        }

        _console.MarkupLine($"[green]Stopped session '{session}' on {Markup.Escape(target.Label ?? target.Host)}.[/]");
        return 0;
    }
}

[Description("Interactively log in to Claude Code on an SSH target, populating its pks-claude-* credentials volume")]
public class AgenticsRunnerClaudeLoginCommand : AsyncCommand<AgenticsRunnerSshTargetSettings>
{
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly IAgenticsRunnerConfigurationService _runnerConfig;
    private readonly ISshKeyStore _keyStore;
    private readonly IInteractiveProcessLauncher _launcher;
    private readonly IAnsiConsole _console;

    public AgenticsRunnerClaudeLoginCommand(
        ISshTargetConfigurationService sshTargets,
        IAgenticsRunnerConfigurationService runnerConfig,
        ISshKeyStore keyStore,
        IInteractiveProcessLauncher launcher,
        IAnsiConsole console)
    {
        _sshTargets = sshTargets;
        _runnerConfig = runnerConfig;
        _keyStore = keyStore;
        _launcher = launcher;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, AgenticsRunnerSshTargetSettings settings)
    {
        var resolved = await SshHandoffCommandHelpers.ResolveAsync(_console, _sshTargets, _runnerConfig, settings);
        if (resolved == null) return 1;
        var (target, registration) = resolved.Value;

        // "project" scope: the default a job uses when its AgentDef.ClaudeCredentialsScope is
        // unset, same choice DetectClaudeCredentialVolumeAsync makes -- see its doc comment.
        var volumeName = ClaudeCredentialVolumes.ResolveVolumeName(registration.Owner, registration.Project, taskId: null, scope: "project");

        MaterializedKey? materialized = null;
        try
        {
            var keyPath = target.KeyPath;
            if (!string.IsNullOrEmpty(target.ManagedKeyId))
            {
                try
                {
                    materialized = await _keyStore.MaterializeAsync(target.ManagedKeyId);
                    keyPath = materialized.Path;
                }
                catch (Exception ex)
                {
                    _console.MarkupLine($"[red]Could not access pks-held key:[/] {Markup.Escape(ex.Message)}");
                    return 1;
                }
            }

            var (fileName, args) = ClaudeLoginCommandBuilder.Build(target, volumeName, keyPath);

            _console.MarkupLine($"[dim]Opening an interactive Claude Code login on {Markup.Escape(target.Username)}@{Markup.Escape(target.Host)}...[/]");
            _console.MarkupLine($"[dim]This populates the '{Markup.Escape(volumeName)}' credentials volume via a one-off container -- log in, then exit (Ctrl+D) to finish.[/]");

            var exitCode = await _launcher.RunAsync(fileName, args);

            if (exitCode == 0)
                _console.MarkupLine("[green]Claude login session ended. The credentials volume is populated for future spawns on this target.[/]");
            else
                _console.MarkupLine($"[yellow]Session exited with code {exitCode}.[/]");

            return exitCode;
        }
        finally
        {
            materialized?.Dispose();
        }
    }
}
