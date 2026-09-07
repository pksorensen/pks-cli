using System.Text.RegularExpressions;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// The two shell scripts that let a job container ask the runner for a git credential, and
/// the fixed paths they are written to. Both are generated per container at spawn, which is
/// what makes it safe to bake a job-scoped bearer token straight into the text.
/// </summary>
public static class GitCredentialHelperScript
{
    public const string SocketPath = "/var/run/pks-creds/creds.sock";
    public const string AskpassPath = "/tmp/git-askpass.sh";
    public const string HelperPath = "/tmp/git-credential-pks.sh";

    /// <summary>
    /// A JWT out of <see cref="JobTokenService"/> is base64url plus two dots and nothing else.
    /// Anything outside that alphabet is not a token we minted, and baking it into a shell
    /// script would be an injection rather than an authorisation.
    /// </summary>
    private static readonly Regex TokenShape = new(@"\A[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\z");

    /// <summary>
    /// Prefers the environment over the baked value, so the GitHub Actions path — which
    /// already passes <c>-e PKS_TOKEN</c> on <c>docker run</c> — gets the header without
    /// having to mint anything twice. The ALP path bakes instead, because remoteEnv is not
    /// visible to the <c>docker exec</c> the job actually runs under.
    /// </summary>
    private static string TokenPreamble(string? jobToken)
    {
        var baked = "";
        if (!string.IsNullOrWhiteSpace(jobToken))
        {
            if (!TokenShape.IsMatch(jobToken))
                throw new ArgumentException("Job token is not a base64url JWT.", nameof(jobToken));
            baked = jobToken;
        }

        // set -- rather than ${TOKEN:+-H "..."}: dash honours the inner quotes, busybox ash
        // is not guaranteed to, and a split header would read as an invalid token rather
        // than as an absent one.
        return
            $"TOKEN=\"${{PKS_TOKEN:-{baked}}}\"\n" +
            "set --\n" +
            "[ -n \"$TOKEN\" ] && set -- -H \"Authorization: Bearer $TOKEN\"\n";
    }

    public static string CredentialHelperFor(string? jobToken) =>
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
        TokenPreamble(jobToken) +
        "RESPONSE=$(curl -s \"$@\" --unix-socket " + SocketPath + " \"http://localhost/git-credential?host=$HOST&repo=$REPO\")\n" +
        "TOKEN=$(printf '%s' \"$RESPONSE\" | sed -n 's/.*\"password\":\"\\([^\"]*\\)\".*/\\1/p')\n" +
        "USER=$(printf '%s' \"$RESPONSE\" | sed -n 's/.*\"username\":\"\\([^\"]*\\)\".*/\\1/p')\n" +
        "[ -z \"$USER\" ] && USER=x-access-token\n" +
        "echo \"username=$USER\"\n" +
        "echo \"password=$TOKEN\"\n";

    /// <summary>
    /// GIT_ASKPASS is handed a prompt, never a repository, so this one cannot name what it
    /// wants. It still carries the bearer: the server resolves the repository from the
    /// token's own entitlement when the request names none.
    /// </summary>
    public static string AskpassFor(string? jobToken) =>
        "#!/bin/sh\n" +
        TokenPreamble(jobToken) +
        "curl -s \"$@\" --unix-socket " + SocketPath + " \"http://localhost/git-credential?host=github.com\" " +
        "| sed -n 's/.*\"password\":\"\\([^\"]*\\)\".*/\\1/p'\n";

    public static string CredentialHelper => CredentialHelperFor(null);

    public static string Askpass => AskpassFor(null);

    public const string UseHttpPathConfigArgs = "config --global credential.useHttpPath true";

    public static string Encode(string script) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script));
}
