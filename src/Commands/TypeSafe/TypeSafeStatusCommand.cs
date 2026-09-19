using PKS.Infrastructure.Services.Security;
using PKS.Infrastructure.Services.TypeSafe;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.TypeSafe;

/// <summary>
/// Report whether a TypeSafe key is stored, which model is the default and which repositories may
/// spend it. Never prints the key.
/// Usage: pks typesafe status
/// </summary>
public class TypeSafeStatusCommand : AsyncCommand<TypeSafeSettings>
{
    private readonly ITypeSafeCredentialService _typeSafe;
    private readonly ISecretStore _secrets;
    private readonly IAnsiConsole _console;

    public TypeSafeStatusCommand(ITypeSafeCredentialService typeSafe, ISecretStore secrets, IAnsiConsole console)
    {
        _typeSafe = typeSafe ?? throw new ArgumentNullException(nameof(typeSafe));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public override async Task<int> ExecuteAsync(CommandContext context, TypeSafeSettings settings)
    {
        var panel = new Panel("[bold cyan]TypeSafe Status[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        _console.Write(panel);
        _console.WriteLine();

        var descriptor = await _secrets.DescribeAsync(TypeSafeCredentialService.ApiKeyKey);
        if (descriptor == null)
        {
            _console.MarkupLine("[yellow]No TypeSafe API key stored on this host.[/]");
            _console.MarkupLine("[cyan]Run 'pks typesafe init' to add one.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .AddColumn("[yellow]Property[/]")
            .AddColumn("[cyan]Value[/]");

        table.AddRow("Stored as", descriptor.Key);
        table.AddRow("Written", descriptor.SetAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));
        table.AddRow("Fingerprint", descriptor.Fingerprint);
        table.AddRow("Endpoint", Markup.Escape(TypeSafeCredentialService.BaseUrl));
        table.AddRow("Default model", Markup.Escape(await _typeSafe.GetDefaultModelAsync()));

        var allowed = await _typeSafe.GetAllowedRepositoriesAsync();
        table.AddRow("Allowed repos", allowed.Count == 0
            ? "[dim](none — run 'pks typesafe allow owner/repo')[/]"
            : Markup.Escape(string.Join(", ", allowed)));

        _console.Write(table);
        _console.WriteLine();

        if (settings.Verbose)
        {
            _console.MarkupLine("[dim]Jobs call [bold]POST /typesafe/systemone[/] on the credential socket; the key never leaves this host.[/]");
            _console.MarkupLine("[dim][bold]GET /typesafe/token[/] hands the raw key to a job that must run an SDK.[/]");
        }

        return 0;
    }
}
