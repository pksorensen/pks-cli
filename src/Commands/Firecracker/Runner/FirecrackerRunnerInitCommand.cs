using System.ComponentModel;
using PKS.Infrastructure.Services.Firecracker;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Firecracker.Runner;

/// <summary>
/// Initializes the Firecracker runner environment: checks prerequisites, prompts for
/// configuration, downloads a kernel, builds the rootfs image, generates SSH keys, and
/// saves configuration.
/// </summary>
public class FirecrackerRunnerInitCommand : Command<FirecrackerRunnerInitCommand.Settings>
{
    private readonly IFirecrackerService _firecrackerService;
    private readonly IFirecrackerRunnerConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public class Settings : FirecrackerSettings
    {
        [CommandOption("--work-dir <PATH>")]
        [Description("Working directory for Firecracker files (default: ~/.pks-cli/firecracker)")]
        public string? WorkDir { get; set; }

        [CommandOption("--vcpus <COUNT>")]
        [Description("Default vCPU count for VMs (default: 2)")]
        public int? Vcpus { get; set; }

        [CommandOption("--mem-mib <SIZE>")]
        [Description("Default memory in MiB for VMs (default: 2048)")]
        public int? MemMib { get; set; }

        [CommandOption("--subnet <CIDR>")]
        [Description("Network subnet for VMs (default: 172.16.0.0/16)")]
        public string? Subnet { get; set; }

        [CommandOption("--skip-rootfs")]
        [Description("Skip building the rootfs image")]
        public bool SkipRootfs { get; set; }

        [CommandOption("--skip-kernel")]
        [Description("Skip downloading the kernel")]
        public bool SkipKernel { get; set; }

        [CommandOption("--dockerfile <PATH>")]
        [Description("Path to custom Dockerfile for rootfs (default: bundled Dockerfile.rootfs)")]
        public string? Dockerfile { get; set; }
    }

    public FirecrackerRunnerInitCommand(
        IFirecrackerService firecrackerService,
        IFirecrackerRunnerConfigurationService configService,
        IAnsiConsole console)
    {
        _firecrackerService = firecrackerService;
        _configService = configService;
        _console = console;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            DisplayBanner();

            // 1. Check prerequisites
            _console.MarkupLine("[bold]Checking prerequisites...[/]");
            _console.WriteLine();

            var (kvmAvailable, firecrackerInstalled, firecrackerVersion) =
                await _firecrackerService.CheckPrerequisitesAsync();

            if (!kvmAvailable)
            {
                _console.MarkupLine("[red]KVM is not available.[/]");
                _console.MarkupLine("[red]Firecracker requires KVM. Ensure your host supports hardware virtualisation[/]");
                _console.MarkupLine("[red]and that /dev/kvm is accessible (e.g. 'sudo chmod 666 /dev/kvm').[/]");
                return 1;
            }

            _console.MarkupLine("[green]KVM available[/]");

            if (!firecrackerInstalled)
            {
                _console.MarkupLine("[red]Firecracker is not installed.[/]");
                _console.MarkupLine("[red]Install Firecracker from https://github.com/firecracker-microvm/firecracker/releases[/]");
                _console.MarkupLine("[red]and ensure the 'firecracker' binary is on your PATH.[/]");
                return 1;
            }

            _console.MarkupLine($"[green]Firecracker installed ({firecrackerVersion.EscapeMarkup()})[/]");
            _console.WriteLine();

            // 2. Resolve configuration — use settings values or interactive prompts
            var defaultWorkDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".pks-cli", "firecracker");

            var workDir = settings.WorkDir
                ?? _console.Prompt(new TextPrompt<string>("Working directory:")
                    .DefaultValue(defaultWorkDir));

            var vcpus = settings.Vcpus
                ?? _console.Prompt(new TextPrompt<int>("Default vCPUs per VM:")
                    .DefaultValue(2));

            var memMib = settings.MemMib
                ?? _console.Prompt(new TextPrompt<int>("Default memory per VM (MiB):")
                    .DefaultValue(2048));

            var subnet = settings.Subnet
                ?? _console.Prompt(new TextPrompt<string>("Network subnet:")
                    .DefaultValue("172.16.0.0/16"));

            _console.WriteLine();

            // 3. Create work directory
            Directory.CreateDirectory(workDir);
            if (settings.Verbose)
                _console.MarkupLine($"[dim]Work directory: {workDir.EscapeMarkup()}[/]");

