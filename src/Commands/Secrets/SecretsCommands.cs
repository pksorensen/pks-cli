using System.ComponentModel;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Secrets;

/// <summary>
/// The user-facing half of the credential quarantine. Everything under <c>pks secrets</c> is built on
/// <see cref="ISecretStore"/>, which has no getter — so these commands can tell you what is stored,
/// when, and whether two machines hold the same value, and there is no command anywhere that prints
/// the value itself. Losing access to a credential is a re-login, not a lookup: that is the trade the
/// design makes on purpose.
/// </summary>
public class SecretsSettings : CommandSettings
{
}

public class SecretsKeySettings : SecretsSettings
{
    [CommandArgument(0, "<key>")]
    [Description("Configuration key the credential is stored under, e.g. github.auth.token")]
    public string Key { get; set; } = string.Empty;
}

public class SecretsSeedHomeSettings : SecretsKeySettings
{
    [CommandOption("--home <DIRECTORY>")]
    [Description("Target HOME whose .pks-cli store should receive the credential")]
    public string Home { get; set; } = string.Empty;
}

/// <summary>
/// Seeds one credential into another HOME's store — the sanctioned replacement for reaching into
/// <c>~/.pks-cli/settings.json</c> and lifting a value out of it, which is what the Aspire AppHost
/// used to do to give the ALP runner a Foundry session. The command never sees the plaintext:
/// <see cref="ISecretSeedingService"/> resolves and re-encrypts it.
/// </summary>
public class SecretsSeedHomeCommand : AsyncCommand<SecretsSeedHomeSettings>
{
    private readonly IAnsiConsole _console;
    private readonly ISecretSeedingService _seeding;

    public SecretsSeedHomeCommand(IAnsiConsole console, ISecretSeedingService seeding)
    {
        _console = console;
        _seeding = seeding;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, SecretsSeedHomeSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Home))
        {
            _console.MarkupLine("[red]--home is required.[/]");
            return 1;
        }

        if (await _seeding.SeedIntoHomeAsync(settings.Key, settings.Home))
        {
            _console.MarkupLine($"[green]Seeded[/] [cyan]{settings.Key.EscapeMarkup()}[/] into [dim]{settings.Home.EscapeMarkup()}/.pks-cli[/].");
            return 0;
        }

        _console.MarkupLine($"[yellow]Nothing stored under[/] [cyan]{settings.Key.EscapeMarkup()}[/] [yellow]— nothing to seed.[/]");
        return 1;
    }
}

public class SecretsListCommand : AsyncCommand<SecretsSettings>
{
    private readonly IAnsiConsole _console;
    private readonly ISecretStore _store;

    public SecretsListCommand(IAnsiConsole console, ISecretStore store)
    {
        _console = console;
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, SecretsSettings settings)
    {
        var secrets = await _store.ListAsync();

        if (secrets.Count == 0)
        {
            _console.MarkupLine("[dim]No credentials stored. Sign in with the relevant command (e.g. [cyan]pks github login[/]).[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Key");
        table.AddColumn("Stored");
        table.AddColumn("Fingerprint");

        foreach (var secret in secrets.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            table.AddRow(
                $"[cyan]{secret.Key.EscapeMarkup()}[/]",
                $"[dim]{secret.SetAt.ToLocalTime():yyyy-MM-dd HH:mm}[/]",
                $"[dim]{secret.Fingerprint}[/]");
        }

        _console.Write(table);
        _console.MarkupLine("[dim]Fingerprints are HMACs keyed by this machine's KEK — the same value on another machine fingerprints differently.[/]");
        return 0;
    }
}

public class SecretsStatusCommand : AsyncCommand<SecretsKeySettings>
{
    private readonly IAnsiConsole _console;
    private readonly ISecretStore _store;

    public SecretsStatusCommand(IAnsiConsole console, ISecretStore store)
    {
        _console = console;
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, SecretsKeySettings settings)
    {
        var descriptor = await _store.DescribeAsync(settings.Key);
        if (descriptor is null)
        {
            _console.MarkupLine($"[yellow]No credential stored under[/] [cyan]{settings.Key.EscapeMarkup()}[/].");
            return 1;
        }

        _console.MarkupLine($"[green]Stored[/] [cyan]{descriptor.Key.EscapeMarkup()}[/]");
        _console.MarkupLine($"  [dim]written:[/]     {descriptor.SetAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        _console.MarkupLine($"  [dim]fingerprint:[/] {descriptor.Fingerprint}");
        return 0;
    }
}

public class SecretsDeleteCommand : AsyncCommand<SecretsKeySettings>
{
    private readonly IAnsiConsole _console;
    private readonly ISecretStore _store;

    public SecretsDeleteCommand(IAnsiConsole console, ISecretStore store)
    {
        _console = console;
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, SecretsKeySettings settings)
    {
        if (await _store.DeleteAsync(settings.Key))
        {
            _console.MarkupLine($"[green]Removed[/] [cyan]{settings.Key.EscapeMarkup()}[/].");
            return 0;
        }

        _console.MarkupLine($"[yellow]Nothing stored under[/] [cyan]{settings.Key.EscapeMarkup()}[/].");
        return 1;
    }
}
