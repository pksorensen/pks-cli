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
        handler.LastRequestBody.Should().Contain("\"startAt\"");
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

    [Fact]
    public async Task SearchIssuesAsync_SinglePage_ShouldNotMakeExtraRequests()
    {
        // total=2, issues returned=2, so no further pages needed
        var searchResponse = """
                {
                    "total": 2,
                    "issues": [
                        {
                            "id": "10001",
                            "key": "UDV-1",
                            "fields": {
                                "summary": "Issue one",
                                "status": { "name": "To Do" },
                                "issuetype": { "name": "Task" },
                                "priority": { "name": "Medium" },
                                "assignee": null,
                                "project": { "key": "UDV" }
                            }
                        },
                        {
                            "id": "10002",
                            "key": "UDV-2",
                            "fields": {
                                "summary": "Issue two",
                                "status": { "name": "Done" },
                                "issuetype": { "name": "Bug" },
                                "priority": { "name": "High" },
                                "assignee": null,
                                "project": { "key": "UDV" }
                            }
                        }
                    ]
                }
                """;

        var handler = new SequentialHttpMessageHandler(HttpStatusCode.OK, searchResponse);
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

        result.Total.Should().Be(2);
        result.Issues.Should().HaveCount(2);
        // 1 for /field discovery + 1 for search (no extra pagination calls)
        handler.SendCount.Should().Be(2);
    }

    [Fact]
    public async Task SearchIssuesAsync_MultiplePages_ShouldFetchAllIssues()
    {
        // Simulate total=3, first page returns 2, second page returns 1
        var fieldDiscoveryResponse = "[]"; // empty field list
        var page1Response = """
                {
                    "total": 3,
                    "issues": [
                        {
                            "id": "10001",
                            "key": "UDV-1",
                            "fields": {
                                "summary": "Issue one",
                                "status": { "name": "To Do" },
                                "issuetype": { "name": "Task" },
                                "priority": { "name": "Medium" },
                                "assignee": null,
                                "project": { "key": "UDV" }
                            }
                        },
                        {
                            "id": "10002",
                            "key": "UDV-2",
                            "fields": {
                                "summary": "Issue two",
                                "status": { "name": "Done" },
                                "issuetype": { "name": "Bug" },
                                "priority": { "name": "High" },
                                "assignee": null,
                                "project": { "key": "UDV" }
                            }
                        }
                    ]
                }
                """;
        var page2Response = """
                {
                    "total": 3,
                    "issues": [
                        {
                            "id": "10003",
                            "key": "UDV-3",
                            "fields": {
                                "summary": "Issue three",
                                "status": { "name": "In Progress" },
                                "issuetype": { "name": "Story" },
                                "priority": { "name": "Low" },
                                "assignee": null,
                                "project": { "key": "UDV" }
                            }
                        }
                    ]
                }
                """;

        var handler = new SequentialHttpMessageHandler(
            HttpStatusCode.OK,
            fieldDiscoveryResponse, page1Response, page2Response);
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

        var result = await service.SearchIssuesAsync("project = UDV ORDER BY created ASC", maxResults: 2);

        result.Total.Should().Be(3);
        result.Issues.Should().HaveCount(3);
        result.Issues.Select(i => i.Key).Should().BeEquivalentTo(new[] { "UDV-1", "UDV-2", "UDV-3" });
        // 1 for /field discovery + 2 for search pages
        handler.SendCount.Should().Be(3);

        // Verify the second search request included startAt=2
        var searchRequests = handler.AllRequestBodies
            .Where(b => b != null && b.Contains("\"jql\""))
            .ToList();
        searchRequests.Should().HaveCount(2);
        searchRequests[0].Should().Contain("\"startAt\":0");
        searchRequests[1].Should().Contain("\"startAt\":2");
    }

    [Fact]
    public async Task SearchIssuesAsync_MultiplePages_Server_ShouldFetchAllIssues()
    {
        // Simulate Server deployment with total=3, pages of 2
        var fieldDiscoveryResponse = "[]";
        var page1Response = """
                {
                    "total": 3,
                    "issues": [
                        {
                            "id": "10001",
                            "key": "SRV-1",
                            "fields": {
                                "summary": "Server issue one",
                                "status": { "name": "Open" },
                                "issuetype": { "name": "Task" },
                                "priority": { "name": "Medium" },
                                "assignee": null,
                                "project": { "key": "SRV" }
                            }
                        },
                        {
                            "id": "10002",
                            "key": "SRV-2",
                            "fields": {
                                "summary": "Server issue two",
                                "status": { "name": "Closed" },
                                "issuetype": { "name": "Bug" },
                                "priority": { "name": "High" },
                                "assignee": null,
                                "project": { "key": "SRV" }
                            }
                        }
                    ]
                }
                """;
        var page2Response = """
                {
                    "total": 3,
                    "issues": [
                        {
                            "id": "10003",
                            "key": "SRV-3",
                            "fields": {
                                "summary": "Server issue three",
                                "status": { "name": "In Progress" },
                                "issuetype": { "name": "Story" },
                                "priority": { "name": "Low" },
                                "assignee": null,
                                "project": { "key": "SRV" }
                            }
                        }
                    ]
                }
                """;

        var handler = new SequentialHttpMessageHandler(
            HttpStatusCode.OK,
            fieldDiscoveryResponse, page1Response, page2Response);
        var httpClient = new HttpClient(handler);

        var configValues = new Dictionary<string, string>
        {
            ["jira:base_url"] = "https://jira.example.com",
            ["jira:auth_method"] = JiraAuthMethod.ApiToken.ToString(),
            ["jira:deployment_type"] = JiraDeploymentType.Server.ToString(),
            ["jira:email"] = "user@example.com",
            ["jira:api_token"] = "token"
        };

        var config = new Mock<IConfigurationService>();
        config.Setup(x => x.GetAsync(It.IsAny<string>()))
                .ReturnsAsync((string key) => configValues.TryGetValue(key, out var value) ? value : null);

        var logger = new Mock<ILogger<JiraService>>();
        var service = new JiraService(httpClient, config.Object, logger.Object);

        var result = await service.SearchIssuesAsync("project = SRV ORDER BY created ASC", maxResults: 2);

        result.Total.Should().Be(3);
        result.Issues.Should().HaveCount(3);
        result.Issues.Select(i => i.Key).Should().BeEquivalentTo(new[] { "SRV-1", "SRV-2", "SRV-3" });
        handler.SendCount.Should().Be(3); // 1 field discovery + 2 search pages

        // Verify startAt was sent in both search requests
        var searchRequests = handler.AllRequestBodies
            .Where(b => b != null && b.Contains("\"jql\""))
            .ToList();
        searchRequests.Should().HaveCount(2);
        searchRequests[0].Should().Contain("\"startAt\":0");
        searchRequests[1].Should().Contain("\"startAt\":2");
    }

    [Fact]
    public async Task SearchIssuesAsync_EmptyResult_ShouldReturnEmptyList()
    {
        var searchResponse = """
                {
                    "total": 0,
                    "issues": []
                }
                """;

        var handler = new SequentialHttpMessageHandler(HttpStatusCode.OK, searchResponse);
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

        result.Total.Should().Be(0);
        result.Issues.Should().BeEmpty();
        // 1 for /field discovery + 1 for search (no extra pagination)
        handler.SendCount.Should().Be(2);
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

    /// <summary>
    /// HTTP message handler that returns different responses for sequential requests.
    /// The first N responses come from the provided list; after that the last response is repeated.
    /// Also tracks all request bodies for verification.
    /// </summary>
    private sealed class SequentialHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly List<string> _responses;

        public int SendCount { get; private set; }
        public List<string?> AllRequestBodies { get; } = new();
        public string? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }

        public SequentialHttpMessageHandler(HttpStatusCode statusCode, params string[] responses)
        {
            _statusCode = statusCode;
            _responses = responses.ToList();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = SendCount;
            SendCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            AllRequestBodies.Add(LastRequestBody);

            // Use the response at the current index, or the last one if we've exhausted the list
            var responseIndex = index < _responses.Count ? index : _responses.Count - 1;
            var body = _responses[responseIndex];

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(body)
            };

            return response;
        }
    }
}