using Xunit;
using Moq;
using FluentAssertions;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Testing;
using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.CLI.Tests.Commands;

public class AzureInitCommandTests
{
    // ─────────────────────────────────────────────
    //  Shared helpers
    // ─────────────────────────────────────────────

    private static Mock<IAzureAuthService> CreateAuthMock(bool authenticated = false)
    {
        var mock = new Mock<IAzureAuthService>();
        mock.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(authenticated);

        if (authenticated)
        {
            mock.Setup(x => x.GetStoredCredentialsAsync())
                .ReturnsAsync(new AzureStoredCredentials
                {
                    TenantId = "test-tenant",
                    RefreshToken = "test-refresh",
                    SubscriptionId = "sub-12345",
                    SubscriptionName = "Test Subscription",
                    CreatedAt = DateTime.UtcNow.AddDays(-1),
                    LastRefreshedAt = DateTime.UtcNow.AddHours(-1)
                });
        }
        else
        {
            mock.Setup(x => x.GetStoredCredentialsAsync())
                .ReturnsAsync((AzureStoredCredentials?)null);
        }

        return mock;
    }

    // ═════════════════════════════════════════════
    //  AzureInitCommand Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Azure")]
    public void Execute_WhenAlreadyAuthenticated_AndNoForce_ReturnsZeroWithoutReauth()
    {
        // Arrange
        var authMock = CreateAuthMock(authenticated: true);
        var console = new TestConsole();
        var command = new AzureInitCommand(authMock.Object, console);
        var settings = new AzureInitCommand.Settings { Force = false };

        // Act
        var result = command.Execute(null!, settings);

        // Assert
        result.Should().Be(0);
        authMock.Verify(x => x.InitiateLoginAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        console.Output.Should().Contain("Already authenticated");
    }

    [Fact]
    [Trait("Category", "Azure")]
    public void Execute_WithForceFlag_InitiatesLoginEvenIfAuthenticated()
    {
        // Arrange
        var authMock = CreateAuthMock(authenticated: true);
        var console = new TestConsole();
        // Press Enter (empty email) so tenant becomes "common"
        console.Input.PushTextWithEnter("");

        authMock.Setup(x => x.InitiateLoginAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Login attempted"));

        var command = new AzureInitCommand(authMock.Object, console);
        var settings = new AzureInitCommand.Settings { Force = true };

        // Act
        var result = command.Execute(null!, settings);

        // Assert — login was initiated despite already being authenticated
        authMock.Verify(x => x.InitiateLoginAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        result.Should().Be(1); // returns 1 because InitiateLoginAsync threw
    }

    [Fact]
    [Trait("Category", "Azure")]
    public void Execute_WithSingleSubscription_AutoSelects()
    {
        // Arrange
        var authMock = CreateAuthMock(authenticated: false);
        var console = new TestConsole();
        // Press Enter (empty email) → common tenant
        console.Input.PushTextWithEnter("");

        var authResult = new AzureAuthResult
        {
            AccessToken = "at",
            RefreshToken = "rt",
            ExpiresIn = 3600,
            TenantId = "common"
        };

        authMock.Setup(x => x.InitiateLoginAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(authResult);

        authMock.Setup(x => x.StoreCredentialsAsync(It.IsAny<AzureStoredCredentials>()))
            .Returns(Task.CompletedTask);

        authMock.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("management-token");

        authMock.Setup(x => x.ListSubscriptionsAsync("management-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AzureSubscription>
            {
                new() { SubscriptionId = "sub-only", DisplayName = "Only Subscription", State = "Enabled", TenantId = "common" }
            });

        var command = new AzureInitCommand(authMock.Object, console);
        var settings = new AzureInitCommand.Settings();

        // Act
        var result = command.Execute(null!, settings);

        // Assert — single subscription was auto-selected, success message shown
        result.Should().Be(0);
        console.Output.Should().Contain("Only Subscription");
    }

    [Fact]
    [Trait("Category", "Azure")]
    public void Execute_StoredCredentials_ContainTenantAndSubscription()
    {
        // Arrange
        var authMock = CreateAuthMock(authenticated: false);
        var console = new TestConsole();
        console.Input.PushTextWithEnter(""); // empty email → common tenant

        var authResult = new AzureAuthResult
        {
            AccessToken = "at",
            RefreshToken = "rt-stored",
            ExpiresIn = 3600,
            TenantId = "common"
        };

        authMock.Setup(x => x.InitiateLoginAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(authResult);

        AzureStoredCredentials? storedCredentials = null;
        authMock.Setup(x => x.StoreCredentialsAsync(It.IsAny<AzureStoredCredentials>()))
            .Callback<AzureStoredCredentials>(c => storedCredentials = c)
            .Returns(Task.CompletedTask);

        authMock.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("management-token");

        authMock.Setup(x => x.ListSubscriptionsAsync("management-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AzureSubscription>
            {
                new() { SubscriptionId = "sub-99", DisplayName = "My Sub", State = "Enabled", TenantId = "common" }
            });

        var command = new AzureInitCommand(authMock.Object, console);
        var settings = new AzureInitCommand.Settings();

        // Act
        command.Execute(null!, settings);

        // Assert — last stored credentials contain tenant + subscription
        storedCredentials.Should().NotBeNull();
        storedCredentials!.TenantId.Should().Be("common");
        storedCredentials.SubscriptionId.Should().Be("sub-99");
        storedCredentials.SubscriptionName.Should().Be("My Sub");
    }
}
