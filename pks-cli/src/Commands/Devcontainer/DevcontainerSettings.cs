using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Base settings for all devcontainer commands
/// </summary>
public abstract class DevcontainerSettings : CommandSettings
{
    [CommandOption("-o|--output-path <PATH>")]
    [Description("Output directory path for devcontainer files")]
    public string OutputPath { get; set; } = Directory.GetCurrentDirectory();

    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }

    [CommandOption("-f|--force")]
    [Description("Force overwrite existing files")]
    public bool Force { get; set; }

    [CommandOption("--dry-run")]
    [Description("Show what would be done without making changes")]
    public bool DryRun { get; set; }
}

/// <summary>
/// Settings for devcontainer initialization commands
/// </summary>
public class DevcontainerInitSettings : DevcontainerSettings
{
    [CommandArgument(0, "[NAME]")]
    [Description("Name of the devcontainer configuration")]
    public string? Name { get; set; }

    [CommandOption("-t|--template <TEMPLATE>")]
    [Description("Template to use for initialization")]
    public string? Template { get; set; }

    [CommandOption("--image <IMAGE>")]
    [Description("Base container image to use")]
    public string? BaseImage { get; set; }

    [CommandOption("--features <FEATURES>")]
    [Description("Comma-separated list of features to include")]
    public string[]? Features { get; set; }

    [CommandOption("--extensions <EXTENSIONS>")]
    [Description("Comma-separated list of VS Code extensions to include")]
    public string[]? Extensions { get; set; }

    [CommandOption("--ports <PORTS>")]
    [Description("Comma-separated list of ports to forward")]
    public string[]? Ports { get; set; }

    [CommandOption("--post-create-command <COMMAND>")]
    [Description("Command to run after container creation")]
    public string? PostCreateCommand { get; set; }

    [CommandOption("--docker-compose")]
    [Description("Use Docker Compose instead of single container")]
    public bool UseDockerCompose { get; set; }

    [CommandOption("-i|--interactive")]
    [Description("Run in interactive mode with prompts")]
    public bool Interactive { get; set; }

    [CommandOption("--workspace-folder <FOLDER>")]
    [Description("Workspace folder path inside container")]
    public string? WorkspaceFolder { get; set; }

    [CommandOption("--env <ENV>")]
    [Description("Environment variables in KEY=VALUE format")]
    public string[]? EnvironmentVariables { get; set; }

    [CommandOption("--include-dev-packages")]
    [Description("Include development packages and tools")]
    public bool IncludeDevPackages { get; set; } = true;

    [CommandOption("--git-credentials")]
    [Description("Enable Git credential sharing")]
    public bool EnableGitCredentials { get; set; } = true;

    [CommandOption("--generate-files <FILES>")]
    [Description("Additional files to generate (gitignore,vscode,readme)")]
    public string[]? GenerateFiles { get; set; }
}

/// <summary>
/// Settings for devcontainer validation commands
/// </summary>
public class DevcontainerValidateSettings : DevcontainerSettings
{
    [CommandArgument(0, "[CONFIG-PATH]")]
    [Description("Path to devcontainer.json file to validate")]
    public string? ConfigPath { get; set; }

    [CommandOption("--strict")]
    [Description("Enable strict validation mode")]
    public bool Strict { get; set; }

    [CommandOption("--check-features")]
    [Description("Validate feature configurations")]
    public bool CheckFeatures { get; set; } = true;

    [CommandOption("--check-extensions")]
    [Description("Validate VS Code extensions")]
    public bool CheckExtensions { get; set; } = true;
}

/// <summary>
/// Settings for devcontainer update commands
/// </summary>
public class DevcontainerUpdateSettings : DevcontainerSettings
{
    [CommandArgument(0, "[CONFIG-PATH]")]
    [Description("Path to devcontainer.json file to update")]
    public string? ConfigPath { get; set; }

    [CommandOption("--add-features <FEATURES>")]
    [Description("Features to add")]
    public string[]? AddFeatures { get; set; }

    [CommandOption("--remove-features <FEATURES>")]
    [Description("Features to remove")]
    public string[]? RemoveFeatures { get; set; }

    [CommandOption("--add-extensions <EXTENSIONS>")]
    [Description("Extensions to add")]
    public string[]? AddExtensions { get; set; }

    [CommandOption("--remove-extensions <EXTENSIONS>")]
    [Description("Extensions to remove")]
    public string[]? RemoveExtensions { get; set; }

    [CommandOption("--add-ports <PORTS>")]
    [Description("Ports to add")]
    public string[]? AddPorts { get; set; }

    [CommandOption("--remove-ports <PORTS>")]
    [Description("Ports to remove")]
    public string[]? RemovePorts { get; set; }

    [CommandOption("--set-image <IMAGE>")]
    [Description("Update base image")]
    public string? SetImage { get; set; }

    [CommandOption("--set-post-create-command <COMMAND>")]
    [Description("Update post-create command")]
    public string? SetPostCreateCommand { get; set; }

    [CommandOption("--backup")]
    [Description("Create backup before updating")]
    public bool CreateBackup { get; set; } = true;
}

/// <summary>
/// Settings for devcontainer feature commands
/// </summary>
public class DevcontainerFeatureSettings : DevcontainerSettings
{
    [CommandOption("-c|--category <CATEGORY>")]
    [Description("Filter features by category")]
    public string? Category { get; set; }

    [CommandOption("-s|--search <QUERY>")]
    [Description("Search features by query")]
    public string? SearchQuery { get; set; }

    [CommandOption("--show-deprecated")]
    [Description("Include deprecated features")]
    public bool ShowDeprecated { get; set; }

    [CommandOption("--refresh")]
    [Description("Refresh feature registry from remote sources")]
    public bool Refresh { get; set; }
}

/// <summary>
/// Settings for devcontainer template commands
/// </summary>
public class DevcontainerTemplateSettings : DevcontainerSettings
{
    [CommandOption("-c|--category <CATEGORY>")]
    [Description("Filter templates by category")]
    public string? Category { get; set; }

    [CommandOption("-s|--search <QUERY>")]
    [Description("Search templates by query")]
    public string? SearchQuery { get; set; }

    [CommandOption("--show-details")]
    [Description("Show detailed template information")]
    public bool ShowDetails { get; set; }
}

/// <summary>
/// Settings for devcontainer wizard command
/// </summary>
public class DevcontainerWizardSettings : DevcontainerSettings
{
    [CommandOption("--skip-templates")]
    [Description("Skip template selection step")]
    public bool SkipTemplates { get; set; }

    [CommandOption("--skip-features")]
    [Description("Skip feature selection step")]
    public bool SkipFeatures { get; set; }

    [CommandOption("--skip-extensions")]
    [Description("Skip extension selection step")]
    public bool SkipExtensions { get; set; }

    [CommandOption("--expert-mode")]
    [Description("Enable expert mode with advanced options")]
    public bool ExpertMode { get; set; }

    [CommandOption("--quick-setup")]
    [Description("Use quick setup with minimal prompts")]
    public bool QuickSetup { get; set; }
}