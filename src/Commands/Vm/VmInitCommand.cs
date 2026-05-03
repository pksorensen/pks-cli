using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

/// <summary>
/// Interactive command to provision a new Azure VM and register it as an SSH target.
/// </summary>
[Description("Provision a new VM and register it as an SSH target")]
public class VmInitCommand : Command<VmInitCommand.Settings>
{
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;
    private readonly ISshTargetConfigurationService _sshService;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly IAnsiConsole _console;

    public VmInitCommand(
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        ISshTargetConfigurationService sshService,
        IAzureVmMetadataService vmMetadata,
        IAnsiConsole console)
    {
        _azureAuth = azureAuth;
        _vmService = vmService;
        _sshService = sshService;
        _vmMetadata = vmMetadata;
        _console = console;
    }

    public class Settings : VmSettings
    {
        [CommandOption("--idle-shutdown <MINUTES>")]
        [Description("Auto-shutdown after N minutes idle (default: 60, 0 = disabled)")]
        public int? IdleShutdown { get; set; }

        [CommandOption("--scheduled-shutdown <TIME>")]
        [Description("Daily hard shutdown at this time UTC, format HH:MM (e.g. 22:00)")]
        public string? ScheduledShutdown { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        // 1. Check Azure authentication
        if (!await _azureAuth.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]No VM provider authenticated. Run 'pks azure init' first.[/]");
            return 1;
        }

        // 2. Get stored credentials
        var creds = await _azureAuth.GetStoredCredentialsAsync();
        if (creds == null)
        {
            _console.MarkupLine("[red]Failed to load Azure credentials.[/]");
            return 1;
        }

        // 3. Get management token
        var token = await _azureAuth.GetAccessTokenAsync("https://management.azure.com/.default");
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to obtain Azure management token.[/]");
            return 1;
        }

        // 4. Prompt VM name
        var defaultName = $"pks-vm-{Guid.NewGuid().ToString("N")[..4]}";
        var vmName = _console.Prompt(
            new TextPrompt<string>("[cyan]VM name:[/]")
                .DefaultValue(defaultName));

        if (string.IsNullOrWhiteSpace(vmName))
            vmName = defaultName;

