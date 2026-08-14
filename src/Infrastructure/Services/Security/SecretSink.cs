using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;

namespace PKS.Infrastructure.Services.Security;

/// <summary>
/// The sanctioned destinations for a credential.
///
/// Most command-layer code that legitimately touches a token does one of four things with it: puts it
/// in a child process's environment, adds it to a docker run line, signs an HTTP request with it, or
/// hands it to an ssh session. None of those needs the command to hold a string — they need the
/// credential to arrive somewhere. These helpers take a <see cref="SecretValue"/> and do the arriving,
/// so a command can authenticate a request without ever naming <c>Reveal</c> and without the gate test
/// having to make an exception for it.
///
/// Absent credentials are a no-op everywhere here rather than an empty value: setting
/// <c>ANTHROPIC_FOUNDRY_API_KEY=</c> or sending <c>Authorization: Bearer</c> makes a missing credential
/// look like a broken one, and the resulting 401 sends whoever is debugging it after the wrong problem.
/// Every method returns whether it wrote anything, so callers that need to branch still can.
/// </summary>
public static class SecretSink
{
    /// <summary>Sets an environment variable on a dictionary destined for a child process.</summary>
    public static bool SetEnvironmentVariable(IDictionary<string, string> environment, string name, SecretValue secret)
    {
        if (!secret.HasValue) return false;
        environment[name] = secret.Reveal()!;
        return true;
    }

    /// <summary>Sets an environment variable on a <see cref="ProcessStartInfo"/> — the shape
    /// <c>psi.Environment[...] = ...</c> used by the voice and proxy commands.</summary>
    public static bool SetEnvironmentVariable(ProcessStartInfo startInfo, string name, SecretValue secret)
    {
        if (!secret.HasValue) return false;
        startInfo.Environment[name] = secret.Reveal();
        return true;
    }

    /// <summary>
    /// Appends a <c>-e NAME=value</c> fragment to a docker/devcontainer command line.
    ///
    /// The value is single-quoted and internal quotes escaped, because this one goes through a shell:
    /// unquoted, a credential containing a space or a <c>$</c> would either split into extra arguments
    /// or be expanded by the remote shell, and the failure mode of the second is a partial credential
    /// in someone's history file.
    /// </summary>
    public static bool AppendDockerEnvArgument(StringBuilder commandLine, string name, SecretValue secret)
    {
        if (!secret.HasValue) return false;
        commandLine.Append($"-e {name}='{secret.Reveal()!.Replace("'", "'\\''")}' ");
        return true;
    }

    /// <summary>Signs one request with a bearer token.</summary>
    public static bool SetBearerToken(HttpRequestMessage request, SecretValue secret, string scheme = "Bearer")
    {
        if (!secret.HasValue) return false;
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, secret.Reveal());
        return true;
    }

    /// <summary>Signs every request from a client with a bearer token.</summary>
    public static bool SetBearerToken(HttpClient client, SecretValue secret, string scheme = "Bearer")
    {
        if (!secret.HasValue) return false;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, secret.Reveal());
        return true;
    }

    /// <summary>Adds a credential to an outgoing form body — the shape of every OAuth token-endpoint
    /// exchange, where <c>refresh_token</c> is a form field rather than a header.</summary>
    public static bool SetFormField(IDictionary<string, string> form, string name, SecretValue secret)
    {
        if (!secret.HasValue) return false;
        form[name] = secret.Reveal()!;
        return true;
    }
}
