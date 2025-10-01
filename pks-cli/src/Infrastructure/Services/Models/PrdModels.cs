using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Configuration for PRD generation and management
/// </summary>
public class PrdConfiguration
{
    public string ProjectName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string Author { get; set; } = string.Empty;
    public List<string> Stakeholders { get; set; } = new();
    public string TargetAudience { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Individual requirement within a PRD
/// </summary>
public class PrdRequirement
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RequirementType Type { get; set; }
    public RequirementPriority Priority { get; set; }
    public RequirementStatus Status { get; set; }
    public List<string> AcceptanceCriteria { get; set; } = new();
    public List<string> Dependencies { get; set; } = new();
    public string Assignee { get; set; } = string.Empty;
    public DateTime? DueDate { get; set; }
    public int EstimatedEffort { get; set; } // Story points or hours
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object> CustomFields { get; set; } = new();
}

/// <summary>
/// User story representation
/// </summary>
public class UserStory
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string AsA { get; set; } = string.Empty; // As a [user type]
    public string IWant { get; set; } = string.Empty; // I want [goal]
    public string SoThat { get; set; } = string.Empty; // So that [benefit]
    public List<string> AcceptanceCriteria { get; set; } = new();
    public UserStoryPriority Priority { get; set; }
    public int EstimatedPoints { get; set; }
    public List<string> RequirementIds { get; set; } = new(); // Linked requirements
}

/// <summary>
/// Section within a PRD document
/// </summary>
public class PrdSection
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Order { get; set; }
    public List<PrdSection> SubSections { get; set; } = new();
    public SectionType Type { get; set; }
    public Dictionary<string, object> Properties { get; set; } = new();
}

