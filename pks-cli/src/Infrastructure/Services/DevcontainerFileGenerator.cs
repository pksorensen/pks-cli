using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for generating devcontainer files
/// </summary>
public class DevcontainerFileGenerator : IDevcontainerFileGenerator
{
    private readonly ILogger<DevcontainerFileGenerator> _logger;

    public DevcontainerFileGenerator(ILogger<DevcontainerFileGenerator> logger)
    {
        _logger = logger;
    }

    public async Task<FileGenerationResult> GenerateDevcontainerJsonAsync(DevcontainerConfiguration configuration, string outputPath)
    {
        var result = new FileGenerationResult();

        try
        {
            var devcontainerDir = Path.Combine(outputPath, ".devcontainer");
            var filePath = Path.Combine(devcontainerDir, "devcontainer.json");

            // Ensure .devcontainer directory exists
            Directory.CreateDirectory(devcontainerDir);

            // Generate JSON content
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            var content = JsonSerializer.Serialize(configuration, jsonOptions);

            // Write file
            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

            result.Success = true;
            result.FilePath = filePath;
            result.Content = content;
            result.FileSize = content.Length;

            _logger.LogDebug("Generated devcontainer.json at {FilePath}", filePath);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate devcontainer.json");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    public async Task<FileGenerationResult> GenerateDockerfileAsync(DevcontainerConfiguration configuration, string outputPath)
    {
        var result = new FileGenerationResult();

        try
        {
            var devcontainerDir = Path.Combine(outputPath, ".devcontainer");
            var filePath = Path.Combine(devcontainerDir, "Dockerfile");

            // Only generate Dockerfile if configuration uses build instead of image
            if (configuration.Build == null)
            {
                result.Success = false;
                result.ErrorMessage = "Configuration does not specify build configuration";
                return result;
            }

            // Ensure .devcontainer directory exists
            Directory.CreateDirectory(devcontainerDir);

            var content = GenerateDockerfileContent(configuration);

            // Write file
            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

            result.Success = true;
            result.FilePath = filePath;
            result.Content = content;
            result.FileSize = content.Length;

            _logger.LogDebug("Generated Dockerfile at {FilePath}", filePath);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Dockerfile");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    public async Task<FileGenerationResult> GenerateDockerComposeAsync(DevcontainerConfiguration configuration, string outputPath)
    {
        var result = new FileGenerationResult();

        try
        {
            var devcontainerDir = Path.Combine(outputPath, ".devcontainer");
            var filePath = Path.Combine(devcontainerDir, "docker-compose.yml");

            // Only generate docker-compose.yml if configuration specifies it
            if (string.IsNullOrEmpty(configuration.DockerComposeFile))
            {
                result.Success = false;
                result.ErrorMessage = "Configuration does not specify Docker Compose";
                return result;
            }

            // Ensure .devcontainer directory exists
            Directory.CreateDirectory(devcontainerDir);

            var content = GenerateDockerComposeContent(configuration);

            // Write file
            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

            result.Success = true;
            result.FilePath = filePath;
            result.Content = content;
            result.FileSize = content.Length;

            _logger.LogDebug("Generated docker-compose.yml at {FilePath}", filePath);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate docker-compose.yml");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    public async Task<PathValidationResult> ValidateOutputPathAsync(string path)
    {
        var result = new PathValidationResult();

        try
        {
            if (string.IsNullOrEmpty(path))
            {
                result.IsValid = false;
                result.Errors.Add("Output path cannot be empty");
                return result;
            }

            var fullPath = Path.GetFullPath(path);
            result.ResolvedPath = fullPath;

            var directory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);

            if (string.IsNullOrEmpty(directory))
            {
                result.IsValid = false;
                result.Errors.Add("Invalid directory path");
                return result;
            }

            result.PathExists = Directory.Exists(directory);
            result.IsDirectory = Directory.Exists(fullPath);

            if (!result.PathExists)
            {
                // Try to create the directory
                try
                {
                    Directory.CreateDirectory(directory);
                    result.PathExists = true;
                    result.CanWrite = true;
                }
                catch (Exception ex)
                {
                    result.CanWrite = false;
                    result.Errors.Add($"Cannot create directory: {ex.Message}");
                }
            }
            else
            {
                // Check write permissions
                try
                {
                    var testFile = Path.Combine(directory, $".test_{Guid.NewGuid():N}");
                    await File.WriteAllTextAsync(testFile, "test");
                    File.Delete(testFile);
                    result.CanWrite = true;
                }
                catch
                {
                    result.CanWrite = false;
                    result.Errors.Add("Directory is not writable");
                }
            }

            result.IsValid = result.PathExists && result.CanWrite;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating output path");
            result.IsValid = false;
            result.Errors.Add($"Path validation error: {ex.Message}");
            return result;
        }
    }

    public async Task<FileGenerationResult> GenerateGitIgnoreAsync(string outputPath)
    {
        var result = new FileGenerationResult();

        try
        {
            var filePath = Path.Combine(outputPath, ".gitignore");

            var content = GenerateGitIgnoreContent();

            // If .gitignore already exists, append our entries
            if (File.Exists(filePath))
            {
                var existingContent = await File.ReadAllTextAsync(filePath);
                if (!existingContent.Contains("# Devcontainer"))
                {
                    content = existingContent + Environment.NewLine + content;
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = "Devcontainer entries already exist in .gitignore";
                    return result;
                }
            }

            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

            result.Success = true;
            result.FilePath = filePath;
            result.Content = content;
            result.FileSize = content.Length;

            _logger.LogDebug("Generated/updated .gitignore at {FilePath}", filePath);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate .gitignore");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    public async Task<FileGenerationResult> GenerateVSCodeSettingsAsync(DevcontainerConfiguration configuration, string outputPath)
    {
        var result = new FileGenerationResult();

        try
        {
            var vscodeDir = Path.Combine(outputPath, ".vscode");
            var filePath = Path.Combine(vscodeDir, "settings.json");

            // Ensure .vscode directory exists
            Directory.CreateDirectory(vscodeDir);

            var settings = GenerateVSCodeSettings(configuration);

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var content = JsonSerializer.Serialize(settings, jsonOptions);

            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

            result.Success = true;
            result.FilePath = filePath;
            result.Content = content;
            result.FileSize = content.Length;

            _logger.LogDebug("Generated VS Code settings.json at {FilePath}", filePath);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate VS Code settings.json");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    public async Task<FileGenerationResult> GenerateReadmeAsync(DevcontainerConfiguration configuration, string outputPath)
    {
        var result = new FileGenerationResult();

        try
        {
            var filePath = Path.Combine(outputPath, "README-devcontainer.md");

            var content = GenerateReadmeContent(configuration);

            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);

            result.Success = true;
            result.FilePath = filePath;
            result.Content = content;
            result.FileSize = content.Length;

            _logger.LogDebug("Generated README-devcontainer.md at {FilePath}", filePath);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate README-devcontainer.md");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    public async Task<List<FileGenerationResult>> GenerateAllFilesAsync(DevcontainerConfiguration configuration, string outputPath, DevcontainerOptions? options = null)
    {
        var results = new List<FileGenerationResult>();

        try
        {
            // Always generate devcontainer.json
            results.Add(await GenerateDevcontainerJsonAsync(configuration, outputPath));

            // Generate Dockerfile if build configuration is specified
            if (configuration.Build != null)
            {
                results.Add(await GenerateDockerfileAsync(configuration, outputPath));
            }

            // Generate docker-compose.yml if specified
            if (!string.IsNullOrEmpty(configuration.DockerComposeFile))
            {
                results.Add(await GenerateDockerComposeAsync(configuration, outputPath));
            }

            // Generate additional files based on options
            if (options != null)
            {
                if (options.CustomSettings.ContainsKey("generateGitIgnore") &&
                    options.CustomSettings["generateGitIgnore"] is true)
                {
                    results.Add(await GenerateGitIgnoreAsync(outputPath));
                }

                if (options.CustomSettings.ContainsKey("generateVSCodeSettings") &&
                    options.CustomSettings["generateVSCodeSettings"] is true)
                {
                    results.Add(await GenerateVSCodeSettingsAsync(configuration, outputPath));
                }

                if (options.CustomSettings.ContainsKey("generateReadme") &&
                    options.CustomSettings["generateReadme"] is true)
                {
                    results.Add(await GenerateReadmeAsync(configuration, outputPath));
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating devcontainer files");
            results.Add(new FileGenerationResult
            {
                Success = false,
                ErrorMessage = $"Failed to generate files: {ex.Message}"
            });
            return results;
        }
    }

    private static string GenerateDockerfileContent(DevcontainerConfiguration configuration)
    {
        var baseImage = !string.IsNullOrEmpty(configuration.Image) ? configuration.Image : "mcr.microsoft.com/dotnet/sdk:8.0";

        var dockerfile = new StringBuilder();
        dockerfile.AppendLine($"FROM {baseImage}");
        dockerfile.AppendLine();
        dockerfile.AppendLine("# Install additional tools");
        dockerfile.AppendLine("RUN apt-get update && apt-get install -y \\");
        dockerfile.AppendLine("    git \\");
        dockerfile.AppendLine("    curl \\");
        dockerfile.AppendLine("    wget \\");
        dockerfile.AppendLine("    unzip \\");
        dockerfile.AppendLine("    && rm -rf /var/lib/apt/lists/*");
        dockerfile.AppendLine();

        // Add feature-specific installations based on configuration
        if (configuration.Features.Any(f => f.Key.Contains("node")))
        {
            dockerfile.AppendLine("# Install Node.js");
            dockerfile.AppendLine("RUN curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \\");
            dockerfile.AppendLine("    && apt-get install -y nodejs");
            dockerfile.AppendLine();
        }

        if (configuration.Features.Any(f => f.Key.Contains("azure-cli")))
        {
            dockerfile.AppendLine("# Install Azure CLI");
            dockerfile.AppendLine("RUN curl -sL https://aka.ms/InstallAzureCLIDeb | bash");
            dockerfile.AppendLine();
        }

        dockerfile.AppendLine("# Set working directory");
        dockerfile.AppendLine($"WORKDIR {configuration.WorkspaceFolder ?? "/workspaces"}");
        dockerfile.AppendLine();

        dockerfile.AppendLine("# Configure git");
        dockerfile.AppendLine($"RUN git config --global --add safe.directory {configuration.WorkspaceFolder ?? "/workspaces"}");
        dockerfile.AppendLine();

        if (configuration.Features.Any(f => f.Key.Contains("dotnet")))
        {
            dockerfile.AppendLine("# Install dotnet tools");
            dockerfile.AppendLine("RUN dotnet tool install -g dotnet-ef");
            dockerfile.AppendLine("RUN dotnet tool install -g dotnet-aspnet-codegenerator");
            dockerfile.AppendLine();
            dockerfile.AppendLine("ENV PATH=\"$PATH:/root/.dotnet/tools\"");
            dockerfile.AppendLine();
        }

        dockerfile.AppendLine("CMD [\"sleep\", \"infinity\"]");

        return dockerfile.ToString();
    }

    private static string GenerateDockerComposeContent(DevcontainerConfiguration configuration)
    {
        var compose = new StringBuilder();
        compose.AppendLine("version: '3.8'");
        compose.AppendLine();
        compose.AppendLine("services:");
        compose.AppendLine($"  {configuration.Service ?? "devcontainer"}:");

        if (configuration.Build != null)
        {
            compose.AppendLine("    build:");
            compose.AppendLine("      context: .");
            compose.AppendLine($"      dockerfile: {configuration.Build.Dockerfile ?? "Dockerfile"}");
        }
        else
        {
            compose.AppendLine($"    image: {configuration.Image}");
        }

        compose.AppendLine("    volumes:");
        compose.AppendLine("      - ../..:/workspaces:cached");

        if (configuration.Mounts?.Any() == true)
        {
            foreach (var mount in configuration.Mounts)
            {
                compose.AppendLine($"      - {mount}");
            }
        }

        compose.AppendLine("    command: sleep infinity");

        if (configuration.ForwardPorts?.Any() == true)
        {
            compose.AppendLine("    ports:");
            foreach (var port in configuration.ForwardPorts)
            {
                compose.AppendLine($"      - '{port}:{port}'");
            }
        }

        if (configuration.RemoteEnv?.Any() == true || configuration.ContainerEnv?.Any() == true)
        {
            compose.AppendLine("    environment:");

            foreach (var env in configuration.RemoteEnv ?? new Dictionary<string, string>())
            {
                compose.AppendLine($"      - {env.Key}={env.Value}");
            }

            foreach (var env in configuration.ContainerEnv ?? new Dictionary<string, string>())
            {
                compose.AppendLine($"      - {env.Key}={env.Value}");
            }
        }

        // Add common services for development
        compose.AppendLine();
        compose.AppendLine("  # Uncomment to add a database service");
        compose.AppendLine("  # database:");
        compose.AppendLine("  #   image: postgres:15");
        compose.AppendLine("  #   restart: unless-stopped");
        compose.AppendLine("  #   volumes:");
        compose.AppendLine("  #     - postgres-data:/var/lib/postgresql/data");
        compose.AppendLine("  #   environment:");
        compose.AppendLine("  #     POSTGRES_PASSWORD: postgres");
        compose.AppendLine("  #     POSTGRES_USER: postgres");
        compose.AppendLine("  #     POSTGRES_DB: testdb");
        compose.AppendLine("  #   ports:");
        compose.AppendLine("  #     - '5432:5432'");
        compose.AppendLine();
        compose.AppendLine("# volumes:");
        compose.AppendLine("#   postgres-data:");

        return compose.ToString();
    }

    private static string GenerateGitIgnoreContent()
    {
        return @"
# Devcontainer
.devcontainer/.vscode/
.devcontainer/docker-compose.override.yml
.devcontainer/docker-compose.override.yaml

# VS Code
.vscode/settings.json
.vscode/launch.json
.vscode/extensions.json
.vscode/*.code-workspace

# Docker
.docker/

# Development certificates
*.pfx
*.p12
";
    }

    private static object GenerateVSCodeSettings(DevcontainerConfiguration configuration)
    {
        var settings = new Dictionary<string, object>();

        // Add common settings
        settings["files.watcherExclude"] = new
        {
            binFolder = "**/{bin,obj}/**",
            nodeModules = "**/node_modules/**"
        };

        // Add feature-specific settings
        if (configuration.Features.Any(f => f.Key.Contains("dotnet")))
        {
            settings["dotnet.completion.showCompletionItemsFromUnimportedNamespaces"] = true;
            settings["dotnet.inlayHints.enableInlayHintsForParameters"] = true;
            settings["dotnet.inlayHints.enableInlayHintsForLiteralParameters"] = true;
            settings["dotnet.inlayHints.enableInlayHintsForIndexerParameters"] = true;
            settings["dotnet.inlayHints.enableInlayHintsForObjectCreationParameters"] = true;
            settings["dotnet.inlayHints.enableInlayHintsForOtherParameters"] = true;
            settings["dotnet.inlayHints.enableInlayHintsForTypes"] = true;
            settings["dotnet.inlayHints.enableInlayHintsForImplicitVariableTypes"] = true;
            settings["dotnet.inlayHints.enableInlayHintsForLambdaParameterTypes"] = true;
            settings["dotnet.inlayHints.enableInlayHintsForImplicitObjectCreation"] = true;
        }

        if (configuration.Features.Any(f => f.Key.Contains("node")))
        {
            settings["typescript.preferences.quoteStyle"] = "double";
            settings["javascript.preferences.quoteStyle"] = "double";
            settings["typescript.suggest.autoImports"] = true;
            settings["javascript.suggest.autoImports"] = true;
        }

        return settings;
    }

    private static string GenerateReadmeContent(DevcontainerConfiguration configuration)
    {
        var readme = new StringBuilder();
        readme.AppendLine($"# {configuration.Name} - Development Container");
        readme.AppendLine();
        readme.AppendLine("This repository includes a complete development environment setup using VS Code Development Containers.");
        readme.AppendLine();
        readme.AppendLine("## Getting Started");
        readme.AppendLine();
        readme.AppendLine("### Prerequisites");
        readme.AppendLine();
        readme.AppendLine("- [VS Code](https://code.visualstudio.com/)");
        readme.AppendLine("- [Dev Containers extension](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers)");
        readme.AppendLine("- [Docker Desktop](https://www.docker.com/products/docker-desktop)");
        readme.AppendLine();
        readme.AppendLine("### Opening in a Development Container");
        readme.AppendLine();
        readme.AppendLine("1. Clone this repository");
        readme.AppendLine("2. Open the folder in VS Code");
        readme.AppendLine("3. When prompted, click 'Reopen in Container' or run the command 'Dev Containers: Reopen in Container'");
        readme.AppendLine("4. Wait for the container to build and start");
        readme.AppendLine();

        if (!string.IsNullOrEmpty(configuration.PostCreateCommand))
        {
            readme.AppendLine("### Post-Create Setup");
            readme.AppendLine();
            readme.AppendLine($"The following command will be run automatically after the container is created:");
            readme.AppendLine();
            readme.AppendLine("```bash");
            readme.AppendLine(configuration.PostCreateCommand);
            readme.AppendLine("```");
            readme.AppendLine();
        }

        if (configuration.ForwardPorts?.Any() == true)
        {
            readme.AppendLine("### Port Forwarding");
            readme.AppendLine();
            readme.AppendLine("The following ports are automatically forwarded:");
            readme.AppendLine();
            foreach (var port in configuration.ForwardPorts)
            {
                readme.AppendLine($"- `{port}` - Application port");
            }
            readme.AppendLine();
        }

        if (configuration.Features?.Any() == true)
        {
            readme.AppendLine("### Included Features");
            readme.AppendLine();
            foreach (var feature in configuration.Features.Keys)
            {
                var featureName = feature.Split('/').Last().Split(':').First();
                readme.AppendLine($"- **{featureName}** - {feature}");
            }
            readme.AppendLine();
        }

        readme.AppendLine("### Development");
        readme.AppendLine();
        readme.AppendLine("Once the container is running, you can:");
        readme.AppendLine();
        readme.AppendLine("- Start coding immediately with all dependencies pre-installed");
        readme.AppendLine("- Use the integrated terminal for command-line operations");
        readme.AppendLine("- Debug your application using VS Code's debugging features");
        readme.AppendLine("- Access forwarded ports from your host machine");
        readme.AppendLine();
        readme.AppendLine("### Customization");
        readme.AppendLine();
        readme.AppendLine("You can customize this development environment by modifying:");
        readme.AppendLine();
        readme.AppendLine("- `.devcontainer/devcontainer.json` - Main configuration file");

        if (configuration.Build != null)
        {
            readme.AppendLine("- `.devcontainer/Dockerfile` - Container build instructions");
        }

        if (!string.IsNullOrEmpty(configuration.DockerComposeFile))
        {
            readme.AppendLine("- `.devcontainer/docker-compose.yml` - Multi-service setup");
        }

        readme.AppendLine();
        readme.AppendLine("For more information about Development Containers, see the [official documentation](https://containers.dev/).");

        return readme.ToString();
    }
}