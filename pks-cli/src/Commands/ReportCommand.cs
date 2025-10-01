using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using PKS.Infrastructure.Services;

namespace PKS.Commands;

/// <summary>
/// Command for creating GitHub issues with system information, version details, and user feedback
/// </summary>
public class ReportCommand : Command<ReportCommand.Settings>
{
    private readonly IReportService _reportService;
    private readonly IAnsiConsole _console;

    public ReportCommand(IReportService reportService, IAnsiConsole? console = null)
    {
        _reportService = reportService;
        _console = console ?? AnsiConsole.Console;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[MESSAGE]")]
        [Description("The message or feedback to include in the report")]
        public string? Message { get; set; }

        [CommandOption("-t|--title <TITLE>")]
        [Description("Title for the GitHub issue")]
        public string? Title { get; set; }

        [CommandOption("--bug")]
        [Description("Report a bug (adds bug label)")]
        public bool IsBug { get; set; }

        [CommandOption("--feature")]
        [Description("Request a feature (adds enhancement label)")]
        public bool IsFeatureRequest { get; set; }

        [CommandOption("--question")]
        [Description("Ask a question (adds question label)")]
        public bool IsQuestion { get; set; }

        [CommandOption("--include-telemetry")]
        [Description("Include anonymous telemetry data in the report")]
        [DefaultValue(true)]
        public bool IncludeTelemetry { get; set; } = true;

        [CommandOption("--include-environment")]
        [Description("Include environment and system information")]
        [DefaultValue(true)]
        public bool IncludeEnvironment { get; set; } = true;

        [CommandOption("--include-version")]
        [Description("Include PKS CLI version information")]
        [DefaultValue(true)]
        public bool IncludeVersion { get; set; } = true;

        [CommandOption("--dry-run")]
        [Description("Preview the report without creating the GitHub issue")]
        public bool DryRun { get; set; }

        [CommandOption("--repo <REPOSITORY>")]
        [Description("Target repository (default: pksorensen/pks-cli)")]
        [DefaultValue("pksorensen/pks-cli")]
        public string Repository { get; set; } = "pksorensen/pks-cli";
    }

    public override int Execute(CommandContext context, Settings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            // Display header
            _console.Write(new Rule("[bold cyan]PKS CLI Report Generator[/]")
                .RuleStyle("cyan"));
            _console.WriteLine();

            // Interactive message collection if not provided
            if (string.IsNullOrEmpty(settings.Message))
            {
                settings.Message = _console.Ask<string>("What would you like to [green]report or share[/]?");
            }

            // Interactive title collection if not provided
            if (string.IsNullOrEmpty(settings.Title))
            {
                var defaultTitle = GenerateDefaultTitle(settings);
                settings.Title = _console.Ask<string>($"Issue [cyan]title[/] ({defaultTitle})?");
            }

            // Determine issue type if not specified
            if (!settings.IsBug && !settings.IsFeatureRequest && !settings.IsQuestion)
            {
                var issueType = _console.Prompt(
                    new SelectionPrompt<string>()
                        .Title("What [green]type[/] of report is this?")
                        .AddChoices("Bug Report", "Feature Request", "Question", "General Feedback"));

                settings.IsBug = issueType == "Bug Report";
                settings.IsFeatureRequest = issueType == "Feature Request";
                settings.IsQuestion = issueType == "Question";
            }

            // Create the report
            ReportResult result;
            if (settings.DryRun)
            {
                _console.WriteLine();
                _console.MarkupLine("[yellow]🔍 Dry run mode - previewing report content...[/]");
                result = await _reportService.PreviewReportAsync(new CreateReportRequest
                {
                    Message = settings.Message,
                    Title = settings.Title,
                    IsBug = settings.IsBug,
                    IsFeatureRequest = settings.IsFeatureRequest,
                    IsQuestion = settings.IsQuestion,
                    IncludeTelemetry = settings.IncludeTelemetry,
                    IncludeEnvironment = settings.IncludeEnvironment,
                    IncludeVersion = settings.IncludeVersion,
                    Repository = settings.Repository
                });
            }
            else
            {
                _console.WriteLine();
                result = await _console.Status()
                    .Spinner(Spinner.Known.Star2)
                    .SpinnerStyle(Style.Parse("green bold"))
                    .StartAsync("Creating GitHub issue...", async ctx =>
                    {
                        ctx.Status("Collecting system information...");
                        await Task.Delay(300);

                        ctx.Status("Gathering telemetry data...");
                        await Task.Delay(200);

                        ctx.Status("Authenticating with GitHub...");
                        await Task.Delay(400);

                        ctx.Status("Creating issue...");
                        return await _reportService.CreateReportAsync(new CreateReportRequest
                        {
                            Message = settings.Message,
                            Title = settings.Title,
                            IsBug = settings.IsBug,
                            IsFeatureRequest = settings.IsFeatureRequest,
                            IsQuestion = settings.IsQuestion,
                            IncludeTelemetry = settings.IncludeTelemetry,
                            IncludeEnvironment = settings.IncludeEnvironment,
                            IncludeVersion = settings.IncludeVersion,
                            Repository = settings.Repository
                        });
                    });
            }

            // Display results
            if (result.Success)
            {
                if (settings.DryRun)
                {
                    DisplayPreview(result);
                }
                else
                {
                    DisplaySuccess(result);
                }
                return 0;
            }
            else
            {
                _console.MarkupLine($"[red]❌ Failed to create report: {result.ErrorMessage}[/]");
                return 1;
            }
        }
        catch (Exception ex)
        {
            _console.WriteException(ex);
            return 1;
        }
    }

    private string GenerateDefaultTitle(Settings settings)
    {
        return settings switch
        {
            { IsBug: true } => "Bug Report: ",
            { IsFeatureRequest: true } => "Feature Request: ",
            { IsQuestion: true } => "Question: ",
            _ => "PKS CLI Feedback: "
        };
    }

    private void DisplayPreview(ReportResult result)
    {
        var panel = new Panel($"""
        [bold]Preview of GitHub Issue[/]
        
        [cyan1]Repository:[/] {result.Repository}
        [cyan1]Title:[/] {result.Title}
        [cyan1]Labels:[/] {string.Join(", ", result.Labels)}
        
        [cyan1]Content Preview:[/]
        {result.Content}
        """)
        .Border(BoxBorder.Double)
        .BorderStyle("yellow")
        .Header(" [bold yellow]🔍 Report Preview[/] ");

        _console.Write(panel);
        _console.WriteLine();
        _console.MarkupLine("[dim]Run without --dry-run to create the actual GitHub issue.[/]");
    }

    private void DisplaySuccess(ReportResult result)
    {
        var panel = new Panel($"""
        🎉 [bold green]Report submitted successfully![/]
        
        [cyan1]GitHub Issue:[/] #{result.IssueNumber}
        [cyan1]Repository:[/] {result.Repository}
        [cyan1]Title:[/] {result.Title}
        [cyan1]Labels:[/] {string.Join(", ", result.Labels)}
        [cyan1]URL:[/] [link]{result.IssueUrl}[/]
        
        Your feedback has been submitted to the PKS CLI team.
        Thank you for helping us improve! 🚀
        
        [dim]You can track the progress of your report at the URL above.[/]
        """)
        .Border(BoxBorder.Double)
        .BorderStyle("green")
        .Header(" [bold green]✅ Report Submitted[/] ");

        _console.Write(panel);
    }
}