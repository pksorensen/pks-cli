using System.Text.RegularExpressions;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Composes the clone URL for a repository the runner has to check out, embedding the GitHub token in
/// the userinfo the way git wants it.
///
/// This lives in the infrastructure layer because the result *is* a credential: anything holding
/// <c>https://x-access-token:ghs_…@github.com/…</c> is holding the token. The runner command used to
/// build the string itself, which put a live token in a command-layer local where the source-scanning
/// gate cannot see it and one <c>MarkupLine($"Cloning {gitUrl}")</c> would have leaked it. Now the
/// command hands over a <see cref="SecretValue"/> and gets a <see cref="SecretValue"/> back — it can
/// pass the URL to the spawner, but it cannot print it.
/// </summary>
public static class GitCloneUrl
{
    /// <summary>
    /// Builds the authenticated clone URL for <paramref name="repository"/>, which may be either a
    /// full http(s) URL or <c>owner/repo</c> shorthand. Without a credential the URL is returned as-is
    /// — public clones still work, private ones fail at git rather than here.
    /// </summary>
    public static SecretValue ForRepository(string repository, SecretValue token)
    {
        if (string.IsNullOrEmpty(repository)) return SecretValue.None;

        var credentials = token.HasValue ? $"x-access-token:{token.Reveal()}@" : string.Empty;

        if (repository.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            repository.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            // Embed only when the URL carries no userinfo of its own. Without this guard a URL like
            // http://x-access-token:T1@host/… gets a second credential prepended, producing two `@`
            // separators, which libcurl rejects with "Port number was not a decimal number".
            var alreadyHasCredentials = Regex.IsMatch(repository, @"^https?://[^/]*@");
            if (alreadyHasCredentials || credentials.Length == 0) return SecretValue.From(repository);

            return SecretValue.From(Regex.Replace(repository, @"^(https?://)", $"$1{credentials}"));
        }

        var suffix = repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? string.Empty : ".git";
        return SecretValue.From($"https://{credentials}github.com/{repository}{suffix}");
    }

    /// <summary>
    /// The same URL with the credential replaced by <c>***</c>, for logs and progress messages. Taking
    /// a <see cref="SecretValue"/> and returning a plain string is safe precisely because the
    /// credential is gone from the result.
    /// </summary>
    public static string Redact(SecretValue url)
        => url.HasValue
            ? Regex.Replace(url.Reveal()!, @"(https?://)([^@]+)@", "$1***@")
            : string.Empty;
}
