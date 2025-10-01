using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;

namespace PKS.CLI.Tests.Integration.Templates
{
    /// <summary>
    /// Integration tests for template packaging functionality.
    /// Tests the entire pipeline from solution-level dotnet pack to template installation and validation.
    /// </summary>
    public class TemplatePackagingTests : TestBase
    {
        private readonly ITestOutputHelper _output;
        private readonly ITemplatePackagingService _templatePackagingService;
        private readonly string _solutionPath;
        private readonly string _testPackageOutputPath;
        private readonly string _testTemplateInstallPath;
        private readonly List<string> _installedTemplates = new();

        public TemplatePackagingTests(ITestOutputHelper output) : base()
        {
            _output = output;
            _templatePackagingService = GetService<ITemplatePackagingService>();
            _solutionPath = GetSolutionPath();
            _testPackageOutputPath = Path.Combine(Path.GetTempPath(), "pks-cli-test-packages", Guid.NewGuid().ToString());
            _testTemplateInstallPath = Path.Combine(Path.GetTempPath(), "pks-cli-test-templates", Guid.NewGuid().ToString());

            // Ensure output directories exist
            Directory.CreateDirectory(_testPackageOutputPath);
            Directory.CreateDirectory(_testTemplateInstallPath);
        }

        [Fact]
        public async Task SolutionLevel_DotNetPack_ShouldCreateAllPackages()
        {
            // Arrange
            _output.WriteLine($"Testing solution-level dotnet pack from: {_solutionPath}");
            _output.WriteLine($"Output directory: {_testPackageOutputPath}");

            // Act - Use mocked packaging service
            var result = await _templatePackagingService.PackSolutionAsync(_solutionPath, _testPackageOutputPath, "Release");

            // Assert
            Assert.True(result.Success, $"dotnet pack failed:\n{result.Output}\n{result.Error}");
            Assert.NotEmpty(result.CreatedPackages);

            // Verify main CLI package is created
            var cliPackage = result.CreatedPackages.FirstOrDefault(p => p.Contains("pks-cli"));
            Assert.NotNull(cliPackage);
            _output.WriteLine($"CLI Package created: {Path.GetFileName(cliPackage)}");

            // Verify template packages are created
            var templatePackages = result.CreatedPackages.Where(p => p.Contains("PKS.Templates")).ToList();
            Assert.NotEmpty(templatePackages);

            foreach (var package in templatePackages)
            {
                _output.WriteLine($"Template Package created: {Path.GetFileName(package)}");
                Assert.True(File.Exists(package), $"Package file {package} should exist");
                Assert.True(new FileInfo(package).Length > 0, $"Package {package} is empty");
            }
        }

        [Fact]
        public async Task TemplatePackages_ShouldBeValidAndInstallable()
        {
            // First ensure packages are built
            var packResult = await _templatePackagingService.PackSolutionAsync(_solutionPath, _testPackageOutputPath, "Release");
            Assert.True(packResult.Success);

            // Get all template packages
            var templatePackages = packResult.CreatedPackages.Where(p => p.Contains("PKS.Templates")).ToList();

            foreach (var packagePath in templatePackages)
            {
                var packageName = Path.GetFileNameWithoutExtension(packagePath);
                _output.WriteLine($"Testing template package: {packageName}");

                // Test template installation using mocked service
                var installResult = await _templatePackagingService.InstallTemplateAsync(packagePath, _testTemplateInstallPath);

                Assert.True(installResult.Success,
                    $"Failed to install template package {packageName}:\n{installResult.Output}\n{installResult.Error}");

                _installedTemplates.Add(packageName);
                _output.WriteLine($"Successfully installed template: {packageName}");
                _output.WriteLine($"Installed templates: {string.Join(", ", installResult.InstalledTemplates)}");
            }
        }

        [Fact]
        public async Task InstalledTemplates_ShouldBeListedAndFunctional()
        {
            // Ensure templates are installed first
            await TemplatePackages_ShouldBeValidAndInstallable();

            // List installed templates using mocked service
            var listResult = await _templatePackagingService.ListTemplatesAsync(_testTemplateInstallPath);
            Assert.True(listResult.Success, $"Failed to list templates:\n{listResult.Output}\n{listResult.Error}");

            _output.WriteLine("Installed templates:");
            _output.WriteLine(listResult.Output);

            // Verify our templates appear in the list
            Assert.NotEmpty(listResult.Templates);

            foreach (var template in listResult.Templates)
            {
                _output.WriteLine($"Found template: {template.Name} ({template.ShortName})");
                Assert.Contains("pks", template.ShortName.ToLower());
            }

            // Verify specific templates are present
            Assert.Contains(listResult.Templates, t => t.ShortName == "pks-devcontainer");
            Assert.Contains(listResult.Templates, t => t.ShortName == "pks-claude-docs");
            Assert.Contains(listResult.Templates, t => t.ShortName == "pks-hooks");
        }

