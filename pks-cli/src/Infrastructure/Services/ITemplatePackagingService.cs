using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.Infrastructure.Services
{
    /// <summary>
    /// Service for packaging .NET templates and managing template operations
    /// </summary>
    public interface ITemplatePackagingService
    {
        /// <summary>
        /// Packages all templates in a solution using dotnet pack
        /// </summary>
        /// <param name="solutionPath">Path to the solution directory</param>
        /// <param name="outputPath">Output directory for packages</param>
        /// <param name="configuration">Build configuration (Release/Debug)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Result of the packaging operation</returns>
        Task<PackagingResult> PackSolutionAsync(string solutionPath, string outputPath, string configuration = "Release", CancellationToken cancellationToken = default);

        /// <summary>
        /// Installs a template package using dotnet new install
        /// </summary>
        /// <param name="packagePath">Path to the .nupkg file</param>
        /// <param name="workingDirectory">Working directory for installation</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Result of the installation operation</returns>
        Task<InstallationResult> InstallTemplateAsync(string packagePath, string workingDirectory, CancellationToken cancellationToken = default);

        /// <summary>
        /// Uninstalls a template package using dotnet new uninstall
        /// </summary>
        /// <param name="packageName">Name of the package to uninstall</param>
        /// <param name="workingDirectory">Working directory for uninstallation</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Result of the uninstallation operation</returns>
        Task<UninstallationResult> UninstallTemplateAsync(string packageName, string workingDirectory, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists installed templates using dotnet new list
        /// </summary>
        /// <param name="workingDirectory">Working directory for listing</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>List of installed templates</returns>
        Task<TemplateListResult> ListTemplatesAsync(string workingDirectory, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a new project from a template using dotnet new
        /// </summary>
        /// <param name="templateName">Short name of the template</param>
        /// <param name="projectName">Name of the project to create</param>
        /// <param name="workingDirectory">Working directory for project creation</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Result of the project creation operation</returns>
        Task<ProjectCreationResult> CreateProjectFromTemplateAsync(string templateName, string projectName, string workingDirectory, CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates package metadata by extracting and examining the .nuspec file
        /// </summary>
        /// <param name="packagePath">Path to the .nupkg file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Package metadata validation result</returns>
        Task<PackageValidationResult> ValidatePackageAsync(string packagePath, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Result of a packaging operation
    /// </summary>
    public class PackagingResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public List<string> CreatedPackages { get; set; } = new();
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Result of a template installation operation
    /// </summary>
    public class InstallationResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public string PackageName { get; set; } = string.Empty;
        public List<string> InstalledTemplates { get; set; } = new();
    }

    /// <summary>
    /// Result of a template uninstallation operation
    /// </summary>
    public class UninstallationResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public string PackageName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Result of listing templates operation
    /// </summary>
    public class TemplateListResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public List<PackagingTemplateInfo> Templates { get; set; } = new();
    }

    /// <summary>
    /// Information about an installed template
    /// </summary>
    public class PackagingTemplateInfo
    {
        public string Name { get; set; } = string.Empty;
        public string ShortName { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
    }

    /// <summary>
    /// Result of creating a project from a template
    /// </summary>
    public class ProjectCreationResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;
        public List<string> CreatedFiles { get; set; } = new();
    }

    /// <summary>
    /// Result of package validation
    /// </summary>
    public class PackageValidationResult
    {
        public bool Success { get; set; }
        public string Error { get; set; } = string.Empty;
        public PackageMetadata? Metadata { get; set; }
    }

    /// <summary>
    /// Package metadata extracted from .nuspec file
    /// </summary>
    public class PackageMetadata
    {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Authors { get; set; } = string.Empty;
        public bool IsTemplate { get; set; }
        public List<string> Tags { get; set; } = new();
    }
}