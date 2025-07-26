using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Infrastructure.Services.MCP.Tools;
using PKS.Infrastructure.Services;
using Xunit;

namespace PKS.CLI.Tests.Integration.Mcp;

/// <summary>
/// Integration tests for MCP report tool registration and invocation
/// </summary>
public class McpReportToolIntegrationTests
{
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<IReportService> _mockReportService;

    public McpReportToolIntegrationTests()
    {
        var services = new ServiceCollection();
        
        // Mock dependencies
        _mockReportService = new Mock<IReportService>();
        services.AddSingleton(_mockReportService.Object);
        
        // Add logging
        services.AddLogging(builder => builder.AddConsole());
        
        // Add the report tool service
        services.AddSingleton<ReportToolService>();
        
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public void ReportToolService_CanBeResolvedFromDependencyInjection()
    {
        // Act
        var reportToolService = _serviceProvider.GetService<ReportToolService>();

        // Assert
        Assert.NotNull(reportToolService);
        Assert.IsType<ReportToolService>(reportToolService);
    }

    [Fact]
    public void ReportToolService_HasMcpServerToolTypeAttribute()
    {
        // Arrange
        var reportToolServiceType = typeof(ReportToolService);

        // Act
        var hasAttribute = reportToolServiceType.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), false).Any();

