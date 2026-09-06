namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Resolves the runner's GitHub App identity from its environment.
///
/// The rule that matters here is that <c>GITHUB_APP_ID</c> declares <em>intent</em> and the key
/// declares nothing. If the mode were inferred from whether a key happens to be present, then a
/// vault approval the owner declined — or a typo in an item name — would not fail; it would
/// quietly fall back to the operator's personal token and every commit would come from a human
/// who did not make it. That is the exact outcome this feature exists to prevent, so a declared
/// App with no usable key is a hard startup failure with a message that says which variable was
/// missing.
///
/// The key is expected to arrive from a vault, not from a file someone left on the box:
///
///   vault agent run --vault vlt_... --item itm_github_app \
///     --file GITHUB_APP_PRIVATE_KEY_FILE=private_key \
///     -- pks agentics runner run --project owner/project
///
/// <c>--file</c> rather than <c>--env</c> is deliberate: it puts the key in an anonymous
/// <c>memfd</c> and passes <c>/proc/self/fd/N</c>, so the key never touches a filesystem and
/// does not sit in this process's environment block. We read it once, at startup, and keep only
/// the parsed key in memory.
/// </summary>
public static class GitHubAppConfigResolver
{
    public const string AppIdVariable = "GITHUB_APP_ID";
    public const string SlugVariable = "GITHUB_APP_SLUG";
    public const string PrivateKeyVariable = "GITHUB_APP_PRIVATE_KEY";
    public const string PrivateKeyFileVariable = "GITHUB_APP_PRIVATE_KEY_FILE";

    /// <summary>
    /// Reads the App configuration, or returns <c>null</c> when no App is declared at all —
    /// which is the ordinary case for a runner that still uses the operator's device-code token.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// An App is declared but its key or slug is missing or unreadable.
    /// </exception>
    public static GitHubAppConfig? Resolve(Func<string, string?>? readVariable = null)
    {
        var read = readVariable ?? Environment.GetEnvironmentVariable;

        var appId = read(AppIdVariable)?.Trim();
        if (string.IsNullOrEmpty(appId)) return null;   // no App declared — not an error.

        var slug = read(SlugVariable)?.Trim();
        if (string.IsNullOrEmpty(slug))
            throw new InvalidOperationException(
                $"{AppIdVariable} is set but {SlugVariable} is not. The slug is the name in " +
                "github.com/apps/<slug> — it is what the install link and the bot's commit " +
                "identity are built from, and it cannot be derived from the App ID.");

        var pem = ReadPrivateKey(read)
            ?? throw new InvalidOperationException(
                $"{AppIdVariable}={appId} declares that this runner should act as a GitHub App, but no " +
                $"private key was provided. Set {PrivateKeyFileVariable} to a path (a vault " +
                $"'agent run --file' fd is the intended source) or {PrivateKeyVariable} to the PEM itself. " +
                "Refusing to start: falling back to the operator's personal token here would silently " +
                "attribute the App's work to a human.");

        return new GitHubAppConfig(appId, slug, pem);
    }

    private static string? ReadPrivateKey(Func<string, string?> read)
    {
        // File first: it is the vault-fed path, and the one we want people to use.
        var path = read(PrivateKeyFileVariable)?.Trim();
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                // Deliberately a plain read rather than an existence check first: the intended
                // source is /proc/self/fd/N, where File.Exists is not a meaningful question and
                // the fd may only be readable once.
                var contents = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(contents)) return contents;
                throw new InvalidOperationException($"{PrivateKeyFileVariable} points at {path}, which is empty.");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"{PrivateKeyFileVariable} points at {path}, which could not be read: {ex.Message}", ex);
            }
        }

        var inline = read(PrivateKeyVariable);
        if (string.IsNullOrWhiteSpace(inline)) return null;

        // A PEM that has travelled through a .env file or a CI secret box usually arrives with
        // its newlines escaped. Unescaping is safe: a real PEM contains no backslashes.
        return inline.Contains("\\n") ? inline.Replace("\\n", "\n") : inline;
    }
}
