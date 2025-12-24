using Xunit;
using Spectre.Console.Testing;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Commands;
using Moq;
using Microsoft.Extensions.DependencyInjection;
using PKS.Infrastructure;
using PKS.CLI.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;

namespace PKS.CLI.Tests.Commands
{
    /// <summary>
    /// Tests for InitCommand - Currently disabled pending refactoring
    /// TODO: Rewrite tests to match new NuGet template discovery implementation
    /// The InitCommand was refactored to use INuGetTemplateDiscoveryService instead of
    /// IInitializationService, and these tests need to be updated accordingly.
    /// </summary>
    public class InitCommandTests : TestBase
    {
        private Mock<INuGetTemplateDiscoveryService> _mockTemplateDiscovery = null!;
        private string _testWorkingDirectory = null!;

        public InitCommandTests()
        {
            // Create a test-specific working directory to avoid Environment.CurrentDirectory issues in containers
            _testWorkingDirectory = CreateTempDirectory();
            InitializeMocks();
        }

        private void InitializeMocks()
        {
            _mockTemplateDiscovery = new Mock<INuGetTemplateDiscoveryService>();

            // TODO: Setup mock behavior for template discovery
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);

            // Ensure mocks are initialized
            if (_mockTemplateDiscovery == null)
            {
                InitializeMocks();
            }

            // Replace the default template discovery service with our mock
            services.AddSingleton<INuGetTemplateDiscoveryService>(_mockTemplateDiscovery.Object);
            services.AddTransient<InitCommand>();
        }

        private InitCommand CreateMockCommand()
        {
            return new InitCommand(_mockTemplateDiscovery.Object, TestConsole, _testWorkingDirectory);
        }

        private async Task<int> ExecuteCommandAsync(InitCommand command, InitCommand.Settings settings)
        {
            var context = new CommandContext(Mock.Of<IRemainingArguments>(), "init", null);
            return await command.ExecuteAsync(context, settings);
        }

        // TODO: Rewrite all tests below to match new implementation
        // The old tests used IInitializationService which is no longer used by InitCommand

        [Fact]
        [Trait("Category", "Core")]
        public async Task Execute_ShouldDiscoverTemplates_WithShortNames()
        {
            // Arrange
            var templates = new List<NuGetDevcontainerTemplate>
            {
                new NuGetDevcontainerTemplate
                {
                    PackageId = "PKS.Templates.DevContainer",
                    Version = "1.0.0",
                    Title = "PKS DevContainer",
                    Description = "Universal DevContainer",
                    ShortNames = new[] { "pks-devcontainer" },
                    Tags = new[] { "pks-templates", "devcontainer" }
                },
                new NuGetDevcontainerTemplate
                {
                    PackageId = "PKS.Templates.ClaudeDotNet9",
                    Version = "1.0.0",
                    Title = "Claude .NET 9",
                    Description = ".NET 9 with Claude",
                    ShortNames = new[] { "pks-claude-dotnet9" },
                    Tags = new[] { "pks-templates", "dotnet9" }
                },
                new NuGetDevcontainerTemplate
                {
                    PackageId = "PKS.Templates.ClaudeDotNet10.Full",
                    Version = "1.0.0",
                    Title = "Claude .NET 10 Full",
                    Description = "Full-featured .NET 10",
                    ShortNames = new[] { "pks-claude-dotnet10-full" },
                    Tags = new[] { "pks-templates", "dotnet10" }
                }
            };

            _mockTemplateDiscovery
                .Setup(x => x.DiscoverTemplatesAsync(
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<bool>(),
                    It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(templates);

            var command = CreateMockCommand();
            var settings = new InitCommand.Settings
            {
                ProjectName = "TestProject",
                Template = "pks-claude-dotnet9", // Use shortName
                Tag = "pks-templates"
            };

            _mockTemplateDiscovery
                .Setup(x => x.ExtractTemplateAsync(
                    "PKS.Templates.ClaudeDotNet9",
                    "1.0.0",
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new NuGetTemplateExtractionResult
                {
                    Success = true,
                    ExtractedPath = Path.Combine(_testWorkingDirectory, "TestProject")
                });

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0);
            _mockTemplateDiscovery.Verify(
                x => x.DiscoverTemplatesAsync(
                    "pks-templates",
                    null,
                    It.IsAny<bool>(),
                    It.IsAny<System.Threading.CancellationToken>()),
                Times.Once);
        }

        [Fact]
        [Trait("Category", "Core")]
        public async Task Execute_ShouldFindTemplateByShortName()
        {
            // Arrange
            var templates = new List<NuGetDevcontainerTemplate>
            {
                new NuGetDevcontainerTemplate
                {
                    PackageId = "PKS.Templates.ClaudeDotNet10.Full",
                    Version = "1.0.0",
                    Title = "Claude .NET 10 Full",
                    Description = "Full-featured .NET 10",
                    ShortNames = new[] { "pks-claude-dotnet10-full" },
                    Tags = new[] { "pks-templates" }
                }
            };

            _mockTemplateDiscovery
                .Setup(x => x.DiscoverTemplatesAsync(
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<bool>(),
                    It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(templates);

            _mockTemplateDiscovery
                .Setup(x => x.ExtractTemplateAsync(
                    "PKS.Templates.ClaudeDotNet10.Full",
                    "1.0.0",
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(new NuGetTemplateExtractionResult
                {
                    Success = true,
                    ExtractedPath = Path.Combine(_testWorkingDirectory, "TestProject")
                });

            var command = CreateMockCommand();
            var settings = new InitCommand.Settings
            {
                ProjectName = "TestProject",
                Template = "pks-claude-dotnet10-full", // Use exact shortName
                Tag = "pks-templates"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0);
            _mockTemplateDiscovery.Verify(
                x => x.ExtractTemplateAsync(
                    "PKS.Templates.ClaudeDotNet10.Full",
                    "1.0.0",
                    It.IsAny<string>(),
                    null,
                    It.IsAny<System.Threading.CancellationToken>()),
                Times.Once);
        }

        [Fact]
        [Trait("Category", "Core")]
        public async Task Execute_ShouldReturnError_WhenTemplateNotFoundByShortName()
        {
            // Arrange
            var templates = new List<NuGetDevcontainerTemplate>
            {
                new NuGetDevcontainerTemplate
                {
                    PackageId = "PKS.Templates.DevContainer",
                    Version = "1.0.0",
                    Title = "PKS DevContainer",
                    ShortNames = new[] { "pks-devcontainer" },
                    Tags = new[] { "pks-templates" }
                }
            };

            _mockTemplateDiscovery
                .Setup(x => x.DiscoverTemplatesAsync(
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<bool>(),
                    It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(templates);

            var command = CreateMockCommand();
            var settings = new InitCommand.Settings
            {
                ProjectName = "TestProject",
                Template = "nonexistent-template",
                Tag = "pks-templates"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1); // Error code
            TestConsole.Output.Should().Contain("not found");
        }

        public override void Dispose()
        {
            // Clean up test working directory
            try
            {
                if (Directory.Exists(_testWorkingDirectory))
                {
                    Directory.Delete(_testWorkingDirectory, true);
                }
            }
            catch
            {
                // Best effort cleanup
            }

            base.Dispose();
        }
    }
}
