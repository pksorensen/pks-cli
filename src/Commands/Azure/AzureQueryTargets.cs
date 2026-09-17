using PKS.Infrastructure.Services.Azure;
using Spectre.Console;

namespace PKS.Commands.Azure;

/// <summary>
/// What <c>pks kusto</c> and <c>pks otel *</c> share when they fan out over the registry:
/// choosing the enabled entries to hit, and explaining a per-resource failure without
/// polluting stdout (which carries the Json/Csv payload).
/// </summary>
internal static class AzureQueryTargets
{
    /// <summary>A console on stderr, so per-resource errors never mix into piped output.</summary>
    public static IAnsiConsole StderrConsole()
        => AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });

    /// <summary>
    /// The entries a query should run against: every enabled one of <paramref name="kind"/>, or
    /// the subset named in <paramref name="requested"/> (by name or key, case-insensitively,
    /// duplicates collapsed). A name that is unknown or disabled is an error — it is printed
    /// with the enabled names to pick from, and null comes back so the caller exits 1.
    /// </summary>
    public static async Task<IReadOnlyList<AzureResourceEntry>?> ResolveAsync(
        IAzureResourceRegistry registry,
        AzureResourceKind kind,
        IReadOnlyList<string>? requested,
        IAnsiConsole console,
        string kindLabel,
        string initCommand)
    {
        var enabled = await registry.ListEnabledAsync(kind);
        if (enabled.Count == 0)
        {
            console.MarkupLine($"[yellow]No enabled {kindLabel.EscapeMarkup()}.[/]");
            console.MarkupLine($"[dim]Run [cyan]{initCommand.EscapeMarkup()}[/] to register one.[/]");
            return null;
        }

        var names = requested?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        if (names is null || names.Count == 0)
            return enabled;

        var chosen = new List<AzureResourceEntry>();
        foreach (var name in names)
        {
            var hit = enabled.FirstOrDefault(e =>
                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Key, name, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                console.MarkupLine($"[red]'{name.EscapeMarkup()}' is not an enabled {SingularOf(kindLabel).EscapeMarkup()}.[/]");
                console.MarkupLine($"[dim]Enabled: {string.Join(", ", enabled.Select(e => e.Name)).EscapeMarkup()}[/]");
                return null;
            }
            if (!chosen.Any(c => string.Equals(c.Key, hit.Key, StringComparison.OrdinalIgnoreCase)))
                chosen.Add(hit);
        }
        return chosen;
    }

    /// <summary>
    /// The one-line explanation of why a resource failed. An expired tenant sign-in names the
    /// tenant and the exact re-auth command for this vertical; everything else is the message.
    /// </summary>
    public static string Describe(Exception error, string initCommand)
        => error is AzureAuthExpiredException expired
            ? $"Azure sign-in for tenant {expired.TenantId} has expired. Run '{initCommand} --reauth {expired.TenantId}' to sign in again."
            : error.Message;

    public static void ReportFailure(IAnsiConsole errorConsole, string kindSingular, AzureResourceEntry resource, Exception error, string initCommand)
        => errorConsole.MarkupLine(
            $"[red]{kindSingular.EscapeMarkup()} {resource.Name.EscapeMarkup()}:[/] {Describe(error, initCommand).EscapeMarkup()}");

    private static string SingularOf(string plural)
        => plural.EndsWith("s", StringComparison.Ordinal) ? plural[..^1] : plural;
}
