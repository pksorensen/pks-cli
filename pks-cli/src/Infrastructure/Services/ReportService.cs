using System.Text;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Implementation of report service for creating GitHub issues with system information
/// </summary>
public class ReportService : IReportService
{
    private readonly IGitHubService _gitHubService;
    private readonly ISystemInformationService _systemInformationService;
    private readonly ITelemetryService _telemetryService;
    private readonly IConfigurationService _configurationService;

    public ReportService(
        IGitHubService gitHubService,
        ISystemInformationService systemInformationService,
        ITelemetryService telemetryService,
        IConfigurationService configurationService)
    {
        _gitHubService = gitHubService;
        _systemInformationService = systemInformationService;
        _telemetryService = telemetryService;
        _configurationService = configurationService;
    }

    public async Task<ReportResult> CreateReportAsync(CreateReportRequest request)
    {
        try
        {
            // Check if user can create reports
            if (!await CanCreateReportsAsync())
            {
                return new ReportResult
                {
                    Success = false,
                    ErrorMessage = "GitHub authentication is required to create reports. Please configure your GitHub token."
                };
            }

            // Build the report content
            var content = await BuildReportContentAsync(request);
            var labels = BuildLabels(request);

            // Parse repository
            var repoParts = request.Repository.Split('/');
            if (repoParts.Length != 2)
            {
                return new ReportResult
                {
                    Success = false,
                    ErrorMessage = "Invalid repository format. Expected format: owner/repository"
                };
            }

            var owner = repoParts[0];
            var repoName = repoParts[1];

            // Create the GitHub issue
            var issue = await _gitHubService.CreateIssueAsync(
                owner,
                repoName,
                request.Title,
                content,
                labels.ToArray());

            return new ReportResult
            {
                Success = true,
                Title = issue.Title,
                Content = content,
                Repository = request.Repository,
                Labels = labels,
                IssueNumber = issue.Number,
                IssueUrl = issue.HtmlUrl,
                CreatedAt = issue.CreatedAt
            };
        }
        catch (Exception ex)
        {
            return new ReportResult
            {
                Success = false,
                ErrorMessage = $"Failed to create report: {ex.Message}"
            };
        }
    }

