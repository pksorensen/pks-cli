using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for managing VS Code extensions in devcontainers
/// </summary>
public class VsCodeExtensionService : IVsCodeExtensionService
{
    private readonly ILogger<VsCodeExtensionService> _logger;
    private List<VsCodeExtension> _extensions = new();
    private readonly object _lock = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly TimeSpan _cacheTimeout = TimeSpan.FromHours(24);

    public VsCodeExtensionService(ILogger<VsCodeExtensionService> logger)
    {
        _logger = logger;
        _ = Task.Run(InitializeExtensionsAsync); // Initialize asynchronously
    }

    public async Task<List<VsCodeExtension>> GetRecommendedExtensionsAsync(string[] categories)
    {
        await EnsureExtensionsLoadedAsync();

        if (!categories.Any())
        {
            return new List<VsCodeExtension>();
        }

        lock (_lock)
        {
            return _extensions.Where(e => categories.Contains(e.Category, StringComparer.OrdinalIgnoreCase)).ToList();
        }
    }

    public async Task<List<VsCodeExtension>> SearchExtensionsAsync(string query)
    {
        await EnsureExtensionsLoadedAsync();

        if (string.IsNullOrEmpty(query))
        {
            return new List<VsCodeExtension>();
        }

        lock (_lock)
        {
            var lowerQuery = query.ToLowerInvariant();
            return _extensions.Where(e =>
                e.Name.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) ||
                e.Description.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) ||
                e.Tags.Any(t => t.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase)) ||
                e.Category.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) ||
                e.Id.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) ||
                e.Publisher.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }
    }

    public async Task<ExtensionValidationResult> ValidateExtensionAsync(string extensionId)
    {
        var result = new ExtensionValidationResult();

        try
        {
            await EnsureExtensionsLoadedAsync();

            lock (_lock)
            {
                var extension = _extensions.FirstOrDefault(e => e.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));

                result.Exists = extension != null;
                result.IsValid = extension != null;
                result.IsCompatible = extension != null; // For now, assume all extensions are compatible

                if (extension != null)
                {
                    result.LatestVersion = extension.Version;
                    result.Dependencies = extension.RequiredFeatures.ToList();
                }
                else
                {
                    result.ErrorMessage = $"Extension '{extensionId}' not found in registry";
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating extension {ExtensionId}", extensionId);
            result.IsValid = false;
            result.Exists = false;
            result.IsCompatible = false;
            result.ErrorMessage = $"Validation error: {ex.Message}";
            return result;
        }
    }

    public async Task<List<VsCodeExtension>> GetEssentialExtensionsAsync(List<string> features)
    {
        await EnsureExtensionsLoadedAsync();

        if (!features.Any())
        {
            return new List<VsCodeExtension>();
        }

        lock (_lock)
        {
            var essentialExtensions = new List<VsCodeExtension>();

            foreach (var feature in features)
            {
                var relevantExtensions = _extensions.Where(e =>
                    e.IsEssential &&
                    (e.RequiredFeatures.Contains(feature, StringComparer.OrdinalIgnoreCase) ||
                     e.Tags.Any(t => feature.Contains(t, StringComparison.OrdinalIgnoreCase)))).ToList();

                essentialExtensions.AddRange(relevantExtensions);
            }

            // Remove duplicates
            return essentialExtensions.DistinctBy(e => e.Id).ToList();
        }
    }

    public async Task<List<string>> GetAvailableCategoriesAsync()
    {
        await EnsureExtensionsLoadedAsync();

        lock (_lock)
        {
            return _extensions.Select(e => e.Category).Distinct().OrderBy(c => c).ToList();
        }
    }

    public async Task<List<VsCodeExtension>> GetExtensionsByCategoryAsync(string category)
    {
        await EnsureExtensionsLoadedAsync();

        lock (_lock)
        {
            return _extensions.Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    public async Task<string?> GetLatestVersionAsync(string extensionId)
    {
        await EnsureExtensionsLoadedAsync();

        lock (_lock)
        {
            return _extensions.FirstOrDefault(e => e.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase))?.Version;
        }
    }

    public async Task<Dictionary<string, ExtensionValidationResult>> ValidateExtensionsAsync(List<string> extensionIds)
    {
        var results = new Dictionary<string, ExtensionValidationResult>();

        foreach (var extensionId in extensionIds)
        {
            results[extensionId] = await ValidateExtensionAsync(extensionId);
        }

        return results;
    }

    private async Task EnsureExtensionsLoadedAsync()
    {
        bool needsRefresh;

        lock (_lock)
        {
            needsRefresh = !_extensions.Any() || DateTime.UtcNow - _lastRefresh > _cacheTimeout;
        }

        if (needsRefresh)
        {
            await RefreshExtensionsAsync();
        }
    }

    private async Task InitializeExtensionsAsync()
    {
        try
        {
            var extensions = await LoadBuiltInExtensionsAsync();

            lock (_lock)
            {
                _extensions = extensions;
                _lastRefresh = DateTime.UtcNow;
            }

            _logger.LogInformation("Initialized with {Count} built-in extensions", extensions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize extensions");
        }
    }

    private async Task RefreshExtensionsAsync()
    {
        try
        {
            _logger.LogInformation("Refreshing VS Code extensions");

            var newExtensions = await LoadBuiltInExtensionsAsync();

            lock (_lock)
            {
                _extensions = newExtensions;
                _lastRefresh = DateTime.UtcNow;
            }

            _logger.LogInformation("Successfully refreshed {Count} extensions", newExtensions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh extensions");
        }
    }

    private static async Task<List<VsCodeExtension>> LoadBuiltInExtensionsAsync()
    {
        await Task.Delay(50); // Simulate async operation

        return new List<VsCodeExtension>
        {
            // .NET Development Extensions
            new()
            {
                Id = "ms-dotnettools.csharp",
                Name = "C#",
                Publisher = "ms-dotnettools",
                Description = "C# for Visual Studio Code (powered by OmniSharp)",
                Category = "language",
                Tags = new[] { "csharp", "dotnet", "language", "programming" },
                Version = "2.0.0",
                IsEssential = true,
                RequiredFeatures = new[] { "dotnet" }
            },
            new()
            {
                Id = "ms-dotnettools.vscode-dotnet-runtime",
                Name = ".NET Install Tool",
                Publisher = "ms-dotnettools",
                Description = "Installs and manages .NET runtimes and SDKs",
                Category = "runtime",
                Tags = new[] { "dotnet", "runtime", "sdk" },
                Version = "2.0.0",
                IsEssential = true,
                RequiredFeatures = new[] { "dotnet" }
            },
            new()
            {
                Id = "ms-dotnettools.blazorwasm-companion",
                Name = "Blazor WASM Debugging",
                Publisher = "ms-dotnettools",
                Description = "Companion extension for debugging Blazor WebAssembly applications",
                Category = "debugger",
                Tags = new[] { "blazor", "webassembly", "debugging" },
                Version = "1.0.0",
                IsEssential = false,
                RequiredFeatures = new[] { "dotnet" }
            },

            // Node.js Development Extensions
            new()
            {
                Id = "ms-vscode.vscode-typescript-next",
                Name = "TypeScript Importer",
                Publisher = "ms-vscode",
                Description = "TypeScript and JavaScript language support",
                Category = "language",
                Tags = new[] { "typescript", "javascript", "node" },
                Version = "5.0.0",
                IsEssential = true,
                RequiredFeatures = new[] { "node" }
            },
            new()
            {
                Id = "esbenp.prettier-vscode",
                Name = "Prettier - Code formatter",
                Publisher = "esbenp",
                Description = "Code formatter using prettier",
                Category = "formatter",
                Tags = new[] { "formatter", "javascript", "typescript", "css", "html" },
                Version = "10.0.0",
                IsEssential = false,
                RequiredFeatures = new[] { "node" }
            },
            new()
            {
                Id = "bradlc.vscode-tailwindcss",
                Name = "Tailwind CSS IntelliSense",
                Publisher = "bradlc",
                Description = "Intelligent Tailwind CSS tooling for VS Code",
                Category = "language",
                Tags = new[] { "css", "tailwind", "styling" },
                Version = "0.10.0",
                IsEssential = false,
                RequiredFeatures = new[] { "node" }
            },

            // Docker Extensions
            new()
            {
                Id = "ms-vscode.vscode-docker",
                Name = "Docker",
                Publisher = "ms-vscode",
                Description = "Makes it easy to create, manage, and debug containerized applications",
                Category = "tool",
                Tags = new[] { "docker", "container", "devops" },
                Version = "1.28.0",
                IsEssential = true,
                RequiredFeatures = new[] { "docker-in-docker" }
            },

            // Kubernetes Extensions
            new()
            {
                Id = "ms-kubernetes-tools.vscode-kubernetes-tools",
                Name = "Kubernetes",
                Publisher = "ms-kubernetes-tools",
                Description = "Develop, deploy and debug Kubernetes applications",
                Category = "kubernetes",
                Tags = new[] { "kubernetes", "kubectl", "helm", "devops" },
                Version = "1.3.0",
                IsEssential = true,
                RequiredFeatures = new[] { "kubectl-helm-minikube" }
            },

            // Azure Extensions
            new()
            {
                Id = "ms-azuretools.vscode-azure-account",
                Name = "Azure Account",
                Publisher = "ms-azuretools",
                Description = "A common Sign-In and Subscription management extension for VS Code",
                Category = "cloud",
                Tags = new[] { "azure", "cloud", "account" },
                Version = "0.12.0",
                IsEssential = true,
                RequiredFeatures = new[] { "azure-cli" }
            },
            new()
            {
                Id = "ms-azuretools.vscode-azureresourcegroups",
                Name = "Azure Resources",
                Publisher = "ms-azuretools",
                Description = "View and manage Azure resources directly from VS Code",
                Category = "cloud",
                Tags = new[] { "azure", "cloud", "resources" },
                Version = "0.8.0",
                IsEssential = false,
                RequiredFeatures = new[] { "azure-cli" }
            },
            new()
            {
                Id = "ms-azuretools.vscode-azurefunctions",
                Name = "Azure Functions",
                Publisher = "ms-azuretools",
                Description = "An Azure Functions extension for Visual Studio Code",
                Category = "cloud",
                Tags = new[] { "azure", "functions", "serverless" },
                Version = "1.13.0",
                IsEssential = false,
                RequiredFeatures = new[] { "azure-cli" }
            },

            // Git Extensions
            new()
            {
                Id = "eamodio.gitlens",
                Name = "GitLens — Git supercharged",
                Publisher = "eamodio",
                Description = "Supercharge Git within VS Code",
                Category = "scm",
                Tags = new[] { "git", "scm", "version-control" },
                Version = "14.0.0",
                IsEssential = false,
                RequiredFeatures = new[] { "git" }
            },

            // GitHub Extensions
            new()
            {
                Id = "github.vscode-github-actions",
                Name = "GitHub Actions",
                Publisher = "github",
                Description = "GitHub Actions workflows and runs for github.com",
                Category = "tool",
                Tags = new[] { "github", "actions", "ci-cd" },
                Version = "0.26.0",
                IsEssential = false,
                RequiredFeatures = new[] { "github-cli" }
            },
            new()
            {
                Id = "github.vscode-pull-request-github",
                Name = "GitHub Pull Requests and Issues",
                Publisher = "github",
                Description = "Pull Request and Issue Provider for GitHub",
                Category = "tool",
                Tags = new[] { "github", "pull-request", "issues" },
                Version = "0.74.0",
                IsEssential = false,
                RequiredFeatures = new[] { "github-cli" }
            },

            // General Development Extensions
            new()
            {
                Id = "ms-vscode.vscode-json",
                Name = "JSON Language Features",
                Publisher = "ms-vscode",
                Description = "JSON language support for Visual Studio Code",
                Category = "language",
                Tags = new[] { "json", "language" },
                Version = "1.0.0",
                IsEssential = false,
                RequiredFeatures = Array.Empty<string>()
            },
            new()
            {
                Id = "redhat.vscode-yaml",
                Name = "YAML",
                Publisher = "redhat",
                Description = "YAML Language Support by Red Hat",
                Category = "language",
                Tags = new[] { "yaml", "language" },
                Version = "1.14.0",
                IsEssential = false,
                RequiredFeatures = Array.Empty<string>()
            },
            new()
            {
                Id = "ms-vscode.vscode-eslint",
                Name = "ESLint",
                Publisher = "ms-vscode",
                Description = "Integrates ESLint JavaScript into VS Code",
                Category = "linter",
                Tags = new[] { "eslint", "javascript", "typescript", "linter" },
                Version = "2.4.0",
                IsEssential = false,
                RequiredFeatures = new[] { "node" }
            },
            new()
            {
                Id = "ms-python.python",
                Name = "Python",
                Publisher = "ms-python",
                Description = "IntelliSense, Linting, Debugging, code formatting, refactoring, unit tests, and more",
                Category = "language",
                Tags = new[] { "python", "language", "programming" },
                Version = "2023.0.0",
                IsEssential = false,
                RequiredFeatures = Array.Empty<string>()
            },
            new()
            {
                Id = "ms-vscode.powershell",
                Name = "PowerShell",
                Publisher = "ms-vscode",
                Description = "Develop PowerShell modules, commands and scripts in Visual Studio Code",
                Category = "language",
                Tags = new[] { "powershell", "scripting" },
                Version = "2023.0.0",
                IsEssential = false,
                RequiredFeatures = Array.Empty<string>()
            }
        };
    }
}