using System.ComponentModel;
using PKS.Infrastructure.Services.Acs;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Acs;

[Description("Send one SMS through the configured Communication Services sender")]
public class AcsSmsSendCommand : Command<AcsSmsSendCommand.Settings>
{
    public class Settings : AcsSettings
    {
        [CommandArgument(0, "[MESSAGE]")]
        [Description("The text to send. Read from stdin when omitted.")]
        public string? Message { get; set; }
    }

    private readonly IAcsSmsService _sms;
    private readonly IAzureResourceRegistry _registry;
    private readonly IAnsiConsole _console;

    public AcsSmsSendCommand(IAcsSmsService sms, IAzureResourceRegistry registry, IAnsiConsole console)
    {
        _sms = sms;
        _registry = registry;
        _console = console;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var message = settings.Message;
        if (string.IsNullOrWhiteSpace(message) && System.Console.IsInputRedirected)
            message = await System.Console.In.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(message))
        {
            _console.MarkupLine("[red]Nothing to send.[/] Pass the text as an argument or pipe it on stdin.");
            return 1;
        }

        var defaults = await _sms.GetDefaultsAsync();
        var interactive = _console.Profile.Capabilities.Interactive;

        string? to = defaults.Recipient;
        if (interactive)
        {
            var prompt = new TextPrompt<string>("Recipient [dim](E.164)[/]:")
                .Validate(v => AcsSmsService.IsE164(v.Trim()) ? ValidationResult.Success() : ValidationResult.Error("[red]Use E.164: a plus sign and 7–15 digits[/]"));
            if (to != null) prompt.DefaultValue(to);
            to = _console.Prompt(prompt).Trim();
        }
        if (string.IsNullOrWhiteSpace(to))
        {
            _console.MarkupLine("[red]No recipient configured.[/] Run [cyan]pks acs init[/] first.");
            return 1;
        }

        string? from = defaults.From?.Key;
        var enabled = await _registry.ListEnabledAsync(AzureResourceKind.CommunicationServices);
        if (from == null && enabled.Count == 1) from = enabled[0].Key;
        if (from == null && enabled.Count > 1 && interactive)
        {
            from = _console.Prompt(new SelectionPrompt<AzureResourceEntry>()
                .Title("Send from:")
                .UseConverter(e => e.Name.EscapeMarkup())
                .AddChoices(enabled)).Key;
        }
        if (from == null)
        {
            _console.MarkupLine("[red]No sender enabled.[/] Run [cyan]pks acs init[/] first.");
            return 1;
        }

        var result = await _console.Status().StartAsync("Sending...", _ => _sms.SendAsync(message, to, from));
        if (!result.Ok)
        {
            _console.MarkupLine($"[red]Send failed:[/] {(result.Error ?? "unknown error").EscapeMarkup()}");
            return 1;
        }

        _console.MarkupLine($"[green]✓ Sent to {to.EscapeMarkup()} from {from.EscapeMarkup()}[/] [dim](message id {(result.MessageId ?? "-").EscapeMarkup()}{(result.Truncated ? $", truncated to {AcsSmsService.MaxMessageLength} characters" : "")})[/]");
        return 0;
    }
}
