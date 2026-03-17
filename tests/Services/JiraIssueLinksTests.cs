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
public class JiraIssueLinksTests
{
    private const string IssueWithLinksJson = """
        {
          "id": "12345",
          "key": "UDV-3496",
          "fields": {
            "summary": "Test issue with links",
            "status": { "name": "In Progress" },
            "issuetype": { "name": "Story" },
            "priority": { "name": "High" },
            "assignee": null,
            "project": { "key": "UDV" },
            "created": "2026-01-10T09:00:00.000+0000",
            "updated": "2026-01-15T10:00:00.000+0000",
            "issuelinks": [
              {
                "id": "10001",
                "type": { "name": "Relates", "inward": "relates to", "outward": "relates to" },
                "outwardIssue": {
                  "key": "UDV-3829",
                  "fields": {
                    "summary": "Related issue",
                    "status": { "name": "Done" },
                    "issuetype": { "name": "Task" }
                  }
                }
              },
              {
                "id": "10002",
                "type": { "name": "Blocks", "inward": "is blocked by", "outward": "blocks" },
                "inwardIssue": {
                  "key": "UDV-1234",
                  "fields": {
                    "summary": "Blocking issue",
                    "status": { "name": "To Do" },
                    "issuetype": { "name": "Bug" }
                  }
                }
              }
            ]
          }
        }
        """;

    private const string IssueWithoutLinksJson = """
        {
          "id": "12346",
          "key": "UDV-3497",
          "fields": {
            "summary": "Test issue without links",
            "status": { "name": "To Do" },
            "issuetype": { "name": "Task" },
            "priority": { "name": "Medium" },
            "assignee": null,
            "project": { "key": "UDV" },
            "created": "2026-01-10T09:00:00.000+0000",
            "updated": "2026-01-15T10:00:00.000+0000"
          }
        }
        """;

    private const string EmptyFieldsJson = "[]";

    [Fact]
    public async Task GetIssueAsync_ParsesIssueLinks_WithOutwardAndInward()
    {
        // Arrange
        var handler = new RoutingMockHttpHandler(new Dictionary<string, string>
        {
            { "/field", EmptyFieldsJson },
            { "/issue/UDV-3496", IssueWithLinksJson }
        });
        var httpClient = new HttpClient(handler);
        var configService = CreateAuthenticatedConfigService();
        var logger = new Mock<ILogger<JiraService>>();

        var service = new JiraService(httpClient, configService, logger.Object);

        // Act
        var issue = await service.GetIssueAsync("UDV-3496");

        // Assert
        issue.Should().NotBeNull();
        issue!.Key.Should().Be("UDV-3496");
        issue.IssueLinks.Should().HaveCount(2);

        // Outward link: relates to UDV-3829
        var outwardLink = issue.IssueLinks.First(l => l.LinkedIssueKey == "UDV-3829");
        outwardLink.Id.Should().Be("10001");
        outwardLink.LinkType.Should().Be("Relates");
        outwardLink.Direction.Should().Be("outward");
        outwardLink.DirectionLabel.Should().Be("relates to");
        outwardLink.LinkedIssueSummary.Should().Be("Related issue");
        outwardLink.LinkedIssueStatus.Should().Be("Done");
        outwardLink.LinkedIssueType.Should().Be("Task");

        // Inward link: is blocked by UDV-1234
        var inwardLink = issue.IssueLinks.First(l => l.LinkedIssueKey == "UDV-1234");
        inwardLink.Id.Should().Be("10002");
        inwardLink.LinkType.Should().Be("Blocks");
        inwardLink.Direction.Should().Be("inward");
        inwardLink.DirectionLabel.Should().Be("is blocked by");
        inwardLink.LinkedIssueSummary.Should().Be("Blocking issue");
        inwardLink.LinkedIssueStatus.Should().Be("To Do");
        inwardLink.LinkedIssueType.Should().Be("Bug");
    }

    [Fact]
    public async Task GetIssueAsync_ReturnsEmptyLinks_WhenNoIssueLinks()
    {
        // Arrange
        var handler = new RoutingMockHttpHandler(new Dictionary<string, string>
        {
            { "/field", EmptyFieldsJson },
            { "/issue/UDV-3497", IssueWithoutLinksJson }
        });
        var httpClient = new HttpClient(handler);
        var configService = CreateAuthenticatedConfigService();
        var logger = new Mock<ILogger<JiraService>>();

        var service = new JiraService(httpClient, configService, logger.Object);

        // Act
        var issue = await service.GetIssueAsync("UDV-3497");

        // Assert
        issue.Should().NotBeNull();
        issue!.Key.Should().Be("UDV-3497");
        issue.IssueLinks.Should().BeEmpty();
    }

    [Fact]
    public void GenerateMarkdown_IncludesLinksSection_WhenIssueLinksPresent()
    {
        var issue = new JiraIssue
        {
            Key = "UDV-3496",
            Summary = "Test issue with links",
            Status = "In Progress",
            IssueType = "Story",
            Priority = "High",
            ProjectKey = "UDV",
            Created = new DateTime(2026, 1, 10, 9, 0, 0),
            IssueLinks = new List<JiraIssueLink>
            {
                new()
                {
                    Id = "10001",
                    LinkType = "Relates",
                    Direction = "outward",
                    DirectionLabel = "relates to",
                    LinkedIssueKey = "UDV-3829",
                    LinkedIssueSummary = "Related issue",
                    LinkedIssueStatus = "Done",
                    LinkedIssueType = "Task"
                },
                new()
                {
                    Id = "10002",
                    LinkType = "Blocks",
                    Direction = "inward",
                    DirectionLabel = "is blocked by",
                    LinkedIssueKey = "UDV-1234",
                    LinkedIssueSummary = "Blocking issue",
                    LinkedIssueStatus = "To Do",
                    LinkedIssueType = "Bug"
                }
            }
        };

        var markdown = PKS.Commands.Jira.JiraBrowseCommand.GenerateMarkdown(issue);

        // Should contain Links section
        markdown.Should().Contain("## Links");

        // Should contain linked issue keys and relationship labels
        markdown.Should().Contain("UDV-3829");
        markdown.Should().Contain("relates to");
        markdown.Should().Contain("UDV-1234");
        markdown.Should().Contain("is blocked by");
    }

    [Fact]
    public void GenerateMarkdown_OmitsLinksSection_WhenNoIssueLinks()
    {
        var issue = new JiraIssue
        {
            Key = "UDV-3497",
            Summary = "No links",
            Status = "To Do",
            IssueType = "Task",
            Priority = "Low",
            ProjectKey = "UDV"
        };

        var markdown = PKS.Commands.Jira.JiraBrowseCommand.GenerateMarkdown(issue);

        markdown.Should().NotContain("## Links");
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
