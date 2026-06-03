namespace PKS.Infrastructure.Services.Security;

/// <summary>
/// Distinguishes a trusted invocation from one that came through the in-container sudo escalation
/// an agent has. In the baked devcontainer the only way an agent (user <c>node</c>) can run pks as
/// the credential-bearing <c>pks</c> user is <c>sudo -u pks /usr/local/bin/pks …</c>, which sets
/// <c>SUDO_USER</c>. A trusted enrollment from the Docker host (<c>docker exec -u pks …</c>) or at
/// image-build time (as root) does NOT set it. This lets enrollment and the not-enrolled gate be
/// kept off the agent's reach without any phone/host-key infrastructure.
/// </summary>
internal static class SecurityContext
{
    /// <summary>True when pks was invoked via <c>sudo</c> (the in-container agent/dev path).</summary>
    public static bool IsSudoInvoked =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SUDO_USER")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SUDO_UID"));

    /// <summary>How a trusted operator should enroll, shown wherever the sudo path is refused.</summary>
    public const string HostEnrollmentHint =
        "Enroll from the Docker host (the agent can't reach it):\n  docker exec -it -u pks <container> pks authenticator init";
}
