using System.ComponentModel;
using System.Net.Http.Headers;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Coolify;

/// <summary>
/// Register a Coolify instance so the runner can auto-inject deployment env vars.
/// Usage: pks coolify register https://projects.si14agents.com
/// </summary>
public class CoolifyRegisterCommand : Command<CoolifyRegisterCommand.Settings>
{
    private readonly ICoolifyConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public CoolifyRegisterCommand(
        ICoolifyConfigurationService configService,
        IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public class Settings : CoolifySettings
    {
        [CommandOption("--token <TOKEN>")]
        [Description("API token (will prompt if not provided)")]
        public string? Token { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var panel = new Panel("[bold cyan]Coolify Register[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        _console.Write(panel);
        _console.WriteLine();

        // Get URL
        var url = settings.Url;
        if (string.IsNullOrEmpty(url))
        {
            url = _console.Ask<string>("[yellow]Coolify instance URL:[/]");
        }

        // Normalize URL
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "https://" + url;
        url = url.TrimEnd('/');

        _console.MarkupLine($"[cyan]Instance:[/] {url}");

        // Get token
        var token = settings.Token;
        if (string.IsNullOrEmpty(token))
        {
            token = _console.Prompt(
                new TextPrompt<string>("[yellow]API token:[/]")
                    .Secret());
        }

        // Verify the token works
        _console.MarkupLine("[dim]Verifying connection...[/]");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await http.GetAsync($"{url}/api/v1/applications");
            if (!response.IsSuccessStatusCode)
            {
                _console.MarkupLine($"[red]Failed to connect: HTTP {(int)response.StatusCode}[/]");
                _console.MarkupLine("[yellow]Check that the URL and token are correct.[/]");
                return 1;
            }

            var body = await response.Content.ReadAsStringAsync();
            // Count applications for display
            var doc = System.Text.Json.JsonDocument.Parse(body);
            var appCount = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : 0;

            _console.MarkupLine($"[green]Connected — found {appCount} application(s)[/]");
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Connection failed: {ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        // Save
        var instance = await _configService.AddInstanceAsync(url, token);
        _console.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Green)
            .AddColumn("[yellow]Property[/]")
            .AddColumn("[cyan]Value[/]");

        table.AddRow("ID", instance.Id);
        table.AddRow("URL", instance.Url);
        table.AddRow("Registered", instance.RegisteredAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));

        _console.Write(table);
        _console.WriteLine();
        _console.MarkupLine("[green]Coolify instance registered.[/]");
        _console.MarkupLine("[cyan]The GitHub runner daemon will auto-inject COOLIFY_WEBHOOK and COOLIFY_TOKEN for matching applications.[/]");

        return 0;
    }
}
