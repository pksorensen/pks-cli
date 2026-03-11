using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.CLI.Tests.Infrastructure;
using Xunit;

namespace PKS.CLI.Tests.Services;

[Trait(TestTraits.Category, TestCategories.Unit)]
public class JiraChangelogTests
{
    [Fact]
    public async Task GetChangelogAsync_ParsesStatusAndAssigneeChanges()
    {
        // Arrange — mock HTTP response with Jira changelog JSON
        var changelogJson = JsonSerializer.Serialize(new
        {
            changelog = new
            {
                histories = new[]
                {
                    new
                    {
                        id = "101",
                        author = new { displayName = "Alice" },
                        created = "2026-01-15T10:00:00.000+0000",
                        items = new[]
                        {
                            new { field = "status", fromString = "To Do", toString = "In Progress" }
                        }
                    },
                    new
                    {
                        id = "102",
                        author = new { displayName = "Bob" },
                        created = "2026-01-20T14:30:00.000+0000",
                        items = new[]
                        {
                            new { field = "assignee", fromString = "Alice", toString = "Bob" },
                            new { field = "status", fromString = "In Progress", toString = "Done" }
                        }
                    }
                }
            }
        });

        var handler = new MockHttpHandler(changelogJson);
        var httpClient = new HttpClient(handler);
        var configService = CreateAuthenticatedConfigService();
        var logger = new Mock<ILogger<JiraService>>();

        var service = new JiraService(httpClient, configService, logger.Object);

        // Act
        var changelog = await service.GetChangelogAsync("TEST-1");

        // Assert
        changelog.Should().HaveCount(2);

        changelog[0].Id.Should().Be("101");
        changelog[0].Author.Should().Be("Alice");
        changelog[0].Items.Should().HaveCount(1);
        changelog[0].Items[0].Field.Should().Be("status");
        changelog[0].Items[0].FromString.Should().Be("To Do");
        changelog[0].Items[0].ToStringValue.Should().Be("In Progress");

        changelog[1].Id.Should().Be("102");
        changelog[1].Author.Should().Be("Bob");
        changelog[1].Items.Should().HaveCount(2);
        changelog[1].Items[0].Field.Should().Be("assignee");
        changelog[1].Items[1].Field.Should().Be("status");
        changelog[1].Items[1].ToStringValue.Should().Be("Done");
    }

    [Fact]
    public async Task GetChangelogAsync_ReturnsEmptyList_WhenNoChangelog()
    {
        var json = JsonSerializer.Serialize(new { changelog = new { histories = Array.Empty<object>() } });
        var handler = new MockHttpHandler(json);
        var httpClient = new HttpClient(handler);
        var configService = CreateAuthenticatedConfigService();
        var logger = new Mock<ILogger<JiraService>>();

        var service = new JiraService(httpClient, configService, logger.Object);

        var changelog = await service.GetChangelogAsync("TEST-2");

        changelog.Should().BeEmpty();
    }

    [Fact]
    public void GenerateMarkdown_IncludesHistorySection_WhenChangelogPresent()
    {
        var issue = new JiraIssue
        {
            Key = "TEST-1",
            Summary = "Test issue",
            Status = "Done",
            IssueType = "Story",
            Priority = "Medium",
            ProjectKey = "TEST",
            Created = new DateTime(2026, 1, 10, 9, 0, 0),
            Changelog = new List<JiraChangelogEntry>
            {
                new()
                {
                    Id = "1",
                    Author = "Alice",
                    Created = new DateTime(2026, 1, 10, 9, 0, 0),
                    Items = new List<JiraChangelogItem>
                    {
                        new() { Field = "status", FromString = "To Do", ToStringValue = "In Progress" }
                    }
                },
                new()
                {
                    Id = "2",
                    Author = "Bob",
                    Created = new DateTime(2026, 1, 15, 14, 0, 0),
                    Items = new List<JiraChangelogItem>
                    {
                        new() { Field = "assignee", FromString = "Alice", ToStringValue = "Bob" },
                        new() { Field = "status", FromString = "In Progress", ToStringValue = "Done" }
                    }
                }
            }
        };

        var markdown = PKS.Commands.Jira.JiraBrowseCommand.GenerateMarkdown(issue);

        // Should contain History section
        markdown.Should().Contain("## History");
        markdown.Should().Contain("| Date | Author | Field | From | To |");

        // Should contain specific transition data
        markdown.Should().Contain("status");
        markdown.Should().Contain("To Do");
        markdown.Should().Contain("In Progress");
        markdown.Should().Contain("Done");
        markdown.Should().Contain("Alice");
        markdown.Should().Contain("Bob");
        markdown.Should().Contain("assignee");
    }

    [Fact]
    public void GenerateMarkdown_OmitsHistorySection_WhenNoChangelog()
    {
        var issue = new JiraIssue
        {
            Key = "TEST-2",
            Summary = "No history",
            Status = "To Do",
            IssueType = "Task",
            Priority = "Low",
            ProjectKey = "TEST"
        };

        var markdown = PKS.Commands.Jira.JiraBrowseCommand.GenerateMarkdown(issue);

        markdown.Should().NotContain("## History");
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

    private class MockHttpHandler : HttpMessageHandler
    {
        private readonly string _response;

        public MockHttpHandler(string response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            });
        }
    }
}
