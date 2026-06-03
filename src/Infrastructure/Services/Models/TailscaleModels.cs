namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Tailscale join credentials persisted under "tailscale.auth.credentials". Uses a
/// pre-generated auth key (tskey-…); <c>pks vm tailscale</c> runs <c>tailscale up</c>
/// with these settings on the target VM.
/// </summary>
public class TailscaleStoredCredentials
{
    public string AuthKey { get; set; } = string.Empty;
    public bool EnableSsh { get; set; } = true;
    public bool AcceptRoutes { get; set; } = true;
    public bool AdvertiseExitNode { get; set; } = true;
    /// <summary>Optional control-plane URL for self-hosted Headscale (null = Tailscale SaaS).</summary>
    public string? LoginServer { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
