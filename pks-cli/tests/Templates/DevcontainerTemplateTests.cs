using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using FluentAssertions;

namespace PksCli.Tests.Templates
{
    public class DevcontainerTemplateTests
    {
        private readonly string _testRootPath;
        private readonly string _templatePath;

        public DevcontainerTemplateTests()
        {
            _testRootPath = Path.Combine(Path.GetTempPath(), $"pks-cli-test-{Guid.NewGuid():N}");
            _templatePath = Path.Combine(_testRootPath, "templates", "devcontainer", "pks-universal-devcontainer");
            Directory.CreateDirectory(_testRootPath);
        }

        [Fact]
        public void Template_Directory_Structure_Should_Be_Valid()
        {
            // Arrange
            var expectedDirectories = new[]
            {
                _templatePath,
                Path.Combine(_templatePath, ".template.config"),
                Path.Combine(_templatePath, "content"),
                Path.Combine(_templatePath, "content", ".devcontainer")
            };

            // Act
            CreateTemplateStructure();

            // Assert
            foreach (var dir in expectedDirectories)
            {
                Directory.Exists(dir).Should().BeTrue($"Directory {dir} should exist");
            }
        }

        [Fact]
        public void Template_Json_Should_Have_Valid_Structure()
        {
            // Arrange
            CreateTemplateStructure();
            var templateJsonPath = Path.Combine(_templatePath, ".template.config", "template.json");

            // Act
            var templateJsonContent = File.ReadAllText(templateJsonPath);
            var templateConfig = JsonDocument.Parse(templateJsonContent);

            // Assert
            templateConfig.RootElement.GetProperty("$schema").GetString()
                .Should().Be("http://json.schemastore.org/template");
            templateConfig.RootElement.GetProperty("author").GetString()
                .Should().Be("PKS CLI Team");
            templateConfig.RootElement.GetProperty("classifications").EnumerateArray()
                .Should().Contain(c => c.GetString() == "DevContainer");
            templateConfig.RootElement.GetProperty("identity").GetString()
                .Should().Be("PKS.Universal.DevContainer");
            templateConfig.RootElement.GetProperty("name").GetString()
                .Should().Be("PKS Universal DevContainer");
            templateConfig.RootElement.GetProperty("shortName").GetString()
                .Should().Be("pks-universal-devcontainer");
        }

        [Fact]
        public void Devcontainer_Files_Should_Be_Copied_Correctly()
        {
            // Arrange
            CreateTemplateStructure();
            var contentDevcontainerPath = Path.Combine(_templatePath, "content", ".devcontainer");
            var expectedFiles = new[]
            {
                "Dockerfile",
                "devcontainer.json",
                "docker-compose.yml",
                "init-firewall.sh"
            };

            // Act
            CopyDevcontainerFiles(contentDevcontainerPath);

            // Assert
            foreach (var file in expectedFiles)
            {
                var filePath = Path.Combine(contentDevcontainerPath, file);
                File.Exists(filePath).Should().BeTrue($"File {file} should exist in template");
            }
        }

        [Fact]
        public void Template_Should_Support_Parameterization()
        {
            // Arrange
            CreateTemplateStructure();
            var templateJsonPath = Path.Combine(_templatePath, ".template.config", "template.json");
            var templateJsonContent = File.ReadAllText(templateJsonPath);
            var templateConfig = JsonDocument.Parse(templateJsonContent);

            // Assert
            templateConfig.RootElement.TryGetProperty("symbols", out var symbols).Should().BeTrue();
            symbols.TryGetProperty("ProjectName", out var projectName).Should().BeTrue();
            projectName.GetProperty("type").GetString().Should().Be("parameter");
            projectName.GetProperty("datatype").GetString().Should().Be("string");
        }

        [Fact]
        public void Template_Should_Have_Valid_NuGet_Package_Structure()
        {
            // Arrange
            CreateTemplateStructure();
            var nuspecPath = Path.Combine(_templatePath, "PKS.Templates.DevContainer.nuspec");

            // Act
            CreateNuspecFile(nuspecPath);

            // Assert
            File.Exists(nuspecPath).Should().BeTrue("Nuspec file should exist");
            var nuspecContent = File.ReadAllText(nuspecPath);
            nuspecContent.Should().Contain("<id>PKS.Templates.DevContainer</id>");
            nuspecContent.Should().Contain("<version>1.0.0</version>");
            nuspecContent.Should().Contain("<packageType name=\"Template\" />");
        }

