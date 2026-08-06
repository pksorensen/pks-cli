using System.ComponentModel;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Consent;

[Description("Deny a consent request")]
public class ConsentDenyCommand : Command<ConsentDenyCommand.Settings>
{
    private readonly IConsentStore _store;
    private readonly IAnsiConsole _console;

    public ConsentDenyCommand(IConsentStore store, IAnsiConsole console)
    {
        _store = store;
        _console = console;
    }

    public class Settings : ConsentSettings
    {
        [CommandArgument(0, "<id>")]
        [Description("Consent request id")]
        public string Id { get; set; } = string.Empty;

        [CommandOption("--reason")]
        [Description("Reason shown to whoever asked")]
        public string? Reason { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        try
        {
            var denied = await _store.DenyAsync(settings.Id, settings.Reason);
            _console.MarkupLine($"[red]Denied[/] {Markup.Escape(denied.Id)} [dim]({Markup.Escape(denied.ActionId)})[/].");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            _console.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
