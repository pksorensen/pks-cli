using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure.Services;

/// <summary>
/// Unit tests for the enhanced ConfigurationService class.
/// Tests file-based persistence, in-memory caching, and error handling.
/// </summary>
public class ConfigurationServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testSettingsPath;
    private readonly ConfigurationService _configService;

    public ConfigurationServiceTests()
    {
        // Create a temporary directory for test settings
        _testDirectory = Path.Combine(Path.GetTempPath(), "pks-cli-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
        
        _testSettingsPath = Path.Combine(_testDirectory, "settings.json");
        
        // Create a standard configuration service for basic testing
        _configService = new ConfigurationService();
    }

    public void Dispose()
    {
        // Clean up test directory
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_WithExistingKey_ReturnsValue()
    {
        // Arrange
        const string key = "test.key";
        const string value = "test.value";
        await _configService.SetAsync(key, value, global: true);

        // Act
        var result = await _configService.GetAsync(key);

        // Assert
        Assert.Equal(value, result);
    }

    [Fact]
    public async Task GetAsync_WithNonExistentKey_ReturnsNull()
    {
        // Act
        var result = await _configService.GetAsync("non.existent.key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_WithGlobalFlag_StoresInMemory()
    {
        // Arrange
        const string key = "cli.test-setting";
        const string value = "test-value";

        // Act
        await _configService.SetAsync(key, value, global: true);

        // Assert - Value should be retrievable
        var result = await _configService.GetAsync(key);
        Assert.Equal(value, result);
    }

    [Fact]
    public async Task SetAsync_WithCliPrefix_StoresInMemory()
    {
        // Arrange
        const string key = "cli.first-time-warning-acknowledged";
        const string value = "true";

        // Act
        await _configService.SetAsync(key, value, global: false);

        // Assert - Value should be retrievable
        var result = await _configService.GetAsync(key);
        Assert.Equal(value, result);
    }

    [Fact]
    public async Task SetAsync_WithoutGlobalFlagOrCliPrefix_StoresInMemory()
    {
        // Arrange
        const string key = "temp.setting";
        const string value = "temp-value";

        // Act
        await _configService.SetAsync(key, value, global: false);

        // Assert - Setting should be in memory
        var memoryValue = await _configService.GetAsync(key);
        Assert.Equal(value, memoryValue);
    }

    [Fact]
    public async Task SetAsync_WithEncryptFlag_StoresEncryptedValue()
    {
        // Arrange
        const string key = "secure.password";
        const string value = "secret123";

        // Act
        await _configService.SetAsync(key, value, global: true, encrypt: true);

        // Assert
        var result = await _configService.GetAsync(key);
        Assert.Equal("***encrypted***", result);
    }

    [Fact]
    public async Task DeleteAsync_RemovesKeyFromMemory()
    {
        // Arrange
        const string key = "cli.to-delete";
        const string value = "delete-me";
        await _configService.SetAsync(key, value, global: true);

        // Verify it exists
        Assert.Equal(value, await _configService.GetAsync(key));

        // Act
        await _configService.DeleteAsync(key);

        // Assert - Should be removed from memory
        Assert.Null(await _configService.GetAsync(key));
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllSettings()
    {
        // Arrange
        var testSettings = new Dictionary<string, string>
        {
            { "key1", "value1" },
            { "key2", "value2" },
            { "cli.setting", "cli-value" }
        };

        foreach (var kvp in testSettings)
        {
            await _configService.SetAsync(kvp.Key, kvp.Value, global: true);
        }

        // Act
        var allSettings = await _configService.GetAllAsync();

        // Assert
        foreach (var kvp in testSettings)
        {
            Assert.True(allSettings.ContainsKey(kvp.Key));
            Assert.Equal(kvp.Value, allSettings[kvp.Key]);
        }
    }

    [Fact]
    public async Task ConfigurationService_FirstTimeWarningWorkflow_WorksInMemory()
    {
        // This test simulates the complete workflow for first-time warning
        
        // Arrange
        const string warningKey = "cli.first-time-warning-acknowledged";

        // Act 1: Check initial state (should be null/false)
        var initialValue = await _configService.GetAsync(warningKey);
        Assert.Null(initialValue);

        // Act 2: Acknowledge warning
        await _configService.SetAsync(warningKey, "true", global: true);

        // Act 3: Verify in-memory value
        var memoryValue = await _configService.GetAsync(warningKey);
        Assert.Equal("true", memoryValue);
        
        // Act 4: Verify subsequent reads return the same value
        var subsequentValue = await _configService.GetAsync(warningKey);
        Assert.Equal("true", subsequentValue);
    }
}

/// <summary>
/// Simplified configuration service tests using the actual ConfigurationService.
/// These tests verify the core functionality works as expected.
/// </summary>
public class SimpleConfigurationServiceTests
{
    [Fact]
    public void ConfigurationService_CanBeInstantiated()
    {
        // Act & Assert - should not throw
        var service = new ConfigurationService();
        Assert.NotNull(service);
    }

    [Fact]
    public async Task ConfigurationService_BasicOperations_Work()
    {
        // Arrange
        var service = new ConfigurationService();
        const string key = "test.key";
        const string value = "test.value";

        // Act - Set a value
        await service.SetAsync(key, value);
        var result = await service.GetAsync(key);

        // Assert
        Assert.Equal(value, result);
    }

    [Fact]
    public async Task ConfigurationService_EncryptedValues_Work()
    {
        // Arrange
        var service = new ConfigurationService();
        const string key = "secure.key";
        const string value = "secret";

        // Act
        await service.SetAsync(key, value, encrypt: true);
        var result = await service.GetAsync(key);

        // Assert - Should return encrypted placeholder
        Assert.Equal("***encrypted***", result);
    }
}