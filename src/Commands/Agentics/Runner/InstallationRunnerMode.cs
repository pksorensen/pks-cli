using System.Diagnostics;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;

namespace PKS.Commands.Agentics.Runner;

/// <summary>
/// <c>pks agentics runner run|start</c> on a self-hosted box, where the installer has described the
/// installation in the environment (<see cref="InstallationContext"/>) and no <c>--project</c> is
/// given: one runner for every project on the box, with no arguments to get wrong.
///
/// <c>run</c> is the host in the foreground (what the systemd unit executes); <c>start</c> installs
/// and starts that unit, so the runner comes back after a reboot.
/// </summary>
internal static class InstallationRunnerMode
{
    public const string UnitName = "agentics-runner.service";
    public const string EnvironmentFile = "/etc/agentics/runner.env";
    private const string UnitPath = "/etc/systemd/system/" + UnitName;

    public static async Task<int> RunAsync(InstallationContext installation, IAgenticsRunnerConfigurationService registrations, IAnsiConsole console)
    {
        if (!File.Exists(installation.KeyPath))
        {
            console.MarkupLine($"[red]The installation key {installation.KeyPath.EscapeMarkup()} is missing or unreadable by this user.[/]");
            return 1;
        }

        // Plain lines, not markup: under systemd this is the journal, and project output carries brackets.
        void Log(string line) => console.WriteLine(line);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var host = new InstallationRunnerHost(
            installation,
            new ManagedRunnerControlClient(http, new OpenSslInstallationTokenSigner()),
            registrations,
            new ChildProcessProjectRunnerLauncher(RunnerLauncher.ResolveSelf(), Log),
            Log);
        await host.RunAsync(cts.Token);
        return 0;
    }

    public static async Task<int> StartServiceAsync(InstallationContext installation, IAnsiConsole console)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/bin/systemctl") && !File.Exists("/usr/bin/systemctl"))
        {
            console.MarkupLine("[yellow]No systemd here.[/] Run [bold]pks agentics runner run[/] in the foreground (or under your own supervisor).");
            return 1;
        }
        if (Environment.UserName != "root")
        {
            console.MarkupLine("[yellow]Installing the runner service needs root:[/] [bold]sudo pks agentics runner start[/]");
            return 1;
        }

        await File.WriteAllTextAsync(UnitPath, BuildUnit(RunnerLauncher.ResolveSelf(), Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), File.Exists(EnvironmentFile)));

        foreach (var args in new[] { "daemon-reload", $"enable {UnitName}", $"restart {UnitName}" })
        {
            var (code, output) = await SystemctlAsync(args);
            if (code != 0)
            {
                console.MarkupLine($"[red]systemctl {args.EscapeMarkup()} failed:[/] {output.EscapeMarkup()}");
                return 1;
            }
        }

        // `restart` succeeds as soon as the process is spawned, so a runner that dies on startup
        // would still be reported as started. Give it a moment and look at what systemd saw.
        await Task.Delay(TimeSpan.FromSeconds(8));
        var (_, state) = await SystemctlAsync($"show -p ActiveState -p NRestarts {UnitName}");
        if (!IsHealthy(state))
        {
            console.MarkupLine($"[red]The host runner does not stay up[/] ({state.Replace('\n', ' ').EscapeMarkup()}).");
            console.MarkupLine($"[dim]See: journalctl -u {UnitName} -n 30[/]");
            return 1;
        }

        console.MarkupLine($"[green]✓[/] Host runner started for installation [bold]{installation.Id.EscapeMarkup()}[/] — every project on {installation.Server.EscapeMarkup()}.");
        console.MarkupLine($"[dim]Logs: journalctl -u {UnitName} -f · Models: pks providers init[/]");
        return 0;
    }

    /// <summary>From <c>systemctl show -p ActiveState -p NRestarts</c>: running, and not by way of
    /// a restart since the one we just asked for.</summary>
    internal static bool IsHealthy(string show)
    {
        var props = show.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0], kv => kv[1]);
        return props.GetValueOrDefault("ActiveState") == "active" && props.GetValueOrDefault("NRestarts") is null or "0";
    }

    internal static string BuildUnit(RunnerLauncherCommand self, string user, string home, bool withEnvironmentFile) =>
        $"""
        # Written by `pks agentics runner start`. Re-running it rewrites this file.
        [Unit]
        Description=Agentics host runner (every project on this installation)
        After=network-online.target docker.service
        Wants=network-online.target

        [Service]
        User={user}
        Environment=HOME={home}
        {(withEnvironmentFile ? $"EnvironmentFile={EnvironmentFile}" : "# no " + EnvironmentFile + " — the AGENTICS_* variables must come from elsewhere")}
        ExecStart={self.BuildCommandLine("--no-logo agentics runner run")}
        Restart=always
        RestartSec=5
        TimeoutStopSec=90

        [Install]
        WantedBy=multi-user.target

        """;

    private static async Task<(int, string)> SystemctlAsync(string args)
    {
        var psi = new ProcessStartInfo("systemctl", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output.Trim());
    }
}
