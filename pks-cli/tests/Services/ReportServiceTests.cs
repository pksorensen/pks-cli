using NSubstitute;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services;

public class ReportServiceTests
{
    private readonly IGitHubService _mockGitHubService;
    private readonly ISystemInformationService _mockSystemInformationService;
    private readonly ITelemetryService _mockTelemetryService;
    private readonly IConfigurationService _mockConfigurationService;
    private readonly ReportService _reportService;

    public ReportServiceTests()
    {
        _mockGitHubService = Substitute.For<IGitHubService>();
        _mockSystemInformationService = Substitute.For<ISystemInformationService>();
        _mockTelemetryService = Substitute.For<ITelemetryService>();
        _mockConfigurationService = Substitute.For<IConfigurationService>();
        
        _reportService = new ReportService(
            _mockGitHubService,
            _mockSystemInformationService,
            _mockTelemetryService,
            _mockConfigurationService);
    }

    [Fact]
    public async Task CreateReportAsync_WithValidRequest_ShouldCreateGitHubIssue()
    {
        // Arrange
        var request = new CreateReportRequest
        {
            Message = "Test bug report",
            Title = "Bug: Test issue",
            IsBug = true,
            Repository = "owner/repo"
        };

        _mockConfigurationService.GetAsync("github.token").Returns("test-token");
        _mockGitHubService.ValidateTokenAsync("test-token").Returns(new GitHubTokenValidation
        {
            IsValid = true,
            Scopes = new[] { "repo" }
        });

        SetupMockServices();

        var expectedIssue = new GitHubIssue
        {
            Number = 123,
            Title = "Bug: Test issue",
            HtmlUrl = "https://github.com/owner/repo/issues/123",
            CreatedAt = DateTime.UtcNow
        };

        _mockGitHubService.CreateIssueAsync("owner", "repo", request.Title, Arg.Any<string>(), Arg.Any<string[]>())
            .Returns(expectedIssue);

        // Act
        var result = await _reportService.CreateReportAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(123, result.IssueNumber);
        Assert.Equal("https://github.com/owner/repo/issues/123", result.IssueUrl);
        Assert.Contains("pks-cli-report", result.Labels);
        Assert.Contains("bug", result.Labels);
    }

    [Fact]
    public async Task CreateReportAsync_WithoutGitHubToken_ShouldReturnError()
    {
        // Arrange
        var request = new CreateReportRequest
        {
            Message = "Test message",
            Title = "Test Title"
        };

        _mockConfigurationService.GetAsync("github.token").Returns((string?)null);

        // Act
        var result = await _reportService.CreateReportAsync(request);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("GitHub authentication is required", result.ErrorMessage);
    }

