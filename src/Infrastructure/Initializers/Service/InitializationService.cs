using PKS.Infrastructure.Initializers.Context;
using PKS.Infrastructure.Initializers.Registry;
using Spectre.Console;

namespace PKS.Infrastructure.Initializers.Service;

/// <summary>
/// Default implementation of the initialization service
/// </summary>
public class InitializationService : IInitializationService
{
    private readonly IInitializerRegistry _registry;

    public InitializationService(IInitializerRegistry registry)
    {
        _registry = registry;
    }

    public async Task<InitializationSummary> InitializeProjectAsync(InitializationContext context)
    {
        var summary = new InitializationSummary
        {
            ProjectName = context.ProjectName,
            Template = context.Template,
            TargetDirectory = context.TargetDirectory,
            StartTime = DateTime.Now
        };

        try
        {
            // Validate target directory
            var validation = await ValidateTargetDirectoryAsync(context.TargetDirectory, context.Force);
            if (!validation.IsValid)
            {
                summary.Success = false;
                summary.ErrorMessage = validation.ErrorMessage;
                summary.EndTime = DateTime.Now;
                return summary;
            }

            // Execute all applicable initializers
            var results = await _registry.ExecuteAllAsync(context);
            summary.Results = results.ToList();

            // Determine overall success
            summary.Success = results.All(r => r.Success);
            summary.EndTime = DateTime.Now;

            // Collect statistics
            summary.FilesCreated = results.SelectMany(r => r.AffectedFiles).Distinct().Count();
            summary.WarningsCount = results.Sum(r => r.Warnings.Count);
            summary.ErrorsCount = results.Sum(r => r.Errors.Count);

            // Display summary
            DisplaySummary(summary);

            return summary;
        }
        catch (Exception ex)
        {
            summary.Success = false;
            summary.ErrorMessage = ex.Message;
            summary.EndTime = DateTime.Now;

            AnsiConsole.WriteException(ex);
            return summary;
        }
    }

