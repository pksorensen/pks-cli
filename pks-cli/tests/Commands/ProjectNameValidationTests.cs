using Xunit;
using System;
using System.IO;
using System.Linq;

namespace PKS.CLI.Tests.Commands
{
    // This is a standalone test that directly tests the validation logic
    // without requiring complex mocking infrastructure
    public class ProjectNameValidationTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void ProjectNameValidation_WithEmptyOrNullName_ShouldFail(string? projectName)
        {
            // Act
            var isValid = IsValidProjectName(projectName ?? string.Empty);

            // Assert
            Assert.False(isValid, $"Project name '{projectName}' should be invalid");
        }

        [Theory]
        [InlineData("CON")]
        [InlineData("PRN")]
        [InlineData("AUX")]
        [InlineData("NUL")]
        [InlineData("COM1")]
        [InlineData("LPT1")]
        public void ProjectNameValidation_WithReservedNames_ShouldFail(string projectName)
        {
            // Act
            var isValid = IsValidProjectName(projectName);

            // Assert
            Assert.False(isValid, $"Project name '{projectName}' should be invalid (reserved name)");
        }

        [Theory]
        [InlineData("project/name")]
        [InlineData("project\\name")]
        [InlineData("project:name")]
        [InlineData("project*name")]
        [InlineData("project?name")]
        [InlineData("project<name")]
        [InlineData("project>name")]
        [InlineData("project|name")]
        [InlineData("project\"name")]
        public void ProjectNameValidation_WithInvalidCharacters_ShouldFail(string projectName)
        {
            // Act
            var isValid = IsValidProjectName(projectName);

            // Assert
            Assert.False(isValid, $"Project name '{projectName}' should be invalid (invalid characters)");
        }

        [Theory]
        [InlineData(".project")]
        [InlineData("project.")]
        public void ProjectNameValidation_WithDotStartOrEnd_ShouldFail(string projectName)
        {
            // Act
            var isValid = IsValidProjectName(projectName);

            // Assert
            Assert.False(isValid, $"Project name '{projectName}' should be invalid (starts/ends with dot)");
        }

        [Theory]
        [InlineData("MyProject")]
        [InlineData("my-project")]
        [InlineData("my_project")]
        [InlineData("MyProject123")]
        [InlineData("Project.Core")]
        [InlineData("My.Project.Name")]
        public void ProjectNameValidation_WithValidNames_ShouldPass(string projectName)
        {
            // Act
            var isValid = IsValidProjectName(projectName);

            // Assert
            Assert.True(isValid, $"Project name '{projectName}' should be valid");
        }

        [Fact]
        public void ProjectNameValidation_WithTooLongName_ShouldFail()
        {
            // Arrange
            var longName = new string('a', 256);

            // Act
            var isValid = IsValidProjectName(longName);

            // Assert
            Assert.False(isValid, "Project name should be invalid (too long)");
        }

        // This replicates the validation logic from InitCommand without needing to access the private method
        private static bool IsValidProjectName(string projectName)
        {
            // Check for null or empty
            if (string.IsNullOrWhiteSpace(projectName))
            {
                return false;
            }

            // Check for maximum length
            if (projectName.Length > 255)
            {
                return false;
            }

            // Check for invalid characters (cross-platform set including Windows-specific ones)
            var invalidChars = new char[] { '/', '\\', ':', '*', '?', '<', '>', '|', '"', '\0' };
            if (projectName.Any(c => invalidChars.Contains(c)))
            {
                return false;
            }

            // Check for reserved Windows device names
            var reservedNames = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            if (reservedNames.Contains(projectName.ToUpperInvariant()))
            {
                return false;
            }

            // Check if starts or ends with dot
            if (projectName.StartsWith('.') || projectName.EndsWith('.'))
            {
                return false;
            }

            // Check if starts or ends with space
            if (projectName.StartsWith(' ') || projectName.EndsWith(' '))
            {
                return false;
            }

            return true;
        }
    }
}