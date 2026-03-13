using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.CLI.Tests.Infrastructure;
using Xunit;

namespace PKS.CLI.Tests.Services;

[Trait(TestTraits.Category, TestCategories.Unit)]
public class JiraRawFieldsTests
{
    private const string AllFieldsIssueJson = """
        {
          "id": "12345",
          "key": "TEST-1",
          "fields": {
            "summary": "Test issue",
            "status": { "name": "In Progress" },
            "issuetype": { "name": "Story" },
            "priority": { "name": "High" },
            "assignee": { "displayName": "Alice" },
            "project": { "key": "TEST" },
            "description": "A test description",
            "labels": ["backend"],
            "components": [],
            "created": "2026-01-10T09:00:00.000+0000",
            "updated": "2026-01-15T10:00:00.000+0000",
            "customfield_10064": "AC: User can login",
            "customfield_99999": { "value": "Some custom value" },
            "customfield_10016": 5.0,
            "reporter": { "displayName": "Bob" },
            "resolution": null
          }
        }
        """;

    private const string RegularIssueJson = """
        {
          "id": "12345",
          "key": "TEST-1",
          "fields": {
            "summary": "Test issue",
            "status": { "name": "In Progress" },
            "issuetype": { "name": "Story" },
            "priority": { "name": "High" },
            "assignee": { "displayName": "Alice" },
            "project": { "key": "TEST" },
            "description": "A test description",
            "labels": ["backend"],
            "components": [],
            "created": "2026-01-10T09:00:00.000+0000",
            "updated": "2026-01-15T10:00:00.000+0000",
            "reporter": { "displayName": "Bob" },
            "resolution": null
          }
        }
        """;

    // Empty array response for the /field discovery endpoint
    private const string EmptyFieldsJson = "[]";

    [Fact]
    public async Task GetIssueWithAllFieldsAsync_IncludesRawFields()
    {
        // Arrange
        var handler = new RoutingMockHttpHandler(new Dictionary<string, string>
        {
            { "/field", EmptyFieldsJson },
            { "/issue/TEST-1", AllFieldsIssueJson }
        });
        var httpClient = new HttpClient(handler);
        var configService = CreateAuthenticatedConfigService();
        var logger = new Mock<ILogger<JiraService>>();

        var service = new JiraService(httpClient, configService, logger.Object);

        // Act
        var issue = await service.GetIssueWithAllFieldsAsync("TEST-1");

        // Assert
        issue.Should().NotBeNull();
        issue!.Key.Should().Be("TEST-1");
        issue.Summary.Should().Be("Test issue");
        issue.RawFields.Should().NotBeNull();
        issue.RawFields.Should().ContainKey("summary");
        issue.RawFields.Should().ContainKey("status");
        issue.RawFields.Should().ContainKey("customfield_10064");
        issue.RawFields.Should().ContainKey("customfield_99999");
        issue.RawFields.Should().ContainKey("customfield_10016");
        issue.RawFields.Should().ContainKey("description");
        issue.RawFields.Should().ContainKey("labels");
    }

    [Fact]
    public async Task GetIssueWithAllFieldsAsync_RawFieldsContainsCustomFieldValues()
    {
        // Arrange
        var handler = new RoutingMockHttpHandler(new Dictionary<string, string>
        {
            { "/field", EmptyFieldsJson },
            { "/issue/TEST-1", AllFieldsIssueJson }
        });
        var httpClient = new HttpClient(handler);
        var configService = CreateAuthenticatedConfigService();
        var logger = new Mock<ILogger<JiraService>>();

        var service = new JiraService(httpClient, configService, logger.Object);

        // Act
        var issue = await service.GetIssueWithAllFieldsAsync("TEST-1");

        // Assert
        issue.Should().NotBeNull();
        var rawFields = issue!.RawFields!;

        // String custom field
        rawFields["customfield_10064"].GetString().Should().Be("AC: User can login");

        // Object custom field
        rawFields["customfield_99999"].GetProperty("value").GetString().Should().Be("Some custom value");

        // Numeric custom field
        rawFields["customfield_10016"].GetDouble().Should().Be(5.0);
    }

    [Fact]
    public async Task GetIssueAsync_DoesNotIncludeRawFields()
    {
        // Arrange
        var handler = new RoutingMockHttpHandler(new Dictionary<string, string>
        {
            { "/field", EmptyFieldsJson },
            { "/issue/TEST-1", RegularIssueJson }
        });
        var httpClient = new HttpClient(handler);
        var configService = CreateAuthenticatedConfigService();
        var logger = new Mock<ILogger<JiraService>>();

        var service = new JiraService(httpClient, configService, logger.Object);

        // Act
        var issue = await service.GetIssueAsync("TEST-1");

        // Assert
        issue.Should().NotBeNull();
        issue!.Key.Should().Be("TEST-1");
        issue.RawFields.Should().BeNull();
    }

