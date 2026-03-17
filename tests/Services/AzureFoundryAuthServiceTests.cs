using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.CLI.Tests.Services;

public class AzureFoundryAuthServiceTests
{
    // ─────────────────────────────────────────────
    //  Shared helpers
    // ─────────────────────────────────────────────

    private static Mock<IConfigurationService> CreateConfigServiceMock(Dictionary<string, string>? initialData = null)
    {
        var mock = new Mock<IConfigurationService>();
        var store = initialData ?? new Dictionary<string, string>();

        mock.Setup(x => x.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => store.TryGetValue(key, out var v) ? v : null);

        mock.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback<string, string, bool, bool>((k, v, _, _) => store[k] = v)
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .Callback<string>(k => store.Remove(k))
            .Returns(Task.CompletedTask);

        return mock;
    }

    private static HttpClient CreateMockHttpClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var mockHandler = new MockHttpMessageHandler(handler);
        return new HttpClient(mockHandler);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }

    private static AzureFoundryAuthService CreateService(
        HttpClient? httpClient = null,
        Mock<IConfigurationService>? configMock = null,
        AzureFoundryAuthConfig? config = null)
    {
        return new AzureFoundryAuthService(
            httpClient ?? new HttpClient(),
            (configMock ?? CreateConfigServiceMock()).Object,
            new Mock<ILogger<AzureFoundryAuthService>>().Object,
            config ?? new AzureFoundryAuthConfig());
    }

    private static FoundryStoredCredentials CreateValidCredentials()
    {
        return new FoundryStoredCredentials
        {
            TenantId = "test-tenant-id",
            RefreshToken = "test-refresh-token",
            SelectedSubscriptionId = "sub-123",
            SelectedSubscriptionName = "My Subscription",
            SelectedResourceEndpoint = "https://myresource.cognitiveservices.azure.com",
            SelectedResourceName = "my-resource",
            SelectedResourceGroup = "my-rg",
            DefaultModel = "gpt-4",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            LastRefreshedAt = DateTime.UtcNow.AddHours(-1)
        };
    }

    // ═════════════════════════════════════════════
    //  1. IsAuthenticatedAsync Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task IsAuthenticated_ReturnsTrue_WhenCredentialsExist()
    {
        // Arrange
        var credentials = CreateValidCredentials();
        var json = JsonSerializer.Serialize(credentials);
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["foundry.auth.credentials"] = json
        });
        var service = CreateService(configMock: configMock);

        // Act
        var result = await service.IsAuthenticatedAsync();

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task IsAuthenticated_ReturnsFalse_WhenNoCredentials()
    {
        // Arrange
        var configMock = CreateConfigServiceMock();
        var service = CreateService(configMock: configMock);

        // Act
        var result = await service.IsAuthenticatedAsync();

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task IsAuthenticated_ReturnsFalse_WhenEmptyRefreshToken()
    {
        // Arrange
        var credentials = CreateValidCredentials();
        credentials.RefreshToken = string.Empty;
        var json = JsonSerializer.Serialize(credentials);
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["foundry.auth.credentials"] = json
        });
        var service = CreateService(configMock: configMock);

        // Act
        var result = await service.IsAuthenticatedAsync();

        // Assert
        result.Should().BeFalse();
    }

    // ═════════════════════════════════════════════
    //  2. GetStoredCredentialsAsync Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task GetStoredCredentials_ReturnsNull_WhenNoData()
    {
        // Arrange
        var configMock = CreateConfigServiceMock();
        var service = CreateService(configMock: configMock);

        // Act
        var result = await service.GetStoredCredentialsAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task GetStoredCredentials_ReturnsCredentials_WhenDataExists()
    {
        // Arrange
        var credentials = CreateValidCredentials();
        var json = JsonSerializer.Serialize(credentials);
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["foundry.auth.credentials"] = json
        });
        var service = CreateService(configMock: configMock);

        // Act
        var result = await service.GetStoredCredentialsAsync();

        // Assert
        result.Should().NotBeNull();
        result!.TenantId.Should().Be("test-tenant-id");
        result.RefreshToken.Should().Be("test-refresh-token");
        result.SelectedSubscriptionId.Should().Be("sub-123");
        result.SelectedSubscriptionName.Should().Be("My Subscription");
        result.SelectedResourceEndpoint.Should().Be("https://myresource.cognitiveservices.azure.com");
        result.SelectedResourceName.Should().Be("my-resource");
        result.SelectedResourceGroup.Should().Be("my-rg");
        result.DefaultModel.Should().Be("gpt-4");
    }

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task GetStoredCredentials_ReturnsNull_OnDeserializationError()
    {
        // Arrange
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["foundry.auth.credentials"] = "this is not valid json {{{{"
        });
        var service = CreateService(configMock: configMock);

        // Act
        var result = await service.GetStoredCredentialsAsync();

        // Assert
        result.Should().BeNull();
    }

    // ═════════════════════════════════════════════
    //  3. StoreCredentialsAsync Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task StoreCredentials_SerializesAndSaves()
    {
        // Arrange
        var configMock = CreateConfigServiceMock();
        var service = CreateService(configMock: configMock);
        var credentials = CreateValidCredentials();

        // Act
        await service.StoreCredentialsAsync(credentials);

        // Assert
        configMock.Verify(x => x.SetAsync(
            "foundry.auth.credentials",
            It.Is<string>(json =>
                json.Contains("test-tenant-id") &&
                json.Contains("test-refresh-token")),
            true,
            false),
            Times.Once);
    }

    // ═════════════════════════════════════════════
    //  4. ClearCredentialsAsync Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task ClearCredentials_DeletesStorageKey()
    {
        // Arrange
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["foundry.auth.credentials"] = JsonSerializer.Serialize(CreateValidCredentials())
        });
        var service = CreateService(configMock: configMock);

        // Act
        await service.ClearCredentialsAsync();

        // Assert
        configMock.Verify(x => x.DeleteAsync("foundry.auth.credentials"), Times.Once);
    }

    // ═════════════════════════════════════════════
    //  5. GetAccessTokenAsync Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task GetAccessToken_ReturnsNull_WhenNotAuthenticated()
    {
        // Arrange — no stored credentials
        var configMock = CreateConfigServiceMock();
        var service = CreateService(configMock: configMock);

        // Act
        var result = await service.GetAccessTokenAsync("https://management.azure.com/.default");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task GetAccessToken_ReturnsToken_WhenRefreshSucceeds()
    {
        // Arrange
        var credentials = CreateValidCredentials();
        var json = JsonSerializer.Serialize(credentials);
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["foundry.auth.credentials"] = json
        });

        var tokenResponse = new FoundryTokenResponse
        {
            AccessToken = "new-access-token",
            RefreshToken = credentials.RefreshToken, // same refresh token (no rotation)
            ExpiresIn = 3600,
            TokenType = "Bearer"
        };

        var httpClient = CreateMockHttpClient(async request =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(tokenResponse))
            };
        });

        var service = CreateService(httpClient: httpClient, configMock: configMock);

        // Act
        var result = await service.GetAccessTokenAsync("https://management.azure.com/.default");

        // Assert
        result.Should().Be("new-access-token");
    }

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task GetAccessToken_UpdatesRefreshToken_WhenRotated()
    {
        // Arrange
        var credentials = CreateValidCredentials();
        var json = JsonSerializer.Serialize(credentials);
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["foundry.auth.credentials"] = json
        });

        var tokenResponse = new FoundryTokenResponse
        {
            AccessToken = "new-access-token",
            RefreshToken = "rotated-refresh-token", // different from original
            ExpiresIn = 3600,
            TokenType = "Bearer"
        };

        var httpClient = CreateMockHttpClient(async request =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(tokenResponse))
            };
        });

        var service = CreateService(httpClient: httpClient, configMock: configMock);

        // Act
        var result = await service.GetAccessTokenAsync("https://management.azure.com/.default");

        // Assert
        result.Should().Be("new-access-token");

        // Verify credentials were stored with the rotated refresh token
        configMock.Verify(x => x.SetAsync(
            "foundry.auth.credentials",
            It.Is<string>(storedJson => storedJson.Contains("rotated-refresh-token")),
            true,
            false),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task GetAccessToken_ReturnsNull_WhenRefreshFails()
    {
        // Arrange
        var credentials = CreateValidCredentials();
        var json = JsonSerializer.Serialize(credentials);
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["foundry.auth.credentials"] = json
        });

        var httpClient = CreateMockHttpClient(async request =>
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\"}")
            };
        });

        var service = CreateService(httpClient: httpClient, configMock: configMock);

        // Act
        var result = await service.GetAccessTokenAsync("https://management.azure.com/.default");

        // Assert
        result.Should().BeNull();
    }

    // ═════════════════════════════════════════════
    //  6. ListSubscriptionsAsync Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task ListSubscriptions_ReturnsSubscriptions()
    {
        // Arrange
        var subscriptionsResponse = new AzureSubscriptionListResponse
        {
            Value = new List<AzureSubscription>
            {
                new() { SubscriptionId = "sub-1", DisplayName = "Dev Subscription", State = "Enabled", TenantId = "tenant-1" },
                new() { SubscriptionId = "sub-2", DisplayName = "Prod Subscription", State = "Enabled", TenantId = "tenant-1" }
            }
        };

        var httpClient = CreateMockHttpClient(async request =>
        {
            request.RequestUri!.ToString().Should().Contain("management.azure.com/subscriptions");
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("test-access-token");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(subscriptionsResponse))
            };
        });

        var service = CreateService(httpClient: httpClient);

        // Act
        var result = await service.ListSubscriptionsAsync("test-access-token");

        // Assert
        result.Should().HaveCount(2);
        result[0].SubscriptionId.Should().Be("sub-1");
        result[0].DisplayName.Should().Be("Dev Subscription");
        result[1].SubscriptionId.Should().Be("sub-2");
        result[1].DisplayName.Should().Be("Prod Subscription");
    }

    // ═════════════════════════════════════════════
    //  7. ListFoundryResourcesAsync Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task ListFoundryResources_FiltersToAIServicesOnly()
    {
        // Arrange — return a mix of resource kinds
        var accountsResponse = new CognitiveServicesAccountListResponse
        {
            Value = new List<CognitiveServicesAccount>
            {
                new()
                {
                    Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/ai-foundry",
                    Name = "ai-foundry",
                    Kind = "AIServices",
                    Location = "eastus",
                    Properties = new CognitiveServicesAccountProperties { Endpoint = "https://ai-foundry.cognitiveservices.azure.com" }
                },
                new()
                {
                    Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/speech-service",
                    Name = "speech-service",
                    Kind = "SpeechServices",
                    Location = "eastus",
                    Properties = new CognitiveServicesAccountProperties { Endpoint = "https://speech-service.cognitiveservices.azure.com" }
                },
                new()
                {
                    Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/ai-hub",
                    Name = "ai-hub",
                    Kind = "OpenAI",
                    Location = "westus",
                    Properties = new CognitiveServicesAccountProperties { Endpoint = "https://ai-hub.services.ai.azure.com" }
                },
                new()
                {
                    Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/text-analytics",
                    Name = "text-analytics",
                    Kind = "TextAnalytics",
                    Location = "westus",
                    Properties = new CognitiveServicesAccountProperties { Endpoint = "https://text-analytics.cognitiveservices.azure.com" }
                }
            }
        };

        var httpClient = CreateMockHttpClient(async request =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(accountsResponse))
            };
        });

        var service = CreateService(httpClient: httpClient);

        // Act
        var result = await service.ListFoundryResourcesAsync("test-access-token", "sub-1");

        // Assert — should only include AIServices kind and .services.ai.azure.com endpoint
        result.Should().HaveCount(2);
        result.Should().Contain(a => a.Name == "ai-foundry");
        result.Should().Contain(a => a.Name == "ai-hub");
        result.Should().NotContain(a => a.Name == "speech-service");
        result.Should().NotContain(a => a.Name == "text-analytics");
    }

    // ═════════════════════════════════════════════
    //  8. ListDeploymentsAsync Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureFoundry")]
    public async Task ListDeployments_ReturnsDeployments()
    {
        // Arrange
        var deploymentsResponse = new FoundryDeploymentListResponse
        {
            Value = new List<FoundryDeployment>
            {
                new()
                {
                    Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/my-account/deployments/gpt-4-deployment",
                    Name = "gpt-4-deployment",
                    Type = "Microsoft.CognitiveServices/accounts/deployments",
                    Properties = new FoundryDeploymentProperties
                    {
                        ProvisioningState = "Succeeded",
                        Model = new FoundryDeploymentModel
                        {
                            Format = "OpenAI",
                            Name = "gpt-4",
                            Version = "0613"
                        }
                    }
                },
                new()
                {
                    Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/my-account/deployments/embed-deployment",
                    Name = "embed-deployment",
                    Type = "Microsoft.CognitiveServices/accounts/deployments",
                    Properties = new FoundryDeploymentProperties
                    {
                        ProvisioningState = "Succeeded",
                        Model = new FoundryDeploymentModel
                        {
                            Format = "OpenAI",
                            Name = "text-embedding-ada-002",
                            Version = "2"
                        }
                    }
                }
            }
        };

        var httpClient = CreateMockHttpClient(async request =>
        {
            request.RequestUri!.ToString().Should().Contain("my-account/deployments");
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(deploymentsResponse))
            };
        });

        var service = CreateService(httpClient: httpClient);

        // Act
        var result = await service.ListDeploymentsAsync("test-access-token", "sub-1", "rg", "my-account");

        // Assert
        result.Should().HaveCount(2);

        result[0].Name.Should().Be("gpt-4-deployment");
        result[0].Properties.ProvisioningState.Should().Be("Succeeded");
        result[0].Properties.Model.Name.Should().Be("gpt-4");
        result[0].Properties.Model.Version.Should().Be("0613");

        result[1].Name.Should().Be("embed-deployment");
        result[1].Properties.Model.Name.Should().Be("text-embedding-ada-002");
    }

    // ─────────────────────────────────────────────
    //  Tenant discovery
    // ─────────────────────────────────────────────

    [Fact]
    public async Task DiscoverTenant_ReturnsTenantId_FromOpenIdConfiguration()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(async request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/userrealm/"))
            {
                var json = JsonSerializer.Serialize(new { DomainName = "contoso.com", NameSpaceType = "Managed" });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }

            if (url.Contains("openid-configuration"))
            {
                var json = JsonSerializer.Serialize(new { issuer = "https://sts.windows.net/72f988bf-86f1-41af-91ab-2d7cd011db47/" });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(httpClient: httpClient);

        // Act
        var result = await service.DiscoverTenantAsync("user@contoso.com");

        // Assert
        result.Should().Be("72f988bf-86f1-41af-91ab-2d7cd011db47");
    }

    [Fact]
    public async Task DiscoverTenant_FallsToDomain_WhenOpenIdFails()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(async request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/userrealm/"))
            {
                var json = JsonSerializer.Serialize(new { DomainName = "contoso.com", NameSpaceType = "Managed" });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(httpClient: httpClient);

        // Act
        var result = await service.DiscoverTenantAsync("user@contoso.com");

        // Assert
        result.Should().Be("contoso.com");
    }

    [Fact]
    public async Task DiscoverTenant_ReturnsNull_WhenUserRealmFails()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(async request =>
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        var service = CreateService(httpClient: httpClient);

        // Act
        var result = await service.DiscoverTenantAsync("invalid@email");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DiscoverTenant_ReturnsNull_WhenNoDomainInResponse()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(async request =>
        {
            var json = JsonSerializer.Serialize(new { NameSpaceType = "Unknown" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var service = CreateService(httpClient: httpClient);

        // Act
        var result = await service.DiscoverTenantAsync("user@unknown.com");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DiscoverTenant_HandlesV2Issuer()
    {
        // Arrange — issuer with /v2.0 suffix
        var httpClient = CreateMockHttpClient(async request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/userrealm/"))
            {
                var json = JsonSerializer.Serialize(new { DomainName = "contoso.com" });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }

            if (url.Contains("openid-configuration"))
            {
                var json = JsonSerializer.Serialize(new { issuer = "https://login.microsoftonline.com/72f988bf-86f1-41af-91ab-2d7cd011db47/v2.0" });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(httpClient: httpClient);

        // Act
        var result = await service.DiscoverTenantAsync("user@contoso.com");

        // Assert
        result.Should().Be("72f988bf-86f1-41af-91ab-2d7cd011db47");
    }
}
