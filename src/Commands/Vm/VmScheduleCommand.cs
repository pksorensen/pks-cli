using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

[Description("Interactively configure scheduled start/stop for a VM")]
public class VmScheduleCommand : Command<VmSettings>
{
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly IAnsiConsole _console;

    public VmScheduleCommand(
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        IAzureVmMetadataService vmMetadata,
        IAnsiConsole console)
    {
        _azureAuth = azureAuth;
        _vmService = vmService;
        _vmMetadata = vmMetadata;
        _console = console;
    }

    public override int Execute(CommandContext context, VmSettings settings)
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync()
    {
        // 1. Load tracked VMs
        var vms = await _vmMetadata.ListAsync();
        if (vms.Count == 0)
        {
            _console.MarkupLine("[red]No VMs tracked. Run 'pks vm init' first.[/]");
            return 1;
        }

        // 2. Pick a VM
        var vmName = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Which VM would you like to schedule?[/]")
                .AddChoices(vms.Select(v => v.VmName)));

        var vm = vms.First(v => v.VmName == vmName);

        // 3. What to configure (multi-select)
        const string OptAutoStart    = "Auto-start: boot at a fixed time each day";
        const string OptAutoStop     = "Auto-shutdown: stop at a fixed time each day";
        const string OptIdleShutdown = "Idle shutdown: stop after N minutes of inactivity";
        const string OptDisableStart = "Disable auto-start";
        const string OptDisableStop  = "Disable auto-shutdown";
        const string OptDisableIdle  = "Disable idle shutdown";