    [Fact]
    public async Task PreviewReportAsync_ShouldReturnPreviewWithoutCreatingIssue()
    {
        // Arrange
        var request = new CreateReportRequest
        {
            Message = "Test message",
            Title = "Test Title",
            IsFeatureRequest = true
        };

        SetupMockServices();

        // Act
        var result = await _reportService.PreviewReportAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Test Title", result.Title);
        Assert.Contains("## User Report", result.Content);
        Assert.Contains("Test message", result.Content);
        Assert.Contains("pks-cli-report", result.Labels);
        Assert.Contains("enhancement", result.Labels);
        Assert.Equal(0, result.IssueNumber); // Preview doesn't create actual issue

        // Verify no GitHub API calls were made
        await _mockGitHubService.DidNotReceive().CreateIssueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>());
    }

    [Fact]
    public async Task CanCreateReportsAsync_WithValidToken_ShouldReturnTrue()
    {
        // Arrange
        _mockConfigurationService.GetAsync("github.token").Returns("valid-token");
        _mockGitHubService.ValidateTokenAsync("valid-token").Returns(new GitHubTokenValidation
        {
            IsValid = true,
            Scopes = new[] { "repo", "user" }
        });

        // Act
        var result = await _reportService.CanCreateReportsAsync();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CanCreateReportsAsync_WithInvalidToken_ShouldReturnFalse()
    {
        // Arrange
        _mockConfigurationService.GetAsync("github.token").Returns("invalid-token");
        _mockGitHubService.ValidateTokenAsync("invalid-token").Returns(new GitHubTokenValidation
        {
            IsValid = false,
            Scopes = Array.Empty<string>()
        });

        // Act
        var result = await _reportService.CanCreateReportsAsync();

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(true, false, false, "bug")]
    [InlineData(false, true, false, "enhancement")]
    [InlineData(false, false, true, "question")]
    [InlineData(false, false, false, "feedback")]
    public async Task PreviewReportAsync_WithDifferentIssueTypes_ShouldSetCorrectLabels(
        bool isBug, bool isFeature, bool isQuestion, string expectedLabel)
    {
        // Arrange
        var request = new CreateReportRequest
        {
            Message = "Test message",
            Title = "Test Title",
            IsBug = isBug,
            IsFeatureRequest = isFeature,
            IsQuestion = isQuestion
        };

        SetupMockServices();

        // Act
        var result = await _reportService.PreviewReportAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("pks-cli-report", result.Labels);
        Assert.Contains(expectedLabel, result.Labels);
    }

    [Fact]
    public async Task PreviewReportAsync_WithAllDataIncluded_ShouldContainAllSections()
    {
        // Arrange
        var request = new CreateReportRequest
        {
            Message = "Test message",
            Title = "Test Title",
            IncludeVersion = true,
            IncludeEnvironment = true,
            IncludeTelemetry = true
        };

        SetupMockServices();

        // Act
        var result = await _reportService.PreviewReportAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("## User Report", result.Content);
        Assert.Contains("## Version Information", result.Content);
        Assert.Contains("## Environment Information", result.Content);
        Assert.Contains("## Usage Statistics", result.Content);
        Assert.Contains("Test message", result.Content);
    }

    [Fact]
    public async Task GetReportRepositoryAsync_ShouldReturnRepositoryInfo()
    {
        // Arrange
        var mockRepo = new GitHubRepository
        {
            Name = "pks-cli",
            FullName = "pksorensen/pks-cli",
            Owner = "pksorensen"
        };

        _mockGitHubService.GetRepositoryAsync("pksorensen", "pks-cli").Returns(mockRepo);
        _mockConfigurationService.GetAsync("github.token").Returns("test-token");
        _mockGitHubService.ValidateTokenAsync("test-token").Returns(new GitHubTokenValidation
        {
            IsValid = true,
            Scopes = new[] { "repo" }
        });

        // Act
        var result = await _reportService.GetReportRepositoryAsync();

        // Assert
        Assert.Equal("pksorensen", result.Owner);
        Assert.Equal("pks-cli", result.Name);
        Assert.Equal("pksorensen/pks-cli", result.FullName);
        Assert.True(result.IsConfigured);
        Assert.True(result.HasWriteAccess);
    }

    private void SetupMockServices()
    {
        _mockSystemInformationService.GetSystemInformationAsync().Returns(new SystemInformation
        {
            PksCliInfo = new PksCliInfo
            {
                Version = "1.0.0",
                AssemblyVersion = "1.0.0.0",
                ProductVersion = "1.0.0",
                GitCommit = "abc123",
                BuildDate = DateTime.UtcNow,
                BuildConfiguration = "Release"
            },
            OperatingSystemInfo = new OperatingSystemInfo
            {
                Name = "Linux",
                OsArchitecture = System.Runtime.InteropServices.Architecture.X64,
                IsWsl = false
            },
            HardwareInfo = new HardwareInfo
            {
                LogicalCores = 8
            },
            DotNetRuntimeInfo = new DotNetRuntimeInfo
            {
                FrameworkVersion = ".NET 8.0.0",
                RuntimeVersion = "8.0.0",
                RuntimeIdentifier = "linux-x64"
            },
            EnvironmentInfo = new EnvironmentInfo
            {
                HasDocker = false,
                CurrentDirectory = "/test"
            }
        });

        _mockTelemetryService.GetTelemetryDataAsync().Returns(new TelemetryData
        {
            IsEnabled = true,
            Usage = new UsageStatistics
            {
                TotalCommands = 10,
                MostUsedCommand = "init",
                DaysActive = 5
            },
            Errors = new ErrorStatistics
            {
                TotalErrors = 2
            },
            Features = new FeatureUsage
            {
                UsedAgenticFeatures = true,
                UsedMcpIntegration = false
            }
        });
    }
}