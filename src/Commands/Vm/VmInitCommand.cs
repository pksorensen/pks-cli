using System.ComponentModel;
using System.Diagnostics;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
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
    private readonly IScalewayService _scaleway;
    private readonly ITailscaleService _tailscale;
    private readonly IAnsiConsole _console;
    private readonly AzureInitCommand _azureInit;
    private readonly PKS.Commands.Scaleway.ScalewayInitCommand _scalewayInit;
    private readonly IActionGuard _guard;

    public VmInitCommand(
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        ISshTargetConfigurationService sshService,
        IAzureVmMetadataService vmMetadata,
        IScalewayService scaleway,
        ITailscaleService tailscale,
        AzureInitCommand azureInit,
        PKS.Commands.Scaleway.ScalewayInitCommand scalewayInit,
        IActionGuard guard,
        IAnsiConsole console)
    {
        _azureAuth = azureAuth;
        _vmService = vmService;
        _sshService = sshService;
        _vmMetadata = vmMetadata;
        _scaleway = scaleway;
        _tailscale = tailscale;
        _azureInit = azureInit;
        _scalewayInit = scalewayInit;
        _guard = guard;
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
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // Choose the cloud to provision on.
        var providerChoice = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Which cloud?[/]")
                .HighlightStyle(Style.Parse("cyan"))
                .AddChoices("Azure", "Scaleway (GPU)"));

        return providerChoice.StartsWith("Scaleway")
            ? await RunScalewayInitAsync(context, settings)
            : await RunAzureInitAsync(context, settings);
    }

    private async Task<int> RunAzureInitAsync(CommandContext context, Settings settings)
    {
        // 1. Check Azure authentication — chain into azure init if not yet done
        if (!await _azureAuth.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[cyan]Azure not yet authenticated. Starting Azure sign-in...[/]");
            var initResult = _azureInit.Execute(context, new AzureInitCommand.Settings());
            if (initResult != 0 || !await _azureAuth.IsAuthenticatedAsync())
            {
                _console.MarkupLine("[red]Azure authentication required to provision a VM.[/]");
                return 1;
            }
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

        // 6. Select VM size — fetch sizes + prices in parallel, then filter by RAM/CPU
        List<PKS.Infrastructure.Services.Models.AzureVmSizeInfo> availableSizes = new();
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Loading available VM sizes and prices in {location}...", async _ =>
            {
                var sizesTask = _vmService.ListVmSizesAsync(token, creds.SubscriptionId, location);
                var pricesTask = _vmService.FetchVmPricesAsync(location);
                await Task.WhenAll(sizesTask, pricesTask);

                availableSizes = sizesTask.Result;
                var prices = pricesTask.Result;
                foreach (var s in availableSizes)
                    if (prices.TryGetValue(s.Name, out var p))
                        s.PricePerHour = p;
            });

        string vmSize;
        if (availableSizes.Count == 0)
        {
            _console.MarkupLine("[yellow]Could not load VM sizes from Azure. Enter a size manually:[/]");
            vmSize = _console.Prompt(new TextPrompt<string>("[cyan]VM size:[/]").DefaultValue("Standard_B2s"));
        }
        else
        {
            // Step-down filters: RAM → CPU → show matching SKUs
            const string AnyOption = "Any";
            var ramOptions = new[] { AnyOption }
                .Concat(availableSizes.Select(s => s.MemoryInMB / 1024).Distinct().OrderBy(x => x).Select(g => $"{g} GB"))
                .ToList();
            var ramChoice = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]How much RAM?[/]")
                    .HighlightStyle(Style.Parse("cyan"))
                    .AddChoices(ramOptions));

            var afterRam = ramChoice == AnyOption
                ? availableSizes
                : availableSizes.Where(s => s.MemoryInMB / 1024 == int.Parse(ramChoice.Replace(" GB", ""))).ToList();

            var cpuOptions = new[] { AnyOption }
                .Concat(afterRam.Select(s => s.NumberOfCores).Distinct().OrderBy(x => x).Select(c => $"{c}"))
                .ToList();
            var cpuChoice = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]How many vCPUs?[/]")
                    .HighlightStyle(Style.Parse("cyan"))
                    .AddChoices(cpuOptions));

            var filtered = cpuChoice == AnyOption
                ? afterRam
                : afterRam.Where(s => s.NumberOfCores == int.Parse(cpuChoice)).ToList();

            if (filtered.Count == 0)
            {
                _console.MarkupLine("[yellow]No matching sizes found — showing all available.[/]");
                filtered = availableSizes;
            }

            var selectedSizeInfo = _console.Prompt(
                new SelectionPrompt<PKS.Infrastructure.Services.Models.AzureVmSizeInfo>()
                    .Title($"[cyan]Select VM size ({filtered.Count} matching):[/]")
                    .PageSize(12)
                    .UseConverter(s => s.DisplayLabel)
                    .HighlightStyle(Style.Parse("cyan"))
                    .AddChoices(filtered));

            vmSize = selectedSizeInfo.Name;
        }

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
            // Gate provisioning a new (billable) VM — before the Status spinner so the guard can prompt.
            try { await _guard.RequireAsync(new ActionRequest(ActionIds.VmCreate, $"Create Azure VM '{vmName}' in {location}", "This provisions billable cloud resources.")); }
            catch (ActionGuardDeniedException ex) { _console.MarkupLine($"[red]Create denied:[/] {Markup.Escape(ex.Message)}"); return 1; }

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
                var retrySize = _console.Prompt(
                    new SelectionPrompt<PKS.Infrastructure.Services.Models.AzureVmSizeInfo>()
                        .Title("[cyan]Select VM size:[/]")
                        .PageSize(12)
                        .UseConverter(s => s.DisplayLabel)
                        .HighlightStyle(Style.Parse("cyan"))
                        .AddChoices(availableSizes.Count > 0 ? availableSizes : new() { new() { Name = vmSize } }));
                vmSize = retrySize.Name;
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

    private async Task<int> RunScalewayInitAsync(CommandContext context, Settings settings)
    {
        // 1. Ensure Scaleway authentication — chain into scaleway init if not yet done
        if (!await _scaleway.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[cyan]Scaleway not yet authenticated. Starting Scaleway sign-in...[/]");
            var initResult = _scalewayInit.Execute(context, new PKS.Commands.Scaleway.ScalewayInitCommand.Settings());
            if (initResult != 0 || !await _scaleway.IsAuthenticatedAsync())
            {
                _console.MarkupLine("[red]Scaleway authentication required to provision an instance.[/]");
                return 1;
            }
        }

        var creds = await _scaleway.GetStoredCredentialsAsync();
        if (creds == null)
        {
            _console.MarkupLine("[red]Failed to load Scaleway credentials.[/]");
            return 1;
        }

        // 2. VM name
        var defaultName = $"pks-gpu-{Guid.NewGuid().ToString("N")[..4]}";
        var vmName = _console.Prompt(new TextPrompt<string>("[cyan]Instance name:[/]").DefaultValue(defaultName));
        if (string.IsNullOrWhiteSpace(vmName)) vmName = defaultName;

        // 3. Zone
        var zone = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Zone:[/]")
                .HighlightStyle(Style.Parse("cyan"))
                .AddChoices(new[] { creds.DefaultZone, "fr-par-2", "pl-waw-2", "fr-par-1", "nl-ams-1" }
                    .Where(z => !string.IsNullOrEmpty(z)).Distinct().ToArray()));

        // 4. Server type — GPU types first
        List<PKS.Infrastructure.Services.Models.ScalewayServerType> types = new();
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
            .StartAsync($"Loading instance types in {zone}...", async _ =>
            {
                types = await _scaleway.ListServerTypesAsync(zone);
            });

        if (types.Count == 0)
        {
            _console.MarkupLine("[red]No instance types returned for this zone.[/]");
            return 1;
        }

        var ordered = types.OrderByDescending(t => t.IsGpu).ThenBy(t => t.Name).ToList();
        var selectedType = _console.Prompt(
            new SelectionPrompt<PKS.Infrastructure.Services.Models.ScalewayServerType>()
                .Title("[cyan]Select instance type[/] [dim](GPU types listed first)[/]:")
                .PageSize(15)
                .UseConverter(t => t.DisplayLabel)
                .HighlightStyle(Style.Parse("cyan"))
                .AddChoices(ordered));

        // 5. Image — filter to the type's architecture
        var arch = string.IsNullOrEmpty(selectedType.Arch) ? "x86_64" : selectedType.Arch;
        List<PKS.Infrastructure.Services.Models.ScalewayImage> images = new();
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
            .StartAsync("Loading images...", async _ =>
            {
                images = await _scaleway.ListImagesAsync(zone, arch);
            });

        if (images.Count == 0)
        {
            _console.MarkupLine("[red]No images available for this zone/architecture.[/]");
            return 1;
        }

        // Surface Ubuntu / GPU-OS images first
        var orderedImages = images
            .OrderByDescending(i => (i.Name ?? "").Contains("gpu", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(i => (i.Name ?? "").Contains("ubuntu", StringComparison.OrdinalIgnoreCase))
            .ThenBy(i => i.Name)
            .ToList();
        var selectedImage = _console.Prompt(
            new SelectionPrompt<PKS.Infrastructure.Services.Models.ScalewayImage>()
                .Title("[cyan]Select OS image:[/]")
                .PageSize(15)
                .UseConverter(i => i.Name)
                .HighlightStyle(Style.Parse("cyan"))
                .AddChoices(orderedImages));

        // 6. SSH key (reuse the same scheme as Azure)
        var keyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", "keys");
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
        // The generated public key is injected via cloud-init user-data at create time so
        // the box accepts our key on first boot (see ScalewayService.CreateServerAsync).
        var scwPubKey = File.Exists(keyPath + ".pub") ? (await File.ReadAllTextAsync(keyPath + ".pub")).Trim() : string.Empty;

        const string adminUser = "root";

        // 6b. Optionally join the tailnet at boot (if `pks tailscale init` has been run)
        string? tailscaleUpArgs = null;
        if (await _tailscale.IsAuthenticatedAsync()
            && _console.Confirm("[cyan]Join this VM to your Tailscale network at boot?[/]", defaultValue: true))
        {
            var tsCreds = (await _tailscale.GetStoredCredentialsAsync())!;
            tailscaleUpArgs = _tailscale.BuildUpArgs(tsCreds, vmName);
        }

        // 7. Confirm
        _console.Write(new Panel(
            $"""
            [cyan1]Instance:[/] {Markup.Escape(vmName)}
            [cyan1]Zone:[/] {Markup.Escape(zone)}
            [cyan1]Type:[/] {Markup.Escape(selectedType.DisplayLabel)}
            [cyan1]Image:[/] {Markup.Escape(selectedImage.Name)}
            [cyan1]Project:[/] {Markup.Escape(creds.DefaultProjectName)} ({Markup.Escape(creds.DefaultProjectId)})
            [cyan1]SSH Key:[/] {Markup.Escape(keyPath)}
            [cyan1]Tailscale:[/] {(tailscaleUpArgs != null ? "[green]join at boot[/]" : "[dim]no[/]")}
            """)
            .Border(BoxBorder.Rounded).BorderStyle("cyan")
            .Header(" [bold cyan]Scaleway Instance[/] "));

        if (!_console.Confirm("[cyan]Create instance?[/]", defaultValue: true))
            return 0;

        // 8. Create
        // Gate provisioning a new (billable, often GPU) instance — before the Status spinner.
        try { await _guard.RequireAsync(new ActionRequest(ActionIds.VmCreate, $"Create Scaleway instance '{vmName}' in {zone}", "This provisions billable GPU/compute resources.")); }
        catch (ActionGuardDeniedException ex) { _console.MarkupLine($"[red]Create denied:[/] {Markup.Escape(ex.Message)}"); return 1; }

        PKS.Infrastructure.Services.Models.ScalewayServer? server = null;
        Exception? createError = null;
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
            .StartAsync("Provisioning instance...", async ctx =>
            {
                try
                {
                    server = await _scaleway.CreateServerAsync(new PKS.Infrastructure.Services.Models.ScalewayCreateOptions
                    {
                        Zone = zone,
                        ProjectId = creds.DefaultProjectId,
                        Name = vmName,
                        CommercialType = selectedType.Name,
                        Image = selectedImage.Id,
                        SshPublicKey = scwPubKey,
                        EnablePublicIp = true,
                        Tags = new[] { "pks" },
                        TailscaleUpArgs = tailscaleUpArgs
                    }, msg => ctx.Status(msg));
                }
                catch (Exception ex) { createError = ex; }
            });

        if (createError != null || server == null)
        {
            _console.MarkupLine($"[red]Instance creation failed:[/] {Markup.Escape(createError?.Message ?? "unknown error")}");
            return 1;
        }

        // 9. Wait for SSH (TCP probe is provider-agnostic)
        if (!string.IsNullOrEmpty(server.PublicIpAddress))
        {
            await _console.Status()
                .SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
                .StartAsync("Waiting for SSH...", async _ =>
                {
                    await _vmService.WaitForSshAsync(server.PublicIpAddress, 22, TimeSpan.FromMinutes(5));
                });

            // 10. Register SSH target
            await _sshService.AddTargetAsync(server.PublicIpAddress, adminUser, 22, keyPath, label: vmName);
        }

        // 11. Save record
        await _vmMetadata.SaveAsync(new AzureVmRecord
        {
            Provider = "scaleway",
            VmName = vmName,
            AdminUsername = adminUser,
            Zone = zone,
            ServerId = server.Id,
            ProjectId = creds.DefaultProjectId,
            Location = zone,
            PublicIpAddress = server.PublicIpAddress,
            SshKeyPath = keyPath,
            VmSize = selectedType.Name,
            IdleShutdownMinutes = 0,
            CreatedAt = DateTime.UtcNow
        });

        _console.Write(new Panel(
            $"""
            [green]Instance provisioned![/]

            [cyan1]Instance:[/] {Markup.Escape(vmName)}
            [cyan1]Public IP:[/] {Markup.Escape(server.PublicIpAddress)}
            [cyan1]Admin User:[/] {adminUser}
            [cyan1]SSH Key:[/] {Markup.Escape(keyPath)}

            [dim]Connect: ssh -i {Markup.Escape(keyPath)} {adminUser}@{Markup.Escape(server.PublicIpAddress)}[/]
            [dim]Stop to save GPU cost: pks vm status → Stop VM[/]
            """)
            .Border(BoxBorder.Rounded).BorderStyle("green")
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