        var actions = _console.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("[cyan]What would you like to configure?[/]")
                .InstructionsText("[dim](Space to select, Enter to confirm)[/]")
                .AddChoices(OptAutoStart, OptAutoStop, OptIdleShutdown,
                            OptDisableStart, OptDisableStop, OptDisableIdle));

        if (actions.Count == 0)
        {
            _console.MarkupLine("[dim]Nothing selected.[/]");
            return 0;
        }

        // 4. Collect values for selected actions
        string? autoStartTime    = null;
        string? autoStopTime     = null;
        int?    idleMinutes      = null;
        bool    disableStart     = actions.Contains(OptDisableStart);
        bool    disableStop      = actions.Contains(OptDisableStop);
        bool    disableIdle      = actions.Contains(OptDisableIdle);

        if (actions.Contains(OptAutoStart))
        {
            autoStartTime = _console.Prompt(
                new TextPrompt<string>("[cyan]Auto-start time (UTC, HH:MM):[/]")
                    .DefaultValue("06:00")
                    .Validate(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{2}:\d{2}$")
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]Use format HH:MM, e.g. 06:00[/]")));
        }

        if (actions.Contains(OptAutoStop))
        {
            autoStopTime = _console.Prompt(
                new TextPrompt<string>("[cyan]Auto-shutdown time (UTC, HH:MM):[/]")
                    .DefaultValue("22:00")
                    .Validate(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{2}:\d{2}$")
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]Use format HH:MM, e.g. 22:00[/]")));
        }

        if (actions.Contains(OptIdleShutdown))
        {
            idleMinutes = _console.Prompt(
                new TextPrompt<int>("[cyan]Idle threshold (minutes):[/]")
                    .DefaultValue(60)
                    .Validate(n => n > 0
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]Must be greater than 0[/]")));
        }

        // 5. Show summary and confirm
        var summary = new System.Text.StringBuilder();
        if (autoStartTime  != null) summary.AppendLine($"[cyan1]Auto-start:[/]    {autoStartTime} UTC daily");
        if (autoStopTime   != null) summary.AppendLine($"[cyan1]Auto-shutdown:[/] {autoStopTime} UTC daily");
        if (idleMinutes    != null) summary.AppendLine($"[cyan1]Idle shutdown:[/] {idleMinutes} minutes");
        if (disableStart)           summary.AppendLine("[cyan1]Disable auto-start[/]");
        if (disableStop)            summary.AppendLine("[cyan1]Disable auto-shutdown[/]");
        if (disableIdle)            summary.AppendLine("[cyan1]Disable idle shutdown[/]");

        _console.Write(new Panel(summary.ToString().TrimEnd())
            .Border(BoxBorder.Rounded)
            .BorderStyle("cyan")
            .Header($" [bold cyan]Schedule for {Markup.Escape(vmName)}[/] "));

        if (!_console.Confirm("[cyan]Apply these changes?[/]", defaultValue: true))
            return 0;

        // 6. Get Azure token
        var token = await _azureAuth.GetAccessTokenAsync("https://management.azure.com/.default");
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to get Azure token. Run 'pks azure init' first.[/]");
            return 1;
        }

        // 7. Resolve VM resource ID (needed for schedule APIs)
        string vmId = string.Empty;
        try { vmId = await GetVmIdAsync(token, vm.SubscriptionId, vm.ResourceGroup, vmName); }
        catch (Exception ex) { _console.MarkupLine($"[yellow]Warning: could not resolve VM ID: {Markup.Escape(ex.Message)}[/]"); }

        // 8. Apply
        var errors = new List<string>();

        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Applying schedule changes...", async ctx =>
            {
                if (autoStartTime != null)
                {
                    ctx.Status($"Setting auto-start to {autoStartTime}...");
                    try { await _vmService.SetScheduledStartAsync(token, vm.SubscriptionId, vm.ResourceGroup, vmName, vm.Location, vmId, autoStartTime); }
                    catch (Exception ex) { errors.Add($"auto-start: {ex.Message}"); }
                }

                if (disableStart)
                {
                    ctx.Status("Disabling auto-start...");
                    try { await _vmService.DisableScheduledStartAsync(token, vm.SubscriptionId, vm.ResourceGroup, vmName); }
                    catch (Exception ex) { errors.Add($"disable auto-start: {ex.Message}"); }
                }

                if (autoStopTime != null)
                {
                    ctx.Status($"Setting auto-shutdown to {autoStopTime}...");
                    try { await _vmService.SetScheduledShutdownAsync(token, vm.SubscriptionId, vm.ResourceGroup, vmName, vm.Location, vmId, autoStopTime); }
                    catch (Exception ex) { errors.Add($"auto-shutdown: {ex.Message}"); }
                }

                if (disableStop)
                {
                    ctx.Status("Disabling auto-shutdown...");
                    try { await _vmService.DisableScheduledShutdownAsync(token, vm.SubscriptionId, vm.ResourceGroup, vmName); }
                    catch (Exception ex) { errors.Add($"disable auto-shutdown: {ex.Message}"); }
                }

                if (idleMinutes != null && !string.IsNullOrEmpty(vm.PublicIpAddress) && !string.IsNullOrEmpty(vm.SshKeyPath))
                {
                    ctx.Status($"Updating idle shutdown to {idleMinutes} minutes...");
                    RunSshCommand(vm.SshKeyPath, vm.PublicIpAddress,
                        $"sudo sed -i 's/^IDLE_THRESHOLD_MINUTES=.*/IDLE_THRESHOLD_MINUTES={idleMinutes}/' /usr/local/bin/pks-idle-monitor && sudo systemctl restart pks-idle-monitor");
                }

                if (disableIdle && !string.IsNullOrEmpty(vm.PublicIpAddress) && !string.IsNullOrEmpty(vm.SshKeyPath))
                {
                    ctx.Status("Disabling idle shutdown...");
                    RunSshCommand(vm.SshKeyPath, vm.PublicIpAddress,
                        "sudo systemctl stop pks-idle-monitor && sudo systemctl disable pks-idle-monitor");
                }

                // Persist changes to local metadata
                if (autoStopTime  != null) vm.ScheduledShutdownUtc = autoStopTime;
                if (disableStop)            vm.ScheduledShutdownUtc = null;
                if (idleMinutes   != null) vm.IdleShutdownMinutes   = idleMinutes.Value;
                if (disableIdle)            vm.IdleShutdownMinutes   = 0;
                await _vmMetadata.SaveAsync(vm);
            });

        foreach (var err in errors)
            _console.MarkupLine($"[yellow]Warning — {Markup.Escape(err)}[/]");

        if (errors.Count == 0)
            _console.MarkupLine("[green]Schedule updated successfully.[/]");

        return errors.Count == 0 ? 0 : 1;
    }

    private static void RunSshCommand(string keyPath, string host, string command)
    {
        try
        {
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ssh")
            {
                Arguments = $"-i \"{keyPath}\" -o StrictHostKeyChecking=no azureuser@{host} \"{command}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(30_000);
        }
        catch { }
    }

    private async Task<string> GetVmIdAsync(string token, string subscriptionId, string resourceGroup, string vmName)
    {
        using var client = new HttpClient();
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{vmName}?api-version=2023-09-01";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"ARM {(int)resp.StatusCode}: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty;
    }
}
