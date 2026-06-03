using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Claude;

/// <summary>
/// <c>pks claude anthropic</c> — pick a first-party Claude model and launch Claude Code against it
/// directly (real Anthropic auth, no proxy/translation needed). Included so the model-family aliases
/// are symmetric: <c>scaleway</c>/<c>mistral</c>/<c>qwen</c> for serverless, <c>codex</c> for Foundry,
/// <c>anthropic</c> for first-party Claude.
/// </summary>
[Description("Run Claude Code on a first-party Anthropic model (no proxy)")]
public sealed class ClaudeAnthropicCommand : AsyncCommand<ClaudeAnthropicCommand.Settings>
{
    private readonly IAnsiConsole _console;

    public ClaudeAnthropicCommand(IAnsiConsole console) => _console = console;

    private static readonly (string Id, string Label)[] Models =
    {
        ("claude-opus-4-8", "claude-opus-4-8 — most capable"),
        ("claude-sonnet-4-6", "claude-sonnet-4-6 — balanced workhorse"),
        ("claude-haiku-4-5", "claude-haiku-4-5 — fast & cheap"),
    };

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[model]")]
        [Description("Claude model id (skips the picker).")]
        public string? Model { get; set; }

        [CommandArgument(1, "[claudeArgs]")]
        [Description("Extra arguments passed through to the claude CLI")]
        public string[] ClaudeArgs { get; set; } = Array.Empty<string>();
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var model = settings.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            if (_console.Profile.Capabilities.Interactive)
            {
                var picked = _console.Prompt(new SelectionPrompt<(string Id, string Label)>()
                    .Title("Pick a [bold]Claude[/] model [dim](default: claude-opus-4-8)[/]:")
                    .UseConverter(m => m.Label)
                    .AddChoices(Models));
                model = picked.Id;
            }
            else
            {
                model = Models[0].Id;
            }
        }

        var psi = new ProcessStartInfo { FileName = "claude", UseShellExecute = false };
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model!);
        foreach (var a in settings.ClaudeArgs) psi.ArgumentList.Add(a);

        _console.MarkupLine($"[green]Launching Claude Code on[/] [bold]{model}[/] [dim](Anthropic)[/]");
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _console.MarkupLine("[red]Failed to start the claude CLI.[/]");
                return 1;
            }
            await proc.WaitForExitAsync();
            return proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _console.MarkupLine("[red]Could not find the [bold]claude[/] CLI on PATH.[/]");
            return 127;
        }
    }
}
