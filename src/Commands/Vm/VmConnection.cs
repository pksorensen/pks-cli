using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;

namespace PKS.Commands.Vm;

/// <summary>
/// All the bits the agent in this devcontainer needs to SSH into a VM.
/// </summary>
public record VmConnectionInfo(
    string Provider,
    string Name,
    string Host,
    int Port,
    string User,
    string KeyPath,
    string? TailscaleIp = null);

/// <summary>
/// Shared, provider-agnostic helpers for bringing a VM up, registering it as an SSH
/// target, and showing connection info. Used by <c>vm start</c>, <c>vm status</c>,
/// <c>vm add-ssh-key</c> and <c>vm tailscale</c>.
/// </summary>
public static class VmConnection
{
    /// <summary>Local private-key path pks uses for a VM, or empty string if it doesn't exist.</summary>
    public static string KeyPathFor(string vmName)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pks-cli", "keys", vmName);
        return File.Exists(path) ? path : string.Empty;
    }

    /// <summary>
    /// Ensure the VM is running and SSH-reachable: start it if stopped, poll for a public
    /// IP, then TCP-probe port 22. Returns the resolved public IP (may be empty on timeout).
    /// </summary>
    public static async Task<string> EnsureReachableAsync(
        AzureVmRecord record,
        IVmProvider provider,
        IAzureVmService vmService,
        IAnsiConsole console,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));

        var status = await provider.GetStatusAsync(record);
        if (status != VmPowerState.Running)
        {
            // Start OUTSIDE any Status spinner — the guarded provider may prompt for a two-factor
            // code, and Spectre forbids interactive prompts inside a live display.
            await provider.StartAsync(record);
            console.MarkupLine($"[dim]Starting {Markup.Escape(record.VmName)}…[/]");
        }

        var ip = record.PublicIpAddress;
        await console.Status()
            .SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
            .StartAsync("Waiting for a public IP...", async _ =>
            {
                while (DateTime.UtcNow < deadline)
                {
                    var resolved = await provider.GetPublicIpAsync(record);
                    if (!string.IsNullOrEmpty(resolved)) { ip = resolved; break; }
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            });

        if (!string.IsNullOrEmpty(ip))
        {
            record.PublicIpAddress = ip;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining < TimeSpan.FromSeconds(20)) remaining = TimeSpan.FromSeconds(20);
            await console.Status()
                .SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
                .StartAsync("Waiting for SSH (port 22)...", async _ =>
                {
                    await vmService.WaitForSshAsync(ip, record.Port(), remaining);
                });
        }

        return ip;
    }

    /// <summary>
    /// Register (or refresh) the SSH target so <c>pks ssh connect &lt;name&gt;</c> and
    /// <c>pks claude --ssh-target &lt;name&gt;</c> work by label. No-op host if blank.
    /// </summary>
    public static async Task RegisterTargetAsync(ISshTargetConfigurationService sshTargets, AzureVmRecord record)
    {
        if (string.IsNullOrEmpty(record.PublicIpAddress)) return;
        var keyPath = string.IsNullOrEmpty(record.SshKeyPath) ? KeyPathFor(record.VmName) : record.SshKeyPath;
        await sshTargets.AddTargetAsync(record.PublicIpAddress, record.AdminUsername, record.Port(), keyPath, label: record.VmName);
    }

    public static VmConnectionInfo ToConnectionInfo(AzureVmRecord record, string providerName, string? tailscaleIp = null)
    {
        var keyPath = string.IsNullOrEmpty(record.SshKeyPath) ? KeyPathFor(record.VmName) : record.SshKeyPath;
        return new VmConnectionInfo(providerName, record.VmName, record.PublicIpAddress, record.Port(),
            record.AdminUsername, keyPath, tailscaleIp);
    }

    /// <summary>Render the copy-friendly connection panel.</summary>
    public static void RenderConnectionPanel(IAnsiConsole console, VmConnectionInfo info)
    {
        var hasKey = !string.IsNullOrEmpty(info.KeyPath) && File.Exists(info.KeyPath);
        var keyArg = hasKey ? $"-i \"{info.KeyPath}\" " : string.Empty;
        var sshCmd = $"ssh {keyArg}-o StrictHostKeyChecking=no -p {info.Port} {info.User}@{info.Host}";

        var lines = new List<string>
        {
            $"[cyan1]Name:[/]      {Markup.Escape(info.Name)}",
            $"[cyan1]Provider:[/]  {Markup.Escape(info.Provider)}",
            $"[cyan1]Host:[/]      {Markup.Escape(string.IsNullOrEmpty(info.Host) ? "(no public IP)" : info.Host)}",
            $"[cyan1]Port:[/]      {info.Port}",
            $"[cyan1]User:[/]      {Markup.Escape(info.User)}",
            $"[cyan1]SSH key:[/]   {Markup.Escape(hasKey ? info.KeyPath : "(none locally)")}",
        };
        if (!string.IsNullOrEmpty(info.TailscaleIp))
            lines.Add($"[cyan1]Tailnet IP:[/] {Markup.Escape(info.TailscaleIp)}");

        lines.Add(string.Empty);
        lines.Add($"[dim]SSH:[/] {Markup.Escape(sshCmd)}");
        if (!string.IsNullOrEmpty(info.TailscaleIp))
            lines.Add($"[dim]Tailnet SSH:[/] ssh {Markup.Escape(info.User)}@{Markup.Escape(info.Name)}  [dim](MagicDNS)[/]");
        lines.Add($"[dim]Connect:[/] pks ssh connect {Markup.Escape(info.Name)}");
        lines.Add($"[dim]Launch Claude:[/] pks claude --ssh-target {Markup.Escape(info.Name)}");
        if (!hasKey)
            lines.Add($"[yellow]No local private key — run [bold]pks vm add-ssh-key[/] to paste it.[/]");

        console.Write(new Panel(string.Join("\n", lines))
            .Border(BoxBorder.Rounded)
            .BorderStyle(string.IsNullOrEmpty(info.Host) ? "yellow" : "green")
            .Header($" [bold green]Connection — {Markup.Escape(info.Name)}[/] "));
    }
}

