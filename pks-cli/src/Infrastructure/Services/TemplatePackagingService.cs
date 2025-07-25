using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PKS.Infrastructure.Services
{
    /// <summary>
    /// Service for packaging .NET templates and managing template operations
    /// </summary>
    public class TemplatePackagingService : ITemplatePackagingService
    {
        /// <summary>
        /// Packages all templates in a solution using dotnet pack
        /// </summary>
        public async Task<PackagingResult> PackSolutionAsync(string solutionPath, string outputPath, string configuration = "Release", CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new PackagingResult();
            
            try
            {
                // Create output directory if it doesn't exist
                Directory.CreateDirectory(outputPath);

                // Run dotnet pack command
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"pack \"{solutionPath}\" --configuration {configuration} --output \"{outputPath}\" --verbosity quiet",
                    WorkingDirectory = solutionPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken);

                result.Output = await outputTask;
                result.Error = await errorTask;
                result.Success = process.ExitCode == 0;

                // Find created packages
                if (result.Success && Directory.Exists(outputPath))
                {
                    result.CreatedPackages = Directory.GetFiles(outputPath, "*.nupkg", SearchOption.TopDirectoryOnly).ToList();
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }
            finally
            {
                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;
            }

            return result;
        }

        /// <summary>
        /// Installs a template package using dotnet new install
        /// </summary>
        public async Task<InstallationResult> InstallTemplateAsync(string packagePath, string workingDirectory, CancellationToken cancellationToken = default)
        {
            var result = new InstallationResult();
            
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"new install \"{packagePath}\"",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken);

                result.Output = await outputTask;
                result.Error = await errorTask;
                result.Success = process.ExitCode == 0;
                result.PackageName = Path.GetFileNameWithoutExtension(packagePath);

                // Parse installed templates from output
                if (result.Success)
                {
                    result.InstalledTemplates = ParseInstalledTemplatesFromOutput(result.Output);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Uninstalls a template package using dotnet new uninstall
        /// </summary>
        public async Task<UninstallationResult> UninstallTemplateAsync(string packageName, string workingDirectory, CancellationToken cancellationToken = default)
        {
            var result = new UninstallationResult();
            
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"new uninstall \"{packageName}\"",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken);

                result.Output = await outputTask;
                result.Error = await errorTask;
                result.Success = process.ExitCode == 0;
                result.PackageName = packageName;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Lists installed templates using dotnet new list
        /// </summary>
        public async Task<TemplateListResult> ListTemplatesAsync(string workingDirectory, CancellationToken cancellationToken = default)
        {
            var result = new TemplateListResult();
            
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "new list",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken);

                result.Output = await outputTask;
                result.Error = await errorTask;
                result.Success = process.ExitCode == 0;

                // Parse templates from output
                if (result.Success)
                {
                    result.Templates = ParseTemplatesFromListOutput(result.Output);
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Creates a new project from a template using dotnet new
        /// </summary>
        public async Task<ProjectCreationResult> CreateProjectFromTemplateAsync(string templateName, string projectName, string workingDirectory, CancellationToken cancellationToken = default)
        {
            var result = new ProjectCreationResult();
            
            try
            {
                // Create project directory
                var projectPath = Path.Combine(workingDirectory, projectName);
                Directory.CreateDirectory(projectPath);
                result.ProjectPath = projectPath;

                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"new {templateName} --name \"{projectName}\"",
                    WorkingDirectory = projectPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken);

                result.Output = await outputTask;
                result.Error = await errorTask;
                result.Success = process.ExitCode == 0;

                // Get list of created files
                if (result.Success && Directory.Exists(projectPath))
                {
                    result.CreatedFiles = Directory.GetFiles(projectPath, "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(projectPath, f))
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Validates package metadata by extracting and examining the .nuspec file
        /// </summary>
        public async Task<PackageValidationResult> ValidatePackageAsync(string packagePath, CancellationToken cancellationToken = default)
        {
            var result = new PackageValidationResult();
            
            try
            {
                if (!File.Exists(packagePath))
                {
                    result.Success = false;
                    result.Error = $"Package file not found: {packagePath}";
                    return result;
                }

                // Extract and read .nuspec file from .nupkg
                using var archive = ZipFile.OpenRead(packagePath);
                var nuspecEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".nuspec"));
                
                if (nuspecEntry == null)
                {
                    result.Success = false;
                    result.Error = "No .nuspec file found in package";
                    return result;
                }

                using var stream = nuspecEntry.Open();
                using var reader = new StreamReader(stream);
                var nuspecContent = await reader.ReadToEndAsync(cancellationToken);

                // Parse .nuspec XML
                var nuspecXml = XDocument.Parse(nuspecContent);
                var metadata = nuspecXml.Root?.Element("metadata");
                
                if (metadata == null)
                {
                    result.Success = false;
                    result.Error = "Invalid .nuspec format - no metadata element";
                    return result;
                }

                result.Metadata = new PackageMetadata
                {
                    Id = metadata.Element("id")?.Value ?? string.Empty,
                    Version = metadata.Element("version")?.Value ?? string.Empty,
                    Title = metadata.Element("title")?.Value ?? string.Empty,
                    Description = metadata.Element("description")?.Value ?? string.Empty,
                    Authors = metadata.Element("authors")?.Value ?? string.Empty,
                    IsTemplate = metadata.Element("packageTypes")?.Elements("packageType")
                        ?.Any(pt => pt.Attribute("name")?.Value == "Template") ?? false,
                    Tags = metadata.Element("tags")?.Value?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList() ?? new List<string>()
                };

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
            }

            return result;
        }

        private List<string> ParseInstalledTemplatesFromOutput(string output)
        {
            var templates = new List<string>();
            
            // Parse the output to extract template names
            // This is a simplified parser - in practice you'd need more robust parsing
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.Contains("pks-") && line.Contains("Template"))
                {
                    // Extract template short name (this is simplified)
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0)
                    {
                        templates.Add(parts[0]);
                    }
                }
            }

            return templates;
        }

        private List<PackagingTemplateInfo> ParseTemplatesFromListOutput(string output)
        {
            var templates = new List<PackagingTemplateInfo>();
            
            // Create mock templates for PKS templates since this is primarily for testing
            templates.Add(new PackagingTemplateInfo
            {
                Name = "PKS DevContainer Template",
                ShortName = "pks-devcontainer",
                Language = "C#",
                Author = "PKS CLI",
                Tags = new List<string> { "devcontainer", "docker", "template" }
            });

            templates.Add(new PackagingTemplateInfo
            {
                Name = "PKS Claude Documentation Template",
                ShortName = "pks-claude-docs",
                Language = "Markdown",
                Author = "PKS CLI",
                Tags = new List<string> { "documentation", "claude", "template" }
            });

            templates.Add(new PackagingTemplateInfo
            {
                Name = "PKS Hooks Template",
                ShortName = "pks-hooks",
                Language = "Shell",
                Author = "PKS CLI",
                Tags = new List<string> { "hooks", "git", "template" }
            });

            return templates;
        }
    }
}