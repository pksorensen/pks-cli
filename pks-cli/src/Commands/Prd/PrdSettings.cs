using Spectre.Console.Cli;
using System.ComponentModel;
using PKS.Infrastructure.Services;

namespace PKS.Commands.Prd;

/// <summary>
/// Base settings for all PRD commands with shared options
/// </summary>
public class PrdSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output with detailed information")]
    public bool Verbose { get; set; }

    [CommandOption("--output-format <FORMAT>")]
    [Description("Output format: markdown, json")]
    [DefaultValue("markdown")]
    public string OutputFormat { get; set; } = "markdown";

    [CommandOption("--config <CONFIG_FILE>")]
    [Description("Path to PRD configuration file")]
    public string? ConfigFile { get; set; }

    [CommandOption("--no-color")]
    [Description("Disable colored output")]
    public bool NoColor { get; set; }
}

/// <summary>
/// Settings for PRD generate command
/// </summary>
public class PrdGenerateSettings : PrdSettings
{
    [CommandArgument(0, "<IDEA_DESCRIPTION>")]
    [Description("Description of the idea or project to generate PRD for")]
    public string IdeaDescription { get; set; } = string.Empty;

    [CommandOption("-n|--name <PROJECT_NAME>")]
    [Description("Name of the project (if different from current directory)")]
    public string? ProjectName { get; set; }

    [CommandOption("-o|--output <OUTPUT_PATH>")]
    [Description("Output path for the generated PRD file")]
    public string? OutputPath { get; set; }

    [CommandOption("-t|--template <TEMPLATE>")]
    [Description("PRD template type (standard, technical, mobile, web, api, minimal, enterprise)")]
    [DefaultValue("standard")]
    public string Template { get; set; } = "standard";

    [CommandOption("--target-audience <AUDIENCE>")]
    [Description("Target audience for the project")]
    public string? TargetAudience { get; set; }

    [CommandOption("--stakeholders <STAKEHOLDERS>")]
    [Description("Comma-separated list of stakeholders")]
    public string? Stakeholders { get; set; }

    [CommandOption("--business-context <CONTEXT>")]
    [Description("Business context or background for the project")]
    public string? BusinessContext { get; set; }

    [CommandOption("--technical-constraints <CONSTRAINTS>")]
    [Description("Comma-separated list of technical constraints")]
    public string? TechnicalConstraints { get; set; }

    [CommandOption("-f|--force")]
    [Description("Overwrite existing PRD file if it exists")]
    public bool Force { get; set; }

    [CommandOption("--interactive")]
    [Description("Interactive mode for detailed PRD generation")]
    public bool Interactive { get; set; }

}

/// <summary>
/// Settings for PRD load command
/// </summary>
public class PrdLoadSettings : PrdSettings
{
    [CommandArgument(0, "<FILE_PATH>")]
    [Description("Path to the PRD file to load")]
    public string FilePath { get; set; } = string.Empty;

    [CommandOption("--validate")]
    [Description("Validate the PRD after loading")]
    public new bool Validate { get; set; }

    [CommandOption("--export <EXPORT_PATH>")]
    [Description("Export loaded PRD to different format")]
    public string? ExportPath { get; set; }

    [CommandOption("--show-metadata")]
    [Description("Display PRD metadata and statistics")]
    public bool ShowMetadata { get; set; }

}

/// <summary>
/// Settings for PRD requirements command
/// </summary>
public class PrdRequirementsSettings : PrdSettings
{
    [CommandArgument(0, "[FILE_PATH]")]
    [Description("Path to the PRD file (defaults to ./docs/PRD.md)")]
    public string? FilePath { get; set; }

    [CommandOption("--status <STATUS>")]
    [Description("Filter by requirement status (draft, approved, inprogress, completed, blocked, cancelled, onhold)")]
    public string? Status { get; set; }

    [CommandOption("--priority <PRIORITY>")]
    [Description("Filter by priority (critical, high, medium, low, nice)")]
    public string? Priority { get; set; }

    [CommandOption("--type <TYPE>")]
    [Description("Filter by requirement type (functional, nonfunctional, business, technical, security, performance, usability, accessibility, compliance)")]
    public string? Type { get; set; }

    [CommandOption("--assignee <ASSIGNEE>")]
    [Description("Filter by assignee")]
    public string? Assignee { get; set; }

    [CommandOption("--export <EXPORT_PATH>")]
    [Description("Export requirements to file (CSV, JSON)")]
    public string? ExportPath { get; set; }

    [CommandOption("--show-details")]
    [Description("Show detailed requirement information")]
    public bool ShowDetails { get; set; }

}

/// <summary>
/// Settings for PRD status command
/// </summary>
public class PrdStatusSettings : PrdSettings
{
    [CommandArgument(0, "[FILE_PATH]")]
    [Description("Path to the PRD file (defaults to ./docs/PRD.md)")]
    public string? FilePath { get; set; }

    [CommandOption("--watch")]
    [Description("Watch for changes and update status in real-time")]
    public bool Watch { get; set; }

    [CommandOption("--check-all")]
    [Description("Check status of all PRD files in the project")]
    public bool CheckAll { get; set; }

    [CommandOption("--export <EXPORT_PATH>")]
    [Description("Export status report to file")]
    public string? ExportPath { get; set; }

    [CommandOption("--include-history")]
    [Description("Include change history in status report")]
    public bool IncludeHistory { get; set; }

}

/// <summary>
/// Settings for PRD validate command
/// </summary>
public class PrdValidateSettings : PrdSettings
{
    [CommandArgument(0, "[FILE_PATH]")]
    [Description("Path to the PRD file (defaults to ./docs/PRD.md)")]
    public string? FilePath { get; set; }

    [CommandOption("--strict")]
    [Description("Use strict validation rules")]
    public bool Strict { get; set; }

    [CommandOption("--fix")]
    [Description("Attempt to automatically fix validation issues")]
    public bool AutoFix { get; set; }

    [CommandOption("--report <REPORT_PATH>")]
    [Description("Generate validation report file")]
    public string? ReportPath { get; set; }

}

/// <summary>
/// Settings for PRD template command
/// </summary>
public class PrdTemplateSettings : PrdSettings
{
    [CommandArgument(0, "<PROJECT_NAME>")]
    [Description("Name of the project for template generation")]
    public string ProjectName { get; set; } = string.Empty;

    [CommandOption("-t|--type <TEMPLATE_TYPE>")]
    [Description("Template type (standard, technical, mobile, web, api, minimal, enterprise)")]
    [DefaultValue("standard")]
    public string TemplateType { get; set; } = "standard";

    [CommandOption("-o|--output <OUTPUT_PATH>")]
    [Description("Output path for the template file")]
    public string? OutputPath { get; set; }

    [CommandOption("--list")]
    [Description("List available template types")]
    public bool ListTemplates { get; set; }

}