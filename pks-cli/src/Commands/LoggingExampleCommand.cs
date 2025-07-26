using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Commands;
using PKS.Infrastructure.Services.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands;

/// <summary>
/// Example command demonstrating the comprehensive logging system
/// </summary>
[Description("Demonstrates the PKS CLI logging system capabilities")]
public class LoggingExampleCommand : LoggingCommandBase<LoggingExampleSettings>
{
    public LoggingExampleCommand(
        ILogger<LoggingExampleCommand> logger,
        ILoggingOrchestrator loggingOrchestrator)
        : base(logger, loggingOrchestrator)
    {
    }

    protected override async Task<int> ExecuteCommandAsync(CommandContext context, LoggingExampleSettings settings)
    {
        AnsiConsole.MarkupLine("[cyan]🔍 PKS CLI Logging System Demo[/]");
        AnsiConsole.WriteLine();

        try
        {
            // Demo 1: Feature usage logging
            await DemoFeatureUsageLogging(settings);

            // Demo 2: User interaction logging
            await DemoUserInteractionLogging(settings);

            // Demo 3: Error handling and logging
            if (settings.SimulateError)
            {
                await DemoErrorLogging();
            }

            // Demo 4: Performance-intensive operation
            await DemoPerformanceLogging(settings);

            // Demo 5: Multiple feature usage
            await DemoMultipleFeatures(settings);

            AnsiConsole.MarkupLine("[green]✅ Logging demo completed successfully![/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]❌ Demo failed: {ex.Message}[/]");
            await LogErrorAsync(ex, "manual_intervention", false);
            return 1;
        }
    }

    private async Task DemoFeatureUsageLogging(LoggingExampleSettings settings)
    {
        AnsiConsole.MarkupLine("[yellow]📊 Demo 1: Feature Usage Logging[/]");

        await LogFeatureUsageAsync("demo_initialization", new Dictionary<string, object>
        {
            ["demo_type"] = "feature_usage",
            ["settings_verbose"] = settings.Verbose,
            ["timestamp"] = DateTime.UtcNow
        });

        AnsiConsole.WriteLine("- Logged feature usage for demo initialization");

        if (settings.Verbose)
        {
            await LogFeatureUsageAsync("verbose_mode", new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["level"] = "high"
            });
            AnsiConsole.WriteLine("- Logged verbose mode feature usage");
        }