    public async Task<IEnumerable<TemplateInfo>> GetAvailableTemplatesAsync()
    {
        var templates = new List<TemplateInfo>();

        // Get templates from template directory
        var templateBasePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates");
        if (Directory.Exists(templateBasePath))
        {
            foreach (var templateDir in Directory.GetDirectories(templateBasePath))
            {
                var templateName = Path.GetFileName(templateDir);
                var infoFile = Path.Combine(templateDir, "template.json");

                var template = new TemplateInfo
                {
                    Name = templateName,
                    DisplayName = templateName.Replace("-", " ").Replace("_", " "),
                    Description = $"Template for {templateName} projects",
                    Path = templateDir
                };

                // Load additional info from template.json if it exists
                if (File.Exists(infoFile))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(infoFile);
                        var info = System.Text.Json.JsonSerializer.Deserialize<TemplateMetadata>(json);
                        if (info != null)
                        {
                            template.DisplayName = info.DisplayName ?? template.DisplayName;
                            template.Description = info.Description ?? template.Description;
                            template.Tags = info.Tags ?? new List<string>();
                            template.Author = info.Author;
                            template.Version = info.Version;
                        }
                    }
                    catch
                    {
                        // Ignore JSON parsing errors
                    }
                }

                templates.Add(template);
            }
        }

        // Add built-in templates
        templates.AddRange(GetBuiltInTemplates());

        return templates.OrderBy(t => t.DisplayName);
    }

    public Task<ValidationResult> ValidateTargetDirectoryAsync(string targetDirectory, bool force)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return Task.FromResult(ValidationResult.Invalid("Target directory cannot be empty"));
        }

        if (Directory.Exists(targetDirectory))
        {
            var files = Directory.GetFileSystemEntries(targetDirectory);
            if (files.Length > 0 && !force)
            {
                return Task.FromResult(ValidationResult.Invalid($"Directory '{targetDirectory}' is not empty. Use --force to overwrite."));
            }
        }
        else
        {
            // Try to create the directory to test permissions
            try
            {
                Directory.CreateDirectory(targetDirectory);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ValidationResult.Invalid($"Cannot create directory '{targetDirectory}': {ex.Message}"));
            }
        }

        return Task.FromResult(ValidationResult.Valid());
    }

    public ValidationResult ValidateProjectName(string projectName)
    {
        // Check for null or empty
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return ValidationResult.Invalid("Project name cannot be empty");
        }

        // Check for maximum length (typical filesystem limit)
        if (projectName.Length > 255)
        {
            return ValidationResult.Invalid("Project name is too long (maximum 255 characters)");
        }

        // Check for invalid characters (cross-platform set including Windows-specific ones)
        var invalidChars = new char[] { '/', '\\', ':', '*', '?', '<', '>', '|', '"', '\0' };
        var foundInvalidChars = projectName.Where(c => invalidChars.Contains(c)).Distinct().ToList();
        if (foundInvalidChars.Any())
        {
            return ValidationResult.Invalid($"Project name contains invalid characters: {string.Join(", ", foundInvalidChars.Select(c => $"'{c}'"))}");
        }

        // Check for reserved Windows device names
        var reservedNames = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        if (reservedNames.Contains(projectName.ToUpperInvariant()))
        {
            return ValidationResult.Invalid($"'{projectName}' is a reserved system name and cannot be used");
        }

        // Check if starts or ends with dot
        if (projectName.StartsWith('.') || projectName.EndsWith('.'))
        {
            return ValidationResult.Invalid("Project name cannot start or end with a dot");
        }

        // Check if starts or ends with space
        if (projectName.StartsWith(' ') || projectName.EndsWith(' '))
        {
            return ValidationResult.Invalid("Project name cannot start or end with a space");
        }

        return ValidationResult.Valid();
    }

    public InitializationContext CreateContext(string projectName, string template, string targetDirectory, bool force, Dictionary<string, object?> options)
    {
        return new InitializationContext
        {
            ProjectName = projectName,
            Template = template,
            TargetDirectory = targetDirectory,
            Force = force,
            WorkingDirectory = Environment.CurrentDirectory,
            Options = options,
            Interactive = !options.ContainsKey("non-interactive")
        };
    }

    private void DisplaySummary(InitializationSummary summary)
    {
        AnsiConsole.WriteLine();

        var panel = new Panel(CreateSummaryContent(summary))
            .Border(BoxBorder.Double)
            .BorderStyle(summary.Success ? "green" : "red")
            .Header(summary.Success ? " [bold green]✓ Initialization Complete[/] " : " [bold red]✗ Initialization Failed[/] ");

        AnsiConsole.Write(panel);
    }

    private string CreateSummaryContent(InitializationSummary summary)
    {
        var content = new List<string>
        {
            $"[bold]Project:[/] {summary.ProjectName}",
            $"[bold]Template:[/] {summary.Template}",
            $"[bold]Location:[/] {summary.TargetDirectory}",
            $"[bold]Duration:[/] {summary.Duration.TotalSeconds:F1}s"
        };

        if (summary.FilesCreated > 0)
        {
            content.Add($"[bold]Files Created:[/] {summary.FilesCreated}");
        }

        if (summary.WarningsCount > 0)
        {
            content.Add($"[bold yellow]Warnings:[/] {summary.WarningsCount}");
        }

        if (summary.ErrorsCount > 0)
        {
            content.Add($"[bold red]Errors:[/] {summary.ErrorsCount}");
        }

        if (!summary.Success && !string.IsNullOrEmpty(summary.ErrorMessage))
        {
            content.Add("");
            content.Add($"[red]Error: {summary.ErrorMessage}[/]");
        }

        return string.Join(Environment.NewLine, content);
    }

    private IEnumerable<TemplateInfo> GetBuiltInTemplates()
    {
        return new[]
        {
            new TemplateInfo
            {
                Name = "console",
                DisplayName = "Console Application",
                Description = "A simple .NET console application",
                Tags = new List<string> { "dotnet", "console", "basic" }
            },
            new TemplateInfo
            {
                Name = "api",
                DisplayName = "Web API",
                Description = "ASP.NET Core Web API application",
                Tags = new List<string> { "dotnet", "api", "web", "aspnetcore" }
            },
            new TemplateInfo
            {
                Name = "web",
                DisplayName = "Web Application",
                Description = "ASP.NET Core MVC web application",
                Tags = new List<string> { "dotnet", "web", "mvc", "aspnetcore" }
            },
            new TemplateInfo
            {
                Name = "agent",
                DisplayName = "Agentic Application",
                Description = "AI-powered agentic application with automation capabilities",
                Tags = new List<string> { "dotnet", "ai", "agent", "automation" }
            }
        };
    }
}