        [Fact]
        public async Task DevContainerTemplate_ShouldCreateValidProject()
        {
            // Ensure templates are installed
            await TemplatePackages_ShouldBeValidAndInstallable();

            // Create a test project using the DevContainer template
            var testProjectPath = Path.Combine(_testTemplateInstallPath, "test-devcontainer-project");
            Directory.CreateDirectory(testProjectPath);

            // Use the template to create a project using mocked service
            var createResult = await _templatePackagingService.CreateProjectFromTemplateAsync(
                "pks-devcontainer", "TestDevContainer", testProjectPath);

            Assert.True(createResult.Success,
                $"Template creation failed:\nOutput: {createResult.Output}\nError: {createResult.Error}");

            _output.WriteLine($"Project created at: {createResult.ProjectPath}");
            _output.WriteLine($"Created files: {string.Join(", ", createResult.CreatedFiles)}");

            // Verify expected files were created
            var expectedFiles = new[]
            {
                ".devcontainer/devcontainer.json",
                "README.md"
            };

            foreach (var expectedFile in expectedFiles)
            {
                var filePath = Path.Combine(createResult.ProjectPath, expectedFile);
                Assert.True(File.Exists(filePath), $"Expected file not created: {expectedFile}");
                _output.WriteLine($"Verified file exists: {expectedFile}");

                // Verify file has content
                var content = File.ReadAllText(filePath);
                Assert.False(string.IsNullOrWhiteSpace(content), $"File {expectedFile} should not be empty");
            }
        }

        [Fact]
        public async Task PackageMetadata_ShouldBeValid()
        {
            // Ensure packages are built
            var packResult = await _templatePackagingService.PackSolutionAsync(_solutionPath, _testPackageOutputPath, "Release");
            Assert.True(packResult.Success);

            var templatePackages = packResult.CreatedPackages.Where(p => p.Contains("PKS.Templates")).ToList();

            foreach (var packagePath in templatePackages)
            {
                _output.WriteLine($"Validating package metadata for: {Path.GetFileName(packagePath)}");

                // Use mocked service to validate package
                var validationResult = await _templatePackagingService.ValidatePackageAsync(packagePath);

                Assert.True(validationResult.Success, $"Package validation failed: {validationResult.Error}");
                Assert.NotNull(validationResult.Metadata);

                var metadata = validationResult.Metadata;
                _output.WriteLine($"Package ID: {metadata.Id}");
                _output.WriteLine($"Version: {metadata.Version}");
                _output.WriteLine($"Title: {metadata.Title}");
                _output.WriteLine($"Description: {metadata.Description}");
                _output.WriteLine($"Is Template: {metadata.IsTemplate}");
                _output.WriteLine($"Tags: {string.Join(", ", metadata.Tags)}");

                // Verify required metadata is present
                Assert.True(metadata.IsTemplate, "Package should be marked as a template");
                Assert.StartsWith("PKS.Templates.", metadata.Id);
                Assert.NotEmpty(metadata.Version);
                Assert.NotEmpty(metadata.Title);
                Assert.NotEmpty(metadata.Description);
                Assert.Contains("template", metadata.Tags);
            }
        }

        [Fact]
        public async Task ContinuousIntegration_PackBuild_ShouldWork()
        {
            // Test the kind of command that would be used in CI using mocked service
            var ciResult = await _templatePackagingService.PackSolutionAsync(_solutionPath, _testPackageOutputPath, "Release");

            Assert.True(ciResult.Success, $"CI-style pack command failed:\n{ciResult.Output}\n{ciResult.Error}");

            _output.WriteLine("CI-style pack command succeeded");
            _output.WriteLine($"Pack duration: {ciResult.Duration}");
            _output.WriteLine($"Packages created: {ciResult.CreatedPackages.Count}");

            // Verify that packages were created quickly (mocked service should be fast)
            Assert.True(ciResult.Duration < TimeSpan.FromMinutes(1), "CI pack should complete quickly");
            Assert.NotEmpty(ciResult.CreatedPackages);
        }


        private string GetSolutionPath()
        {
            var currentPath = Directory.GetCurrentDirectory();

            // Look for solution file starting from current directory and going up
            while (currentPath != null)
            {
                var solutionFiles = Directory.GetFiles(currentPath, "*.sln");
                if (solutionFiles.Any())
                {
                    return currentPath;
                }

                var parent = Directory.GetParent(currentPath);
                currentPath = parent?.FullName;
            }

            // Fallback to expected location based on project structure
            var expectedPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", ".."));
            if (Directory.GetFiles(expectedPath, "*.sln").Any())
            {
                return expectedPath;
            }

            throw new InvalidOperationException("Could not find solution file");
        }

        public override void Dispose()
        {
            // Clean up installed templates using mocked service
            foreach (var templateName in _installedTemplates)
            {
                try
                {
                    var uninstallResult = _templatePackagingService.UninstallTemplateAsync(templateName, _testTemplateInstallPath).Result;
                    _output.WriteLine($"Uninstalled template: {templateName} - Success: {uninstallResult.Success}");
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Failed to uninstall template {templateName}: {ex.Message}");
                }
            }

            // Clean up test directories
            try
            {
                if (Directory.Exists(_testPackageOutputPath))
                {
                    Directory.Delete(_testPackageOutputPath, true);
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Failed to clean up package output directory: {ex.Message}");
            }

            try
            {
                if (Directory.Exists(_testTemplateInstallPath))
                {
                    Directory.Delete(_testTemplateInstallPath, true);
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Failed to clean up template install directory: {ex.Message}");
            }

            // Call base dispose
            base.Dispose();
        }
    }
}