        AnsiConsole.WriteLine();
    }

    private async Task DemoUserInteractionLogging(LoggingExampleSettings settings)
    {
        AnsiConsole.MarkupLine("[yellow]👤 Demo 2: User Interaction Logging[/]");

        if (settings.Interactive)
        {
            var name = await LoggedPromptAsync(
                () => AnsiConsole.Ask<string>("What's your [green]name[/]?"),
                "Enter your name",
                "name_input"
            );

            var favoriteColor = await LoggedPromptAsync(
                () => AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("What's your [green]favorite color[/]?")
                        .AddChoices("Red", "Green", "Blue", "Yellow", "Purple")
                ),
                "Select favorite color",
                "color_selection"
            );

            AnsiConsole.MarkupLine($"Hello [bold]{name}[/]! Your favorite color is [bold {favoriteColor.ToLower()}]{favoriteColor}[/]!");

            await LogFeatureUsageAsync("personalization", new Dictionary<string, object>
            {
                ["user_name"] = name,
                ["favorite_color"] = favoriteColor,
                ["interaction_completed"] = true
            });
        }
        else
        {
            AnsiConsole.WriteLine("- Skipping interactive demo (use --interactive to enable)");
            await LogFeatureUsageAsync("non_interactive_mode");
        }

        AnsiConsole.WriteLine();
    }

    private async Task DemoErrorLogging()
    {
        AnsiConsole.MarkupLine("[yellow]⚠️  Demo 3: Error Handling and Logging[/]");

        try
        {
            // Simulate different types of errors
            var errorType = Random.Shared.Next(1, 4);

            await LogFeatureUsageAsync("error_simulation", new Dictionary<string, object>
            {
                ["error_type"] = errorType,
                ["simulated"] = true
            });

            switch (errorType)
            {
                case 1:
                    throw new ArgumentException("Simulated argument error for demo");
                case 2:
                    throw new FileNotFoundException("Simulated file not found error for demo");
                case 3:
                    throw new InvalidOperationException("Simulated invalid operation error for demo");
                default:
                    throw new Exception("Simulated generic error for demo");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Caught error: {ex.Message}[/]");

            // Ask user what they want to do
            var userAction = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("How would you like to handle this error?")
                    .AddChoices("Continue", "Retry", "Get Help", "Exit")
            );

            await LogErrorAsync(ex, userAction.ToLower(), userAction == "Continue");

            if (userAction == "Exit")
            {
                throw;
            }

            AnsiConsole.MarkupLine("[green]Error handled gracefully![/]");
        }

        AnsiConsole.WriteLine();
    }

    private async Task DemoPerformanceLogging(LoggingExampleSettings settings)
    {
        AnsiConsole.MarkupLine("[yellow]⚡ Demo 4: Performance Monitoring[/]");

        await WithStatusAsync(async () =>
        {
            await LogFeatureUsageAsync("performance_intensive_operation", new Dictionary<string, object>
            {
                ["operation_type"] = "simulation",
                ["duration_ms"] = settings.DelayMs
            });

            // Simulate some work
            var iterations = settings.DelayMs / 10;
            for (int i = 0; i < iterations; i++)
            {
                await Task.Delay(10);

                // Simulate some memory allocation
                var data = new byte[1024 * 10]; // 10KB
                GC.KeepAlive(data);
            }

            return "Operation completed";
        },
        $"Processing intensive operation ({settings.DelayMs}ms)",
        "performance_simulation");

        // Manual performance metrics logging
        await LogPerformanceMetricsAsync(
            settings.DelayMs,
            GC.GetTotalMemory(false) / (1024.0 * 1024.0), // Current memory in MB
            25.5 // Simulated CPU usage
        );

        AnsiConsole.WriteLine("- Logged performance metrics for intensive operation");
        AnsiConsole.WriteLine();
    }

    private async Task DemoMultipleFeatures(LoggingExampleSettings settings)
    {
        AnsiConsole.MarkupLine("[yellow]🔧 Demo 5: Multiple Feature Usage[/]");

        var features = new[]
        {
            ("data_processing", new Dictionary<string, object> { ["records"] = 1000, ["format"] = "json" }),
            ("validation", new Dictionary<string, object> { ["rules"] = 5, ["passed"] = true }),
            ("export", new Dictionary<string, object> { ["format"] = "csv", ["size_mb"] = 2.5 }),
            ("notification", new Dictionary<string, object> { ["type"] = "email", ["sent"] = true })
        };

        foreach (var (featureName, featureData) in features)
        {
            await WithFeatureTrackingAsync(async () =>
            {
                await Task.Delay(100); // Simulate work
                AnsiConsole.WriteLine($"- Executed {featureName} feature");
                return true;
            }, featureName, featureData);
        }

        AnsiConsole.WriteLine();
    }
}

/// <summary>
/// Settings for the logging example command
/// </summary>
public class LoggingExampleSettings : CommandSettings
{
    [Description("Enable verbose output")]
    [CommandOption("-v|--verbose")]
    public bool Verbose { get; set; }

    [Description("Enable interactive prompts")]
    [CommandOption("-i|--interactive")]
    public bool Interactive { get; set; }

    [Description("Simulate an error for demonstration")]
    [CommandOption("--simulate-error")]
    public bool SimulateError { get; set; }

    [Description("Delay in milliseconds for performance demo")]
    [CommandOption("-d|--delay")]
    [DefaultValue(1000)]
    public int DelayMs { get; set; } = 1000;

    [Description("User ID for logging (optional)")]
    [CommandOption("-u|--user")]
    public string? UserId { get; set; }
}