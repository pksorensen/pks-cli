namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// The shell scripts written into a job container so git can reach
/// <see cref="GitCredentialServer"/> over its bind-mounted Unix socket.
///
/// These live here rather than inline at the two call sites because they had drifted into two
/// near-identical copies, and because the important detail is easy to lose: the credential
/// helper must forward the repository git asks about. A GitHub App token is scoped to a single
/// repository, so a helper that throws git's stdin away — as both copies did — leaves the
/// server unable to mint anything narrower than "whatever the runner last guessed".
/// </summary>
public static class GitCredentialHelperScript
{
    /// <summary>Where the credential socket's directory is bind-mounted inside a job container.</summary>
    public const string SocketPath = "/var/run/pks-creds/creds.sock";

    public const string AskpassPath = "/tmp/git-askpass.sh";
    public const string HelperPath = "/tmp/git-credential-pks.sh";

    /// <summary>
    /// A git credential helper, speaking git's credential protocol properly.
    ///
    /// Git writes <c>protocol=</c>, <c>host=</c> and — when <c>credential.useHttpPath</c> is set —
    /// <c>path=owner/repo.git</c> on stdin, then a blank line. We read those instead of discarding
    /// them, and pass the host and path on so the server can scope the token it mints.
    ///
    /// <c>username</c> comes from the server when it offers one (a GitHub App installation token
    /// must be used as <c>x-access-token</c>), and falls back to that same value, which is also
    /// correct for the operator's OAuth token.
    /// </summary>
    public const string CredentialHelper =
        "#!/bin/sh\n" +
        "# Read git's credential request from stdin: protocol=, host=, path=, then a blank line.\n" +
        "HOST=github.com\n" +
        "REPO=\n" +
        "while IFS= read -r line; do\n" +
        "  [ -z \"$line\" ] && break\n" +
        "  case \"$line\" in\n" +
        "    host=*) HOST=${line#host=} ;;\n" +
        "    path=*) REPO=${line#path=} ;;\n" +
        "  esac\n" +
        "done\n" +
        "RESPONSE=$(curl -s --unix-socket " + SocketPath + " \"http://localhost/git-credential?host=$HOST&repo=$REPO\")\n" +
        "TOKEN=$(printf '%s' \"$RESPONSE\" | sed -n 's/.*\"password\":\"\\([^\"]*\\)\".*/\\1/p')\n" +
        "USER=$(printf '%s' \"$RESPONSE\" | sed -n 's/.*\"username\":\"\\([^\"]*\\)\".*/\\1/p')\n" +
        "[ -z \"$USER\" ] && USER=x-access-token\n" +
        "echo \"username=$USER\"\n" +
        "echo \"password=$TOKEN\"\n";

    /// <summary>
    /// The GIT_ASKPASS fallback, for the paths that set <c>core.askpass</c> rather than a helper.
    ///
    /// Askpass is handed a human prompt string and nothing else, so it cannot say which repository
    /// it wants. In GitHub App mode the server answers these from the repository the runner named
    /// for the job. The credential helper above is the better path and is preferred wherever both
    /// are configured.
    /// </summary>
    public const string Askpass =
        "#!/bin/sh\n" +
        "curl -s --unix-socket " + SocketPath + " \"http://localhost/git-credential?host=github.com\" " +
        "| sed -n 's/.*\"password\":\"\\([^\"]*\\)\".*/\\1/p'\n";

    /// <summary>
    /// Makes git send the repository path to the credential helper. Without this git sends only
    /// the host, and every repository on github.com looks identical to the server.
    /// </summary>
    public const string UseHttpPathConfigArgs = "config --global credential.useHttpPath true";

    /// <summary>Base64 of a script, for handing to <c>docker exec … base64 -d</c> without quoting grief.</summary>
    public static string Encode(string script) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script));
}