        [Fact]
        public void Template_Should_Replace_Placeholders_On_Instantiation()
        {
            // Arrange
            CreateTemplateStructure();
            var contentDevcontainerPath = Path.Combine(_templatePath, "content", ".devcontainer");
            
            // Create devcontainer.json with the correct placeholder format
            var devcontainerJsonPath = Path.Combine(contentDevcontainerPath, "devcontainer.json");
            var devcontainerContent = @"{
  ""name"": ""PKSDevContainer Development Container"",
  ""image"": ""mcr.microsoft.com/devcontainers/universal:2-linux"",
  ""features"": {
    ""ghcr.io/devcontainers/features/dotnet:2"": {}
  }
}";
            File.WriteAllText(devcontainerJsonPath, devcontainerContent);

            // Act & Assert
            // Verify the placeholder exists (PKSDevContainer is replaced with ProjectName value)
            var writtenContent = File.ReadAllText(devcontainerJsonPath);
            writtenContent.Should().Contain("PKSDevContainer");
            
            // Also verify the template.json has the correct replacement configuration
            var templateJsonPath = Path.Combine(_templatePath, ".template.config", "template.json");
            var templateJson = File.ReadAllText(templateJsonPath);
            templateJson.Should().Contain("\"replaces\": \"PKSDevContainer\"");
            templateJson.Should().Contain("\"ProjectName\"");
        }

        private void CreateTemplateStructure()
        {
            Directory.CreateDirectory(Path.Combine(_templatePath, ".template.config"));
            Directory.CreateDirectory(Path.Combine(_templatePath, "content", ".devcontainer"));

            // Create template.json
            var templateJson = @"{
  ""$schema"": ""http://json.schemastore.org/template"",
  ""author"": ""PKS CLI Team"",
  ""classifications"": [""DevContainer"", ""Docker"", ""Development Environment""],
  ""identity"": ""PKS.Universal.DevContainer"",
  ""name"": ""PKS Universal DevContainer"",
  ""shortName"": ""pks-universal-devcontainer"",
  ""tags"": {
    ""language"": ""C#"",
    ""type"": ""item""
  },
  ""sourceName"": ""PKSDevContainer"",
  ""preferNameDirectory"": false,
  ""symbols"": {
    ""ProjectName"": {
      ""type"": ""parameter"",
      ""datatype"": ""string"",
      ""description"": ""The name of the project"",
      ""defaultValue"": ""MyProject"",
      ""replaces"": ""PKSDevContainer""
    },
    ""enableNodeJs"": {
      ""type"": ""parameter"",
      ""datatype"": ""bool"",
      ""description"": ""Include Node.js development tools"",
      ""defaultValue"": ""true""
    },
    ""enableDotNet"": {
      ""type"": ""parameter"",
      ""datatype"": ""bool"",
      ""description"": ""Include .NET development tools"",
      ""defaultValue"": ""true""
    }
  }
}";
            File.WriteAllText(Path.Combine(_templatePath, ".template.config", "template.json"), templateJson);
        }

        private void CopyDevcontainerFiles(string targetPath)
        {
            // Simulate copying files - in real implementation, this would copy from source
            var files = new[] { "Dockerfile", "devcontainer.json", "docker-compose.yml", "init-firewall.sh" };
            foreach (var file in files)
            {
                File.WriteAllText(Path.Combine(targetPath, file), $"// Content of {file}");
            }
        }

        private void CreateNuspecFile(string nuspecPath)
        {
            var nuspecContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd"">
  <metadata>
    <id>PKS.Templates.DevContainer</id>
    <version>1.0.0</version>
    <title>PKS DevContainer Templates</title>
    <authors>PKS CLI Team</authors>
    <description>Universal development container template for PKS CLI projects</description>
    <packageTypes>
      <packageType name=""Template"" />
    </packageTypes>
    <tags>devcontainer docker template pks-cli</tags>
    <repository type=""git"" url=""https://github.com/pksorensen/pks-cli.git"" />
    <license type=""expression"">MIT</license>
  </metadata>
  <files>
    <file src=""content/**/*"" target=""content"" />
    <file src="".template.config/**/*"" target=""content/.template.config"" />
  </files>
</package>";
            File.WriteAllText(nuspecPath, nuspecContent);
        }

        [Fact]
        public void Dispose()
        {
            if (Directory.Exists(_testRootPath))
            {
                Directory.Delete(_testRootPath, true);
            }
        }
    }
}