    public async Task<ReportResult> PreviewReportAsync(CreateReportRequest request)
    {
        try
        {
            var content = await BuildReportContentAsync(request);
            var labels = BuildLabels(request);

            return new ReportResult
            {
                Success = true,
                Title = request.Title,
                Content = content,
                Repository = request.Repository,
                Labels = labels,
                IssueNumber = 0, // Preview doesn't create an actual issue
                IssueUrl = $"https://github.com/{request.Repository}/issues/new",
                CreatedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new ReportResult
            {
                Success = false,
                ErrorMessage = $"Failed to preview report: {ex.Message}"
            };
        }
    }

    public async Task<bool> CanCreateReportsAsync()
    {
        try
        {
            // Check if we have a valid GitHub token
            var token = await _configurationService.GetAsync("github.token");
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            // Validate the token
            var validation = await _gitHubService.ValidateTokenAsync(token);
            return validation.IsValid && validation.Scopes.Contains("repo");
        }
        catch
        {
            return false;
        }
    }

    public async Task<ReportRepositoryInfo> GetReportRepositoryAsync()
    {
        const string defaultRepo = "pksorensen/pks-cli";
        var repoParts = defaultRepo.Split('/');

        try
        {
            var owner = repoParts[0];
            var repoName = repoParts[1];

            var repository = await _gitHubService.GetRepositoryAsync(owner, repoName);
            var hasAccess = await CanCreateReportsAsync();

            return new ReportRepositoryInfo
            {
                Owner = owner,
                Name = repoName,
                FullName = defaultRepo,
                Url = $"https://github.com/{defaultRepo}",
                HasWriteAccess = hasAccess,
                IsConfigured = repository != null
            };
        }
        catch
        {
            return new ReportRepositoryInfo
            {
                Owner = repoParts[0],
                Name = repoParts[1],
                FullName = defaultRepo,
                Url = $"https://github.com/{defaultRepo}",
                HasWriteAccess = false,
                IsConfigured = false
            };
        }
    }

    private async Task<string> BuildReportContentAsync(CreateReportRequest request)
    {
        var content = new StringBuilder();

        // User message
        content.AppendLine("## User Report");
        content.AppendLine();
        content.AppendLine(request.Message);
        content.AppendLine();

        // Version information
        if (request.IncludeVersion)
        {
            var versionInfo = await _systemInformationService.GetPksCliInfoAsync();
            content.AppendLine("## Version Information");
            content.AppendLine();
            content.AppendLine($"- **PKS CLI Version**: {versionInfo.Version}");
            content.AppendLine($"- **Assembly Version**: {versionInfo.AssemblyVersion}");
            content.AppendLine($"- **Product Version**: {versionInfo.ProductVersion}");
            content.AppendLine($"- **Git Commit**: {versionInfo.GitCommit ?? "Unknown"}");
            content.AppendLine($"- **Build Date**: {versionInfo.BuildDate:yyyy-MM-dd HH:mm:ss} UTC");
            content.AppendLine($"- **Configuration**: {versionInfo.BuildConfiguration}");
            content.AppendLine();
        }

        // Environment information
        if (request.IncludeEnvironment)
        {
            var systemInfo = await _systemInformationService.GetSystemInformationAsync();

            content.AppendLine("## Environment Information");
            content.AppendLine();
            content.AppendLine("### System");
            content.AppendLine($"- **Operating System**: {systemInfo.OperatingSystemInfo.OsDescription}");
            content.AppendLine($"- **Architecture**: {systemInfo.OperatingSystemInfo.OsArchitecture}");
            content.AppendLine($"- **Processor Count**: {systemInfo.HardwareInfo.LogicalCores}");
            content.AppendLine($"- **Is WSL**: {systemInfo.OperatingSystemInfo.IsWsl}");
            content.AppendLine($"- **Current Shell**: {systemInfo.OperatingSystemInfo.CurrentShell}");
            content.AppendLine();

            content.AppendLine("### .NET Runtime");
            content.AppendLine($"- **Framework**: {systemInfo.DotNetRuntimeInfo.RuntimeVersion}");
            content.AppendLine($"- **Runtime Version**: {systemInfo.DotNetRuntimeInfo.FrameworkVersion}");
            content.AppendLine($"- **Runtime ID**: {systemInfo.DotNetRuntimeInfo.RuntimeIdentifier}");
            content.AppendLine($"- **Target Framework**: {systemInfo.DotNetRuntimeInfo.TargetFramework}");
            content.AppendLine($"- **Process Architecture**: {systemInfo.OperatingSystemInfo.ProcessArchitecture}");
            content.AppendLine($"- **OS Architecture**: {systemInfo.OperatingSystemInfo.OsArchitecture}");
            content.AppendLine();

            content.AppendLine("### Development Tools");
            content.AppendLine($"- **Docker**: {(systemInfo.EnvironmentInfo.HasDocker ? systemInfo.EnvironmentInfo.DockerVersion : "Not available")}");
            content.AppendLine($"- **Git**: {(systemInfo.EnvironmentInfo.HasGit ? systemInfo.EnvironmentInfo.GitVersion : "Not available")}");
            content.AppendLine($"- **Node.js**: {(systemInfo.EnvironmentInfo.HasNode ? systemInfo.EnvironmentInfo.NodeVersion : "Not available")}");
            content.AppendLine($"- **Kubernetes**: {(systemInfo.EnvironmentInfo.HasKubernetes ? systemInfo.EnvironmentInfo.KubernetesVersion : "Not available")}");
            content.AppendLine();
        }

        // Telemetry information
        if (request.IncludeTelemetry)
        {
            var telemetryData = await _telemetryService.GetTelemetryDataAsync();

            if (telemetryData.IsEnabled)
            {
                content.AppendLine("## Usage Statistics (Anonymous)");
                content.AppendLine();
                content.AppendLine($"- **Total Commands Executed**: {telemetryData.Usage.TotalCommands}");
                content.AppendLine($"- **Most Used Command**: {telemetryData.Usage.MostUsedCommand}");
                content.AppendLine($"- **Days Active**: {telemetryData.Usage.DaysActive}");
                content.AppendLine($"- **Total Errors**: {telemetryData.Errors.TotalErrors}");

                if (telemetryData.Usage.CommandCounts.Any())
                {
                    content.AppendLine();
                    content.AppendLine("### Command Usage");
                    foreach (var command in telemetryData.Usage.CommandCounts.OrderByDescending(kv => kv.Value).Take(5))
                    {
                        content.AppendLine($"- **{command.Key}**: {command.Value} times");
                    }
                }

                if (telemetryData.Features.TemplateUsage.Any())
                {
                    content.AppendLine();
                    content.AppendLine("### Template Usage");
                    foreach (var template in telemetryData.Features.TemplateUsage)
                    {
                        content.AppendLine($"- **{template.Key}**: {template.Value} times");
                    }
                }

                content.AppendLine();
                content.AppendLine("### Features Used");
                content.AppendLine($"- **Agentic Features**: {telemetryData.Features.UsedAgenticFeatures}");
                content.AppendLine($"- **MCP Integration**: {telemetryData.Features.UsedMcpIntegration}");
                content.AppendLine($"- **Devcontainers**: {telemetryData.Features.UsedDevcontainers}");
                content.AppendLine($"- **GitHub Integration**: {telemetryData.Features.UsedGitHubIntegration}");
                content.AppendLine($"- **Kubernetes Deployment**: {telemetryData.Features.UsedKubernetesDeployment}");
                content.AppendLine();
            }
            else
            {
                content.AppendLine("## Usage Statistics");
                content.AppendLine();
                content.AppendLine("*Telemetry is disabled - no usage statistics available*");
                content.AppendLine();
            }
        }

        // Footer
        content.AppendLine("---");
        content.AppendLine();
        content.AppendLine("*This report was automatically generated by PKS CLI's report command.*");
        content.AppendLine($"*Report created at: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*");

        return content.ToString();
    }

    private List<string> BuildLabels(CreateReportRequest request)
    {
        var labels = new List<string> { "pks-cli-report" };

        if (request.IsBug)
        {
            labels.Add("bug");
        }

        if (request.IsFeatureRequest)
        {
            labels.Add("enhancement");
        }

        if (request.IsQuestion)
        {
            labels.Add("question");
        }

        if (!request.IsBug && !request.IsFeatureRequest && !request.IsQuestion)
        {
            labels.Add("feedback");
        }

        return labels;
    }
}