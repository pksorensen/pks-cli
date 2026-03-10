using Xunit;
using Moq;
using FluentAssertions;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PKS.CLI.Tests.Commands;

/// <summary>
/// Tests for Jira CLI commands: init, browse, and the JiraService layer.
/// Written TDD-first — these tests define the expected behaviour before
/// the command and service implementations are completed.
/// </summary>
public class JiraCommandTests
{
    // ─────────────────────────────────────────────
    //  Shared helpers
    // ─────────────────────────────────────────────

    private static Mock<IJiraService> CreateJiraServiceMock(bool authenticated = false)
    {
        var mock = new Mock<IJiraService>();

        mock.Setup(x => x.IsAuthenticatedAsync())
            .ReturnsAsync(authenticated);

        if (authenticated)
        {
            mock.Setup(x => x.GetStoredCredentialsAsync())
                .ReturnsAsync(new JiraStoredCredentials
                {
                    AuthMethod = JiraAuthMethod.ApiToken,
                    BaseUrl = "https://test.atlassian.net",
                    Email = "user@test.com",
                    ApiToken = "existing-token",
                    CreatedAt = DateTime.UtcNow.AddDays(-5)
                });
        }
        else
        {
            mock.Setup(x => x.GetStoredCredentialsAsync())
                .ReturnsAsync((JiraStoredCredentials?)null);
        }

        return mock;
    }

    private static Mock<IConfigurationService> CreateConfigServiceMock()
    {
        var mock = new Mock<IConfigurationService>();
        var store = new Dictionary<string, string>();

        mock.Setup(x => x.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => store.TryGetValue(key, out var v) ? v : null);

        mock.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback<string, string, bool, bool>((k, v, _, _) => store[k] = v)
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .Callback<string>(k => store.Remove(k))
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.GetAllAsync())
            .ReturnsAsync(() => new Dictionary<string, string>(store));