            // 4. Generate SSH keypair (if not exists)
            var sshKeyPath = Path.Combine(workDir, "vm-key");
            if (!File.Exists(sshKeyPath))
            {
                await _console.Status()
                    .SpinnerStyle(Style.Parse("cyan"))
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Generating SSH keypair...", async _ =>
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(
                            "ssh-keygen",
                            $"-t ed25519 -f {sshKeyPath} -N \"\" -q")
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        };
                        var proc = System.Diagnostics.Process.Start(psi)!;
                        await proc.WaitForExitAsync();
                        if (proc.ExitCode != 0)
                            throw new InvalidOperationException("Failed to generate SSH keypair");
                    });
                _console.MarkupLine("[green]SSH keypair generated[/]");
            }
            else
            {
                _console.MarkupLine("[dim]SSH keypair already exists, skipping[/]");
            }

            // 5. Download kernel (unless --skip-kernel)
            var kernelPath = Path.Combine(workDir, "vmlinux");
            if (!settings.SkipKernel)
            {
                if (!File.Exists(kernelPath))
                {
                    await _console.Status()
                        .SpinnerStyle(Style.Parse("cyan"))
                        .Spinner(Spinner.Known.Dots)
                        .StartAsync("Downloading Linux kernel...", async _ =>
                        {
                            kernelPath = await _firecrackerService.DownloadKernelAsync(workDir);
                        });
                    _console.MarkupLine($"[green]Kernel downloaded:[/] {kernelPath.EscapeMarkup()}");
                }
                else
                {
                    _console.MarkupLine("[dim]Kernel already exists, skipping[/]");
                }
            }
            else
            {
                _console.MarkupLine("[dim]Kernel download skipped (--skip-kernel)[/]");
            }

            // 6. Build rootfs (unless --skip-rootfs)
            var rootfsPath = Path.Combine(workDir, "rootfs.ext4");
            if (!settings.SkipRootfs)
            {
                var dockerfile = settings.Dockerfile ?? FindDockerfile();
                await _console.Status()
                    .SpinnerStyle(Style.Parse("cyan"))
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Building rootfs image (this may take a few minutes)...", async _ =>
                    {
                        rootfsPath = await _firecrackerService.BuildRootfsAsync(
                            dockerfile, rootfsPath, sshKeyPath + ".pub");
                    });
                _console.MarkupLine($"[green]Rootfs image built:[/] {rootfsPath.EscapeMarkup()}");
            }
            else
            {
                _console.MarkupLine("[dim]Rootfs build skipped (--skip-rootfs)[/]");
            }

            _console.WriteLine();

            // 7. Save configuration
            var config = await _configService.LoadAsync();
            config.Defaults = new FirecrackerDefaults
            {
                DefaultVcpus = vcpus,
                DefaultMemMib = memMib,
                KernelPath = kernelPath,
                BaseRootfsPath = rootfsPath,
                NetworkSubnet = subnet,
                WorkDir = workDir
            };
            await _configService.SaveAsync(config);

            _console.MarkupLine("[green]Configuration saved[/]");
            _console.WriteLine();

            // 8. Display summary
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Green)
                .AddColumn("[yellow]Property[/]")
                .AddColumn("[cyan]Value[/]");

            table.AddRow("Work Directory", workDir.EscapeMarkup());
            table.AddRow("vCPUs", vcpus.ToString());
            table.AddRow("Memory (MiB)", memMib.ToString());
            table.AddRow("Network Subnet", subnet.EscapeMarkup());
            table.AddRow("Kernel", kernelPath.EscapeMarkup());
            table.AddRow("Rootfs", rootfsPath.EscapeMarkup());
            table.AddRow("SSH Key", sshKeyPath.EscapeMarkup());

            var summaryPanel = new Panel(table)
                .BorderStyle(Style.Parse("green"))
                .Header("[bold green]Initialization Complete[/]");
            _console.Write(summaryPanel);
            _console.WriteLine();

            _console.MarkupLine("[dim]Run 'pks firecracker runner start' to begin processing jobs.[/]");

            return 0;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Initialization failed: {ex.Message.EscapeMarkup()}[/]");
            if (settings.Verbose)
            {
                _console.WriteException(ex);
            }
            return 1;
        }
    }

    /// <summary>
    /// Searches common locations for the bundled Dockerfile.rootfs used to build the rootfs image.
    /// </summary>
    private static string FindDockerfile()
    {
        var candidates = new[]
        {
            // Relative to CWD
            Path.Combine(Directory.GetCurrentDirectory(), "firecracker", "Dockerfile.rootfs"),
            // Same directory as the executing binary
            Path.Combine(AppContext.BaseDirectory, "firecracker", "Dockerfile.rootfs"),
            // System-wide install location
            "/usr/share/pks-cli/firecracker/Dockerfile.rootfs"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            "Could not find Dockerfile.rootfs. Provide a path explicitly with --dockerfile. " +
            $"Searched: {string.Join(", ", candidates)}");
    }

    private void DisplayBanner()
    {
        var panel = new Panel("[bold cyan]Firecracker Runner Initialization[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        _console.Write(panel);
        _console.WriteLine();
    }
}
