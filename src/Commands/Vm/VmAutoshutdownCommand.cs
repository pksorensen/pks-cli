using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

[Description("Configure auto-shutdown for a VM")]
public class VmAutoshutdownCommand : Command<VmAutoshutdownCommand.Settings>
{
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly VmProviderRegistry _providers;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public VmAutoshutdownCommand(
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        IAzureVmMetadataService vmMetadata,
        VmProviderRegistry providers,
        IActionGuard guard,
        IAnsiConsole console)
    {
        _azureAuth = azureAuth;
        _vmService = vmService;
        _vmMetadata = vmMetadata;
        _providers = providers;
        _guard = guard;
        _console = console;
    }

    public class Settings : VmSettings
    {
        [CommandArgument(0, "[VM_NAME]")]
        [Description("VM name or label (interactive if omitted)")]
        public string? VmName { get; set; }

        [CommandOption("--idle <MINUTES>")]
        [Description("Idle shutdown threshold in minutes (0 = disable)")]
        public int? IdleMinutes { get; set; }

        [CommandOption("--scheduled <TIME>")]
        [Description("Daily shutdown time in UTC, format HH:MM (e.g. 22:00)")]
        public string? ScheduledTime { get; set; }

        [CommandOption("--disable")]
        [Description("Disable all auto-shutdown")]
        public bool Disable { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        // 1. Load VM records
        var records = await _vmMetadata.ListAsync();

        // 2. If no records, show error
        if (records.Count == 0)
        {
            _console.MarkupLine("[red]No VMs tracked. Use 'pks vm init' to create a VM.[/]");
            return 1;
        }

        // 3. Resolve VM name
        var vmName = settings.VmName;
        if (string.IsNullOrWhiteSpace(vmName))
        {
            vmName = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select a VM:[/]")
                    .AddChoices(records.Select(r => r.VmName)));
        }

        // 4. Find the record
        var record = await _vmMetadata.FindAsync(vmName);
        if (record == null)
        {
            _console.MarkupLine($"[red]VM '{Markup.Escape(vmName)}' not found in tracked VMs.[/]");
            return 1;
        }

        // 4b. Scheduled/idle shutdown is provider-specific (Azure DevTest schedules + an
        // SSH idle monitor). Providers without server-side scheduling are not supported yet.
        var provider = _providers.Resolve(record);
        if (!provider.SupportsScheduledShutdown)
        {
            _console.MarkupLine($"[yellow]Auto-shutdown is not supported for {Markup.Escape(provider.DisplayName)} VMs yet.[/]");
            _console.MarkupLine("[dim]Stop it manually with [bold]pks vm status[/] → Stop VM when idle.[/]");
            return 0;
        }

        // 4c. Gate changing the auto-shutdown policy.
        try { await _guard.RequireAsync(new ActionRequest(ActionIds.VmAutoshutdownWrite, $"Change auto-shutdown for VM '{record.VmName}'")); }
        catch (ActionGuardDeniedException ex) { _console.MarkupLine($"[red]Denied:[/] {Markup.Escape(ex.Message)}"); return 1; }

        // 5. Get management token
        var token = await _azureAuth.GetAccessTokenAsync("https://management.azure.com/.default");
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to obtain Azure management token. Run 'pks azure init' first.[/]");
            return 1;
        }

        // 6. Handle --disable
        if (settings.Disable)
        {
            try
            {
                await _vmService.DisableScheduledShutdownAsync(token, record.SubscriptionId, record.ResourceGroup, vmName);
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[yellow]Warning: Could not disable scheduled shutdown via ARM: {Markup.Escape(ex.Message)}[/]");
            }

            // SSH: stop idle monitor
            if (!string.IsNullOrEmpty(record.PublicIpAddress) && !string.IsNullOrEmpty(record.SshKeyPath))
            {
                RunSshCommand(record.SshKeyPath, record.PublicIpAddress,
                    "sudo systemctl stop pks-idle-monitor && sudo systemctl disable pks-idle-monitor");
            }

            record.IdleShutdownMinutes = 0;
            record.ScheduledShutdownUtc = null;
            await _vmMetadata.SaveAsync(record);

            _console.MarkupLine($"[green]Auto-shutdown disabled for VM '{Markup.Escape(vmName)}'.[/]");
            return 0;
        }

        // 7. Handle --scheduled
        if (!string.IsNullOrEmpty(settings.ScheduledTime))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(settings.ScheduledTime, @"^\d{2}:\d{2}$"))
            {
                _console.MarkupLine("[red]Invalid scheduled time format. Use HH:MM (e.g. 22:00).[/]");
                return 1;
            }

            // Get VM ID from ARM
            string vmId = string.Empty;
            try
            {
                vmId = await GetVmIdAsync(token, record.SubscriptionId, record.ResourceGroup, vmName);
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[yellow]Warning: Could not resolve VM ID: {Markup.Escape(ex.Message)}[/]");
            }

            await _vmService.SetScheduledShutdownAsync(
                token, record.SubscriptionId, record.ResourceGroup,
                vmName, record.Location, vmId, settings.ScheduledTime);

            record.ScheduledShutdownUtc = settings.ScheduledTime;
        }

        // 8. Handle --idle
        if (settings.IdleMinutes.HasValue)
        {
            var n = settings.IdleMinutes.Value;

            if (!string.IsNullOrEmpty(record.PublicIpAddress) && !string.IsNullOrEmpty(record.SshKeyPath))
            {
                if (n > 0)
                {
                    RunSshCommand(record.SshKeyPath, record.PublicIpAddress,
                        $"sudo sed -i 's/^IDLE_THRESHOLD_MINUTES=.*/IDLE_THRESHOLD_MINUTES={n}/' /usr/local/bin/pks-idle-monitor && sudo systemctl restart pks-idle-monitor");
                }
                else
                {
                    RunSshCommand(record.SshKeyPath, record.PublicIpAddress,
                        "sudo systemctl stop pks-idle-monitor && sudo systemctl disable pks-idle-monitor");
                }
            }

            record.IdleShutdownMinutes = n;
        }

        // 9. Save updated record
        await _vmMetadata.SaveAsync(record);

        // 10. Show updated config
        var idleDisplay = record.IdleShutdownMinutes > 0 ? $"{record.IdleShutdownMinutes} min" : "disabled";
        var scheduledDisplay = record.ScheduledShutdownUtc ?? "none";

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1);
        table.AddColumn("[bold cyan]Setting[/]");
        table.AddColumn("[bold cyan]Value[/]");
        table.AddRow("VM Name", Markup.Escape(record.VmName));
        table.AddRow("Idle Shutdown", Markup.Escape(idleDisplay));
        table.AddRow("Scheduled Shutdown", Markup.Escape(scheduledDisplay));
        _console.Write(table);

        return 0;
    }

    private static void RunSshCommand(string keyPath, string host, string command)
    {
        try
        {
            var proc = Process.Start(new ProcessStartInfo("ssh")
            {
                Arguments = $"-i \"{keyPath}\" -o StrictHostKeyChecking=no azureuser@{host} \"{command}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            proc?.WaitForExit(30_000);
        }
        catch
        {
            // Best-effort; ignore failures
        }
    }

    private async Task<string> GetVmIdAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName)
    {
        using var client = new System.Net.Http.HttpClient();
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{vmName}?api-version=2023-09-01";
        var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"ARM {(int)resp.StatusCode} getting VM: {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty;
    }
}