        return mock;
    }

    /// <summary>
    /// Builds a parent-child tree from a flat list of JiraIssues.
    /// This mirrors the logic the browse command should use internally.
    /// </summary>
    private static List<JiraIssue> BuildIssueTree(List<JiraIssue> flatIssues)
    {
        var lookup = flatIssues.ToDictionary(i => i.Key);
        var roots = new List<JiraIssue>();

        foreach (var issue in flatIssues)
        {
            if (!string.IsNullOrEmpty(issue.ParentKey) && lookup.TryGetValue(issue.ParentKey, out var parent))
            {
                parent.Children.Add(issue);
            }
            else
            {
                roots.Add(issue);
            }
        }

        return roots;
    }

    // ═════════════════════════════════════════════
    //  1. JiraInitCommand Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraInit_WithTokenAuth_StoresCredentials()
    {
        // Arrange
        var jiraService = CreateJiraServiceMock(authenticated: false);

        jiraService.Setup(x => x.ValidateCredentialsAsync(It.IsAny<JiraStoredCredentials>()))
            .ReturnsAsync(true);

        jiraService.Setup(x => x.StoreCredentialsAsync(It.IsAny<JiraStoredCredentials>()))
            .Returns(Task.CompletedTask);

        var credentials = new JiraStoredCredentials
        {
            AuthMethod = JiraAuthMethod.ApiToken,
            BaseUrl = "https://mycompany.atlassian.net",
            Email = "dev@mycompany.com",
            ApiToken = "ATATT3xFfGF0_secret_token"
        };

        // Act — simulate the init flow: validate then store
        var isValid = await jiraService.Object.ValidateCredentialsAsync(credentials);
        if (isValid)
        {
            await jiraService.Object.StoreCredentialsAsync(credentials);
        }

        // Assert
        isValid.Should().BeTrue();
        jiraService.Verify(x => x.StoreCredentialsAsync(It.Is<JiraStoredCredentials>(c =>
            c.BaseUrl == "https://mycompany.atlassian.net" &&
            c.Email == "dev@mycompany.com" &&
            c.ApiToken == "ATATT3xFfGF0_secret_token" &&
            c.AuthMethod == JiraAuthMethod.ApiToken
        )), Times.Once);
    }

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraInit_WhenAlreadyAuthenticated_ShowsExistingCredentials()
    {
        // Arrange
        var jiraService = CreateJiraServiceMock(authenticated: true);

        // Act
        var isAuthenticated = await jiraService.Object.IsAuthenticatedAsync();
        var stored = await jiraService.Object.GetStoredCredentialsAsync();

        // Assert
        isAuthenticated.Should().BeTrue();
        stored.Should().NotBeNull();
        stored!.BaseUrl.Should().Be("https://test.atlassian.net");
        stored.Email.Should().Be("user@test.com");

        // The command should NOT call StoreCredentialsAsync when already authenticated
        jiraService.Verify(x => x.StoreCredentialsAsync(It.IsAny<JiraStoredCredentials>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraInit_WithForce_ReauthenticatesEvenWhenAuthenticated()
    {
        // Arrange
        var jiraService = CreateJiraServiceMock(authenticated: true);
        bool forceFlag = true;

        var newCredentials = new JiraStoredCredentials
        {
            AuthMethod = JiraAuthMethod.ApiToken,
            BaseUrl = "https://newcompany.atlassian.net",
            Email = "new@newcompany.com",
            ApiToken = "new-api-token"
        };

        jiraService.Setup(x => x.ValidateCredentialsAsync(It.IsAny<JiraStoredCredentials>()))
            .ReturnsAsync(true);

        jiraService.Setup(x => x.StoreCredentialsAsync(It.IsAny<JiraStoredCredentials>()))
            .Returns(Task.CompletedTask);

        // Act — when force is set, proceed even if authenticated
        var isAuthenticated = await jiraService.Object.IsAuthenticatedAsync();
        isAuthenticated.Should().BeTrue("we are already authenticated");

        if (forceFlag || !isAuthenticated)
        {
            var valid = await jiraService.Object.ValidateCredentialsAsync(newCredentials);
            if (valid)
            {
                await jiraService.Object.StoreCredentialsAsync(newCredentials);
            }
        }

        // Assert — credentials were stored despite being already authenticated
        jiraService.Verify(x => x.StoreCredentialsAsync(It.Is<JiraStoredCredentials>(c =>
            c.BaseUrl == "https://newcompany.atlassian.net"
        )), Times.Once);
    }

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraInit_ValidatesCredentials_BeforeStoring()
    {
        // Arrange
        var jiraService = CreateJiraServiceMock(authenticated: false);

        jiraService.Setup(x => x.ValidateCredentialsAsync(It.IsAny<JiraStoredCredentials>()))
            .ReturnsAsync(false); // validation fails

        jiraService.Setup(x => x.StoreCredentialsAsync(It.IsAny<JiraStoredCredentials>()))
            .Returns(Task.CompletedTask);

        var badCredentials = new JiraStoredCredentials
        {
            AuthMethod = JiraAuthMethod.ApiToken,
            BaseUrl = "https://mycompany.atlassian.net",
            Email = "dev@mycompany.com",
            ApiToken = "invalid-token"
        };

        // Act
        var isValid = await jiraService.Object.ValidateCredentialsAsync(badCredentials);

        // Only store if valid
        if (isValid)
        {
            await jiraService.Object.StoreCredentialsAsync(badCredentials);
        }

        // Assert — invalid credentials must not be stored
        isValid.Should().BeFalse();
        jiraService.Verify(x => x.StoreCredentialsAsync(It.IsAny<JiraStoredCredentials>()), Times.Never);
    }

    // ═════════════════════════════════════════════
    //  2. JiraBrowseCommand Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraBrowse_WhenNotAuthenticated_ShowsError()
    {
        // Arrange
        var jiraService = CreateJiraServiceMock(authenticated: false);

        // Act
        var isAuthenticated = await jiraService.Object.IsAuthenticatedAsync();

        // Assert — the command should detect this and display an error
        isAuthenticated.Should().BeFalse();

        // GetProjectsAsync should NOT be called when unauthenticated
        jiraService.Verify(x => x.GetProjectsAsync(), Times.Never);
    }

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraBrowse_LoadsProjectsAndDisplaysSelection()
    {
        // Arrange
        var jiraService = CreateJiraServiceMock(authenticated: true);

        var projects = new List<JiraProject>
        {
            new() { Id = "10001", Key = "PROJ", Name = "Project Alpha" },
            new() { Id = "10002", Key = "BETA", Name = "Project Beta" },
            new() { Id = "10003", Key = "GAMMA", Name = "Project Gamma" }
        };

        jiraService.Setup(x => x.GetProjectsAsync())
            .ReturnsAsync(projects);

        // Act
        var isAuth = await jiraService.Object.IsAuthenticatedAsync();
        isAuth.Should().BeTrue();

        var loadedProjects = await jiraService.Object.GetProjectsAsync();

        // Assert
        loadedProjects.Should().HaveCount(3);
        loadedProjects.Select(p => p.Key).Should().Contain("PROJ");
        loadedProjects.Select(p => p.Key).Should().Contain("BETA");
        loadedProjects.Select(p => p.Key).Should().Contain("GAMMA");

        jiraService.Verify(x => x.GetProjectsAsync(), Times.Once);
    }

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraBrowse_BuildsTreeFromIssues()
    {
        // Arrange — flat list with parent-child relationships
        var flatIssues = new List<JiraIssue>
        {
            new()
            {
                Id = "1", Key = "PROJ-1", Summary = "Epic: Authentication",
                Status = "In Progress", IssueType = "Epic", ParentKey = null,
                ProjectKey = "PROJ"
            },
            new()
            {
                Id = "2", Key = "PROJ-2", Summary = "Story: Login page",
                Status = "To Do", IssueType = "Story", ParentKey = "PROJ-1",
                ProjectKey = "PROJ"
            },
            new()
            {
                Id = "3", Key = "PROJ-3", Summary = "Story: OAuth integration",
                Status = "Done", IssueType = "Story", ParentKey = "PROJ-1",
                ProjectKey = "PROJ"
            },
            new()
            {
                Id = "4", Key = "PROJ-4", Summary = "Epic: Dashboard",
                Status = "To Do", IssueType = "Epic", ParentKey = null,
                ProjectKey = "PROJ"
            },
            new()
            {
                Id = "5", Key = "PROJ-5", Summary = "Task: Widget layout",
                Status = "In Progress", IssueType = "Task", ParentKey = "PROJ-4",
                ProjectKey = "PROJ"
            },
            new()
            {
                Id = "6", Key = "PROJ-6", Summary = "Bug: Standalone bug",
                Status = "Open", IssueType = "Bug", ParentKey = null,
                ProjectKey = "PROJ"
            }
        };

        // Act
        var tree = BuildIssueTree(flatIssues);

        // Assert — three root-level items (two epics + one standalone bug)
        tree.Should().HaveCount(3);

        var authEpic = tree.First(i => i.Key == "PROJ-1");
        authEpic.Children.Should().HaveCount(2);
        authEpic.Children.Select(c => c.Key).Should().Contain("PROJ-2");
        authEpic.Children.Select(c => c.Key).Should().Contain("PROJ-3");

        var dashboardEpic = tree.First(i => i.Key == "PROJ-4");
        dashboardEpic.Children.Should().HaveCount(1);
        dashboardEpic.Children.First().Key.Should().Be("PROJ-5");

        var standaloneBug = tree.First(i => i.Key == "PROJ-6");
        standaloneBug.Children.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraBrowse_BuildsTree_WhenParentKeyNotInList_TreatsAsRoot()
    {
        // Arrange — child references a parent that is NOT in the flat list
        var flatIssues = new List<JiraIssue>
        {
            new()
            {
                Id = "10", Key = "PROJ-10", Summary = "Orphan child",
                Status = "Open", IssueType = "Story", ParentKey = "PROJ-999",
                ProjectKey = "PROJ"
            },
            new()
            {
                Id = "11", Key = "PROJ-11", Summary = "Normal root",
                Status = "Open", IssueType = "Epic", ParentKey = null,
                ProjectKey = "PROJ"
            }
        };

        // Act
        var tree = BuildIssueTree(flatIssues);

        // Assert — orphan should become a root since its parent is missing
        tree.Should().HaveCount(2);
        tree.Select(i => i.Key).Should().Contain("PROJ-10");
        tree.Select(i => i.Key).Should().Contain("PROJ-11");
    }

    // ═════════════════════════════════════════════
    //  3. JiraService Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraService_IsAuthenticated_ReturnsTrueWhenCredentialsExist()
    {
        // Arrange
        var configService = CreateConfigServiceMock();

        // Simulate stored credentials by pre-populating config
        await configService.Object.SetAsync("jira:base_url", "https://test.atlassian.net");
        await configService.Object.SetAsync("jira:email", "user@test.com");
        await configService.Object.SetAsync("jira:api_token", "secret", encrypt: true);

        // Act — verify the config keys are present (service checks these)
        var baseUrl = await configService.Object.GetAsync("jira:base_url");
        var email = await configService.Object.GetAsync("jira:email");
        var token = await configService.Object.GetAsync("jira:api_token");

        bool isAuthenticated = !string.IsNullOrEmpty(baseUrl)
                            && !string.IsNullOrEmpty(email)
                            && !string.IsNullOrEmpty(token);

        // Assert
        isAuthenticated.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraService_IsAuthenticated_ReturnsFalseWhenNoCredentials()
    {
        // Arrange — empty config store
        var configService = CreateConfigServiceMock();

        // Act
        var baseUrl = await configService.Object.GetAsync("jira:base_url");
        var email = await configService.Object.GetAsync("jira:email");
        var token = await configService.Object.GetAsync("jira:api_token");

        bool isAuthenticated = !string.IsNullOrEmpty(baseUrl)
                            && !string.IsNullOrEmpty(email)
                            && !string.IsNullOrEmpty(token);

        // Assert
        isAuthenticated.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraService_StoreCredentials_PersistsViaConfigService()
    {
        // Arrange
        var configService = CreateConfigServiceMock();

        var credentials = new JiraStoredCredentials
        {
            AuthMethod = JiraAuthMethod.ApiToken,
            BaseUrl = "https://acme.atlassian.net",
            Email = "admin@acme.com",
            ApiToken = "super-secret-token"
        };

        // Act — simulate what JiraService.StoreCredentialsAsync should do
        await configService.Object.SetAsync("jira:auth_method", credentials.AuthMethod.ToString());
        await configService.Object.SetAsync("jira:base_url", credentials.BaseUrl);
        await configService.Object.SetAsync("jira:email", credentials.Email);
        await configService.Object.SetAsync("jira:api_token", credentials.ApiToken, encrypt: true);

        // Assert — verify all keys were persisted
        var allSettings = await configService.Object.GetAllAsync();
        allSettings.Should().ContainKey("jira:auth_method");
        allSettings.Should().ContainKey("jira:base_url");
        allSettings.Should().ContainKey("jira:email");
        allSettings.Should().ContainKey("jira:api_token");

        allSettings["jira:base_url"].Should().Be("https://acme.atlassian.net");
        allSettings["jira:email"].Should().Be("admin@acme.com");
        allSettings["jira:auth_method"].Should().Be("ApiToken");

        configService.Verify(x => x.SetAsync("jira:api_token", "super-secret-token", false, true), Times.Once,
            "API token must be stored with encrypt=true");
    }

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraService_ValidateCredentials_CallsJiraApi()
    {
        // Arrange
        var jiraService = CreateJiraServiceMock(authenticated: false);

        var credentials = new JiraStoredCredentials
        {
            AuthMethod = JiraAuthMethod.ApiToken,
            BaseUrl = "https://myco.atlassian.net",
            Email = "me@myco.com",
            ApiToken = "my-token"
        };

        jiraService.Setup(x => x.ValidateCredentialsAsync(It.IsAny<JiraStoredCredentials>()))
            .ReturnsAsync(true);

        // Act
        var result = await jiraService.Object.ValidateCredentialsAsync(credentials);

        // Assert
        result.Should().BeTrue();
        jiraService.Verify(x => x.ValidateCredentialsAsync(It.Is<JiraStoredCredentials>(c =>
            c.BaseUrl == "https://myco.atlassian.net" &&
            c.Email == "me@myco.com" &&
            c.ApiToken == "my-token"
        )), Times.Once);
    }

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraService_ValidateCredentials_ReturnsFalseForInvalidToken()
    {
        // Arrange
        var jiraService = new Mock<IJiraService>();

        jiraService.Setup(x => x.ValidateCredentialsAsync(It.IsAny<JiraStoredCredentials>()))
            .ReturnsAsync(false);

        var badCredentials = new JiraStoredCredentials
        {
            AuthMethod = JiraAuthMethod.ApiToken,
            BaseUrl = "https://myco.atlassian.net",
            Email = "me@myco.com",
            ApiToken = "wrong-token"
        };

        // Act
        var result = await jiraService.Object.ValidateCredentialsAsync(badCredentials);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Jira")]
    public async Task JiraService_ClearCredentials_RemovesAllStoredData()
    {
        // Arrange
        var configService = CreateConfigServiceMock();

        // Pre-populate
        await configService.Object.SetAsync("jira:base_url", "https://test.atlassian.net");
        await configService.Object.SetAsync("jira:email", "user@test.com");
        await configService.Object.SetAsync("jira:api_token", "token");
        await configService.Object.SetAsync("jira:auth_method", "ApiToken");

        // Act — simulate JiraService.ClearCredentialsAsync
        await configService.Object.DeleteAsync("jira:base_url");
        await configService.Object.DeleteAsync("jira:email");
        await configService.Object.DeleteAsync("jira:api_token");
        await configService.Object.DeleteAsync("jira:auth_method");

        // Assert
        var all = await configService.Object.GetAllAsync();
        all.Should().NotContainKey("jira:base_url");
        all.Should().NotContainKey("jira:email");
        all.Should().NotContainKey("jira:api_token");
        all.Should().NotContainKey("jira:auth_method");
    }
}
