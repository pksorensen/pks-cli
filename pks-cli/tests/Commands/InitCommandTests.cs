using Xunit;
using Spectre.Console.Testing;
using Spectre.Console.Cli;
using PKS.Infrastructure.Initializers.Service;
using PKS.Infrastructure.Initializers.Context;
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
    public class InitCommandTests : TestBase
    {
        private Mock<IInitializationService> _mockInitializationService = null!;
        private string _testWorkingDirectory = null!;

        public InitCommandTests()
        {
            // Create a test-specific working directory to avoid Environment.CurrentDirectory issues in containers
            _testWorkingDirectory = CreateTempDirectory();
            InitializeMocks();
        }

        private void InitializeMocks()
        {
            _mockInitializationService = new Mock<IInitializationService>();
            
            // Setup default mock behavior
            _mockInitializationService.Setup(x => x.ValidateProjectName(It.IsAny<string>()))
                .Returns((string name) => 
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return ValidationResult.Invalid("Project name is required");
                    if (name.Length > 255)
                        return ValidationResult.Invalid("Project name is too long");
                    if (IsReservedWindowsName(name))
                        return ValidationResult.Invalid("Project name is a reserved Windows name");
                    if (HasInvalidCharacters(name))
                        return ValidationResult.Invalid("Project name contains invalid characters");
                    if (name.StartsWith('.') || name.EndsWith('.'))
                        return ValidationResult.Invalid("Project name cannot start or end with a dot");
                    return ValidationResult.Valid();
                });
            
            // Setup ValidateTargetDirectoryAsync to prevent null reference exceptions
            _mockInitializationService.Setup(x => x.ValidateTargetDirectoryAsync(It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(ValidationResult.Valid());
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            
            // Ensure mocks are initialized
            if (_mockInitializationService == null)
            {
                InitializeMocks();
            }
            
            // Replace the default initialization service with our mock
            services.AddSingleton<IInitializationService>(_mockInitializationService.Object);
            services.AddTransient<InitCommand>();
        }
        
        private bool IsReservedWindowsName(string name)
        {
            var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", 
                                  "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", 
                                  "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            return reserved.Contains(name.ToUpperInvariant());
        }
        
        private bool HasInvalidCharacters(string name)
        {
            var invalidChars = new[] { '/', '\\', ':', '*', '?', '<', '>', '|', '"' };
            return name.Any(c => invalidChars.Contains(c));
        }

        [Theory]
        [Trait("Category", "Core")]
        [InlineData("")]
        [InlineData(null)]
        public async Task Execute_ShouldPromptForProjectName_WhenProjectNameIsEmpty(string? projectName)
        {
            // Arrange
            // For empty project names, the command will try to prompt for input
            // We provide test input to simulate user entering an invalid name with forbidden characters
            TestConsole.Input.PushTextWithEnter("invalid/name"); // This will cause validation to fail due to '/' character
            
            var command = CreateMockCommand();
            var settings = new InitCommand.Settings
            {
                ProjectName = projectName,
                Template = "console",
                Description = "Test project"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1); // Should fail validation
            _mockInitializationService.Verify(x => x.InitializeProjectAsync(It.IsAny<InitializationContext>()), Times.Never);
            AssertConsoleOutput("What's the name of your project?");
        }

        [Fact]
        [Trait("Category", "Core")]
        public async Task Execute_ShouldReturnError_WhenProjectNameIsWhitespace()
        {
            // Arrange
            var command = CreateMockCommand();
            var settings = new InitCommand.Settings
            {
                ProjectName = "   ", // Whitespace only
                Template = "console",
                Description = "Test project"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1);
            _mockInitializationService.Verify(x => x.InitializeProjectAsync(It.IsAny<InitializationContext>()), Times.Never);
        }

        [Theory]
        [Trait("Category", "Core")]
        [InlineData("CON")]
        [InlineData("PRN")]
        [InlineData("AUX")]
        [InlineData("NUL")]
        [InlineData("COM1")]
        [InlineData("COM2")]
        [InlineData("COM3")]
        [InlineData("COM4")]
        [InlineData("COM5")]
        [InlineData("COM6")]
        [InlineData("COM7")]
        [InlineData("COM8")]
        [InlineData("COM9")]
        [InlineData("LPT1")]
        [InlineData("LPT2")]
        [InlineData("LPT3")]
        [InlineData("LPT4")]
        [InlineData("LPT5")]
        [InlineData("LPT6")]
        [InlineData("LPT7")]
        [InlineData("LPT8")]
        [InlineData("LPT9")]
        public async Task Execute_ShouldReturnError_WhenProjectNameIsReservedWindowsName(string projectName)
        {
            // Arrange
            var command = CreateMockCommand();
            var settings = new InitCommand.Settings
            {
                ProjectName = projectName,
                Template = "console"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1);
            _mockInitializationService.Verify(x => x.InitializeProjectAsync(It.IsAny<InitializationContext>()), Times.Never);
        }

        [Theory]
        [Trait("Category", "Core")]
        [InlineData("project/name")]
        [InlineData("project\\name")]
        [InlineData("project:name")]
        [InlineData("project*name")]
        [InlineData("project?name")]
        [InlineData("project<name")]
        [InlineData("project>name")]
        [InlineData("project|name")]
        [InlineData("project\"name")]
        public async Task Execute_ShouldReturnError_WhenProjectNameContainsInvalidCharacters(string projectName)
        {
            // Arrange
            var command = CreateMockCommand();
            var settings = new InitCommand.Settings
            {
                ProjectName = projectName,
                Template = "console"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1);
            _mockInitializationService.Verify(x => x.InitializeProjectAsync(It.IsAny<InitializationContext>()), Times.Never);
        }

        [Theory]
        [Trait("Category", "Core")]
        [InlineData(".project")]
        [InlineData("..project")]
        [InlineData("project.")]
        [InlineData("project..")]
        public async Task Execute_ShouldReturnError_WhenProjectNameStartsOrEndsWithDot(string projectName)
        {
            // Arrange
            var command = CreateMockCommand();
            var settings = new InitCommand.Settings
            {
                ProjectName = projectName,
                Template = "console"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1);
            _mockInitializationService.Verify(x => x.InitializeProjectAsync(It.IsAny<InitializationContext>()), Times.Never);
        }

        [Theory]
        [Trait("Category", "Core")]
        [InlineData("MyProject")]
        [InlineData("my-project")]
        [InlineData("my_project")]
        [InlineData("MyProject123")]
        [InlineData("Project.Core")]
        [InlineData("My.Project.Name")]
        public async Task Execute_ShouldProceed_WhenProjectNameIsValid(string projectName)
        {
            // Arrange
            var command = CreateMockCommand();
            var settings = new InitCommand.Settings
            {
                ProjectName = projectName,
                Template = "console",
                Description = "Test project"
            };

            // Use test working directory instead of Environment.CurrentDirectory for container safety
            var targetDirectory = Path.Combine(_testWorkingDirectory, projectName);
            
            _mockInitializationService
                .Setup(x => x.ValidateTargetDirectoryAsync(targetDirectory, false))
                .ReturnsAsync(ValidationResult.Valid());

            _mockInitializationService
                .Setup(x => x.CreateContext(projectName, "console", targetDirectory, false, It.IsAny<Dictionary<string, object?>>()))
                .Returns(new InitializationContext
                {
                    ProjectName = projectName,
                    Template = "console",
                    TargetDirectory = targetDirectory,
                    WorkingDirectory = _testWorkingDirectory,
                    Force = false
                });

            var summary = new InitializationSummary
            {
                ProjectName = projectName,
                Template = "console",
                TargetDirectory = targetDirectory,
                Success = true,
                FilesCreated = 5,
                StartTime = DateTime.Now.AddSeconds(-1),
                EndTime = DateTime.Now
            };
            
            _mockInitializationService
                .Setup(x => x.InitializeProjectAsync(It.IsAny<InitializationContext>()))
                .ReturnsAsync(summary);

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0);
            _mockInitializationService.Verify(x => x.InitializeProjectAsync(It.IsAny<InitializationContext>()), Times.Once);
        }

        [Fact]
        [Trait("Category", "Core")]
        public async Task Execute_ShouldReturnError_WhenProjectNameExceedsMaxLength()
        {
            // Arrange
            var command = CreateMockCommand();
            var longProjectName = new string('a', 256); // Exceeds typical filesystem limits
            var settings = new InitCommand.Settings
            {
                ProjectName = longProjectName,
                Template = "console"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1);
            _mockInitializationService.Verify(x => x.InitializeProjectAsync(It.IsAny<InitializationContext>()), Times.Never);
        }

        private InitCommand CreateMockCommand()
        {
            return new InitCommand(_mockInitializationService.Object, TestConsole, _testWorkingDirectory);
        }

        private async Task<int> ExecuteCommandAsync(InitCommand command, InitCommand.Settings settings)
        {
            var context = new CommandContext(Mock.Of<IRemainingArguments>(), "init", null);
            return await command.ExecuteAsync(context, settings);
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
                // Ignore cleanup errors
            }
            
            base.Dispose();
        }
    }
}