using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Infrastructure.Services.MCP.Tools;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using Xunit;

namespace PKS.CLI.Tests.Services.MCP.Tools;

/// <summary>
/// Unit tests for ReportToolService MCP tools
/// </summary>
public class ReportToolServiceTests
{
    private readonly Mock<ILogger<ReportToolService>> _mockLogger;
    private readonly Mock<IReportService> _mockReportService;
    private readonly ReportToolService _reportToolService;

    public ReportToolServiceTests()
    {
        _mockLogger = new Mock<ILogger<ReportToolService>>();
        _mockReportService = new Mock<IReportService>();
        _reportToolService = new ReportToolService(_mockLogger.Object, _mockReportService.Object);
    }

    [Theory]
    [InlineData("Test bug report", "Bug Report: Test", true, false, false)]
    [InlineData("New feature needed", "Feature Request: New", false, true, false)]
    [InlineData("How does this work?", "Question: How", false, false, true)]
    [InlineData("General feedback", "PKS CLI Feedback: General", false, false, false)]
    public async Task CreateReportAsync_WithValidParameters_ReturnsSuccessResult(
        string message, string expectedTitlePrefix, bool isBug, bool isFeatureRequest, bool isQuestion)
    {
        // Arrange
        var expectedResult = new ReportResult
        {
            Success = true,
            IssueNumber = 123,
            IssueUrl = "https://github.com/pksorensen/pks-cli/issues/123",
            Repository = "pksorensen/pks-cli",
            Title = $"{expectedTitlePrefix} feature",
            Labels = new List<string> { isBug ? "bug" : isFeatureRequest ? "enhancement" : isQuestion ? "question" : "feedback" },
            CreatedAt = DateTime.UtcNow
        };

        _mockReportService.Setup(x => x.CreateReportAsync(It.IsAny<CreateReportRequest>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _reportToolService.CreateReportAsync(
            message: message,
            title: null,
            isBug: isBug,
            isFeatureRequest: isFeatureRequest,
            isQuestion: isQuestion);

        // Assert
        var resultObj = Assert.IsType<object>(result);
        var properties = resultObj.GetType().GetProperties();
        var successProp = properties.FirstOrDefault(p => p.Name == "success");
        Assert.NotNull(successProp);
        Assert.True((bool)successProp.GetValue(resultObj)!);

        var issueNumberProp = properties.FirstOrDefault(p => p.Name == "issueNumber");
        Assert.NotNull(issueNumberProp);
        Assert.Equal(123, (int)issueNumberProp.GetValue(resultObj)!);

        _mockReportService.Verify(x => x.CreateReportAsync(It.Is<CreateReportRequest>(req =>
            req.Message == message &&
            req.IsBug == isBug &&
            req.IsFeatureRequest == isFeatureRequest &&
            req.IsQuestion == isQuestion
        )), Times.Once);
    }

    [Fact]
    public async Task CreateReportAsync_WithEmptyMessage_ReturnsErrorResult()
    {
        // Act
        var result = await _reportToolService.CreateReportAsync(
            message: "",
            title: "Test Title");

        // Assert
        var resultObj = Assert.IsType<object>(result);
        var properties = resultObj.GetType().GetProperties();
        var successProp = properties.FirstOrDefault(p => p.Name == "success");
        Assert.NotNull(successProp);
        Assert.False((bool)successProp.GetValue(resultObj)!);

        var errorProp = properties.FirstOrDefault(p => p.Name == "error");
        Assert.NotNull(errorProp);
        Assert.Equal("Message is required", (string)errorProp.GetValue(resultObj)!);

        _mockReportService.Verify(x => x.CreateReportAsync(It.IsAny<CreateReportRequest>()), Times.Never);
    }

    [Fact]
    public async Task CreateReportAsync_WithCustomTitle_UsesProvidedTitle()
    {
        // Arrange
        var customTitle = "Custom Issue Title";
        var expectedResult = new ReportResult
        {
            Success = true,
            IssueNumber = 456,
            IssueUrl = "https://github.com/pksorensen/pks-cli/issues/456",
            Repository = "pksorensen/pks-cli",
            Title = customTitle,
            Labels = new List<string> { "bug" },
            CreatedAt = DateTime.UtcNow
        };

        _mockReportService.Setup(x => x.CreateReportAsync(It.IsAny<CreateReportRequest>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _reportToolService.CreateReportAsync(
            message: "Test message",
            title: customTitle,
            isBug: true);

        // Assert
        _mockReportService.Verify(x => x.CreateReportAsync(It.Is<CreateReportRequest>(req =>
            req.Title == customTitle
        )), Times.Once);
    }

    [Fact]
    public async Task CreateReportAsync_WhenServiceFails_ReturnsErrorResult()
    {
        // Arrange
        var failedResult = new ReportResult
        {
            Success = false,
            ErrorMessage = "GitHub API error"
        };

        _mockReportService.Setup(x => x.CreateReportAsync(It.IsAny<CreateReportRequest>()))
            .ReturnsAsync(failedResult);

        // Act
        var result = await _reportToolService.CreateReportAsync(
            message: "Test message");

        // Assert
        var resultObj = Assert.IsType<object>(result);
        var properties = resultObj.GetType().GetProperties();
        var successProp = properties.FirstOrDefault(p => p.Name == "success");
        Assert.NotNull(successProp);
        Assert.False((bool)successProp.GetValue(resultObj)!);

        var errorProp = properties.FirstOrDefault(p => p.Name == "error");
        Assert.NotNull(errorProp);
        Assert.Equal("GitHub API error", (string)errorProp.GetValue(resultObj)!);
    }

    [Fact]
    public async Task CreateReportAsync_WhenExceptionThrown_ReturnsErrorResult()
    {
        // Arrange
        _mockReportService.Setup(x => x.CreateReportAsync(It.IsAny<CreateReportRequest>()))
            .ThrowsAsync(new Exception("Network error"));

        // Act
        var result = await _reportToolService.CreateReportAsync(
            message: "Test message");

        // Assert
        var resultObj = Assert.IsType<object>(result);
        var properties = resultObj.GetType().GetProperties();
        var successProp = properties.FirstOrDefault(p => p.Name == "success");
        Assert.NotNull(successProp);
        Assert.False((bool)successProp.GetValue(resultObj)!);

        var errorProp = properties.FirstOrDefault(p => p.Name == "error");
        Assert.NotNull(errorProp);
        Assert.Equal("Network error", (string)errorProp.GetValue(resultObj)!);
    }

    [Fact]
    public async Task PreviewReportAsync_WithValidParameters_ReturnsPreviewResult()
    {
        // Arrange
        var expectedResult = new ReportResult
        {
            Success = true,
            Repository = "pksorensen/pks-cli",
            Title = "Test Preview",
            Labels = new List<string> { "enhancement" },
            Content = "This is a preview of the report content with system information..."
        };

        _mockReportService.Setup(x => x.PreviewReportAsync(It.IsAny<CreateReportRequest>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _reportToolService.PreviewReportAsync(
            message: "Test preview message",
            isFeatureRequest: true);

        // Assert
        var resultObj = Assert.IsType<object>(result);
        var properties = resultObj.GetType().GetProperties();
        var successProp = properties.FirstOrDefault(p => p.Name == "success");
        Assert.NotNull(successProp);
        Assert.True((bool)successProp.GetValue(resultObj)!);

        var previewProp = properties.FirstOrDefault(p => p.Name == "preview");
        Assert.NotNull(previewProp);
        var previewObj = previewProp.GetValue(resultObj)!;
        var previewProps = previewObj.GetType().GetProperties();
        
        var titleProp = previewProps.FirstOrDefault(p => p.Name == "title");
        Assert.NotNull(titleProp);
        Assert.Equal("Test Preview", (string)titleProp.GetValue(previewObj)!);

        _mockReportService.Verify(x => x.PreviewReportAsync(It.Is<CreateReportRequest>(req =>
            req.Message == "Test preview message" &&
            req.IsFeatureRequest == true
        )), Times.Once);
    }

    [Fact]
    public async Task PreviewReportAsync_WithEmptyMessage_ReturnsErrorResult()
    {
        // Act
        var result = await _reportToolService.PreviewReportAsync(
            message: "");

        // Assert
        var resultObj = Assert.IsType<object>(result);
        var properties = resultObj.GetType().GetProperties();
        var successProp = properties.FirstOrDefault(p => p.Name == "success");
        Assert.NotNull(successProp);
        Assert.False((bool)successProp.GetValue(resultObj)!);

        var errorProp = properties.FirstOrDefault(p => p.Name == "error");
        Assert.NotNull(errorProp);
        Assert.Equal("Message is required", (string)errorProp.GetValue(resultObj)!);

        _mockReportService.Verify(x => x.PreviewReportAsync(It.IsAny<CreateReportRequest>()), Times.Never);
    }

    [Fact]
    public async Task GetReportCapabilitiesAsync_WithValidAccess_ReturnsCapabilities()
    {
        // Arrange
        var repositoryInfo = new ReportRepositoryInfo
        {
            Owner = "pksorensen",
            Name = "pks-cli",
            FullName = "pksorensen/pks-cli",
            Url = "https://github.com/pksorensen/pks-cli",
            HasWriteAccess = true,
            IsConfigured = true
        };

        _mockReportService.Setup(x => x.CanCreateReportsAsync()).ReturnsAsync(true);
        _mockReportService.Setup(x => x.GetReportRepositoryAsync()).ReturnsAsync(repositoryInfo);

        // Act
        var result = await _reportToolService.GetReportCapabilitiesAsync();

        // Assert
        var resultObj = Assert.IsType<object>(result);
        var properties = resultObj.GetType().GetProperties();
        var successProp = properties.FirstOrDefault(p => p.Name == "success");
        Assert.NotNull(successProp);
        Assert.True((bool)successProp.GetValue(resultObj)!);

        var capabilitiesProp = properties.FirstOrDefault(p => p.Name == "capabilities");
        Assert.NotNull(capabilitiesProp);
        var capabilitiesObj = capabilitiesProp.GetValue(resultObj)!;
        var capabilitiesProps = capabilitiesObj.GetType().GetProperties();
        
        var canCreateProp = capabilitiesProps.FirstOrDefault(p => p.Name == "canCreateReports");
        Assert.NotNull(canCreateProp);
        Assert.True((bool)canCreateProp.GetValue(capabilitiesObj)!);

        var repositoryProp = properties.FirstOrDefault(p => p.Name == "repository");
        Assert.NotNull(repositoryProp);
        var repositoryObj = repositoryProp.GetValue(resultObj)!;
        var repositoryProps = repositoryObj.GetType().GetProperties();
        
        var fullNameProp = repositoryProps.FirstOrDefault(p => p.Name == "fullName");
        Assert.NotNull(fullNameProp);
        Assert.Equal("pksorensen/pks-cli", (string)fullNameProp.GetValue(repositoryObj)!);

        _mockReportService.Verify(x => x.CanCreateReportsAsync(), Times.Once);
        _mockReportService.Verify(x => x.GetReportRepositoryAsync(), Times.Once);
    }

    [Fact]
    public async Task GetReportCapabilitiesAsync_WhenExceptionThrown_ReturnsErrorResult()
    {
        // Arrange
        _mockReportService.Setup(x => x.CanCreateReportsAsync())
            .ThrowsAsync(new Exception("Authentication error"));

        // Act
        var result = await _reportToolService.GetReportCapabilitiesAsync();

        // Assert
        var resultObj = Assert.IsType<object>(result);
        var properties = resultObj.GetType().GetProperties();
        var successProp = properties.FirstOrDefault(p => p.Name == "success");
        Assert.NotNull(successProp);
        Assert.False((bool)successProp.GetValue(resultObj)!);

        var errorProp = properties.FirstOrDefault(p => p.Name == "error");
        Assert.NotNull(errorProp);
        Assert.Equal("Authentication error", (string)errorProp.GetValue(resultObj)!);
    }

    [Fact]
    public async Task CreateBugReportAsync_WithValidDescription_CreatesReportWithBugSettings()
    {
        // Arrange
        var expectedResult = new ReportResult
        {
            Success = true,
            IssueNumber = 789,
            IssueUrl = "https://github.com/pksorensen/pks-cli/issues/789",
            Repository = "pksorensen/pks-cli",
            Title = "Bug Report: Application crashes",
            Labels = new List<string> { "bug" },
            CreatedAt = DateTime.UtcNow
        };

        _mockReportService.Setup(x => x.CreateReportAsync(It.IsAny<CreateReportRequest>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _reportToolService.CreateBugReportAsync(
            bugDescription: "Application crashes when clicking save button");

        // Assert
        var resultObj = Assert.IsType<object>(result);
        var properties = resultObj.GetType().GetProperties();
        var successProp = properties.FirstOrDefault(p => p.Name == "success");
        Assert.NotNull(successProp);
        Assert.True((bool)successProp.GetValue(resultObj)!);

        _mockReportService.Verify(x => x.CreateReportAsync(It.Is<CreateReportRequest>(req =>
            req.Message == "Application crashes when clicking save button" &&
            req.IsBug == true &&
            req.IsFeatureRequest == false &&
            req.IsQuestion == false &&
            req.IncludeTelemetry == true &&
            req.IncludeEnvironment == true &&
            req.IncludeVersion == true
        )), Times.Once);
    }

    [Fact]
    public async Task CreateFeatureRequestAsync_WithValidDescription_CreatesReportWithFeatureSettings()
    {
        // Arrange
        var expectedResult = new ReportResult
        {
            Success = true,
            IssueNumber = 101,
            IssueUrl = "https://github.com/pksorensen/pks-cli/issues/101",
            Repository = "pksorensen/pks-cli",
            Title = "Feature Request: Add dark mode",
            Labels = new List<string> { "enhancement" },
            CreatedAt = DateTime.UtcNow
        };

        _mockReportService.Setup(x => x.CreateReportAsync(It.IsAny<CreateReportRequest>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _reportToolService.CreateFeatureRequestAsync(
            featureDescription: "Add dark mode support to the CLI interface");

        // Assert
        var resultObj = Assert.IsType<object>(result);
        var properties = resultObj.GetType().GetProperties();
        var successProp = properties.FirstOrDefault(p => p.Name == "success");
        Assert.NotNull(successProp);
        Assert.True((bool)successProp.GetValue(resultObj)!);

        _mockReportService.Verify(x => x.CreateReportAsync(It.Is<CreateReportRequest>(req =>
            req.Message == "Add dark mode support to the CLI interface" &&
            req.IsBug == false &&
            req.IsFeatureRequest == true &&
            req.IsQuestion == false &&
            req.IncludeTelemetry == false &&
            req.IncludeEnvironment == false &&
            req.IncludeVersion == true
        )), Times.Once);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    public async Task CreateReportAsync_WithIncludeFlags_PassesCorrectSettings(
        bool includeTelemetry, bool includeEnvironment, bool includeVersion, bool expectedTelemetry)
    {
        // Arrange
        var expectedResult = new ReportResult { Success = true, IssueNumber = 999 };
        _mockReportService.Setup(x => x.CreateReportAsync(It.IsAny<CreateReportRequest>()))
            .ReturnsAsync(expectedResult);

        // Act
        await _reportToolService.CreateReportAsync(
            message: "Test message",
            includeTelemetry: includeTelemetry,
            includeEnvironment: includeEnvironment,
            includeVersion: includeVersion);

        // Assert
        _mockReportService.Verify(x => x.CreateReportAsync(It.Is<CreateReportRequest>(req =>
            req.IncludeTelemetry == expectedTelemetry &&
            req.IncludeEnvironment == includeEnvironment &&
            req.IncludeVersion == includeVersion
        )), Times.Once);
    }

    [Fact]
    public async Task CreateReportAsync_WithCustomRepository_UsesSpecifiedRepository()
    {
        // Arrange
        var customRepository = "myorg/my-project";
        var expectedResult = new ReportResult { Success = true, Repository = customRepository, IssueNumber = 222 };
        _mockReportService.Setup(x => x.CreateReportAsync(It.IsAny<CreateReportRequest>()))
            .ReturnsAsync(expectedResult);

        // Act
        await _reportToolService.CreateReportAsync(
            message: "Test message",
            repository: customRepository);

        // Assert
        _mockReportService.Verify(x => x.CreateReportAsync(It.Is<CreateReportRequest>(req =>
            req.Repository == customRepository
        )), Times.Once);
    }
}