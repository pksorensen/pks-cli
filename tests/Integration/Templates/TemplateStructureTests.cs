using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Integration.Templates
{
    /// <summary>
    /// Tests for template structure and configuration validation without external process dependencies.
    /// These tests verify template files exist, are properly configured, and have valid metadata.
    /// </summary>
    public class TemplateStructureTests
    {
        private readonly ITestOutputHelper _output;
        private readonly string _solutionPath;
        private readonly string _templatesPath;

        public TemplateStructureTests(ITestOutputHelper output)
        {
            _output = output;
            _solutionPath = GetSolutionPath();
            _templatesPath = Path.Combine(_solutionPath, "templates");
        }

        [Fact]
        public void TemplatesDirectory_ShouldExist()
        {
            // Arrange & Act
            var exists = Directory.Exists(_templatesPath);

            // Assert
            Assert.True(exists, $"Templates directory should exist at: {_templatesPath}");
            _output.WriteLine($"Templates directory found at: {_templatesPath}");
        }

        [Fact]
        public void DevcontainerTemplate_ShouldHaveValidProjectFile()
        {
            // Arrange
            var devcontainerTemplatePath = Path.Combine(_templatesPath, "devcontainer");
            var projectFile = Path.Combine(devcontainerTemplatePath, "PKS.Templates.DevContainer.csproj");

            // Act & Assert
            Assert.True(Directory.Exists(devcontainerTemplatePath), $"Devcontainer template directory should exist: {devcontainerTemplatePath}");
            Assert.True(File.Exists(projectFile), $"Devcontainer project file should exist: {projectFile}");

            var projectContent = File.ReadAllText(projectFile);
            _output.WriteLine($"Project file content: {projectContent}");

            // Verify key properties exist
            Assert.Contains("<PackageType>Template</PackageType>", projectContent);
            Assert.Contains("<PackageId>PKS.Templates.DevContainer</PackageId>", projectContent);
            Assert.Contains("<TargetFramework>netstandard2.0</TargetFramework>", projectContent);
        }

        [Fact]
        public void DevcontainerTemplate_ShouldHaveTemplateConfigJson()
        {
            // Arrange
            var templateConfigPath = Path.Combine(_templatesPath, "devcontainer", ".template.config", "template.json");

            // Act & Assert
            Assert.True(File.Exists(templateConfigPath), $"Template config should exist: {templateConfigPath}");

            var configContent = File.ReadAllText(templateConfigPath);
            _output.WriteLine($"Template config: {configContent}");

            // Verify it's valid JSON
            var configJson = JsonDocument.Parse(configContent);
            var root = configJson.RootElement;

            Assert.True(root.TryGetProperty("name", out _), "Template config should have 'name' property");
            Assert.True(root.TryGetProperty("shortName", out _), "Template config should have 'shortName' property");
            Assert.True(root.TryGetProperty("identity", out _), "Template config should have 'identity' property");
        }

        [Fact]
        public void DevcontainerTemplate_ShouldHaveValidContentStructure()
        {
            // Arrange
            var contentPath = Path.Combine(_templatesPath, "devcontainer", "content");
            var devcontainerPath = Path.Combine(contentPath, ".devcontainer");

            // Act & Assert
            Assert.True(Directory.Exists(contentPath), $"Template content directory should exist: {contentPath}");
            Assert.True(Directory.Exists(devcontainerPath), $"Devcontainer directory should exist: {devcontainerPath}");

            // Check for essential devcontainer files
            var devcontainerJson = Path.Combine(devcontainerPath, "devcontainer.json");
            Assert.True(File.Exists(devcontainerJson), $"devcontainer.json should exist: {devcontainerJson}");

            var dockerFile = Path.Combine(devcontainerPath, "Dockerfile");
            Assert.True(File.Exists(dockerFile), $"Dockerfile should exist: {dockerFile}");

            _output.WriteLine($"Devcontainer content structure validated at: {devcontainerPath}");
        }

        [Fact]
        public void DevcontainerTemplate_DevcontainerJsonShouldBeValid()
        {
            // Arrange
            var devcontainerJsonPath = Path.Combine(_templatesPath, "devcontainer", "content", ".devcontainer", "devcontainer.json");

            // Act & Assert
            Assert.True(File.Exists(devcontainerJsonPath), $"devcontainer.json should exist: {devcontainerJsonPath}");

            var jsonContent = File.ReadAllText(devcontainerJsonPath);
            _output.WriteLine($"Devcontainer JSON content: {jsonContent}");

            // Verify it's valid JSON and has required properties
            var json = JsonDocument.Parse(jsonContent);
            var root = json.RootElement;

            Assert.True(root.TryGetProperty("name", out _), "devcontainer.json should have 'name' property");

            // Check for build configuration
            if (root.TryGetProperty("build", out var buildElement))
            {
                Assert.True(buildElement.TryGetProperty("dockerfile", out _), "Build section should specify dockerfile");
            }
            else if (root.TryGetProperty("image", out _))
            {
                // Image-based configuration is also valid
                _output.WriteLine("Using image-based devcontainer configuration");
            }
            else
            {
                Assert.Fail("devcontainer.json should have either 'build' or 'image' property");
            }
        }

        [Fact]
        public void SolutionFile_ShouldIncludeTemplateProjects()
        {
            // Arrange
            var solutionFile = Directory.GetFiles(_solutionPath, "*.sln").FirstOrDefault();
            Assert.NotNull(solutionFile);

            // Act
            var solutionContent = File.ReadAllText(solutionFile);
            _output.WriteLine($"Solution file: {solutionFile}");

            // Assert
            Assert.Contains("PKS.Templates.DevContainer", solutionContent);
        }

        [Fact]
        public void ClaudeDocumentationTemplates_ShouldExist()
        {
            // Arrange
            var claudeDocsPath = Path.Combine(_templatesPath, "claude-docs");
            var claudeModularPath = Path.Combine(_templatesPath, "claude-modular");

            // Act & Assert
            Assert.True(Directory.Exists(claudeDocsPath), $"Claude docs templates should exist: {claudeDocsPath}");
            Assert.True(Directory.Exists(claudeModularPath), $"Claude modular templates should exist: {claudeModularPath}");

            // Check for essential files
            var claudeMd = Path.Combine(claudeDocsPath, "CLAUDE.md");
            Assert.True(File.Exists(claudeMd), $"CLAUDE.md should exist: {claudeMd}");

            var modularClaude = Path.Combine(claudeModularPath, "CLAUDE.md");
            Assert.True(File.Exists(modularClaude), $"Modular CLAUDE.md should exist: {modularClaude}");

            _output.WriteLine($"Claude documentation templates validated");
        }

        [Fact]
        public void McpTemplates_ShouldExist()
        {
            // Arrange
            var mcpPath = Path.Combine(_templatesPath, "mcp");

            // Act & Assert
            Assert.True(Directory.Exists(mcpPath), $"MCP templates should exist: {mcpPath}");

            // Check for essential MCP files
            var mcpJson = Path.Combine(mcpPath, ".mcp.json");
            Assert.True(File.Exists(mcpJson), $"MCP configuration should exist: {mcpJson}");

            var mcpSseJson = Path.Combine(mcpPath, ".mcp.sse.json");
            Assert.True(File.Exists(mcpSseJson), $"MCP SSE configuration should exist: {mcpSseJson}");

            var mcpReadme = Path.Combine(mcpPath, "MCP-README.md");
            Assert.True(File.Exists(mcpReadme), $"MCP README should exist: {mcpReadme}");

            _output.WriteLine($"MCP templates validated");
        }

        [Fact]
        public void PrdTemplates_ShouldExist()
        {
            // Arrange
            var prdPath = Path.Combine(_templatesPath, "prd");

            // Act & Assert
            Assert.True(Directory.Exists(prdPath), $"PRD templates should exist: {prdPath}");

            // Check for essential PRD files
            var prdTemplate = Path.Combine(prdPath, "PRD-template.md");
            Assert.True(File.Exists(prdTemplate), $"PRD template should exist: {prdTemplate}");

            var requirementsTemplate = Path.Combine(prdPath, "requirements-template.md");
            Assert.True(File.Exists(requirementsTemplate), $"Requirements template should exist: {requirementsTemplate}");

            var userStoriesTemplate = Path.Combine(prdPath, "user-stories-template.md");
            Assert.True(File.Exists(userStoriesTemplate), $"User stories template should exist: {userStoriesTemplate}");

            _output.WriteLine($"PRD templates validated");
        }

        [Fact]
        public void AllTemplateFiles_ShouldHaveValidContent()
        {
            // Arrange
            var templateDirectories = Directory.GetDirectories(_templatesPath);
            Assert.NotEmpty(templateDirectories);

            // Act & Assert
            foreach (var templateDir in templateDirectories)
            {
                var templateName = Path.GetFileName(templateDir);
                _output.WriteLine($"Validating template: {templateName}");

                // Check that template directory has files (skip empty directories like 'hooks')
                var files = Directory.GetFiles(templateDir, "*", SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    _output.WriteLine($"Template {templateName} is empty - skipping file validation");
                    continue;
                }

                // Check for any obviously corrupted files
                foreach (var file in files)
                {
                    var fileInfo = new FileInfo(file);
                    Assert.True(fileInfo.Length > 0, $"File should not be empty: {file}");

                    // Don't validate binary files
                    var extension = Path.GetExtension(file).ToLowerInvariant();
                    if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico")
                    {
                        continue;
                    }

                    // For text files, ensure they're readable
                    try
                    {
                        var content = File.ReadAllText(file);
                        Assert.False(string.IsNullOrWhiteSpace(content), $"Text file should not be empty: {file}");
                    }
                    catch (Exception ex)
                    {
                        Assert.Fail($"Failed to read file {file}: {ex.Message}");
                    }
                }

                _output.WriteLine($"Template {templateName} validation complete - {files.Length} files checked");
            }
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
    }
}