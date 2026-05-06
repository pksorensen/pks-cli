using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Voice;

[Description("Browse past voice dictations and re-inject the selected text")]
public class VoiceShowCommand : AsyncCommand<VoiceShowCommand.Settings>
{
    private readonly IAnsiConsole _console;

    public VoiceShowCommand(IAnsiConsole console)
    {
        _console = console;
    }

    public class Settings : VoiceSettings
    {
        [CommandOption("--count|-n")]
        [Description("Number of recent dictations to show (default: 30)")]
        public int Count { get; set; } = 30;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var historyFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".heypoul_history.jsonl");

        if (!File.Exists(historyFile))
        {
            _console.MarkupLine("[dim]No dictation history yet. Use [bold]pks voice start[/] and say something.[/]");
            return 0;
        }

        var entries = await LoadHistoryAsync(historyFile, settings.Count);
        if (entries.Count == 0)
        {
            _console.MarkupLine("[dim]History file is empty.[/]");
            return 0;
        }

        var choices = entries
            .Select(e =>
            {
                var ts = e.Timestamp.ToLocalTime().ToString("MM-dd HH:mm");
                var icon = e.IsCommand ? "⚡" : "💬";
                var preview = e.Text.Length > 60 ? e.Text[..57] + "…" : e.Text;
                return $"{icon} {ts}  {preview}";
            })
            .ToList();

        var selected = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select a dictation to re-inject:[/]")
                .PageSize(15)
                .AddChoices(choices));

        var idx = choices.IndexOf(selected);
        if (idx < 0) return 0;

        var entry = entries[idx];
        var textToInject = entry.Injected ?? entry.Text;

        _console.MarkupLine($"[dim]Injecting: {Markup.Escape(textToInject)}[/]");

        // Write to clipboard via platform-appropriate method, then also print for piping.
        await TryWriteClipboardAsync(textToInject);

        // Also print so the user can pipe or copy manually.
        _console.WriteLine(textToInject);
        return 0;
    }

    private static async Task<List<HistoryEntry>> LoadHistoryAsync(string path, int maxEntries)
    {
        var lines = await File.ReadAllLinesAsync(path);
        var results = new List<HistoryEntry>();

        foreach (var line in lines.Reverse())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<HistoryEntry>(line, JsonOptions);
                if (entry != null) results.Add(entry);
            }
            catch { /* skip malformed lines */ }

            if (results.Count >= maxEntries) break;
        }

        results.Reverse(); // chronological order (oldest first)
        return results;
    }

    private static async Task TryWriteClipboardAsync(string text)
    {
        // Best-effort: try platform clipboard tools silently.
        string? tool = null;
        string? args = null;

        if (OperatingSystem.IsWindows())
        {
            tool = "powershell";
            args = $"-Command \"Set-Clipboard -Value '{text.Replace("'", "''")}'\"";
        }
        else if (OperatingSystem.IsMacOS())
        {
            tool = "pbcopy";
        }
        else
        {
            // Linux: try xclip then xsel
            foreach (var candidate in new[] { "xclip -selection clipboard", "xsel --clipboard --input" })
            {
                var parts = candidate.Split(' ', 2);
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(parts[0])
                    {
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                    };
                    if (parts.Length > 1)
                        foreach (var a in parts[1].Split(' '))
                            psi.ArgumentList.Add(a);
                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p != null)
                    {
                        await p.StandardInput.WriteAsync(text);
                        p.StandardInput.Close();
                        await p.WaitForExitAsync();
                        return;
                    }
                }
                catch { /* try next */ }
            }
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(tool!)
            {
                RedirectStandardInput = (tool != "powershell"),
                UseShellExecute = false,
            };
            if (args != null) psi.ArgumentList.Add(args);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p != null)
            {
                if (tool != "powershell")
                {
                    await p.StandardInput.WriteAsync(text);
                    p.StandardInput.Close();
                }
                await p.WaitForExitAsync();
            }
        }
        catch { /* clipboard is best-effort */ }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class HistoryEntry
    {
        public DateTime Timestamp { get; set; }
        public string Text { get; set; } = "";
        public string? Injected { get; set; }
        public bool IsCommand { get; set; }
    }
}
