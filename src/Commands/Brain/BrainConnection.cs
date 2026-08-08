using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Discovery;
using PKS.Infrastructure.Services.Oidc;
using Spectre.Console;

namespace PKS.Commands.Brain;

/// Shared "which receiver, and with what credential" step for the brain
/// commands that talk to one.
///
/// Push and restore had the same twenty lines of endpoint-and-token handling
/// with slightly different wording, which is how one of them ends up missing a
/// security check. The device-code panel lives here too: the prompt has to be
/// rendered by the command layer (the resolver must stay testable without a
/// console), and there is no reason for two versions of it.
internal static class BrainConnection
{
    /// Discovery, printed. Returns the endpoint plus the receiver's own limits.
    public static async Task<(BrainEndpoint Endpoint, ReceiverCapabilities Capabilities)> ResolveAsync(
        IAgenticPlatformDiscovery discovery,
        string server,
        CancellationToken ct = default)
    {
        var endpoint = await discovery.BrainAsync(server, ct);
        var caps = await discovery.CapabilitiesAsync(endpoint, ct);

        return (endpoint, caps);
    }

    /// Prints where we are pushing and, when the host said so itself, what it
    /// calls itself. A receiver that answered discovery is a receiver we know
    /// speaks the protocol; one that did not is a guess, and the user should be
    /// able to see which of the two they are looking at.
    public static void PrintTarget(BrainEndpoint endpoint, ReceiverCapabilities caps)
    {
        AnsiConsole.MarkupLine($"[grey]Endpoint  [/] [cyan]{Markup.Escape(endpoint.ApiBase)}[/]");
        if (endpoint.PlatformName is { Length: > 0 } name)
            AnsiConsole.MarkupLine($"[grey]Receiver  [/] {Markup.Escape(name)}"
                + (caps.Implementation is { Length: > 0 } impl ? $" [grey]({Markup.Escape(impl)})[/]" : ""));
        else if (!endpoint.Discovered)
            AnsiConsole.MarkupLine("[grey]Receiver  [/] [grey]no platform index — using the conventional paths[/]");
    }

    /// The Spectre panel for RFC 8628's user code. Kept identical to
    /// `pks agentics init`'s, because it is the same act and a user who has
    /// seen one should recognise the other.
    public static void ShowDeviceCode(DeviceCodePrompt prompt)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
                $"Visit:  [cyan]{Markup.Escape(prompt.BestUri)}[/]\n" +
                $"Code:   [bold yellow]{Markup.Escape(prompt.UserCode)}[/]")
            .Header("[bold]Authorize pks-cli[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 1));
        AnsiConsole.WriteLine();
    }

    /// The browser fallback's prompt. Printed even when the browser opens by
    /// itself: over SSH or in a container it will not, and the URL is then the
    /// only way through.
    public static void ShowAuthorizeUrl(string url)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
                $"Open this to sign in:\n[cyan]{Markup.Escape(url)}[/]\n\n" +
                "[grey]The browser must run on this machine — the login comes back to 127.0.0.1.[/]")
            .Header("[bold]Authorize pks-cli[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 1));
        AnsiConsole.WriteLine();
    }

    /// What to print when nothing could be resolved. Splits the two real
    /// causes: a receiver that never said how to log in, and one that did but
    /// where the user has not.
    public static void PrintNoCredential(BrainEndpoint endpoint, bool interactiveWasAllowed)
    {
        AnsiConsole.MarkupLine("[red]No credential for this receiver.[/]");
        if (endpoint.Origin.Contains("agentics.dk", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("  Sign in with [bold]pks agentics init[/].");
        }
        else if (interactiveWasAllowed)
        {
            AnsiConsole.MarkupLine(
                $"  {Markup.Escape(endpoint.Origin)} did not publish an authorization server we can use,");
            AnsiConsole.MarkupLine("  or the login did not complete. Mint an upload token there instead");
            AnsiConsole.MarkupLine($"  and pass it as [bold]--token[/] or in [bold]{BrainTokenResolver.EnvVar}[/].");
        }
        else
        {
            AnsiConsole.MarkupLine("  Run it once without [bold]--no-login[/] to sign in,");
            AnsiConsole.MarkupLine($"  or set [bold]{BrainTokenResolver.EnvVar}[/] to a minted upload token.");
        }
    }
}
