using Xunit;
using Moq;
using FluentAssertions;
using PKS.Commands.Foundry;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Testing;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.CLI.Tests.Commands;

/// <summary>
/// Tests for Foundry CLI commands: init, status, and token.
/// Covers authentication flows, credential display, and token retrieval.
/// </summary>
public class FoundryInitCommandTests
{
    // ─────────────────────────────────────────────
    //  Shared helpers
    // ─────────────────────────────────────────────

    private static Mock<IAzureFoundryAuthService> CreateAuthServiceMock(bool authenticated = false)
    {
        var mock = new Mock<IAzureFoundryAuthService>();

        mock.Setup(x => x.IsAuthenticatedAsync())
            .ReturnsAsync(authenticated);

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

    private static AzureFoundryAuthConfig CreateDefaultConfig()
    {
        return new AzureFoundryAuthConfig();
    }

    // ═════════════════════════════════════════════
    //  1. FoundryStatusCommand Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Foundry")]
    public void Status_ShowsNotAuthenticated_WhenNoCredentials()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: false);
        var console = new TestConsole();
        var command = new FoundryStatusCommand(authMock.Object, console);
        var settings = new FoundrySettings();

        // Act
        var result = command.Execute(null!, settings);

        // Assert
        var output = console.Output;
        output.Should().Contain("Not authenticated");
    }

    [Fact]
    [Trait("Category", "Foundry")]
    public void Status_ShowsCredentials_WhenAuthenticated()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: true);
        var console = new TestConsole();
        var command = new FoundryStatusCommand(authMock.Object, console);
        var settings = new FoundrySettings();

        // Act
        var result = command.Execute(null!, settings);

        // Assert
        var output = console.Output;
        output.Should().Contain("test-tenant");
        output.Should().Contain("Test Sub");
        output.Should().Contain("test-foundry");
        output.Should().Contain("https://test.services.ai.azure.com");
        output.Should().Contain("claude-sonnet-4-6");
    }

    [Fact]
    [Trait("Category", "Foundry")]
    public void Status_ReturnsZero_WhenNotAuthenticated()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: false);
        var console = new TestConsole();
        var command = new FoundryStatusCommand(authMock.Object, console);
        var settings = new FoundrySettings();

        // Act
        var result = command.Execute(null!, settings);

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Foundry")]
    public void Status_ReturnsZero_WhenAuthenticated()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: true);
        var console = new TestConsole();
        var command = new FoundryStatusCommand(authMock.Object, console);
        var settings = new FoundrySettings();

        // Act
        var result = command.Execute(null!, settings);

        // Assert
        result.Should().Be(0);
    }

    // ═════════════════════════════════════════════
    //  2. FoundryTokenCommand Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Foundry")]
    public void Token_ReturnsError_WhenNotAuthenticated()
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

    [Fact]
    [Trait("Category", "Foundry")]
    public void Token_PrintsToken_WhenAuthenticated()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: true);
        var config = CreateDefaultConfig();

        authMock.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("test-access-token");

        var command = new FoundryTokenCommand(authMock.Object, config);
        var settings = new FoundryTokenCommand.Settings();

        // Capture Console.Write output
        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            // Act
            var result = command.Execute(null!, settings);

            // Assert
            result.Should().Be(0);
            writer.ToString().Should().Contain("test-access-token");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    [Trait("Category", "Foundry")]
    public void Token_ReturnsError_WhenTokenRefreshFails()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: true);
        var config = CreateDefaultConfig();

        authMock.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var command = new FoundryTokenCommand(authMock.Object, config);
        var settings = new FoundryTokenCommand.Settings();

        // Act
        var result = command.Execute(null!, settings);

        // Assert
        result.Should().Be(1);
    }

    // ═════════════════════════════════════════════
    //  3. FoundryInitCommand Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Foundry")]
    public void Init_ShowsAlreadyAuthenticated_WhenNotForced()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: true);
        var config = CreateDefaultConfig();
        var console = new TestConsole();
        var command = new FoundryInitCommand(authMock.Object, config, console);
        var settings = new FoundryInitCommand.Settings { Force = false };

        // Act
        var result = command.Execute(null!, settings);

        // Assert
        result.Should().Be(0);
        var output = console.Output;
        output.Should().Contain("Already authenticated");
    }

    [Fact]
    [Trait("Category", "Foundry")]
    public void Init_SkipsAuthCheck_WhenForced()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: true);
        var config = CreateDefaultConfig();
        var console = new TestConsole();
        // Provide empty input (press Enter) for the email prompt, selecting "common" tenant
        console.Input.PushTextWithEnter("");
        var command = new FoundryInitCommand(authMock.Object, config, console);
        var settings = new FoundryInitCommand.Settings { Force = true };

        // Setup InitiateLoginAsync to throw so we can verify it was called
        authMock.Setup(x => x.InitiateLoginAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Login attempted"));

        // Act
        var result = command.Execute(null!, settings);

        // Assert — InitiateLoginAsync was called (force bypasses the already-authenticated check)
        authMock.Verify(x => x.InitiateLoginAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // The command returns 1 because the login threw an exception
        result.Should().Be(1);
        var output = console.Output;
        output.Should().Contain("Authentication failed");
    }

    [Fact]
    public void Init_DiscoversTenant_WhenEmailProvided()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: false);
        var config = CreateDefaultConfig();
        var console = new TestConsole();
        // Provide email input for the email prompt
        console.Input.PushTextWithEnter("user@contoso.com");
        var command = new FoundryInitCommand(authMock.Object, config, console);
        var settings = new FoundryInitCommand.Settings();

        // Setup tenant discovery to return a tenant ID
        authMock.Setup(x => x.DiscoverTenantAsync("user@contoso.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync("discovered-tenant-id");

        // Setup InitiateLoginAsync to throw so we can verify the tenant that was passed
        authMock.Setup(x => x.InitiateLoginAsync("discovered-tenant-id", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Login attempted"));

        // Act
        var result = command.Execute(null!, settings);

        // Assert — InitiateLoginAsync was called with the discovered tenant
        authMock.Verify(x => x.DiscoverTenantAsync("user@contoso.com", It.IsAny<CancellationToken>()), Times.Once);
        authMock.Verify(x => x.InitiateLoginAsync("discovered-tenant-id", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        var output = console.Output;
        output.Should().Contain("Found tenant");
        output.Should().Contain("discovered-tenant-id");
    }

    [Fact]
    public void Init_UsesCommonTenant_WhenEmailEmpty()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: false);
        var config = CreateDefaultConfig();
        var console = new TestConsole();
        // Press Enter (empty email)
        console.Input.PushTextWithEnter("");
        var command = new FoundryInitCommand(authMock.Object, config, console);
        var settings = new FoundryInitCommand.Settings();

        authMock.Setup(x => x.InitiateLoginAsync("common", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Login attempted"));

        // Act
        command.Execute(null!, settings);

        // Assert — uses "common" tenant, no discovery call
        authMock.Verify(x => x.DiscoverTenantAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        authMock.Verify(x => x.InitiateLoginAsync("common", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Init_SkipsEmailPrompt_WhenTenantFlagProvided()
    {
        // Arrange
        var authMock = CreateAuthServiceMock(authenticated: false);
        var config = CreateDefaultConfig();
        var console = new TestConsole();
        // No input pushed — the prompt should be skipped entirely
        var command = new FoundryInitCommand(authMock.Object, config, console);
        var settings = new FoundryInitCommand.Settings { TenantId = "explicit-tenant" };

        authMock.Setup(x => x.InitiateLoginAsync("explicit-tenant", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Login attempted"));

        // Act
        command.Execute(null!, settings);

        // Assert — uses explicit tenant, no discovery, no prompt
        authMock.Verify(x => x.DiscoverTenantAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        authMock.Verify(x => x.InitiateLoginAsync("explicit-tenant", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
