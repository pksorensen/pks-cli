using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.MsGraph;

public class MsGraphAuthenticationServiceTests
{
    private readonly Mock<IConfigurationService> _configServiceMock;
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly Mock<ILogger<MsGraphAuthenticationService>> _loggerMock;
    private readonly MsGraphAuthConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;

    public MsGraphAuthenticationServiceTests()
    {
        _configServiceMock = new Mock<IConfigurationService>();
        _httpHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpHandlerMock.Object);
        _loggerMock = new Mock<ILogger<MsGraphAuthenticationService>>();
        _config = new MsGraphAuthConfig
        {
            ClientId = "test-client-id",
            TenantId = "test-tenant-id"
        };

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    private MsGraphAuthenticationService CreateService()
    {
        return new MsGraphAuthenticationService(
            _httpClient,
            _configServiceMock.Object,
            _loggerMock.Object,
            _config);
    }

    [Fact]
    public async Task StoreToken_PersistsViaConfigurationService()
    {
        // Arrange
        var service = CreateService();
        var token = new MsGraphStoredToken
        {
            AccessToken = "test-access-token",
            RefreshToken = "test-refresh-token",
            Scopes = new[] { "User.Read" },
            ClientId = "test-client-id",
            TenantId = "test-tenant-id",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            IsValid = true,
            LastValidated = DateTime.UtcNow
        };

        _configServiceMock
            .Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await service.StoreTokenAsync(token);

        // Assert
        result.Should().BeTrue();
        _configServiceMock.Verify(
            x => x.SetAsync("msgraph.auth.token", It.IsAny<string>(), true, false),
            Times.Once);
    }

    [Fact]
    public async Task GetStoredToken_ReturnsDeserialized_WhenExists()
    {
        // Arrange
        var service = CreateService();
        var storedToken = new MsGraphStoredToken
        {
            AccessToken = "stored-access-token",
            RefreshToken = "stored-refresh-token",
            Scopes = new[] { "User.Read", "Mail.Read" },
            ClientId = "test-client-id",
            TenantId = "test-tenant-id",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ExpiresAt = new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc),
            IsValid = true,
            LastValidated = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var tokenJson = JsonSerializer.Serialize(storedToken, _jsonOptions);

        _configServiceMock
            .Setup(x => x.GetAsync("msgraph.auth.token"))
            .ReturnsAsync(tokenJson);

        // Act
        var result = await service.GetStoredTokenAsync();

        // Assert
        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("stored-access-token");
        result.RefreshToken.Should().Be("stored-refresh-token");
        result.Scopes.Should().Contain("User.Read");
        result.Scopes.Should().Contain("Mail.Read");
        result.ClientId.Should().Be("test-client-id");
        result.TenantId.Should().Be("test-tenant-id");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task GetStoredToken_ReturnsNull_WhenNotStored()
    {
        // Arrange
        var service = CreateService();
        _configServiceMock
            .Setup(x => x.GetAsync("msgraph.auth.token"))
            .ReturnsAsync((string?)null);

        // Act
        var result = await service.GetStoredTokenAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task IsAuthenticated_ReturnsFalse_WhenNoToken()
    {
        // Arrange
        var service = CreateService();
        _configServiceMock
            .Setup(x => x.GetAsync("msgraph.auth.token"))
            .ReturnsAsync((string?)null);

        // Act
        var result = await service.IsAuthenticatedAsync();

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthenticated_ReturnsFalse_WhenExpired()
    {
        // Arrange
        var service = CreateService();
        var expiredToken = new MsGraphStoredToken
        {
            AccessToken = "expired-token",
            Scopes = new[] { "User.Read" },
            ClientId = "test-client-id",
            TenantId = "test-tenant-id",
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            ExpiresAt = DateTime.UtcNow.AddHours(-1), // Expired 1 hour ago
            IsValid = true,
            LastValidated = DateTime.UtcNow.AddMinutes(-30)
        };

        var tokenJson = JsonSerializer.Serialize(expiredToken, _jsonOptions);
        _configServiceMock
            .Setup(x => x.GetAsync("msgraph.auth.token"))
            .ReturnsAsync(tokenJson);

        // Act
        var result = await service.IsAuthenticatedAsync();

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task StoreConfig_PersistsClientIdAndTenantId()
    {
        // Arrange
        var service = CreateService();
        _configServiceMock
            .Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        // Act
        await service.StoreConfigAsync("my-client-id", "my-tenant-id");

        // Assert
        _configServiceMock.Verify(
            x => x.SetAsync(
                "msgraph.auth.config",
                It.Is<string>(json => json.Contains("my-client-id") && json.Contains("my-tenant-id")),
                true,
                false),
            Times.Once);
    }

    [Fact]
    public async Task LoadConfig_ReturnsConfig_WhenExists()
    {
        // Arrange
        var service = CreateService();
        var configJson = JsonSerializer.Serialize(
            new { client_id = "loaded-client-id", tenant_id = "loaded-tenant-id" });

        _configServiceMock
            .Setup(x => x.GetAsync("msgraph.auth.config"))
            .ReturnsAsync(configJson);

        // Act
        var result = await service.LoadConfigAsync();

        // Assert
        result.Should().NotBeNull();
        result!.ClientId.Should().Be("loaded-client-id");
        result.TenantId.Should().Be("loaded-tenant-id");
    }

    [Fact]
    public async Task LoadConfig_ReturnsNull_WhenNotStored()
    {
        // Arrange
        var service = CreateService();
        _configServiceMock
            .Setup(x => x.GetAsync("msgraph.auth.config"))
            .ReturnsAsync((string?)null);

        // Act
        var result = await service.LoadConfigAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetValidAccessToken_ReturnsToken_WhenNotExpired()
    {
        // Arrange
        var service = CreateService();
        var validToken = new MsGraphStoredToken
        {
            AccessToken = "valid-access-token",
            RefreshToken = "refresh-token",
            Scopes = new[] { "User.Read" },
            ClientId = "test-client-id",
            TenantId = "test-tenant-id",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), // Expires in 1 hour
            IsValid = true,
            LastValidated = DateTime.UtcNow
        };

        var tokenJson = JsonSerializer.Serialize(validToken, _jsonOptions);
        _configServiceMock
            .Setup(x => x.GetAsync("msgraph.auth.token"))
            .ReturnsAsync(tokenJson);

        // Act
        var result = await service.GetValidAccessTokenAsync();

        // Assert
        result.Should().Be("valid-access-token");
    }

    [Fact]
    public async Task GetValidAccessToken_ReturnsNull_WhenNoToken()
    {
        // Arrange
        var service = CreateService();
        _configServiceMock
            .Setup(x => x.GetAsync("msgraph.auth.token"))
            .ReturnsAsync((string?)null);

        // Act
        var result = await service.GetValidAccessTokenAsync();

        // Assert
        result.Should().BeNull();
    }
}
