using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

public class AsciiCommand : Command<AsciiCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[TEXT]")]
        [Description("Text to convert to ASCII art")]
        public string? Text { get; set; }

        [CommandOption("-s|--style <STYLE>")]
        [Description("ASCII art style (banner, block, digital, starwars)")]
        [DefaultValue("banner")]
        public string Style { get; set; } = "banner";

        [CommandOption("-c|--color <COLOR>")]
        [Description("Color for the ASCII art")]
        [DefaultValue("cyan")]
        public string Color { get; set; } = "cyan";

        [CommandOption("--gradient")]
        [Description("Apply gradient coloring")]
        public bool UseGradient { get; set; }

        [CommandOption("--animate")]
        [Description("Animate the ASCII art")]
        public bool Animate { get; set; }
    }

    public override int Execute(CommandContext context, Settings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        
        if (string.IsNullOrEmpty(settings.Text))
        {
            settings.Text = AnsiConsole.Ask<string>("What text should I convert to [cyan]ASCII art[/]?");
        }

        if (settings.Animate)
        {
            return AnimateAsciiArt(settings);
        }

        return DisplayAsciiArt(settings);
    }

    private int DisplayAsciiArt(Settings settings)
    {
        var asciiArt = GenerateAsciiArt(settings.Text!, settings.Style);
        
        if (settings.UseGradient)
        {
            DisplayGradientArt(asciiArt, settings.Color);
        }
        else
        {
            AnsiConsole.MarkupLine($"[{settings.Color}]{asciiArt}[/]");
        }

        // Display generation info
        var infoPanel = new Panel($"""
        🎨 [bold]ASCII Art Generated![/]
        
        [dim]Text:[/] {settings.Text}
        [dim]Style:[/] {settings.Style}
        [dim]Color:[/] {settings.Color}
        {(settings.UseGradient ? "[dim]Gradient:[/] Enabled" : "")}
        """)
        .Border(BoxBorder.Rounded)
        .BorderStyle("green")
        .Header(" [bold green]🎨 Art Generation Complete[/] ");

        AnsiConsole.Write(infoPanel);

        return 0;
    }

    private int AnimateAsciiArt(Settings settings)
    {
        var frames = new[]
        {
            GenerateAsciiArt(settings.Text!, "banner"),
            GenerateAsciiArt(settings.Text!, "block"),
            GenerateAsciiArt(settings.Text!, "digital")
        };

        var colors = new[] { "cyan", "magenta", "yellow", "green" };

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Star)
            .SpinnerStyle(Style.Parse("cyan bold"))
            .Start("Generating animated ASCII art...", ctx =>
            {
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    for (int i = 0; i < frames.Length; i++)
                    {
                        ctx.Status($"Frame {i + 1}/{frames.Length} - Cycle {cycle + 1}/3");
                        
                        Console.Clear();
                        var color = colors[i % colors.Length];
                        AnsiConsole.MarkupLine($"[{color}]{frames[i]}[/]");
                        Thread.Sleep(800);
                    }
                }
            });

        AnsiConsole.MarkupLine("[green]🎬 Animation complete![/]");
        return 0;
    }

    private void DisplayGradientArt(string art, string baseColor)
    {
        var lines = art.Split('\n');
        var colors = baseColor.ToLower() switch
        {
            "cyan" => new[] { "cyan1", "cyan2", "cyan3" },
            "red" => new[] { "red1", "red2", "red3" },
            "green" => new[] { "green1", "green2", "green3" },
            "yellow" => new[] { "yellow1", "yellow2", "yellow3" },
            _ => new[] { "white", "grey", "dim" }
        };

        for (int i = 0; i < lines.Length; i++)
        {
            var colorIndex = (i * colors.Length) / Math.Max(lines.Length, 1);
            colorIndex = Math.Min(colorIndex, colors.Length - 1);
            AnsiConsole.MarkupLine($"[{colors[colorIndex]}]{lines[i]}[/]");
        }
    }

    private string GenerateAsciiArt(string text, string style)
    {
        // Simple ASCII art generation based on style
        return style.ToLower() switch
        {
            "banner" => GenerateBannerStyle(text),
            "block" => GenerateBlockStyle(text),
            "digital" => GenerateDigitalStyle(text),
            "starwars" => GenerateStarWarsStyle(text),
            _ => GenerateBannerStyle(text)
        };
    }

    private string GenerateBannerStyle(string text)
    {
        return $"""
        ╔══════════════════════════════════╗
        ║           {text.ToUpper().PadLeft(10).PadRight(18)}           ║
        ╚══════════════════════════════════╝
        """;
    }

    private string GenerateBlockStyle(string text)
    {
        var result = "";
        foreach (char c in text.ToUpper())
        {
            result += c switch
            {
                'A' => "██╗   ██╗\n██║   ██║\n██████╔╝\n██╔══██╗\n██║  ██║\n╚═╝  ╚═╝\n",
                'B' => "██████╗ \n██╔══██╗\n██████╔╝\n██╔══██╗\n██████╔╝\n╚═════╝ \n",
                _ => "███████╗\n██╔════╝\n███████╗\n╚════██║\n███████║\n╚══════╝\n"
            };
        }
        return result;
    }

    private string GenerateDigitalStyle(string text)
    {
        return $"""
        ┌─────────────────────────────────┐
        │ >>> {text.ToUpper().PadRight(24)} │
        │ [SYSTEM INITIALIZED]            │
        │ [AGENTIC MODE: ACTIVE]          │
        │ [STATUS: READY]                 │
        └─────────────────────────────────┘
        """;
    }

    private string GenerateStarWarsStyle(string text)
    {
        return $"""
        ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░
        ░░  {text.ToUpper().PadLeft(15).PadRight(25)}  ░░
        ░░      A LONG TIME AGO...       ░░
        ░░    IN A CODEBASE FAR AWAY     ░░
        ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░
        """;
    }
}