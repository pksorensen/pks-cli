using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services.Security;
using PKS.Infrastructure.Services.Update;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Update;

/// <summary>
/// Self-update, mirroring <c>aspire update</c>: pick a channel (stable / daily), show the version
/// diff, confirm, then apply via the right mechanism for how pks was installed. Replacing the
/// binary is the most sensitive action, so it is gated by the <c>pks.update</c> second factor.
/// In the baked devcontainer pks runs as the (non-root) pks user and cannot replace itself, so it
/// delegates to a host-root command instead.
/// </summary>
[Description("Update pks to the latest version (stable or daily channel)")]
public class UpdateCommand : Command<UpdateCommand.Settings>
{
    private readonly IUpdateService _update;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public UpdateCommand(IUpdateService update, IActionGuard guard, IAnsiConsole console)
    {
        _update = update;
        _guard = guard;
        _console = console;
    }

    public class Settings : CommandSettings
    {
        [CommandOption("--self")]
        [Description("Update the pks CLI itself")]
        public bool Self { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        var method = _update.InstallMethod;

        var channel = await _update.GetChannelAsync();
        if (channel == null)
        {
            channel = _console.Prompt(new SelectionPrompt<UpdateChannel>()
                .Title("[cyan]Which update channel?[/]")
                .HighlightStyle(Style.Parse("cyan"))
                .UseConverter(c => c == UpdateChannel.Stable
                    ? "stable  [dim]— released versions (nuget.org)[/]"
                    : "daily   [dim]— latest preview from main[/]")
                .AddChoices(UpdateChannel.Stable, UpdateChannel.Daily));
            await _update.SetChannelAsync(channel.Value);
        }

        string? latest = null;
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
            .StartAsync($"Checking {channel} channel…", async _ => latest = await _update.GetLatestVersionAsync(channel.Value));

        if (string.IsNullOrEmpty(latest))
        {
            _console.MarkupLine("[red]Couldn't reach the update feed. Check connectivity and try again.[/]");
            return 1;
        }

        var current = _update.CurrentVersion;
        if (!_update.IsNewer(latest))
        {
            _console.MarkupLine($"[green]Already up to date.[/] [dim]({current}, {channel} channel)[/]");
            return 0;
        }

        _console.Write(new Panel($"[dim]Current:[/] {Markup.Escape(current)}\n[bold green]Latest:[/]  {Markup.Escape(latest)}   [dim]({channel} channel)[/]")
            .Border(BoxBorder.Rounded).BorderStyle("cyan").Header(" [bold cyan]pks update[/] "));

        if (!_console.Confirm($"Update to {Markup.Escape(latest)}?", defaultValue: true))
            return 0;

        // Replacing the pks binary is the most sensitive action — gate it (a swapped binary could
        // otherwise disable the whole gate). Skipped only when no factor is enrolled (TOFU).
        try
        {
            await _guard.RequireAsync(new ActionRequest(ActionIds.PksUpdate, $"Update pks {current} → {latest} ({channel} channel)"));
        }
        catch (ActionGuardDeniedException ex)
        {
            _console.MarkupLine($"[red]Update denied:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        return method switch
        {
            InstallMethod.DotnetTool => RunDotnetToolUpdate(channel.Value),
            InstallMethod.Baked => DelegateToHost(latest),
            InstallMethod.Npm => PrintGuidance("Update with:", "[cyan]npm install -g @pks-cli/cli@latest[/]"),
            InstallMethod.StandaloneBinary => PrintGuidance($"Download pks {Markup.Escape(latest)} and replace this binary:", $"[dim]{Markup.Escape(_update.PackageId)} {Markup.Escape(latest)}[/]"),
            _ => PrintGuidance("This pks was run from source (dotnet run).", "[dim]Rebuild from the repo to pick up changes.[/]"),
        };
    }

    private int RunDotnetToolUpdate(UpdateChannel channel)
    {
        var args = $"tool update -g {_update.PackageId}";
        if (channel == UpdateChannel.Daily) args += " --prerelease";
        _console.MarkupLine($"[dim]$ dotnet {args}[/]");
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("dotnet", args) { UseShellExecute = false });
            proc!.WaitForExit();
            if (proc.ExitCode == 0) _console.MarkupLine("[green]✓ Updated. Re-run pks to use the new version.[/]");
            else _console.MarkupLine("[red]Update failed — see output above.[/]");
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Couldn't launch dotnet:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private int DelegateToHost(string latest)
    {
        _console.Write(new Panel(string.Join("\n", new[]
        {
            "This pks is [bold]baked into the devcontainer image[/] and runs as the [bold]pks[/] user,",
            "which cannot replace /usr/local/bin/pks. Run the swap from the Docker [bold]host[/]:",
            string.Empty,
            "  [cyan]./scripts/host/pks-devswap.sh release   <container>[/]  [dim]# install the released binary[/]",
            "  [cyan]./scripts/host/pks-devswap.sh workspace <container>[/]  [dim]# build from this workspace (dev loop)[/]",
            string.Empty,
            $"Target version on the [bold]{ _update.PackageId }[/] feed: [bold]{Markup.Escape(latest)}[/]",
        }))
            .Border(BoxBorder.Rounded).BorderStyle("yellow").Header(" [bold yellow]Host action required[/] "));
        return 0;
    }

    private int PrintGuidance(string line1, string line2)
    {
        _console.MarkupLine(line1);
        _console.MarkupLine("  " + line2);
        return 0;
    }
}
