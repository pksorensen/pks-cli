using Xunit;
using Moq;
using FluentAssertions;
using PKS.Commands.Foundry;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System;
using System.IO;
using System.Threading;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PKS.CLI.Tests.Commands;

/// <summary>
/// Tests for FoundryTokenCommand: not-authenticated guard, raw token output,
/// and gzip+base64 JSON output when stdout is redirected.
/// </summary>
public class FoundryTokenCommandTests
{
    // ─────────────────────────────────────────────
    //  Shared helpers
    // ─────────────────────────────────────────────

    private static Mock<IAzureFoundryAuthService> CreateAuthServiceMock(bool authenticated)
    {
        var mock = new Mock<IAzureFoundryAuthService>();
        mock.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(authenticated);

        if (authenticated)
        {
            mock.Setup(x => x.GetStoredCredentialsAsync())
                .ReturnsAsync(new FoundryStoredCredentials
                {
                    TenantId = "test-tenant",
                    RefreshToken = "test-refresh",
                    SelectedSubscriptionId = "sub-12345",
                    SelectedSubscriptionName = "Test Sub",
                    SelectedResourceName = "test-foundry",
                    SelectedResourceEndpoint = "https://test.services.ai.azure.com",
                    SelectedResourceGroup = "test-rg",
                    DefaultModel = "claude-sonnet-4-6",
                    CreatedAt = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc),
                    LastRefreshedAt = new DateTime(2026, 3, 16, 8, 0, 0, DateTimeKind.Utc),
                });
        }
        else
        {
            mock.Setup(x => x.GetStoredCredentialsAsync())
                .ReturnsAsync((FoundryStoredCredentials?)null);
        }

        return mock;
    }

    private static AzureFoundryAuthConfig CreateDefaultConfig() => new AzureFoundryAuthConfig();

    // ═════════════════════════════════════════════
    //  Test 1: Not authenticated → returns 1
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Foundry")]
    public void Token_ReturnsOne_WhenNotAuthenticated()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: false);
        var config = CreateDefaultConfig();
        var command = new FoundryTokenCommand(authMock.Object, config);
        var settings = new FoundryTokenCommand.Settings();

        // Act
        var result = command.Execute(null!, settings);

        // Assert
        result.Should().Be(1);
    }

    // ═════════════════════════════════════════════
    //  Test 2: Authenticated + stdout redirected (no --json) → raw token written
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Foundry")]
    public void Token_WritesRawToken_WhenOutputRedirectedAndNoJson()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: true);
        var config = CreateDefaultConfig();

        authMock.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("my-raw-token-value");

        var writer = new StringWriter();
        var command = new FoundryTokenCommand(authMock.Object, config);
        command.IsOutputRedirected = () => true;
        command.GetOutput = () => writer;
        var settings = new FoundryTokenCommand.Settings { Json = false };

        // Act
        var result = command.Execute(null!, settings);

        // Assert
        result.Should().Be(0);
        writer.ToString().Should().Contain("my-raw-token-value");
    }

    // ═════════════════════════════════════════════
    //  Test 3: Authenticated + stdout redirected + --json → gzip+base64 written
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Foundry")]
    public void Token_WritesGzipBase64Json_WhenOutputRedirectedAndJsonFlag()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: true);
        var config = CreateDefaultConfig();

        authMock.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("my-jwt-token");

        var writer = new StringWriter();
        var command = new FoundryTokenCommand(authMock.Object, config);
        command.IsOutputRedirected = () => true;
        command.GetOutput = () => writer;
        var settings = new FoundryTokenCommand.Settings { Json = true };

        // Act
        var result = command.Execute(null!, settings);

        // Assert
        result.Should().Be(0);

        var output = writer.ToString().Trim();

        // Must be valid base64
        var decodedBytes = Convert.FromBase64String(output);

        // Must decompress successfully with GZip
        using var ms = new MemoryStream(decodedBytes);
        using var gzip = new GZipStream(ms, CompressionMode.Decompress);
        using var sr = new StreamReader(gzip, Encoding.UTF8);
        var json = sr.ReadToEnd();

        // Must be valid JSON with expected fields
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("token").GetString().Should().Be("my-jwt-token");
        root.GetProperty("endpoint").GetString().Should().Be("https://test.services.ai.azure.com");
        root.GetProperty("model").GetString().Should().Be("claude-sonnet-4-6");
        root.GetProperty("resource").GetString().Should().Be("test-foundry");
        root.GetProperty("subscription").GetString().Should().Be("Test Sub");
    }
}
