using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.LogAnalytics;

[Description("Show Log Analytics configuration and connection status")]
public class LogAnalyticsStatusCommand : Command<LogAnalyticsStatusCommand.Settings>
{
    public class Settings : LogAnalyticsSettings { }

    private readonly ILogAnalyticsConfigService _configService;
    private readonly ILogAnalyticsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public LogAnalyticsStatusCommand(
        ILogAnalyticsConfigService configService,
        ILogAnalyticsQueryService queryService,
        IAnsiConsole console)
    {
        _configService = configService;
        _queryService = queryService;
        _console = console;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        if (!await _configService.IsConfiguredAsync())
        {
            _console.MarkupLine("[yellow]Log Analytics is not configured.[/]");
            _console.MarkupLine("[dim]Run [cyan]pks loganalytics init[/] to configure.[/]");
            return 0;
        }

        var config = await _configService.GetConfigAsync();
        if (config is null) return 0;

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Setting").AddColumn("Value");
        table.AddRow("Workspace ID", config.WorkspaceId);
        table.AddRow("Workspace", config.WorkspaceName.EscapeMarkupOrDim());
        table.AddRow("Resource ID", config.ResourceId.EscapeMarkupOrDim());
        table.AddRow("Subscription", config.SubscriptionId.EscapeMarkupOrDim());
        table.AddRow("Auth", "[dim]Azure AD (via pks foundry)[/]");
        table.AddRow("Configured At", config.RegisteredAt == DateTime.MinValue
            ? "[dim]unknown[/]"
            : config.RegisteredAt.ToString("yyyy-MM-dd HH:mm") + " UTC");

        _console.Write(table);
        _console.WriteLine();

        await _console.Status().StartAsync("Testing connection...", async ctx =>
        {
            var result = await _queryService.TestConnectionAsync();
            if (result.Success)
                _console.MarkupLine($"[green]Connected[/] - {(result.WorkspaceName ?? "Log Analytics workspace").EscapeMarkup()}");
            else
                _console.MarkupLine($"[red]Connection failed:[/] {(result.ErrorMessage ?? "Unknown error").EscapeMarkup()}");
        });

        return 0;
    }
}

internal static class LogAnalyticsStatusMarkupExtensions
{
    public static string EscapeMarkupOrDim(this string? value)
        => string.IsNullOrWhiteSpace(value) ? "[dim]not set[/]" : value.EscapeMarkup();
}
