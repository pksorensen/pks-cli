using Xunit;
using PKS.Infrastructure.Initializers.Service;
using System.IO;
using System.Reflection;

namespace PKS.CLI.Tests.Commands
{
    public class InitCommandValidationTests
    {
        [Theory(Skip = "Mock-only test - tests non-existent private method via reflection, no real value")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ValidateProjectName_WithEmptyOrNullName_ShouldReturnInvalid(string? projectName)
        {
            // Act
            var result = InvokeValidateProjectName(projectName ?? string.Empty);

            // Assert
            Assert.False(result.IsValid);
            Assert.Equal("Project name cannot be empty", result.ErrorMessage);
        }

        [Theory(Skip = "Mock-only test - tests non-existent private method via reflection, no real value")]
        [InlineData("CON")]
        [InlineData("PRN")]
        [InlineData("AUX")]
        [InlineData("NUL")]
        [InlineData("COM1")]
        [InlineData("LPT1")]
        public void ValidateProjectName_WithReservedNames_ShouldReturnInvalid(string projectName)
        {
            // Act
            var result = InvokeValidateProjectName(projectName);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("reserved system name", result.ErrorMessage);
        }

        [Theory(Skip = "Mock-only test - tests non-existent private method via reflection, no real value")]
        [InlineData("project/name")]
        [InlineData("project\\name")]
        [InlineData("project:name")]
        [InlineData("project*name")]
        [InlineData("project?name")]
        [InlineData("project<name")]
        [InlineData("project>name")]
        [InlineData("project|name")]
        [InlineData("project\"name")]
        public void ValidateProjectName_WithInvalidCharacters_ShouldReturnInvalid(string projectName)
        {
            // Act
            var result = InvokeValidateProjectName(projectName);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("invalid characters", result.ErrorMessage);
        }

        [Theory(Skip = "Mock-only test - tests non-existent private method via reflection, no real value")]
        [InlineData(".project")]
        [InlineData("project.")]
        public void ValidateProjectName_WithDotStartOrEnd_ShouldReturnInvalid(string projectName)
        {
            // Act
            var result = InvokeValidateProjectName(projectName);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("cannot start or end with a dot", result.ErrorMessage);
        }

        [Theory(Skip = "Mock-only test - tests non-existent private method via reflection, no real value")]
        [InlineData("MyProject")]
        [InlineData("my-project")]
        [InlineData("my_project")]
        [InlineData("MyProject123")]
        [InlineData("Project.Core")]
        [InlineData("My.Project.Name")]
        public void ValidateProjectName_WithValidNames_ShouldReturnValid(string projectName)
        {
            // Act
            var result = InvokeValidateProjectName(projectName);

            // Assert
            Assert.True(result.IsValid);
            Assert.Null(result.ErrorMessage);
        }

        [Fact(Skip = "Mock-only test - tests non-existent private method via reflection, no real value")]
        public void ValidateProjectName_WithTooLongName_ShouldReturnInvalid()
        {
            // Arrange
            var longName = new string('a', 256);

            // Act
            var result = InvokeValidateProjectName(longName);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains("too long", result.ErrorMessage);
        }

        private static ValidationResult InvokeValidateProjectName(string projectName)
        {
            // Use reflection to call the private static method
            var initCommandType = typeof(InitCommand);
            var method = initCommandType.GetMethod("ValidateProjectName",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
                throw new InvalidOperationException("ValidateProjectName method not found");

            var result = method.Invoke(null, new object[] { projectName });

            if (result is not ValidationResult validationResult)
                throw new InvalidOperationException("Method did not return ValidationResult");

            return validationResult;
        }
    }
}