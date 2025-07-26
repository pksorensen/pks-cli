using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PKS.Commands;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands;

public class ReportCommandTests
{
    private readonly IReportService _mockReportService;
    private readonly TestConsole _testConsole;
    private readonly ReportCommand _command;

    public ReportCommandTests()
    {
        _mockReportService = Substitute.For<IReportService>();
        _testConsole = new TestConsole();
        _command = new ReportCommand(_mockReportService, _testConsole);
    }

    [Fact]
    public async Task ExecuteAsync_WithValidMessage_ShouldCreateReport()
    {
        // Arrange
        var settings = new ReportCommand.Settings
        {
            Message = "Test message",
            Title = "Test Title",
            IsBug = true
        };

        var expectedResult = new ReportResult
        {
            Success = true,
            Title = "Test Title",
            Repository = "pksorensen/pks-cli",
            IssueNumber = 123,
            IssueUrl = "https://github.com/pksorensen/pks-cli/issues/123",
            Labels = new List<string> { "pks-cli-report", "bug" }
        };

        _mockReportService.CreateReportAsync(Arg.Any<CreateReportRequest>())
            .Returns(Task.FromResult(expectedResult));

        var context = new CommandContext(Array.Empty<string>(), "report", null);

        // Act
        var result = await _command.ExecuteAsync(context, settings);

        // Assert
        Assert.Equal(0, result);
        await _mockReportService.Received(1).CreateReportAsync(Arg.Any<CreateReportRequest>());
    }

    [Fact]
    public async Task ExecuteAsync_WithDryRun_ShouldPreviewReport()
    {
        // Arrange
        var settings = new ReportCommand.Settings
        {
            Message = "Test message",
            Title = "Test Title",
            DryRun = true
        };

        var expectedResult = new ReportResult
        {
            Success = true,
            Title = "Test Title",
            Content = "## User Report\n\nTest message\n\n",
            Repository = "pksorensen/pks-cli",
            Labels = new List<string> { "pks-cli-report", "feedback" }
        };

        _mockReportService.PreviewReportAsync(Arg.Any<CreateReportRequest>())
            .Returns(Task.FromResult(expectedResult));

        var context = new CommandContext(Array.Empty<string>(), "report", null);

        // Act
        var result = await _command.ExecuteAsync(context, settings);

        // Assert
        Assert.Equal(0, result);
        await _mockReportService.Received(1).PreviewReportAsync(Arg.Any<CreateReportRequest>());
        await _mockReportService.DidNotReceive().CreateReportAsync(Arg.Any<CreateReportRequest>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenReportFails_ShouldReturnErrorCode()
    {
        // Arrange
        var settings = new ReportCommand.Settings
        {
            Message = "Test message",
            Title = "Test Title"
        };

        var expectedResult = new ReportResult
        {
            Success = false,
            ErrorMessage = "GitHub authentication failed"
        };

        _mockReportService.CreateReportAsync(Arg.Any<CreateReportRequest>())
            .Returns(Task.FromResult(expectedResult));

        var context = new CommandContext(Array.Empty<string>(), "report", null);

        // Act
        var result = await _command.ExecuteAsync(context, settings);

        // Assert
        Assert.Equal(1, result);
    }

    [Theory]
    [InlineData(true, false, false, new[] { "pks-cli-report", "bug" })]
    [InlineData(false, true, false, new[] { "pks-cli-report", "enhancement" })]
    [InlineData(false, false, true, new[] { "pks-cli-report", "question" })]
    [InlineData(false, false, false, new[] { "pks-cli-report", "feedback" })]
    public async Task ExecuteAsync_WithDifferentIssueTypes_ShouldSetCorrectLabels(
        bool isBug, bool isFeature, bool isQuestion, string[] expectedLabels)
    {
        // Arrange
        var settings = new ReportCommand.Settings
        {
            Message = "Test message",
            Title = "Test Title",
            IsBug = isBug,
            IsFeatureRequest = isFeature,
            IsQuestion = isQuestion,
            DryRun = true
        };

        var capturedRequest = default(CreateReportRequest);
        _mockReportService.PreviewReportAsync(Arg.Do<CreateReportRequest>(req => capturedRequest = req))
            .Returns(Task.FromResult(new ReportResult { Success = true, Labels = expectedLabels.ToList() }));

        var context = new CommandContext(Array.Empty<string>(), "report", null);

        // Act
        await _command.ExecuteAsync(context, settings);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.Equal(isBug, capturedRequest.IsBug);
        Assert.Equal(isFeature, capturedRequest.IsFeatureRequest);
        Assert.Equal(isQuestion, capturedRequest.IsQuestion);
    }

    [Fact]
    public void Settings_ShouldHaveCorrectDefaultValues()
    {
        // Arrange & Act
        var settings = new ReportCommand.Settings();

        // Assert
        Assert.True(settings.IncludeTelemetry);
        Assert.True(settings.IncludeEnvironment);
        Assert.True(settings.IncludeVersion);
        Assert.Equal("pksorensen/pks-cli", settings.Repository);
        Assert.False(settings.DryRun);
        Assert.False(settings.IsBug);
        Assert.False(settings.IsFeatureRequest);
        Assert.False(settings.IsQuestion);
    }
}