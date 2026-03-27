using FluentAssertions;
using PKS.Infrastructure.Services.Models;
using PKS.CLI.Tests.Infrastructure;
using PKS.Commands.Jira;
using Xunit;

namespace PKS.CLI.Tests.Commands;

[Trait(TestTraits.Category, TestCategories.Unit)]
public class JiraBrowseExportTests
{
    [Fact]
    public void GetStaleIssues_ReturnsAll_WhenCacheIsEmpty()
    {
        // Arrange
        var issues = new List<JiraIssue>
        {
            CreateIssue("TEST-1", new DateTime(2026, 1, 10)),
            CreateIssue("TEST-2", new DateTime(2026, 1, 15)),
            CreateIssue("TEST-3", new DateTime(2026, 1, 20)),
        };
        var cache = new Dictionary<string, DateTime?>();

        // Act
        var stale = JiraBrowseCommand.GetStaleIssues(issues, cache);

        // Assert
        stale.Should().HaveCount(3);
        stale.Select(i => i.Key).Should().BeEquivalentTo("TEST-1", "TEST-2", "TEST-3");
    }

    [Fact]
    public void GetStaleIssues_ReturnsOnlyUpdated_WhenCacheHasOlderTimestamps()
    {
        // Arrange — cache has A at Jan 10, B at Jan 15
        var cache = new Dictionary<string, DateTime?>
        {
            ["TEST-A"] = new DateTime(2026, 1, 10),
            ["TEST-B"] = new DateTime(2026, 1, 15),
        };

        // Issues: A unchanged (Jan 10), B updated (Jan 20)
        var issues = new List<JiraIssue>
        {
            CreateIssue("TEST-A", new DateTime(2026, 1, 10)),
            CreateIssue("TEST-B", new DateTime(2026, 1, 20)),
        };

        // Act
        var stale = JiraBrowseCommand.GetStaleIssues(issues, cache);

        // Assert — only B is stale because its timestamp is newer than the cache
        stale.Should().HaveCount(1);
        stale[0].Key.Should().Be("TEST-B");
    }

    [Fact]
    public void GetStaleIssues_ReturnsNewIssues_NotInCache()
    {
        // Arrange — cache only knows about A
        var cache = new Dictionary<string, DateTime?>
        {
            ["TEST-A"] = new DateTime(2026, 1, 10),
        };

        // Issues: A unchanged, B is new (not in cache)
        var issues = new List<JiraIssue>
        {
            CreateIssue("TEST-A", new DateTime(2026, 1, 10)),
            CreateIssue("TEST-B", new DateTime(2026, 1, 20)),
        };

        // Act
        var stale = JiraBrowseCommand.GetStaleIssues(issues, cache);

        // Assert — only B returned because it is not in the cache
        stale.Should().HaveCount(1);
        stale[0].Key.Should().Be("TEST-B");
    }

    [Fact]
    public void GetStaleIssues_ReturnsEmpty_WhenAllUpToDate()
    {
        // Arrange — cache timestamps match issue timestamps exactly
        var cache = new Dictionary<string, DateTime?>
        {
            ["TEST-A"] = new DateTime(2026, 1, 10),
            ["TEST-B"] = new DateTime(2026, 1, 15),
            ["TEST-C"] = new DateTime(2026, 1, 20),
        };

        var issues = new List<JiraIssue>
        {
            CreateIssue("TEST-A", new DateTime(2026, 1, 10)),
            CreateIssue("TEST-B", new DateTime(2026, 1, 15)),
            CreateIssue("TEST-C", new DateTime(2026, 1, 20)),
        };

        // Act
        var stale = JiraBrowseCommand.GetStaleIssues(issues, cache);

        // Assert
        stale.Should().BeEmpty();
    }

    // --- Helpers ---

    private static JiraIssue CreateIssue(string key, DateTime? updated) => new()
    {
        Key = key,
        Updated = updated,
        Summary = $"Summary for {key}",
        Status = "To Do",
        IssueType = "Story",
        Priority = "Medium",
        ProjectKey = key.Split('-')[0],
    };
}
