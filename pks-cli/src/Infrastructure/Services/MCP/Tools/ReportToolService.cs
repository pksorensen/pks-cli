using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PKS.CLI.Infrastructure.Services.MCP.Tools;

/// <summary>
/// MCP tool service for PKS CLI report functionality
/// This service provides MCP tools for creating GitHub issues with system information and user feedback
/// </summary>
[McpServerToolType]
public class ReportToolService
{
    private readonly ILogger<ReportToolService> _logger;
    private readonly IReportService _reportService;

    public ReportToolService(
        ILogger<ReportToolService> logger,
        IReportService reportService)
    {
        _logger = logger;
        _reportService = reportService;
    }

    /// <summary>
    /// Create a GitHub issue report with system information and user feedback
    /// This tool connects to the real PKS report command functionality
    /// </summary>
    [McpServerTool]
    [Description("Create a GitHub issue report with system information and user feedback")]
    public async Task<object> CreateReportAsync(
        string message,
        string? title = null,
        bool isBug = false,
        bool isFeatureRequest = false,
        bool isQuestion = false,
        bool includeTelemetry = true,
        bool includeEnvironment = true,
        bool includeVersion = true,
        string repository = "pksorensen/pks-cli")
    {
        _logger.LogInformation("MCP Tool: Creating report with message: {Message}, type: Bug={IsBug} Feature={IsFeatureRequest} Question={IsQuestion}",
            message, isBug, isFeatureRequest, isQuestion);

        try
        {
            // Validate required parameters
            if (string.IsNullOrWhiteSpace(message))
            {
                return new
                {
                    success = false,
                    error = "Message is required",
                    message = "Report message cannot be empty"
                };
            }

            // Generate default title if not provided
            if (string.IsNullOrWhiteSpace(title))
            {
                title = GenerateDefaultTitle(isBug, isFeatureRequest, isQuestion);
            }

            // Create the report request
            var request = new CreateReportRequest
            {
                Message = message,
                Title = title,
                IsBug = isBug,
                IsFeatureRequest = isFeatureRequest,
                IsQuestion = isQuestion,
                IncludeTelemetry = includeTelemetry,
                IncludeEnvironment = includeEnvironment,
                IncludeVersion = includeVersion,
                Repository = repository
            };

            // Execute the report creation
            var result = await _reportService.CreateReportAsync(request);

            if (result.Success)
            {
                return new
                {
                    success = true,
                    issueNumber = result.IssueNumber,
                    issueUrl = result.IssueUrl,
                    repository = result.Repository,
                    title = result.Title,
                    labels = result.Labels.ToArray(),
                    createdAt = result.CreatedAt,
                    message = $"Report submitted successfully as issue #{result.IssueNumber}"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    error = result.ErrorMessage,
                    message = $"Failed to create report: {result.ErrorMessage}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create report");
            return new
            {
                success = false,
                error = ex.Message,
                message = $"Failed to create report: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Preview what a report would look like without creating the GitHub issue
    /// This tool provides a dry-run mode for report creation
    /// </summary>
    [McpServerTool]
    [Description("Preview what a report would look like without creating the GitHub issue")]
    public async Task<object> PreviewReportAsync(
        string message,
        string? title = null,
        bool isBug = false,
        bool isFeatureRequest = false,
        bool isQuestion = false,
        bool includeTelemetry = true,
        bool includeEnvironment = true,
        bool includeVersion = true,
        string repository = "pksorensen/pks-cli")
    {
        _logger.LogInformation("MCP Tool: Previewing report with message: {Message}, type: Bug={IsBug} Feature={IsFeatureRequest} Question={IsQuestion}",
            message, isBug, isFeatureRequest, isQuestion);

        try
        {
            // Validate required parameters
            if (string.IsNullOrWhiteSpace(message))
            {
                return new
                {
                    success = false,
                    error = "Message is required",
                    message = "Report message cannot be empty"
                };
            }

            // Generate default title if not provided
            if (string.IsNullOrWhiteSpace(title))
            {
                title = GenerateDefaultTitle(isBug, isFeatureRequest, isQuestion);
            }

            // Create the report request
            var request = new CreateReportRequest
            {
                Message = message,
                Title = title,
                IsBug = isBug,
                IsFeatureRequest = isFeatureRequest,
                IsQuestion = isQuestion,
                IncludeTelemetry = includeTelemetry,
                IncludeEnvironment = includeEnvironment,
                IncludeVersion = includeVersion,
                Repository = repository
            };

            // Preview the report
            var result = await _reportService.PreviewReportAsync(request);

            if (result.Success)
            {
                return new
                {
                    success = true,
                    preview = new
                    {
                        repository = result.Repository,
                        title = result.Title,
                        labels = result.Labels.ToArray(),
                        content = result.Content,
                        contentLength = result.Content.Length
                    },
                    message = "Report preview generated successfully",
                    note = "This is a preview only. Use CreateReport to actually submit the issue."
                };
            }
            else
            {
                return new
                {
                    success = false,
                    error = result.ErrorMessage,
                    message = $"Failed to preview report: {result.ErrorMessage}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to preview report");
            return new
            {
                success = false,
                error = ex.Message,
                message = $"Failed to preview report: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Get information about report capabilities and repository access
    /// This tool checks user permissions and repository configuration
    /// </summary>
    [McpServerTool]
    [Description("Get information about report capabilities and repository access")]
    public async Task<object> GetReportCapabilitiesAsync()
    {
        _logger.LogInformation("MCP Tool: Getting report capabilities");

        try
        {
            // Check if user can create reports
            var canCreateReports = await _reportService.CanCreateReportsAsync();

            // Get repository information
            var repositoryInfo = await _reportService.GetReportRepositoryAsync();

            return new
            {
                success = true,
                capabilities = new
                {
                    canCreateReports,
                    hasWriteAccess = repositoryInfo.HasWriteAccess,
                    isConfigured = repositoryInfo.IsConfigured
                },
                repository = new
                {
                    owner = repositoryInfo.Owner,
                    name = repositoryInfo.Name,
                    fullName = repositoryInfo.FullName,
                    url = repositoryInfo.Url
                },
                supportedReportTypes = new[]
                {
                    new { type = "bug", label = "Bug Report", description = "Report software bugs and issues" },
                    new { type = "feature", label = "Feature Request", description = "Request new features or enhancements" },
                    new { type = "question", label = "Question", description = "Ask questions about PKS CLI" },
                    new { type = "feedback", label = "General Feedback", description = "Provide general feedback or suggestions" }
                },
                includeOptions = new
                {
                    telemetry = new { enabled = true, description = "Include anonymous telemetry data" },
                    environment = new { enabled = true, description = "Include environment and system information" },
                    version = new { enabled = true, description = "Include PKS CLI version information" }
                },
                message = "Report capabilities retrieved successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get report capabilities");
            return new
            {
                success = false,
                error = ex.Message,
                message = $"Failed to get report capabilities: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Create a bug report with predefined settings optimized for bug reporting
    /// This is a convenience tool that sets appropriate defaults for bug reports
    /// </summary>
    [McpServerTool]
    [Description("Create a bug report with predefined settings optimized for bug reporting")]
    public async Task<object> CreateBugReportAsync(
        string bugDescription,
        string? title = null,
        bool includeTelemetry = true,
        bool includeEnvironment = true,
        bool includeVersion = true,
        string repository = "pksorensen/pks-cli")
    {
        _logger.LogInformation("MCP Tool: Creating bug report with description: {BugDescription}", bugDescription);

        // Use the main CreateReport tool with bug-specific settings
        return await CreateReportAsync(
            message: bugDescription,
            title: title ?? $"Bug Report: {bugDescription.Split('.').FirstOrDefault()?.Trim()}",
            isBug: true,
            isFeatureRequest: false,
            isQuestion: false,
            includeTelemetry: includeTelemetry,
            includeEnvironment: includeEnvironment,
            includeVersion: includeVersion,
            repository: repository);
    }

    /// <summary>
    /// Create a feature request with predefined settings optimized for feature requests
    /// This is a convenience tool that sets appropriate defaults for feature requests
    /// </summary>
    [McpServerTool]
    [Description("Create a feature request with predefined settings optimized for feature requests")]
    public async Task<object> CreateFeatureRequestAsync(
        string featureDescription,
        string? title = null,
        bool includeTelemetry = false,
        bool includeEnvironment = false,
        bool includeVersion = true,
        string repository = "pksorensen/pks-cli")
    {
        _logger.LogInformation("MCP Tool: Creating feature request with description: {FeatureDescription}", featureDescription);

        // Use the main CreateReport tool with feature request-specific settings
        return await CreateReportAsync(
            message: featureDescription,
            title: title ?? $"Feature Request: {featureDescription.Split('.').FirstOrDefault()?.Trim()}",
            isBug: false,
            isFeatureRequest: true,
            isQuestion: false,
            includeTelemetry: includeTelemetry,
            includeEnvironment: includeEnvironment,
            includeVersion: includeVersion,
            repository: repository);
    }

    private string GenerateDefaultTitle(bool isBug, bool isFeatureRequest, bool isQuestion)
    {
        return (isBug, isFeatureRequest, isQuestion) switch
        {
            (true, false, false) => "Bug Report: ",
            (false, true, false) => "Feature Request: ",
            (false, false, true) => "Question: ",
            _ => "PKS CLI Feedback: "
        };
    }
}