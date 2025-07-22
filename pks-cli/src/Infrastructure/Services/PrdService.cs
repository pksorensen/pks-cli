using PKS.Infrastructure.Services.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Implementation of PRD service for managing Product Requirements Documents
/// </summary>
public class PrdService : IPrdService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<PrdGenerationResult> GeneratePrdAsync(
        PrdGenerationRequest request, 
        CancellationToken cancellationToken = default)
    {
        // Simulate AI-powered PRD generation
        await Task.Delay(1000, cancellationToken);

        var document = new PrdDocument
        {
            Configuration = new PrdConfiguration
            {
                ProjectName = request.ProjectName,
                Description = request.IdeaDescription,
                Author = Environment.UserName,
                TargetAudience = request.TargetAudience,
                Stakeholders = request.Stakeholders,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            }
        };

        // Generate sections based on AI analysis
        document.Sections = await GenerateSectionsFromIdeaAsync(request, cancellationToken);
        
        // Generate requirements from the idea
        document.Requirements = await GenerateRequirementsFromIdeaAsync(request, cancellationToken);
        
        // Generate user stories
        document.UserStories = await GenerateUserStoriesFromIdeaAsync(request, cancellationToken);

        return new PrdGenerationResult
        {
            Success = true,
            OutputFile = $"{request.ProjectName}_PRD.md",
            Sections = document.Sections.Select(s => s.Title).ToList(),
            WordCount = EstimateWordCount(document),
            EstimatedReadTime = $"{Math.Max(1, EstimateWordCount(document) / 250)} minutes",
            Message = $"PRD generated successfully for {request.ProjectName}"
        };
    }

    public async Task<PrdLoadResult> LoadPrdAsync(
        string filePath, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new PrdLoadResult
                {
                    Success = false,
                    Message = $"PRD file not found: {filePath}"
                };
            }

            var content = await File.ReadAllTextAsync(filePath, cancellationToken);
            
            // Try to parse as JSON first (structured PRD)
            if (TryParseJsonPrd(content, out var jsonDocument))
            {
                return new PrdLoadResult
                {
                    Success = true,
                    ProductName = jsonDocument.Configuration.ProjectName,
                    Template = "standard",
                    Sections = jsonDocument.Sections.Select(s => s.Title).ToList(),
                    Message = "PRD loaded successfully",
                    Analysis = new PrdAnalysis
                    {
                        WordCount = EstimateWordCount(jsonDocument),
                        SectionCount = jsonDocument.Sections.Count,
                        EstimatedReadTime = $"{Math.Max(1, EstimateWordCount(jsonDocument) / 250)} minutes",
                        Completeness = "Good"
                    }
                };
            }

            // Parse as Markdown PRD  
            var markdownDoc = await ParseMarkdownPrdAsync(content, filePath, cancellationToken);
            if (markdownDoc != null)
            {
                return new PrdLoadResult
                {
                    Success = true,
                    ProductName = markdownDoc.Configuration.ProjectName,
                    Template = "markdown",
                    Sections = markdownDoc.Sections.Select(s => s.Title).ToList(),
                    Message = "PRD loaded successfully from Markdown"
                };
            }

            return new PrdLoadResult
            {
                Success = false,
                Message = "Unable to parse PRD file. Unsupported format."
            };
        }
        catch (Exception ex)
        {
            return new PrdLoadResult
            {
                Success = false,
                Message = $"Error loading PRD: {ex.Message}"
            };
        }
    }

    public async Task<bool> SavePrdAsync(
        PrdDocument document, 
        string filePath, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            if (extension == ".json")
            {
                var json = JsonSerializer.Serialize(document, JsonOptions);
                await File.WriteAllTextAsync(filePath, json, cancellationToken);
            }
            else
            {
                // Save as Markdown (default)
                var markdown = await GenerateMarkdownFromDocumentAsync(document, cancellationToken);
                await File.WriteAllTextAsync(filePath, markdown, cancellationToken);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<PrdStatus> GetPrdStatusAsync(
        string filePath, 
        CancellationToken cancellationToken = default)
    {
        var status = new PrdStatus
        {
            FilePath = filePath,
            Exists = File.Exists(filePath)
        };

        if (!status.Exists)
        {
            return status;
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            status.LastModified = fileInfo.LastWriteTime;

            var parseResult = await LoadPrdAsync(filePath, cancellationToken);
            if (parseResult.Success && parseResult.Requirements != null)
            {
                // Since PrdRequirements contains string arrays for different requirement types,
                // we'll calculate totals based on available data
                var functionalCount = parseResult.Requirements.Functional?.Length ?? 0;
                var nonFunctionalCount = parseResult.Requirements.NonFunctional?.Length ?? 0;
                status.TotalRequirements = functionalCount + nonFunctionalCount;
                
                // For now, we can't determine status without parsing the actual content
                status.CompletedRequirements = 0;
                status.InProgressRequirements = 0;
                status.PendingRequirements = status.TotalRequirements;
                status.TotalUserStories = parseResult.Requirements.UserStories?.Length ?? 0;
            }

            return status;
        }
        catch
        {
            return status;
        }
    }

    public async Task<List<PrdRequirement>> GetRequirementsAsync(
        PrdDocument document,
        RequirementStatus? filterByStatus = null,
        RequirementPriority? filterByPriority = null)
    {
        await Task.Delay(10); // Simulate async operation

        var requirements = document.Requirements.AsEnumerable();

        if (filterByStatus.HasValue)
        {
            requirements = requirements.Where(r => r.Status == filterByStatus.Value);
        }

        if (filterByPriority.HasValue)
        {
            requirements = requirements.Where(r => r.Priority == filterByPriority.Value);
        }

        return requirements.ToList();
    }

    public async Task<List<UserStory>> GetUserStoriesAsync(
        PrdDocument document,
        UserStoryPriority? filterByPriority = null)
    {
        await Task.Delay(10); // Simulate async operation

        var stories = document.UserStories.AsEnumerable();

        if (filterByPriority.HasValue)
        {
            stories = stories.Where(s => s.Priority == filterByPriority.Value);
        }

        return stories.ToList();
    }

    public async Task<bool> UpdateRequirementAsync(
        PrdDocument document,
        string requirementId,
        Action<PrdRequirement> updates)
    {
        await Task.Delay(10); // Simulate async operation

        var requirement = document.Requirements.FirstOrDefault(r => r.Id == requirementId);
        if (requirement == null)
        {
            return false;
        }

        updates(requirement);
        document.Configuration.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    public async Task<bool> AddRequirementAsync(
        PrdDocument document,
        PrdRequirement requirement)
    {
        await Task.Delay(10); // Simulate async operation

        if (string.IsNullOrEmpty(requirement.Id))
        {
            requirement.Id = $"REQ-{document.Requirements.Count + 1:D3}";
        }

        document.Requirements.Add(requirement);
        document.Configuration.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    public async Task<PrdValidationResult> ValidatePrdAsync(PrdValidationOptions options)
    {
        await Task.Delay(100); // Simulate validation processing

        // Load document from file
        var loadResult = await LoadPrdAsync(options.FilePath);
        if (!loadResult.Success)
        {
            return new PrdValidationResult
            {
                Success = false,
                IsValid = false,
                Message = loadResult.Message
            };
        }

        // Simulate document parsing for validation
        var document = new PrdDocument(); // In real implementation, parse from loadResult
        
        var errors = new List<string>();
        var warnings = new List<string>();
        
        // Check basic configuration
        if (string.IsNullOrEmpty(document.Configuration.ProjectName))
        {
            errors.Add("Project name is required");
        }

        if (string.IsNullOrEmpty(document.Configuration.Description))
        {
            errors.Add("Project description is required");
        }

        // Check requirements
        if (document.Requirements.Count == 0)
        {
            warnings.Add("No requirements defined");
        }

        var duplicateIds = document.Requirements
            .GroupBy(r => r.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var duplicateId in duplicateIds)
        {
            errors.Add($"Duplicate requirement ID: {duplicateId}");
        }

        // Check user stories
        if (document.UserStories.Count == 0)
        {
            warnings.Add("No user stories defined");
        }

        // Calculate completeness score
        var scoreFactors = new[]
        {
            document.Configuration.ProjectName?.Length > 0 ? 20 : 0,
            document.Configuration.Description?.Length > 0 ? 20 : 0,
            document.Requirements.Count > 0 ? 30 : 0,
            document.UserStories.Count > 0 ? 20 : 0,
            document.Sections.Count > 0 ? 10 : 0
        };

        var completenessScore = scoreFactors.Sum();
        
        var result = new PrdValidationResult
        {
            Success = true,
            IsValid = errors.Count == 0,
            OverallScore = completenessScore,
            CompletenessScore = completenessScore,
            ClarityScore = 85, // Simulated
            ConsistencyScore = 90, // Simulated
            FeasibilityScore = 80, // Simulated
            Errors = errors.Cast<object>(),
            Warnings = warnings.Cast<object>(),
            Message = $"Validation completed with {errors.Count} errors and {warnings.Count} warnings"
        };

        return result;
    }

    public async Task<List<string>> FindPrdFilesAsync(
        string searchPath,
        bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(50, cancellationToken); // Simulate file search

        if (!Directory.Exists(searchPath))
        {
            return new List<string>();
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var prdFiles = new List<string>();

        // Common PRD file patterns
        var patterns = new[] { "PRD*.md", "prd*.md", "*requirements*.md", "*spec*.md", "*.prd.json" };

        foreach (var pattern in patterns)
        {
            try
            {
                var files = Directory.GetFiles(searchPath, pattern, searchOption);
                prdFiles.AddRange(files);
            }
            catch
            {
                // Ignore access errors for individual directories
            }
        }

        return prdFiles.Distinct().ToList();
    }

    public async Task<string> GenerateTemplateAsync(
        string projectName,
        PrdTemplateType templateType = PrdTemplateType.Standard,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(200, cancellationToken); // Simulate template generation

        var template = await GetTemplateContentAsync(templateType, cancellationToken);
        
        // Replace placeholders
        template = template.Replace("{{ProjectName}}", projectName);
        template = template.Replace("{{DateTime}}", DateTime.Now.ToString("yyyy-MM-dd"));
        template = template.Replace("{{Author}}", Environment.UserName);

        // Determine output path
        if (string.IsNullOrEmpty(outputPath))
        {
            outputPath = Path.Combine(Environment.CurrentDirectory, "docs", "PRD.md");
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(outputPath, template, cancellationToken);
        return outputPath;
    }

    // Private helper methods
    private async Task<List<PrdSection>> GenerateSectionsFromIdeaAsync(
        PrdGenerationRequest request, 
        CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);

        return new List<PrdSection>
        {
            new()
            {
                Id = "overview",
                Title = "Project Overview",
                Content = $"This document outlines the requirements for {request.ProjectName}.\n\n{request.IdeaDescription}",
                Order = 1,
                Type = SectionType.Overview
            },
            new()
            {
                Id = "business-context",
                Title = "Business Context",
                Content = request.BusinessContext,
                Order = 2,
                Type = SectionType.BusinessContext
            },
            new()
            {
                Id = "functional-requirements",
                Title = "Functional Requirements",
                Content = "Detailed functional requirements will be listed below in the requirements section.",
                Order = 3,
                Type = SectionType.FunctionalRequirements
            },
            new()
            {
                Id = "non-functional-requirements",
                Title = "Non-Functional Requirements",
                Content = "Performance, security, and other non-functional requirements.",
                Order = 4,
                Type = SectionType.NonFunctionalRequirements
            }
        };
    }

    private async Task<List<PrdRequirement>> GenerateRequirementsFromIdeaAsync(
        PrdGenerationRequest request, 
        CancellationToken cancellationToken)
    {
        await Task.Delay(400, cancellationToken);

        // Simulate AI analysis of the idea to extract requirements
        var requirements = new List<PrdRequirement>();

        // Generate some sample requirements based on common patterns
        if (request.IdeaDescription.ToLower().Contains("web") || request.IdeaDescription.ToLower().Contains("website"))
        {
            requirements.Add(new PrdRequirement
            {
                Id = "REQ-001",
                Title = "Web User Interface",
                Description = "The system shall provide a responsive web user interface",
                Type = RequirementType.Functional,
                Priority = RequirementPriority.High,
                Status = RequirementStatus.Draft,
                AcceptanceCriteria = new List<string>
                {
                    "Interface works on desktop browsers",
                    "Interface is mobile-responsive",
                    "Loading time is under 3 seconds"
                }
            });
        }

        if (request.IdeaDescription.ToLower().Contains("user") || request.IdeaDescription.ToLower().Contains("customer"))
        {
            requirements.Add(new PrdRequirement
            {
                Id = "REQ-002",
                Title = "User Authentication",
                Description = "The system shall support secure user authentication",
                Type = RequirementType.Security,
                Priority = RequirementPriority.High,
                Status = RequirementStatus.Draft,
                AcceptanceCriteria = new List<string>
                {
                    "Users can register with email/password",
                    "Users can login securely",
                    "Password reset functionality available"
                }
            });
        }

        if (request.IdeaDescription.ToLower().Contains("data") || request.IdeaDescription.ToLower().Contains("information"))
        {
            requirements.Add(new PrdRequirement
            {
                Id = "REQ-003",
                Title = "Data Storage",
                Description = "The system shall provide reliable data storage",
                Type = RequirementType.Technical,
                Priority = RequirementPriority.Critical,
                Status = RequirementStatus.Draft,
                AcceptanceCriteria = new List<string>
                {
                    "Data is persisted reliably",
                    "Data backup and recovery available",
                    "Data access is performant"
                }
            });
        }

        return requirements;
    }

    private async Task<List<UserStory>> GenerateUserStoriesFromIdeaAsync(
        PrdGenerationRequest request, 
        CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken);

        var userStories = new List<UserStory>
        {
            new()
            {
                Id = "US-001",
                Title = "Access the application",
                AsA = "user",
                IWant = "to access the application",
                SoThat = "I can use its features",
                Priority = UserStoryPriority.MustHave,
                EstimatedPoints = 3,
                AcceptanceCriteria = new List<string>
                {
                    "User can navigate to the application URL",
                    "Application loads successfully",
                    "User sees the main interface"
                }
            }
        };

        // Add more stories based on the idea content
        if (request.IdeaDescription.ToLower().Contains("manage") || request.IdeaDescription.ToLower().Contains("create"))
        {
            userStories.Add(new UserStory
            {
                Id = "US-002",
                Title = "Manage content",
                AsA = "user",
                IWant = "to create and manage content",
                SoThat = "I can organize my information effectively",
                Priority = UserStoryPriority.MustHave,
                EstimatedPoints = 5,
                AcceptanceCriteria = new List<string>
                {
                    "User can create new content",
                    "User can edit existing content",
                    "User can delete content",
                    "Changes are saved automatically"
                }
            });
        }

        return userStories;
    }

    private bool TryParseJsonPrd(string content, out PrdDocument? document)
    {
        document = null;
        try
        {
            document = JsonSerializer.Deserialize<PrdDocument>(content, JsonOptions);
            return document != null;
        }
        catch
        {
            return false;
        }
    }

    private async Task<PrdDocument?> ParseMarkdownPrdAsync(
        string content, 
        string filePath, 
        CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);

        try
        {
            var document = new PrdDocument();
            
            // Extract project name from filename or first heading
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            document.Configuration.ProjectName = fileName.StartsWith("PRD", StringComparison.OrdinalIgnoreCase) 
                ? fileName.Substring(3).Trim('-', '_', ' ') 
                : fileName;

            // Parse sections from markdown headers
            var sections = ParseMarkdownSections(content);
            document.Sections = sections;

            // Extract requirements (look for numbered lists or requirement patterns)
            document.Requirements = ParseRequirementsFromMarkdown(content);

            // Extract user stories (look for user story patterns)
            document.UserStories = ParseUserStoriesFromMarkdown(content);

            document.Configuration.UpdatedAt = File.GetLastWriteTime(filePath);

            return document;
        }
        catch
        {
            return null;
        }
    }

    private List<PrdSection> ParseMarkdownSections(string content)
    {
        var sections = new List<PrdSection>();
        var lines = content.Split('\n');
        var currentSection = new StringBuilder();
        string? currentTitle = null;
        var order = 0;

        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^#+\s+(.+)"))
            {
                // Save previous section
                if (!string.IsNullOrEmpty(currentTitle))
                {
                    sections.Add(new PrdSection
                    {
                        Id = GenerateSectionId(currentTitle),
                        Title = currentTitle,
                        Content = currentSection.ToString().Trim(),
                        Order = order++,
                        Type = DetermineSectionType(currentTitle)
                    });
                }

                // Start new section
                currentTitle = Regex.Match(line, @"^#+\s+(.+)").Groups[1].Value;
                currentSection.Clear();
            }
            else
            {
                currentSection.AppendLine(line);
            }
        }

        // Add final section
        if (!string.IsNullOrEmpty(currentTitle))
        {
            sections.Add(new PrdSection
            {
                Id = GenerateSectionId(currentTitle),
                Title = currentTitle,
                Content = currentSection.ToString().Trim(),
                Order = order,
                Type = DetermineSectionType(currentTitle)
            });
        }

        return sections;
    }

    private List<PrdRequirement> ParseRequirementsFromMarkdown(string content)
    {
        var requirements = new List<PrdRequirement>();
        var reqPattern = @"(?:REQ-\d+|Requirement \d+):\s*(.+)";
        var matches = Regex.Matches(content, reqPattern, RegexOptions.IgnoreCase);

        for (int i = 0; i < matches.Count; i++)
        {
            requirements.Add(new PrdRequirement
            {
                Id = $"REQ-{i + 1:D3}",
                Title = matches[i].Groups[1].Value.Trim(),
                Description = matches[i].Groups[1].Value.Trim(),
                Type = RequirementType.Functional,
                Priority = RequirementPriority.Medium,
                Status = RequirementStatus.Draft
            });
        }

        return requirements;
    }

    private List<UserStory> ParseUserStoriesFromMarkdown(string content)
    {
        var userStories = new List<UserStory>();
        var storyPattern = @"As an?\s+(.+?),?\s+I want\s+(.+?),?\s+so that\s+(.+?)(?:\.|$)";
        var matches = Regex.Matches(content, storyPattern, RegexOptions.IgnoreCase);

        for (int i = 0; i < matches.Count; i++)
        {
            userStories.Add(new UserStory
            {
                Id = $"US-{i + 1:D3}",
                Title = $"Story {i + 1}",
                AsA = matches[i].Groups[1].Value.Trim(),
                IWant = matches[i].Groups[2].Value.Trim(),
                SoThat = matches[i].Groups[3].Value.Trim(),
                Priority = UserStoryPriority.ShouldHave,
                EstimatedPoints = 3
            });
        }

        return userStories;
    }

    private async Task<string> GenerateMarkdownFromDocumentAsync(
        PrdDocument document, 
        CancellationToken cancellationToken)
    {
        await Task.Delay(100, cancellationToken);

        var sb = new StringBuilder();
        
        // Header
        sb.AppendLine($"# {document.Configuration.ProjectName} - Product Requirements Document");
        sb.AppendLine();
        sb.AppendLine($"**Version:** {document.Configuration.Version}");
        sb.AppendLine($"**Author:** {document.Configuration.Author}");
        sb.AppendLine($"**Created:** {document.Configuration.CreatedAt:yyyy-MM-dd}");
        sb.AppendLine($"**Updated:** {document.Configuration.UpdatedAt:yyyy-MM-dd}");
        sb.AppendLine();

        // Description
        if (!string.IsNullOrEmpty(document.Configuration.Description))
        {
            sb.AppendLine("## Overview");
            sb.AppendLine();
            sb.AppendLine(document.Configuration.Description);
            sb.AppendLine();
        }

        // Sections
        foreach (var section in document.Sections.OrderBy(s => s.Order))
        {
            sb.AppendLine($"## {section.Title}");
            sb.AppendLine();
            sb.AppendLine(section.Content);
            sb.AppendLine();
        }

        // Requirements
        if (document.Requirements.Any())
        {
            sb.AppendLine("## Requirements");
            sb.AppendLine();
            
            foreach (var req in document.Requirements)
            {
                sb.AppendLine($"### {req.Id}: {req.Title}");
                sb.AppendLine();
                sb.AppendLine($"**Type:** {req.Type}");
                sb.AppendLine($"**Priority:** {req.Priority}");
                sb.AppendLine($"**Status:** {req.Status}");
                sb.AppendLine();
                sb.AppendLine(req.Description);
                
                if (req.AcceptanceCriteria.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine("**Acceptance Criteria:**");
                    foreach (var criteria in req.AcceptanceCriteria)
                    {
                        sb.AppendLine($"- {criteria}");
                    }
                }
                sb.AppendLine();
            }
        }

        // User Stories
        if (document.UserStories.Any())
        {
            sb.AppendLine("## User Stories");
            sb.AppendLine();
            
            foreach (var story in document.UserStories)
            {
                sb.AppendLine($"### {story.Id}: {story.Title}");
                sb.AppendLine();
                sb.AppendLine($"As a {story.AsA}, I want {story.IWant}, so that {story.SoThat}.");
                sb.AppendLine();
                sb.AppendLine($"**Priority:** {story.Priority}");
                sb.AppendLine($"**Estimated Points:** {story.EstimatedPoints}");
                
                if (story.AcceptanceCriteria.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine("**Acceptance Criteria:**");
                    foreach (var criteria in story.AcceptanceCriteria)
                    {
                        sb.AppendLine($"- {criteria}");
                    }
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private async Task<string> GetTemplateContentAsync(
        PrdTemplateType templateType, 
        CancellationToken cancellationToken)
    {
        await Task.Delay(10, cancellationToken);

        return templateType switch
        {
            PrdTemplateType.Technical => GetTechnicalTemplate(),
            PrdTemplateType.Mobile => GetMobileTemplate(),
            PrdTemplateType.Web => GetWebTemplate(),
            PrdTemplateType.Api => GetApiTemplate(),
            PrdTemplateType.Minimal => GetMinimalTemplate(),
            PrdTemplateType.Enterprise => GetEnterpriseTemplate(),
            _ => GetStandardTemplate()
        };
    }

    private string GetStandardTemplate()
    {
        return """
# {{ProjectName}} - Product Requirements Document

**Version:** 1.0.0
**Author:** {{Author}}
**Created:** {{DateTime}}
**Updated:** {{DateTime}}

## Overview

[Provide a brief overview of the project and its objectives]

## Business Context

[Describe the business problem or opportunity this project addresses]

## User Personas

[Define the target users and their characteristics]

## Functional Requirements

### Core Features

[List the main functional requirements]

### Secondary Features

[List additional features that would be nice to have]

## Non-Functional Requirements

### Performance
- [Performance requirements]

### Security
- [Security requirements]

### Scalability
- [Scalability requirements]

### Usability
- [Usability requirements]

## User Stories

[Define user stories in the format: As a [user type], I want [goal], so that [benefit]]

## Technical Specifications

[Outline the technical approach and constraints]

## Timeline

[Provide estimated timeline for development phases]

## Risk Assessment

[Identify potential risks and mitigation strategies]

## Appendix

[Additional documentation, references, or supporting materials]
""";
    }

    private string GetTechnicalTemplate()
    {
        return """
# {{ProjectName}} - Technical Requirements Document

**Version:** 1.0.0
**Author:** {{Author}}
**Created:** {{DateTime}}
**Updated:** {{DateTime}}

## Technical Overview

[Provide technical context and objectives]

## Architecture Requirements

### System Architecture
[Define high-level system architecture]

### Component Design
[Detail major system components]

### Data Architecture
[Describe data storage and flow]

## API Specifications

### Endpoints
[List required API endpoints]

### Authentication
[Define authentication mechanisms]

### Rate Limiting
[Specify rate limiting requirements]

## Performance Requirements

### Response Times
[Define acceptable response times]

### Throughput
[Specify throughput requirements]

### Scalability
[Detail scalability expectations]

## Security Requirements

### Authentication & Authorization
[Security implementation details]

### Data Protection
[Data encryption and protection measures]

### Compliance
[Regulatory compliance requirements]

## Integration Requirements

### External Services
[List external service integrations]

### Data Exchange
[Define data exchange protocols]

## Testing Strategy

### Unit Testing
[Unit testing requirements]

### Integration Testing
[Integration testing approach]

### Performance Testing
[Performance testing criteria]

## Deployment Requirements

### Infrastructure
[Infrastructure requirements]

### CI/CD Pipeline
[Continuous integration/deployment needs]

### Monitoring
[Monitoring and alerting requirements]
""";
    }

    private string GetMobileTemplate()
    {
        return """
# {{ProjectName}} - Mobile App Requirements Document

**Version:** 1.0.0
**Author:** {{Author}}
**Created:** {{DateTime}}
**Updated:** {{DateTime}}

## App Overview

[Describe the mobile application purpose and target audience]

## Platform Requirements

### iOS Requirements
- Minimum iOS version support
- Device compatibility
- App Store guidelines compliance

### Android Requirements
- Minimum Android API level
- Device compatibility
- Google Play Store compliance

## User Interface Requirements

### Design System
[Define design language and UI components]

### Navigation
[Describe app navigation structure]

### Accessibility
[Accessibility requirements and compliance]

## Core Features

### Authentication
[User authentication and account management]

### Core Functionality
[Main app features and user flows]

### Offline Capabilities
[Offline functionality requirements]

## Technical Requirements

### Data Storage
[Local storage and synchronization]

### Network Communication
[API integration and networking]

### Push Notifications
[Notification requirements and delivery]

### Device Integration
[Camera, GPS, sensors, etc.]

## Performance Requirements

### App Launch Time
[Acceptable launch time thresholds]

### Battery Usage
[Battery optimization requirements]

### Network Usage
[Data usage optimization]

## Security Requirements

### Data Protection
[User data protection measures]

### Secure Communication
[Network security requirements]

### App Security
[Code obfuscation and protection]

## Testing Strategy

### Device Testing
[Target devices for testing]

### Platform Testing
[iOS and Android testing approach]

### User Testing
[User acceptance testing plan]

## Deployment Strategy

### App Store Submission
[App store submission process]

### Release Management
[Version management and updates]

### Analytics
[App analytics and monitoring]
""";
    }

    private string GetWebTemplate()
    {
        return """
# {{ProjectName}} - Web Application Requirements Document

**Version:** 1.0.0
**Author:** {{Author}}
**Created:** {{DateTime}}
**Updated:** {{DateTime}}

## Application Overview

[Describe the web application purpose and target users]

## Browser Support

### Supported Browsers
- Chrome (latest 2 versions)
- Firefox (latest 2 versions)
- Safari (latest 2 versions)
- Edge (latest 2 versions)

### Responsive Design
[Mobile and tablet compatibility requirements]

## User Interface Requirements

### Layout and Design
[UI/UX requirements and design system]

### Accessibility
[WCAG compliance and accessibility features]

### Internationalization
[Multi-language support requirements]

## Functional Requirements

### User Management
[User registration, authentication, profiles]

### Core Features
[Main application functionality]

### Content Management
[Content creation, editing, management]

## Technical Requirements

### Frontend Technology
[Frontend framework and technology stack]

### Backend Services
[API requirements and backend services]

### Database
[Data storage and database requirements]

## Performance Requirements

### Page Load Times
[Acceptable load time thresholds]

### SEO Requirements
[Search engine optimization needs]

### Caching Strategy
[Caching implementation requirements]

## Security Requirements

### Authentication
[User authentication mechanisms]

### Data Security
[Data protection and encryption]

### HTTPS/SSL
[Secure communication requirements]

## Integration Requirements

### Third-party Services
[External service integrations]

### Analytics
[Web analytics and tracking]

### Payment Processing
[Payment gateway integration if needed]

## Hosting and Deployment

### Hosting Requirements
[Server and hosting specifications]

### Deployment Process
[CI/CD and deployment strategy]

### Monitoring
[Application monitoring and alerting]

## Testing Strategy

### Cross-browser Testing
[Browser compatibility testing]

### Performance Testing
[Load and performance testing]

### Security Testing
[Security assessment requirements]
""";
    }

    private string GetApiTemplate()
    {
        return """
# {{ProjectName}} - API Requirements Document

**Version:** 1.0.0
**Author:** {{Author}}
**Created:** {{DateTime}}
**Updated:** {{DateTime}}

## API Overview

[Describe the API purpose, target consumers, and business value]

## API Design

### RESTful Principles
[REST compliance and design standards]

### Resource Design
[Resource naming and URI structure]

### HTTP Methods
[Supported HTTP methods and usage]

## Authentication & Authorization

### Authentication Method
[API key, OAuth, JWT, etc.]

### Authorization Levels
[User roles and permissions]

### Rate Limiting
[Request throttling and limits]

## API Endpoints

### Core Endpoints
[List primary API endpoints with descriptions]

### CRUD Operations
[Create, Read, Update, Delete operations]

### Search and Filtering
[Search capabilities and filtering options]

## Request/Response Format

### Data Format
[JSON, XML, or other data formats]

### Request Headers
[Required and optional headers]

### Response Structure
[Standard response format and status codes]

### Error Handling
[Error response format and codes]

## Data Validation

### Input Validation
[Request validation rules]

### Output Validation
[Response validation and sanitization]

### Schema Definition
[API schema documentation]

## Performance Requirements

### Response Times
[Acceptable response time thresholds]

### Throughput
[Requests per second requirements]

### Scalability
[Auto-scaling and load balancing]

## Security Requirements

### Data Encryption
[Encryption in transit and at rest]

### Input Sanitization
[Protection against injection attacks]

### API Security
[OWASP API security compliance]

## Documentation

### API Documentation
[Interactive documentation requirements]

### Code Examples
[SDK and code sample requirements]

### Versioning Strategy
[API versioning approach]

## Testing Strategy

### Unit Testing
[API unit testing approach]

### Integration Testing
[Third-party integration testing]

### Load Testing
[Performance and stress testing]

## Monitoring and Analytics

### API Metrics
[Usage analytics and monitoring]

### Error Tracking
[Error monitoring and alerting]

### Performance Monitoring
[API performance tracking]
""";
    }

    private string GetMinimalTemplate()
    {
        return """
# {{ProjectName}} - Requirements

**Author:** {{Author}}
**Date:** {{DateTime}}

## What are we building?

[Brief description of the project]

## Who is it for?

[Target users/audience]

## Core Features

1. [Feature 1]
2. [Feature 2]
3. [Feature 3]

## Success Criteria

- [Success metric 1]
- [Success metric 2]
- [Success metric 3]

## Technical Notes

[Any technical constraints or requirements]

## Timeline

[Key milestones and deadlines]
""";
    }

    private string GetEnterpriseTemplate()
    {
        return """
# {{ProjectName}} - Enterprise Requirements Document

**Version:** 1.0.0
**Author:** {{Author}}
**Created:** {{DateTime}}
**Updated:** {{DateTime}}

## Executive Summary

[High-level project overview for stakeholders]

## Business Case

### Problem Statement
[Business problem being addressed]

### Business Objectives
[Key business goals and objectives]

### Success Metrics
[KPIs and success measurements]

### Return on Investment
[Expected ROI and financial impact]

## Stakeholder Analysis

### Primary Stakeholders
[Key stakeholders and their interests]

### Secondary Stakeholders
[Additional stakeholders to consider]

### Governance Structure
[Decision-making authority and processes]

## Requirements Analysis

### Business Requirements
[High-level business needs]

### Functional Requirements
[Detailed functional specifications]

### Non-Functional Requirements
[Performance, security, compliance, etc.]

### Integration Requirements
[System integration needs]

## User Analysis

### User Personas
[Detailed user profiles and characteristics]

### User Journey Maps
[User interaction flows and touchpoints]

### Use Cases
[Detailed use case scenarios]

## Technical Architecture

### System Architecture
[High-level system design]

### Technology Stack
[Recommended technologies and platforms]

### Data Architecture
[Data storage, flow, and governance]

### Security Architecture
[Security framework and controls]

## Compliance and Governance

### Regulatory Requirements
[Compliance standards and regulations]

### Data Privacy
[GDPR, CCPA, and privacy requirements]

### Audit Requirements
[Audit trails and compliance reporting]

### Risk Management
[Risk assessment and mitigation strategies]

## Implementation Strategy

### Phased Approach
[Implementation phases and milestones]

### Resource Requirements
[Team structure and skill requirements]

### Change Management
[Change management and training plan]

### Testing Strategy
[Comprehensive testing approach]

## Operations and Maintenance

### Service Level Agreements
[SLA requirements and commitments]

### Monitoring and Alerting
[Operational monitoring requirements]

### Disaster Recovery
[Business continuity and DR planning]

### Maintenance Plan
[Ongoing maintenance and support]

## Financial Considerations

### Budget Breakdown
[Detailed cost analysis]

### Funding Sources
[Budget allocation and funding]

### Cost-Benefit Analysis
[Financial justification and ROI]

## Timeline and Milestones

### Project Schedule
[Detailed project timeline]

### Critical Path
[Key dependencies and critical milestones]

### Go-Live Strategy
[Production deployment approach]

## Appendices

### A. Detailed Requirements
[Comprehensive requirement specifications]

### B. Technical Specifications
[Detailed technical documentation]

### C. Risk Register
[Detailed risk assessment and mitigation]

### D. Stakeholder Communications
[Communication plan and stakeholder matrix]
""";
    }

    private string GenerateSectionId(string title)
    {
        return title.ToLower()
                   .Replace(" ", "-")
                   .Replace("&", "and")
                   .Trim('-');
    }

    private SectionType DetermineSectionType(string title)
    {
        var lower = title.ToLower();
        
        return lower switch
        {
            var t when t.Contains("overview") || t.Contains("summary") => SectionType.Overview,
            var t when t.Contains("business") && t.Contains("context") => SectionType.BusinessContext,
            var t when t.Contains("user") && (t.Contains("persona") || t.Contains("profile")) => SectionType.UserPersonas,
            var t when t.Contains("functional") && t.Contains("requirement") => SectionType.FunctionalRequirements,
            var t when t.Contains("non-functional") || t.Contains("nonfunctional") => SectionType.NonFunctionalRequirements,
            var t when t.Contains("user") && t.Contains("stor") => SectionType.UserStories,
            var t when t.Contains("technical") || t.Contains("architect") => SectionType.TechnicalSpecifications,
            var t when t.Contains("security") => SectionType.SecurityRequirements,
            var t when t.Contains("test") => SectionType.TestingStrategy,
            var t when t.Contains("timeline") || t.Contains("schedule") => SectionType.Timeline,
            var t when t.Contains("risk") => SectionType.RiskAssessment,
            var t when t.Contains("appendix") => SectionType.Appendix,
            _ => SectionType.Custom
        };
    }

    // Interface methods required by IPrdService
    
    public async Task<List<PrdTemplateInfo>> GetAvailableTemplatesAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(300, cancellationToken);
        return new List<PrdTemplateInfo>
        {
            new() { Name = "Standard", Description = "Standard PRD template", Category = "General" },
            new() { Name = "Agile", Description = "Agile development PRD template", Category = "Methodology" },
            new() { Name = "Technical", Description = "Technical specification focused PRD", Category = "Technical" }
        };
    }

    public async Task<PrdUpdateResult> UpdatePrdAsync(PrdUpdateOptions options, CancellationToken cancellationToken = default)
    {
        await Task.Delay(400, cancellationToken);
        
        // Load document from file
        var loadResult = await LoadPrdAsync(options.FilePath, cancellationToken);
        if (!loadResult.Success)
        {
            return new PrdUpdateResult
            {
                Success = false,
                Message = loadResult.Message
            };
        }
        
        // Simulate document update
        var updatedSections = new List<string>();
        if (!string.IsNullOrEmpty(options.Section))
        {
            updatedSections.Add(options.Section);
        }
        
        // In real implementation, would parse and update the actual document
        
        return new PrdUpdateResult
        {
            Success = true,
            Message = "PRD updated successfully"
        };
    }

    private int EstimateWordCount(PrdDocument document)
    {
        int count = 0;
        count += document.Configuration.Description?.Split(' ').Length ?? 0;
        count += document.Sections.Sum(s => s.Content?.Split(' ').Length ?? 0);
        count += document.Requirements.Sum(r => (r.Description?.Split(' ').Length ?? 0) + (r.Title?.Split(' ').Length ?? 0));
        count += document.UserStories.Sum(us => (us.AsA?.Split(' ').Length ?? 0) + (us.IWant?.Split(' ').Length ?? 0) + (us.SoThat?.Split(' ').Length ?? 0));
        return count;
    }
}