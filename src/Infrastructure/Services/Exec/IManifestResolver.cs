namespace PKS.Infrastructure.Services.Exec;

/// <summary>
/// Turns a manifest into the environment a child process should run with.
///
/// This is the half of the discovery protocol that touches credentials, which is why it is a service
/// and not a helper on the command: filling <c>{apikey}</c> means reading a stored key, and the command
/// layer is not allowed to. What comes back is a <see cref="ResolvedEnvironment"/>, which a command can
/// hand to a process but cannot read a secret out of.
/// </summary>
public interface IManifestResolver
{
    /// <summary>
    /// Walks the manifest's capabilities, asks the operator what is not already decided, and resolves
    /// every placeholder. Returns null when a required capability has no provider the operator is
    /// signed in to — the caller should stop rather than start something that will fail later.
    /// </summary>
    Task<ResolvedEnvironment?> ResolveAsync(PksManifest manifest, ManifestResolveOptions options);

    /// <summary>Shuts down anything the resolution started — today, the loopback identity proxy.
    /// Safe to call when nothing was started.</summary>
    void Release();
}

/// <summary>How to resolve, when the operator has already said.</summary>
public sealed class ManifestResolveOptions
{
    /// <summary>Skip the provider prompt and use this kind. Null means ask (or take the only one).</summary>
    public string? PreferredProvider { get; init; }

    /// <summary>Bind the identity proxy to a fixed port instead of a free one.</summary>
    public int? ImdsPort { get; init; }

    /// <summary>Take the defaults for every prompt. What an unattended run needs, and what makes the
    /// difference between a CI job that configures itself and one that hangs on a question.</summary>
    public bool NonInteractive { get; init; }

    /// <summary>Configure optional capabilities without asking first. `pks aspire run` sets this,
    /// because the operator asking for a run with a model is the confirmation.</summary>
    public bool AcceptOptional { get; init; }
}
