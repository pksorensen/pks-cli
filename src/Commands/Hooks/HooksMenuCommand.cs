using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Hooks;

/// <summary>
/// Default command for `pks hooks` — interactive TUI for configuring hook behaviour.
/// </summary>
public class HooksMenuCommand : AsyncCommand<HooksSettings>
{
    private readonly IConfigurationService _config;
    private readonly IHooksService _hooksService;

    public HooksMenuCommand(IConfigurationService config, IHooksService hooksService)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _hooksService = hooksService ?? throw new ArgumentNullException(nameof(hooksService));
    }

    public override async Task<int> ExecuteAsync(CommandContext context, HooksSettings settings)
    {
        AnsiConsole.Write(new Rule("[cyan]PKS Hooks[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.WriteLine();

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What would you like to configure?")
                .AddChoices("Quality (lint check on stop)", "Exit"));

        if (choice.StartsWith("Exit"))
        {
            return 0;
        }

        return await ConfigureQualityAsync(settings);
    }

    private async Task<int> ConfigureQualityAsync(HooksSettings settings)
    {
        var current = await _config.GetAsync("hooks:quality:lint_command");
        var isEnabled = !string.IsNullOrWhiteSpace(current);

        AnsiConsole.WriteLine();

        if (isEnabled)
        {
            AnsiConsole.MarkupLine($"[green]Currently enabled:[/] {current}");
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]Currently disabled[/]");
        }

        AnsiConsole.WriteLine();

        var choices = isEnabled
            ? new[] { "Change command", "Disable", "Cancel" }
            : new[] { "Enable", "Cancel" };

        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Quality lint check:")
                .AddChoices(choices));

        if (action == "Cancel")
        {
            return 0;
        }

        if (action == "Disable")
        {
            await _config.DeleteAsync("hooks:quality:lint_command");
            AnsiConsole.MarkupLine("[yellow]✓ Lint check disabled — Claude will stop without running lint.[/]");

            return 0;
        }

        // Enable or Change — pick a lint command
        var presets = new[]
        {
            "npm run lint",
            "dotnet build --no-restore",
            "npx eslint .",
            "Custom…",
        };

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select lint command:")
                .AddChoices(presets));

        string lintCmd;
        if (selected == "Custom…")
        {
            lintCmd = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter lint command:")
                    .PromptStyle("cyan"));
        }
        else
        {
            lintCmd = selected;
        }

        await _config.SetAsync("hooks:quality:lint_command", lintCmd);

        // Ensure Stop hook is registered in .claude/settings.json
        var hookInstalled = await EnsureStopHookAsync(settings);

        AnsiConsole.WriteLine();

        var panel = new Panel(
            $"[green]Lint command saved:[/] {lintCmd}\n\n" +
            (hookInstalled
                ? "[dim]Stop hook is active — Claude will run lint before finishing.[/]"
                : "[yellow]Run [cyan]pks hooks init[/] to activate the Stop hook in this project.[/]"))
        {
            Header = new PanelHeader("[cyan]Quality check configured[/]"),
            Border = BoxBorder.Rounded,
        };

        AnsiConsole.Write(panel);

        return 0;
    }

    private async Task<bool> EnsureStopHookAsync(HooksSettings settings)
    {
        try
        {
            var claudeSettingsPath = settings.Scope switch
            {
                SettingsScope.User => Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude", "settings.json"),
                _ => Path.Combine(Directory.GetCurrentDirectory(), ".claude", "settings.json"),
            };

            if (File.Exists(claudeSettingsPath))
            {
                var text = await File.ReadAllTextAsync(claudeSettingsPath);

                if (text.Contains("pks hooks stop"))
                {
                    return true;
                }
            }

            // Stop hook not present — initialize it
            await _hooksService.InitializeClaudeCodeHooksAsync(false, settings.Scope);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