/// <summary>
/// Complete PRD document structure
/// </summary>
public class PrdDocument
{
    public PrdConfiguration Configuration { get; set; } = new();
    public List<PrdSection> Sections { get; set; } = new();
    public List<PrdRequirement> Requirements { get; set; } = new();
    public List<UserStory> UserStories { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();

    // Helper methods
    public PrdSection? GetSection(string id) =>
        Sections.FirstOrDefault(s => s.Id == id) ??
        Sections.SelectMany(s => GetAllSubSections(s)).FirstOrDefault(s => s.Id == id);

    public PrdRequirement? GetRequirement(string id) =>
        Requirements.FirstOrDefault(r => r.Id == id);

    public UserStory? GetUserStory(string id) =>
        UserStories.FirstOrDefault(us => us.Id == id);

    private IEnumerable<PrdSection> GetAllSubSections(PrdSection section)
    {
        yield return section;
        foreach (var subSection in section.SubSections.SelectMany(GetAllSubSections))
            yield return subSection;
    }
}

/// <summary>
/// PRD generation request
/// </summary>
public class PrdGenerationRequest
{
    public string IdeaDescription { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string TargetAudience { get; set; } = string.Empty;
    public List<string> Stakeholders { get; set; } = new();
    public string BusinessContext { get; set; } = string.Empty;
    public List<string> TechnicalConstraints { get; set; } = new();
    public Dictionary<string, object> AdditionalContext { get; set; } = new();
}

/// <summary>
/// PRD parsing result
/// </summary>
public class PrdParsingResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public PrdDocument? Document { get; set; }
    public List<string> Warnings { get; set; } = new();
    public Dictionary<string, object> ParsedMetadata { get; set; } = new();
}

/// <summary>
/// PRD status information
/// </summary>
public class PrdStatus
{
    public string FilePath { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public DateTime LastModified { get; set; }
    public int TotalRequirements { get; set; }
    public int CompletedRequirements { get; set; }
    public int InProgressRequirements { get; set; }
    public int PendingRequirements { get; set; }
    public int TotalUserStories { get; set; }
    public double CompletionPercentage => TotalRequirements > 0 ?
        (double)CompletedRequirements / TotalRequirements * 100 : 0;
    public List<string> RecentChanges { get; set; } = new();
}

// Enums
public enum RequirementType
{
    Functional,
    NonFunctional,
    Business,
    Technical,
    Security,
    Performance,
    Usability,
    Accessibility,
    Compliance
}

public enum RequirementPriority
{
    Critical = 1,
    High = 2,
    Medium = 3,
    Low = 4,
    Nice = 5
}

public enum RequirementStatus
{
    Draft,
    Approved,
    InProgress,
    Completed,
    Blocked,
    Cancelled,
    OnHold
}

public enum UserStoryPriority
{
    MustHave = 1,
    ShouldHave = 2,
    CouldHave = 3,
    WontHave = 4
}

public enum SectionType
{
    Overview,
    BusinessContext,
    UserPersonas,
    FunctionalRequirements,
    NonFunctionalRequirements,
    UserStories,
    TechnicalSpecifications,
    Architecture,
    SecurityRequirements,
    TestingStrategy,
    Timeline,
    RiskAssessment,
    Appendix,
    Custom
}

/// <summary>
/// Configuration for PRD generation operations
/// </summary>
public class PrdGenerationOptions
{
    public string ProductName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? TargetAudience { get; set; }
    public string Template { get; set; } = "standard";
    public bool IncludeTechnicalSpecs { get; set; } = true;
    public bool IncludeUserStories { get; set; } = true;
    public bool IncludeAcceptanceCriteria { get; set; } = true;
    public string? OutputPath { get; set; }
    public List<string> AdditionalSections { get; set; } = new();
    public Dictionary<string, object> CustomProperties { get; set; } = new();
}

/// <summary>
/// Configuration for PRD update operations
/// </summary>
public class PrdUpdateOptions
{
    public string FilePath { get; set; } = string.Empty;
    public string? Section { get; set; }
    public string? Content { get; set; }
    public string Mode { get; set; } = "append"; // append, replace, insert
    public bool CreateBackup { get; set; } = true;
    public Dictionary<string, object> UpdateMetadata { get; set; } = new();
}

/// <summary>
/// Configuration for PRD validation operations
/// </summary>
public class PrdValidationOptions
{
    public string FilePath { get; set; } = string.Empty;
    public string Strictness { get; set; } = "standard"; // lenient, standard, strict
    public bool IncludeSuggestions { get; set; } = true;
    public List<string> RequiredSections { get; set; } = new();
    public List<string> SkipValidations { get; set; } = new();
    public Dictionary<string, object> ValidationRules { get; set; } = new();
}

/// <summary>
/// Information about a PRD template
/// </summary>
public class PrdTemplateInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<string> RequiredFields { get; set; } = new();
    public Dictionary<string, object> DefaultValues { get; set; } = new();
    public string Complexity { get; set; } = "standard";
    public bool IsDefault { get; set; } = false;
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public List<string> Requirements { get; set; } = new();
}

/// <summary>
/// Request to update a PRD document
/// </summary>
public class PrdUpdateRequest
{
    public string FilePath { get; set; } = string.Empty;
    public string? Section { get; set; }
    public string? Content { get; set; }
    public string UpdateType { get; set; } = "append"; // append, replace, insert
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Result of PRD generation operation
/// </summary>
public class PrdGenerationResult
{
    public bool Success { get; set; }
    public string? OutputFile { get; set; }
    public List<string> Sections { get; set; } = new();
    public int WordCount { get; set; }
    public string EstimatedReadTime { get; set; } = string.Empty;
    public string? Message { get; set; }
}

/// <summary>
/// Result of PRD loading operation
/// </summary>
public class PrdLoadResult
{
    public bool Success { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public List<string> Sections { get; set; } = new();
    public string? Message { get; set; }
    public PrdAnalysis? Analysis { get; set; }
    public PrdRequirements? Requirements { get; set; }
}

/// <summary>
/// Analysis information for a PRD
/// </summary>
public class PrdAnalysis
{
    public int WordCount { get; set; }
    public int SectionCount { get; set; }
    public string EstimatedReadTime { get; set; } = string.Empty;
    public string Completeness { get; set; } = string.Empty;
    public string[] MissingElements { get; set; } = Array.Empty<string>();
    public string[] Recommendations { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Requirements extracted from a PRD
/// </summary>
public class PrdRequirements
{
    public string[] Functional { get; set; } = Array.Empty<string>();
    public string[] NonFunctional { get; set; } = Array.Empty<string>();
    public string[] UserStories { get; set; } = Array.Empty<string>();
    public string[] AcceptanceCriteria { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Result of PRD validation operation
/// </summary>
public class PrdValidationResult
{
    public bool Success { get; set; }
    public bool IsValid { get; set; }
    public double OverallScore { get; set; }
    public double CompletenessScore { get; set; }
    public double ClarityScore { get; set; }
    public double ConsistencyScore { get; set; }
    public double FeasibilityScore { get; set; }
    public IEnumerable<object>? Errors { get; set; }
    public IEnumerable<object>? Warnings { get; set; }
    public IEnumerable<PrdSuggestion>? Suggestions { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Suggestion for PRD improvement
/// </summary>
public class PrdSuggestion
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Result of PRD update operation
/// </summary>
public class PrdUpdateResult
{
    public bool Success { get; set; }
    public string? BackupFile { get; set; }
    public List<string>? UpdatedSections { get; set; }
    public string? AddedContent { get; set; }
    public string? ChangesSummary { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Extended template information with additional properties
/// </summary>
public class PrdTemplateInfoExtended : PrdTemplateInfo
{
    public string Version { get; set; } = string.Empty;
    public List<string>? Sections { get; set; }
    public List<string>? Features { get; set; }
    public string EstimatedGenerationTime { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
}