        // Assert
        Assert.True(hasAttribute, "ReportToolService should have McpServerToolType attribute");
    }

    [Fact]
    public void ReportToolService_HasCorrectMcpToolMethods()
    {
        // Arrange
        var reportToolServiceType = typeof(ReportToolService);
        var expectedMethods = new[]
        {
            "CreateReportAsync",
            "PreviewReportAsync", 
            "GetReportCapabilitiesAsync",
            "CreateBugReportAsync",
            "CreateFeatureRequestAsync"
        };

        // Act
        var methods = reportToolServiceType.GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).Any())
            .Select(m => m.Name)
            .ToArray();

        // Assert
        foreach (var expectedMethod in expectedMethods)
        {
            Assert.Contains(expectedMethod, methods);
        }

        Assert.Equal(expectedMethods.Length, methods.Length);
    }

    [Fact]
    public void ReportToolService_McpToolMethodsHaveDescriptions()
    {
        // Arrange
        var reportToolServiceType = typeof(ReportToolService);

        // Act
        var mcpMethods = reportToolServiceType.GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).Any());

        // Assert
        foreach (var method in mcpMethods)
        {
            var descriptionAttribute = method.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .Cast<System.ComponentModel.DescriptionAttribute>()
                .FirstOrDefault();

            Assert.NotNull(descriptionAttribute);
            Assert.False(string.IsNullOrEmpty(descriptionAttribute.Description), 
                $"Method {method.Name} should have a non-empty description");
        }
    }

    [Fact]
    public async Task ReportToolService_CreateReportAsync_IntegratesWithReportService()
    {
        // Arrange
        var reportToolService = _serviceProvider.GetRequiredService<ReportToolService>();
        var expectedResult = new ReportResult
        {
            Success = true,
            IssueNumber = 123,
            IssueUrl = "https://github.com/pksorensen/pks-cli/issues/123",
            Repository = "pksorensen/pks-cli",
            Title = "Test Report",
            Labels = new List<string> { "bug" },
            CreatedAt = DateTime.UtcNow
        };

        _mockReportService.Setup(x => x.CreateReportAsync(It.IsAny<CreateReportRequest>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await reportToolService.CreateReportAsync(
            message: "Integration test message",
            isBug: true);

        // Assert
        Assert.NotNull(result);
        _mockReportService.Verify(x => x.CreateReportAsync(It.Is<CreateReportRequest>(req =>
            req.Message == "Integration test message" &&
            req.IsBug == true
        )), Times.Once);
    }

    [Fact]
    public async Task ReportToolService_PreviewReportAsync_IntegratesWithReportService()
    {
        // Arrange
        var reportToolService = _serviceProvider.GetRequiredService<ReportToolService>();
        var expectedResult = new ReportResult
        {
            Success = true,
            Repository = "pksorensen/pks-cli",
            Title = "Preview Test",
            Labels = new List<string> { "enhancement" },
            Content = "Preview content..."
        };

        _mockReportService.Setup(x => x.PreviewReportAsync(It.IsAny<CreateReportRequest>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await reportToolService.PreviewReportAsync(
            message: "Preview integration test",
            isFeatureRequest: true);

        // Assert
        Assert.NotNull(result);
        _mockReportService.Verify(x => x.PreviewReportAsync(It.Is<CreateReportRequest>(req =>
            req.Message == "Preview integration test" &&
            req.IsFeatureRequest == true
        )), Times.Once);
    }

    [Fact]
    public async Task ReportToolService_GetReportCapabilitiesAsync_IntegratesWithReportService()
    {
        // Arrange
        var reportToolService = _serviceProvider.GetRequiredService<ReportToolService>();
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
        var result = await reportToolService.GetReportCapabilitiesAsync();

        // Assert
        Assert.NotNull(result);
        _mockReportService.Verify(x => x.CanCreateReportsAsync(), Times.Once);
        _mockReportService.Verify(x => x.GetReportRepositoryAsync(), Times.Once);
    }

    [Theory]
    [InlineData("CreateReportAsync", typeof(string), "message")]
    [InlineData("PreviewReportAsync", typeof(string), "message")]
    [InlineData("CreateBugReportAsync", typeof(string), "bugDescription")]
    [InlineData("CreateFeatureRequestAsync", typeof(string), "featureDescription")]
    public void ReportToolService_McpToolMethods_HaveRequiredParameters(string methodName, Type parameterType, string parameterName)
    {
        // Arrange
        var reportToolServiceType = typeof(ReportToolService);

        // Act
        var method = reportToolServiceType.GetMethod(methodName);

        // Assert
        Assert.NotNull(method);
        var parameters = method.GetParameters();
        Assert.Contains(parameters, p => p.Name == parameterName && p.ParameterType == parameterType);
    }

    [Fact]
    public void ReportToolService_McpToolMethods_ReturnTaskOfObject()
    {
        // Arrange
        var reportToolServiceType = typeof(ReportToolService);
        var mcpMethods = reportToolServiceType.GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).Any());

        // Act & Assert
        foreach (var method in mcpMethods)
        {
            Assert.Equal(typeof(Task<object>), method.ReturnType);
        }
    }

    [Fact]
    public async Task ReportToolService_CreateBugReportAsync_UsesCorrectDefaults()
    {
        // Arrange
        var reportToolService = _serviceProvider.GetRequiredService<ReportToolService>();
        var expectedResult = new ReportResult
        {
            Success = true,
            IssueNumber = 456,
            IssueUrl = "https://github.com/pksorensen/pks-cli/issues/456",
            Repository = "pksorensen/pks-cli",
            Title = "Bug Report: Test bug",
            Labels = new List<string> { "bug" },
            CreatedAt = DateTime.UtcNow
        };

        _mockReportService.Setup(x => x.CreateReportAsync(It.IsAny<CreateReportRequest>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await reportToolService.CreateBugReportAsync(
            bugDescription: "Test bug description");

        // Assert
        _mockReportService.Verify(x => x.CreateReportAsync(It.Is<CreateReportRequest>(req =>
            req.IsBug == true &&
            req.IsFeatureRequest == false &&
            req.IsQuestion == false &&
            req.IncludeTelemetry == true &&
            req.IncludeEnvironment == true &&
            req.IncludeVersion == true
        )), Times.Once);
    }

    [Fact]
    public async Task ReportToolService_CreateFeatureRequestAsync_UsesCorrectDefaults()
    {
        // Arrange
        var reportToolService = _serviceProvider.GetRequiredService<ReportToolService>();
        var expectedResult = new ReportResult
        {
            Success = true,
            IssueNumber = 789,
            IssueUrl = "https://github.com/pksorensen/pks-cli/issues/789",
            Repository = "pksorensen/pks-cli",
            Title = "Feature Request: Test feature",
            Labels = new List<string> { "enhancement" },
            CreatedAt = DateTime.UtcNow
        };

        _mockReportService.Setup(x => x.CreateReportAsync(It.IsAny<CreateReportRequest>()))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await reportToolService.CreateFeatureRequestAsync(
            featureDescription: "Test feature description");

        // Assert
        _mockReportService.Verify(x => x.CreateReportAsync(It.Is<CreateReportRequest>(req =>
            req.IsBug == false &&
            req.IsFeatureRequest == true &&
            req.IsQuestion == false &&
            req.IncludeTelemetry == false &&
            req.IncludeEnvironment == false &&
            req.IncludeVersion == true
        )), Times.Once);
    }

    [Fact]
    public async Task ReportToolService_AllMethods_HandleExceptionsGracefully()
    {
        // Arrange
        var reportToolService = _serviceProvider.GetRequiredService<ReportToolService>();
        _mockReportService.Setup(x => x.CreateReportAsync(It.IsAny<CreateReportRequest>()))
            .ThrowsAsync(new Exception("Test exception"));
        _mockReportService.Setup(x => x.PreviewReportAsync(It.IsAny<CreateReportRequest>()))
            .ThrowsAsync(new Exception("Test exception"));
        _mockReportService.Setup(x => x.CanCreateReportsAsync())
            .ThrowsAsync(new Exception("Test exception"));

        // Act & Assert
        var createResult = await reportToolService.CreateReportAsync("test message");
        var previewResult = await reportToolService.PreviewReportAsync("test message");
        var capabilitiesResult = await reportToolService.GetReportCapabilitiesAsync();
        var bugResult = await reportToolService.CreateBugReportAsync("test bug");
        var featureResult = await reportToolService.CreateFeatureRequestAsync("test feature");

        // All results should be non-null objects indicating graceful exception handling
        Assert.NotNull(createResult);
        Assert.NotNull(previewResult);
        Assert.NotNull(capabilitiesResult);
        Assert.NotNull(bugResult);
        Assert.NotNull(featureResult);
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }
}