        // 5. List resource groups
        List<AzureResourceGroup> resourceGroups;
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Loading resource groups...", async _ =>
            {
                resourceGroups = await _vmService.ListResourceGroupsAsync(token, creds.SubscriptionId);
            });

        resourceGroups = await _vmService.ListResourceGroupsAsync(token, creds.SubscriptionId);

        const string CreateNewOption = "+ Create new resource group";
        var rgChoices = new List<string> { CreateNewOption };
        rgChoices.AddRange(resourceGroups.Select(r => r.Name));

        var rgChoice = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select a resource group:[/]")
                .AddChoices(rgChoices));

        AzureResourceGroup selectedRg;
        string location;

        if (rgChoice == CreateNewOption)
        {
            var rgName = _console.Prompt(
                new TextPrompt<string>("[cyan]New resource group name:[/]")
                    .DefaultValue("pks-vms"));

            location = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select location:[/]")
                    .AddChoices("eastus", "westeurope", "northeurope", "uksouth", "australiaeast"));

            await _console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Creating resource group '{rgName}'...", async _ =>
                {
                    selectedRg = await _vmService.EnsureResourceGroupAsync(token, creds.SubscriptionId, rgName, location);
                });

            selectedRg = await _vmService.EnsureResourceGroupAsync(token, creds.SubscriptionId, rgName, location);
        }
        else
        {
            selectedRg = resourceGroups.First(r => r.Name == rgChoice);
            location = selectedRg.Location;
        }

        // 6. Select VM size
        var sizeMap = new Dictionary<string, string>
        {
            ["Standard_B1s (1 vCPU, 1 GB RAM)"] = "Standard_B1s",
            ["Standard_B2s (2 vCPU, 4 GB RAM)"] = "Standard_B2s",
            ["Standard_B4ms (4 vCPU, 16 GB RAM)"] = "Standard_B4ms",
            ["Standard_D2s_v3 (2 vCPU, 8 GB RAM)"] = "Standard_D2s_v3"
        };

        var sizeDisplay = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select VM size:[/]")
                .AddChoices(sizeMap.Keys)
                .HighlightStyle(Style.Parse("cyan")));

        var vmSize = sizeMap[sizeDisplay];

        // 6b. Select OS disk size
        const string CustomDiskOption = "Custom — type a number";
        var diskSizeMap = new Dictionary<string, int>
        {
            ["128 GB — recommended for devcontainer builds with playwright/chromium"] = 128,
            ["64 GB — light development, single project"] = 64,
            ["256 GB — heavy: multiple devcontainers, large datasets"] = 256,
            ["512 GB"] = 512,
            [CustomDiskOption] = 0
        };

        var diskSizeDisplay = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]OS disk size:[/]")
                .AddChoices(diskSizeMap.Keys)
                .HighlightStyle(Style.Parse("cyan")));

        int osDiskSizeGb;
        if (diskSizeDisplay == CustomDiskOption)
        {
            osDiskSizeGb = _console.Prompt(
                new TextPrompt<int>("[cyan]Disk size in GB:[/]")
                    .Validate(g => g >= 30 && g <= 4096
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]Disk size must be between 30 and 4096 GB.[/]")));
        }
        else
        {
            osDiskSizeGb = diskSizeMap[diskSizeDisplay];
        }

        // 7. Generate SSH key
        var keyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pks-cli", "keys");
        Directory.CreateDirectory(keyDir);
        var keyPath = Path.Combine(keyDir, vmName);

        if (!File.Exists(keyPath))
        {
            var keygen = Process.Start(new ProcessStartInfo("ssh-keygen")
            {
                Arguments = $"-t ed25519 -f \"{keyPath}\" -N \"\" -C \"pks-{vmName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (keygen != null) await keygen.WaitForExitAsync();
        }

        var pubKey = await File.ReadAllTextAsync(keyPath + ".pub");

        // 8. Show summary and confirm
        var idleMinutes = settings.IdleShutdown ?? 60;
        var idleDisplay = idleMinutes > 0 ? $"{idleMinutes} min" : "disabled";
        var scheduledDisplay = settings.ScheduledShutdown != null ? $"{Markup.Escape(settings.ScheduledShutdown)} UTC" : "none";

        _console.Write(new Panel(
            $"""
            [cyan1]VM Name:[/] {Markup.Escape(vmName)}
            [cyan1]Resource Group:[/] {Markup.Escape(selectedRg.Name)}
            [cyan1]Location:[/] {Markup.Escape(location)}
            [cyan1]VM Size:[/] {Markup.Escape(vmSize)}
            [cyan1]OS Disk:[/] {osDiskSizeGb} GB
            [cyan1]SSH Key:[/] {Markup.Escape(keyPath)}
            [cyan1]Idle Shutdown:[/] {idleDisplay}
            [cyan1]Scheduled Shutdown:[/] {scheduledDisplay}
            """)
            .Border(BoxBorder.Rounded)
            .BorderStyle("cyan")
            .Header(" [bold cyan]VM Configuration[/] "));

        var confirmed = _console.Confirm("[cyan]Create VM?[/]", defaultValue: true);
        if (!confirmed)
            return 0;

        // 9. Create VM (with retry loop for SkuNotAvailable)
        AzureVmInfo? vmInfo = null;
        while (true)
        {
            Exception? createError = null;
            await _console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Provisioning VM...", async ctx =>
                {
                    try
                    {
                        vmInfo = await _vmService.CreateVmAsync(new AzureVmCreateOptions
                        {
                            AccessToken = token,
                            SubscriptionId = creds.SubscriptionId,
                            ResourceGroupName = selectedRg.Name,
                            Location = location,
                            VmName = vmName,
                            VmSize = vmSize,
                            AdminUsername = "azureuser",
                            SshPublicKey = pubKey.Trim(),
                            InstallDocker = true,
                            IdleShutdownMinutes = settings.IdleShutdown ?? 60,
                            ScheduledShutdownUtc = settings.ScheduledShutdown,
                            OsDiskSizeGb = osDiskSizeGb
                        }, msg => ctx.Status(msg));
                    }
                    catch (Exception ex) { createError = ex; }
                });

            if (createError == null) break;

            // Check if this is a capacity/SKU error we can recover from
            var isSkuError = createError.Message.Contains("SkuNotAvailable", StringComparison.OrdinalIgnoreCase)
                          || createError.Message.Contains("Capacity", StringComparison.OrdinalIgnoreCase);

            _console.WriteLine();
            _console.MarkupLine($"[red]VM creation failed:[/] {Markup.Escape(ExtractArmMessage(createError.Message))}");
            _console.WriteLine();

            if (!isSkuError)
                return 1;

            // Offer recovery options
            var retryChoice = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]The selected size has no capacity in this location. What would you like to do?[/]")
                    .AddChoices(
                        "Try a different VM size (same location)",
                        "Try a different location (same size)",
                        "Cancel"));

            if (retryChoice.StartsWith("Try a different VM size"))
            {
                var newSizeDisplay = _console.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[cyan]Select VM size:[/]")
                        .AddChoices(sizeMap.Keys)
                        .HighlightStyle(Style.Parse("cyan")));
                vmSize = sizeMap[newSizeDisplay];
                _console.MarkupLine($"[dim]Retrying with size: {Markup.Escape(vmSize)}...[/]");
            }
            else if (retryChoice.StartsWith("Try a different location"))
            {
                location = _console.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[cyan]Select location:[/]")
                        .AddChoices("eastus", "westeurope", "northeurope", "uksouth", "australiaeast", "swedencentral"));
                _console.MarkupLine($"[yellow]Note: existing network resources in '{Markup.Escape(selectedRg.Name)}' will be reused — they are in '{Markup.Escape(selectedRg.Location)}'. If the new location is different you may need to use a different resource group.[/]");
                _console.MarkupLine($"[dim]Retrying with location: {Markup.Escape(location)}...[/]");
            }
            else
            {
                return 0;
            }
        }

        if (vmInfo == null)
        {
            _console.MarkupLine("[red]VM creation failed.[/]");
            return 1;
        }

        // 10. Wait for SSH
        var sshReady = false;
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Waiting for SSH to become available...", async _ =>
            {
                sshReady = await _vmService.WaitForSshAsync(vmInfo.PublicIpAddress, 22, TimeSpan.FromMinutes(5));
            });

        if (!sshReady)
        {
            _console.MarkupLine("[yellow]SSH is not yet available. The VM may still be initializing.[/]");
        }
        else
        {
            // 10b. Wait for cloud-init to finish (Docker install runs here)
            await _console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Waiting for cloud-init to complete (installing Docker...)...", async ctx =>
                {
                    var sshArgs = $"-o StrictHostKeyChecking=no -o BatchMode=yes -p 22";
                    if (!string.IsNullOrEmpty(keyPath))
                        sshArgs += $" -i \"{keyPath}\"";

                    var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
                    while (DateTime.UtcNow < deadline)
                    {
                        try
                        {
                            var psi = new System.Diagnostics.ProcessStartInfo("ssh")
                            {
                                Arguments = $"{sshArgs} azureuser@{vmInfo.PublicIpAddress} \"cloud-init status 2>/dev/null || echo done\"",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            };
                            using var proc = System.Diagnostics.Process.Start(psi);
                            if (proc != null)
                            {
                                var output = await proc.StandardOutput.ReadToEndAsync();
                                await proc.WaitForExitAsync();
                                if (output.Contains("done") || output.Contains("disabled"))
                                    break;
                                if (output.Contains("running"))
                                    ctx.Status($"cloud-init still running... ({(int)(deadline - DateTime.UtcNow).TotalMinutes}m left)");
                                else if (output.Contains("error"))
                                {
                                    _console.MarkupLine("[yellow]cloud-init reported an error — Docker may not have installed correctly.[/]");
                                    break;
                                }
                            }
                        }
                        catch { }
                        await Task.Delay(TimeSpan.FromSeconds(10));
                    }
                });
        }

        // 11. Register as SSH target
        await _sshService.AddTargetAsync(vmInfo.PublicIpAddress, "azureuser", 22, keyPath, label: vmName);

        // 12. Save VM metadata
        await _vmMetadata.SaveAsync(new AzureVmRecord
        {
            VmName = vmName,
            SubscriptionId = creds.SubscriptionId,
            SubscriptionName = creds.SubscriptionName ?? string.Empty,
            ResourceGroup = selectedRg.Name,
            Location = location,
            PublicIpAddress = vmInfo.PublicIpAddress,
            SshKeyPath = keyPath,
            VmSize = vmSize,
            IdleShutdownMinutes = settings.IdleShutdown ?? 60,
            ScheduledShutdownUtc = settings.ScheduledShutdown,
            OsDiskSizeGb = osDiskSizeGb,
            CreatedAt = DateTime.UtcNow
        });

        // 13. Show success
        _console.Write(new Panel(
            $"""
            [green]VM provisioned successfully![/]

            [cyan1]VM Name:[/] {Markup.Escape(vmName)}
            [cyan1]Public IP:[/] {Markup.Escape(vmInfo.PublicIpAddress)}
            [cyan1]Admin User:[/] azureuser
            [cyan1]SSH Key:[/] {Markup.Escape(keyPath)}

            [dim]Connect: ssh -i {Markup.Escape(keyPath)} azureuser@{Markup.Escape(vmInfo.PublicIpAddress)}[/]
            [dim]Spawn devcontainer: pks devcontainer spawn --ssh-target {Markup.Escape(vmName)}[/]
            """)
            .Border(BoxBorder.Rounded)
            .BorderStyle("green")
            .Header(" [bold green]Success[/] "));

        return 0;
    }

    // Extract the human-readable message from an ARM error, falling back to the raw exception message
    private static string ExtractArmMessage(string raw)
    {
        try
        {
            var start = raw.IndexOf('{');
            if (start < 0) return raw;
            using var doc = System.Text.Json.JsonDocument.Parse(raw[start..]);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                return msg.GetString() ?? raw;
        }
        catch { }
        return raw;
    }
}