internal static class AzureVmRecordPortExtensions
{
    /// <summary>SSH port for the record. We use 22 everywhere today; centralised for future per-VM ports.</summary>
    public static int Port(this AzureVmRecord _) => 22;
}

/// <summary>Parsing for pasted SSH private keys.</summary>
public static class SshKeyText
{
    /// <summary>
    /// Read a PEM/OpenSSH private key from a line source, stopping automatically at the
    /// "-----END … PRIVATE KEY-----" line (so a paste works without a sentinel). Lines
    /// before "-----BEGIN" are ignored (prompt echo / blanks). Returns null if no valid
    /// BEGIN…END block is found.
    /// </summary>
    public static string? ReadPrivateKey(Func<string?> nextLine)
    {
        var sb = new System.Text.StringBuilder();
        var started = false;
        string? line;
        while ((line = nextLine()) != null)
        {
            var t = line.TrimEnd();
            if (!started)
            {
                if (t.Contains("-----BEGIN") && t.Contains("PRIVATE KEY-----"))
                {
                    started = true;
                    sb.Append(t).Append('\n');
                }
                continue; // skip anything before BEGIN
            }
            sb.Append(t).Append('\n');
            if (t.Contains("-----END") && t.Contains("PRIVATE KEY-----")) break;
        }
        var text = sb.ToString();
        return started && text.Contains("-----END") ? text : null;
    }
}

/// <summary>Shared VM picker: resolve by name argument, auto-select when only one, else prompt.</summary>
public static class VmSelection
{
    public static AzureVmRecord? Pick(IAnsiConsole console, List<AzureVmRecord> vms, string? nameArg, string title)
    {
        if (!string.IsNullOrWhiteSpace(nameArg))
            return vms.FirstOrDefault(v => string.Equals(v.VmName, nameArg, StringComparison.OrdinalIgnoreCase));

        if (vms.Count == 1) return vms[0];

        var choice = console.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .HighlightStyle(Style.Parse("cyan"))
                .PageSize(15)
                .AddChoices(vms.Select(v => v.VmName)));
        return vms.First(v => v.VmName == choice);
    }
}
