using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services;

public class JiraServiceTests
{
    [Fact]
    public async Task StoreCredentialsAsync_ShouldPersistAllJiraKeysGlobally_AndKeepTokenReadable()
    {
        var handler = new CountingHttpMessageHandler(HttpStatusCode.OK, "{\"self\":\"ok\"}");
        var httpClient = new HttpClient(handler);
        var config = new Mock<IConfigurationService>();
        var logger = new Mock<ILogger<JiraService>>();
        var service = new JiraService(httpClient, config.Object, logger.Object);

        var credentials = new JiraStoredCredentials
        {
            BaseUrl = "https://example.atlassian.net",
            Email = "user@example.com",
            ApiToken = "token",
            AuthMethod = JiraAuthMethod.ApiToken,
            DeploymentType = JiraDeploymentType.Cloud,
            CreatedAt = DateTime.UtcNow,
            LastRefreshedAt = DateTime.UtcNow
        };

        await service.StoreCredentialsAsync(credentials);

        config.Verify(x => x.SetAsync("jira:base_url", "https://example.atlassian.net", true, false), Times.Once);
        config.Verify(x => x.SetAsync("jira:auth_method", "ApiToken", true, false), Times.Once);
        config.Verify(x => x.SetAsync("jira:deployment_type", "Cloud", true, false), Times.Once);
        config.Verify(x => x.SetAsync("jira:email", "user@example.com", true, false), Times.Once);
        config.Verify(x => x.SetAsync("jira:api_token", "token", true, false), Times.Once);
        config.Verify(x => x.SetAsync("jira:created_at", It.IsAny<string>(), true, false), Times.Once);
        config.Verify(x => x.SetAsync("jira:last_refreshed_at", It.IsAny<string>(), true, false), Times.Once);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_ShouldUseHttpClientOnce_AndReturnSuccess()
    {
        var handler = new CountingHttpMessageHandler(HttpStatusCode.OK, "{\"self\":\"ok\"}");
        var httpClient = new HttpClient(handler);
        var config = new Mock<IConfigurationService>();
        var logger = new Mock<ILogger<JiraService>>();
        var service = new JiraService(httpClient, config.Object, logger.Object);

        var credentials = new JiraStoredCredentials
        {
            BaseUrl = "https://example.atlassian.net",
            Email = "user@example.com",
            ApiToken = "token",
            AuthMethod = JiraAuthMethod.ApiToken,
            DeploymentType = JiraDeploymentType.Cloud
        };

        var result = await service.ValidateCredentialsAsync(credentials);

        result.Should().BeTrue();
        handler.SendCount.Should().Be(1);
        handler.LastRequestUri.Should().Be("https://example.atlassian.net/rest/api/3/myself");
        handler.LastAuthorizationScheme.Should().Be("Basic");
    }

    [Fact]
    public async Task ValidateCredentialsAsync_ShouldTrimWhitespaceFromCredentials()
    {
        var handler = new CountingHttpMessageHandler(HttpStatusCode.OK, "{\"self\":\"ok\"}");
        var httpClient = new HttpClient(handler);
        var config = new Mock<IConfigurationService>();
        var logger = new Mock<ILogger<JiraService>>();
        var service = new JiraService(httpClient, config.Object, logger.Object);

        var credentials = new JiraStoredCredentials
        {
            BaseUrl = "  https://example.atlassian.net/  ",
            Email = "  user@example.com  ",
            ApiToken = "  token  ",
            AuthMethod = JiraAuthMethod.ApiToken,
            DeploymentType = JiraDeploymentType.Cloud
        };

        var result = await service.ValidateCredentialsAsync(credentials);

        result.Should().BeTrue();
        handler.SendCount.Should().Be(1);
        handler.LastRequestUri.Should().Be("https://example.atlassian.net/rest/api/3/myself");
        handler.LastAuthorizationScheme.Should().Be("Basic");
        handler.LastAuthorizationParameter.Should().NotBeNullOrEmpty();

        var decoded = Encoding.ASCII.GetString(Convert.FromBase64String(handler.LastAuthorizationParameter!));
        decoded.Should().Be("user@example.com:token");
    }

    [Fact]
    public async Task SearchIssuesAsync_ForCloud_ShouldUseSearchJqlEndpoint()
    {
        var responseBody = """
                {
                    "total": 1,
                    "issues": [
                        {
                            "id": "10001",
                            "key": "UDV-1",
                            "fields": {
                                "summary": "Test issue",
                                "status": { "name": "To Do" },
                                "issuetype": { "name": "Task" },
                                "priority": { "name": "Medium" },
                                "assignee": null,
                                "project": { "key": "UDV" }
                            }
                        }
                    ]
                }
                """;

        var handler = new CountingHttpMessageHandler(HttpStatusCode.OK, responseBody);
        var httpClient = new HttpClient(handler);

        var configValues = new Dictionary<string, string>
        {
            ["jira:base_url"] = "https://example.atlassian.net",
            ["jira:auth_method"] = JiraAuthMethod.ApiToken.ToString(),
            ["jira:deployment_type"] = JiraDeploymentType.Cloud.ToString(),
            ["jira:email"] = "user@example.com",
            ["jira:api_token"] = "token",
            ["jira:cloud_id"] = "cloud-123"
        };

        var config = new Mock<IConfigurationService>();
        config.Setup(x => x.GetAsync(It.IsAny<string>()))
                .ReturnsAsync((string key) => configValues.TryGetValue(key, out var value) ? value : null);

        var logger = new Mock<ILogger<JiraService>>();
        var service = new JiraService(httpClient, config.Object, logger.Object);

        var result = await service.SearchIssuesAsync("project = UDV ORDER BY created ASC", maxResults: 100);

        handler.SendCount.Should().Be(2); // 1 for /field discovery + 1 for search
        handler.LastRequestUri.Should().Be("https://api.atlassian.com/ex/jira/cloud-123/rest/api/3/search/jql");
        handler.LastRequestBody.Should().NotContain("\"startAt\"");
        result.Total.Should().Be(1);
        result.Issues.Should().ContainSingle(i => i.Key == "UDV-1");
    }

    [Fact]
    public async Task SearchIssuesAsync_WhenTotalMissing_ShouldFallbackToIssueCount()
    {
        var responseBody = """
                {
                    "issues": [
                        {
                            "id": "10001",
                            "key": "UDV-1",
                            "fields": {
                                "summary": "Test issue",
                                "status": { "name": "To Do" },
                                "issuetype": { "name": "Task" },
                                "priority": { "name": "Medium" },
                                "assignee": null,
                                "project": { "key": "UDV" }
                            }
                        }
                    ]
                }
                """;

        var handler = new CountingHttpMessageHandler(HttpStatusCode.OK, responseBody);
        var httpClient = new HttpClient(handler);

        var configValues = new Dictionary<string, string>
        {
            ["jira:base_url"] = "https://example.atlassian.net",
            ["jira:auth_method"] = JiraAuthMethod.ApiToken.ToString(),
            ["jira:deployment_type"] = JiraDeploymentType.Cloud.ToString(),
            ["jira:email"] = "user@example.com",
            ["jira:api_token"] = "token",
            ["jira:cloud_id"] = "cloud-123"
        };

        var config = new Mock<IConfigurationService>();
        config.Setup(x => x.GetAsync(It.IsAny<string>()))
                .ReturnsAsync((string key) => configValues.TryGetValue(key, out var value) ? value : null);

        var logger = new Mock<ILogger<JiraService>>();
        var service = new JiraService(httpClient, config.Object, logger.Object);

        var result = await service.SearchIssuesAsync("project = UDV ORDER BY created ASC", maxResults: 100);

        result.Total.Should().Be(1);
        result.Issues.Should().HaveCount(1);
    }

    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public int SendCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }
        public string? LastRequestBody { get; private set; }

        public CountingHttpMessageHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body)
            };

            return response;
        }
    }
}