    [Fact]
    public async Task GetIssueWithAllFieldsAsync_ExtractsAcceptanceCriteria_FromStoredFieldId()
    {
        // Arrange: AC auto-discovery returns nothing, but config has a stored field ID
        var issueJson = """
            {
              "id": "12345",
              "key": "TEST-1",
              "fields": {
                "summary": "Test issue",
                "status": { "name": "In Progress" },
                "issuetype": { "name": "Story" },
                "priority": { "name": "High" },
                "assignee": { "displayName": "Alice" },
                "project": { "key": "TEST" },
                "description": "A test description",
                "labels": [],
                "components": [],
                "created": "2026-01-10T09:00:00.000+0000",
                "updated": "2026-01-15T10:00:00.000+0000",
                "customfield_10064": "AC: User can login successfully",
                "reporter": { "displayName": "Bob" },
                "resolution": null
              }
            }
            """;

        var handler = new RoutingMockHttpHandler(new Dictionary<string, string>
        {
            { "/field", "[]" },
            { "/issue/TEST-1", issueJson }
        });
        var httpClient = new HttpClient(handler);

        var configMock = new Mock<IConfigurationService>();
        configMock.Setup(c => c.GetAsync("jira:base_url")).ReturnsAsync("https://test.atlassian.net");
        configMock.Setup(c => c.GetAsync("jira:email")).ReturnsAsync("test@example.com");
        configMock.Setup(c => c.GetAsync("jira:api_token")).ReturnsAsync("test-token");
        configMock.Setup(c => c.GetAsync("jira:auth_method")).ReturnsAsync("ApiToken");
        configMock.Setup(c => c.GetAsync("jira:deployment_type")).ReturnsAsync("Cloud");
        configMock.Setup(c => c.GetAsync("jira:cloud_id")).ReturnsAsync("");
        configMock.Setup(c => c.GetAsync("jira:access_token")).ReturnsAsync("");
        configMock.Setup(c => c.GetAsync("jira:refresh_token")).ReturnsAsync("");
        configMock.Setup(c => c.GetAsync("jira:username")).ReturnsAsync("");
        configMock.Setup(c => c.GetAsync("jira:created_at")).ReturnsAsync(DateTime.UtcNow.ToString("O"));
        configMock.Setup(c => c.GetAsync("jira:last_refreshed_at")).ReturnsAsync(DateTime.UtcNow.ToString("O"));
        configMock.Setup(c => c.GetAsync("jira:ac_field_id")).ReturnsAsync("customfield_10064");

        var logger = new Mock<ILogger<JiraService>>();
        var service = new JiraService(httpClient, configMock.Object, logger.Object);

        // Act
        var issue = await service.GetIssueWithAllFieldsAsync("TEST-1");

        // Assert
        issue.Should().NotBeNull();
        issue!.Key.Should().Be("TEST-1");
        issue.AcceptanceCriteria.Should().Be("AC: User can login successfully");
    }

    // --- Helpers ---

    private static IConfigurationService CreateAuthenticatedConfigService()
    {
        var mock = new Mock<IConfigurationService>();
        mock.Setup(c => c.GetAsync("jira:base_url")).ReturnsAsync("https://test.atlassian.net");
        mock.Setup(c => c.GetAsync("jira:email")).ReturnsAsync("test@example.com");
        mock.Setup(c => c.GetAsync("jira:api_token")).ReturnsAsync("test-token");
        mock.Setup(c => c.GetAsync("jira:auth_method")).ReturnsAsync("ApiToken");
        mock.Setup(c => c.GetAsync("jira:deployment_type")).ReturnsAsync("Cloud");
        mock.Setup(c => c.GetAsync("jira:cloud_id")).ReturnsAsync("");
        mock.Setup(c => c.GetAsync("jira:access_token")).ReturnsAsync("");
        mock.Setup(c => c.GetAsync("jira:refresh_token")).ReturnsAsync("");
        mock.Setup(c => c.GetAsync("jira:username")).ReturnsAsync("");
        mock.Setup(c => c.GetAsync("jira:created_at")).ReturnsAsync(DateTime.UtcNow.ToString("O"));
        mock.Setup(c => c.GetAsync("jira:last_refreshed_at")).ReturnsAsync(DateTime.UtcNow.ToString("O"));
        return mock.Object;
    }

    /// <summary>
    /// HTTP handler that routes responses based on URL path segments.
    /// Matches if the request URI contains the registered path key.
    /// </summary>
    private class RoutingMockHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _routes;

        public RoutingMockHttpHandler(Dictionary<string, string> routes)
        {
            _routes = routes;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.PathAndQuery ?? string.Empty;

            foreach (var route in _routes)
            {
                if (uri.Contains(route.Key))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(route.Value, Encoding.UTF8, "application/json")
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
