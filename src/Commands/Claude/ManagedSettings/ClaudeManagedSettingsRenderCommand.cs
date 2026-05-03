using PKS.Infrastructure.Services.Claude;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Claude.ManagedSettings;

[Description("Render Claude Code managed-settings.json from registered marketplaces")]
public class ClaudeManagedSettingsRenderCommand : AsyncCommand<ClaudeManagedSettingsRenderCommand.Settings>
{
    private readonly IClaudeMarketplaceConfigurationService _configService;
    private readonly IClaudeManagedSettingsRenderer _renderer;
    private readonly IAnsiConsole _console;

    public class Settings : CommandSettings
    {
        [CommandOption("--output <PATH>")]
        [Description("Write output to file instead of stdout")]
        public string? OutputPath { get; set; }
    }

    public ClaudeManagedSettingsRenderCommand(
        IClaudeMarketplaceConfigurationService configService,
        IClaudeManagedSettingsRenderer renderer,
        IAnsiConsole console)
    {
        _configService = configService;
        _renderer = renderer;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var config = await _configService.LoadAsync();
        var json = _renderer.Render(config);

        if (!string.IsNullOrEmpty(settings.OutputPath))
        {
            var dir = Path.GetDirectoryName(settings.OutputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(settings.OutputPath, json);
            _console.MarkupLine($"[green]Managed settings written to {settings.OutputPath.EscapeMarkup()}[/]");
        }
        else
        {
            _console.WriteLine(json);
        }

        return 0;
    }
}
