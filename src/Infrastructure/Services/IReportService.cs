namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for creating and managing user reports sent to GitHub
/// </summary>
public interface IReportService
{
    /// <summary>
    /// Creates a new report by submitting a GitHub issue
    /// </summary>
    /// <param name="request">Report creation request</param>
    /// <returns>Result of the report creation</returns>
    Task<ReportResult> CreateReportAsync(CreateReportRequest request);

    /// <summary>
    /// Previews what a report would look like without creating it
    /// </summary>
    /// <param name="request">Report creation request</param>
    /// <returns>Preview of the report content</returns>
    Task<ReportResult> PreviewReportAsync(CreateReportRequest request);

    /// <summary>
    /// Validates if the user has necessary permissions to create reports
    /// </summary>
    /// <returns>True if user can create reports</returns>
    Task<bool> CanCreateReportsAsync();

    /// <summary>
    /// Gets the configured repository for reports
    /// </summary>
    /// <returns>Repository information</returns>
    Task<ReportRepositoryInfo> GetReportRepositoryAsync();
}

/// <summary>
/// Request to create a new report
/// </summary>
public class CreateReportRequest
{
    public string Message { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsBug { get; set; }
    public bool IsFeatureRequest { get; set; }
    public bool IsQuestion { get; set; }
    public bool IncludeTelemetry { get; set; } = true;
    public bool IncludeEnvironment { get; set; } = true;
    public bool IncludeVersion { get; set; } = true;
    public string Repository { get; set; } = "pksorensen/pks-cli";
}

/// <summary>
/// Result of creating or previewing a report
/// </summary>
public class ReportResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public List<string> Labels { get; set; } = new();
    public int IssueNumber { get; set; }
    public string IssueUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Information about the repository configured for reports
/// </summary>
public class ReportRepositoryInfo
{
    public string Owner { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool HasWriteAccess { get; set; }
    public bool IsConfigured { get; set; }
}