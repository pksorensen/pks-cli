using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Spectre.Console;
using Spectre.Console.Testing;
using System.Text;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Base class for all test classes providing common utilities and setup
/// </summary>
public abstract class TestBase : IDisposable
{
    protected readonly IServiceProvider ServiceProvider;
    protected readonly TestConsole TestConsole;
    protected readonly StringBuilder LogOutput;
    protected readonly Mock<ILogger> MockLogger;

    protected TestBase()
    {
        // Setup test console for Spectre.Console testing
        TestConsole = new TestConsole();

        // Setup logging capture
        LogOutput = new StringBuilder();
        MockLogger = new Mock<ILogger>();

        // Setup mock logger to capture log messages
        MockLogger.Setup(x => x.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception, Delegate>((level, eventId, state, exception, formatter) =>
            {
                LogOutput.AppendLine($"[{level}] {formatter.DynamicInvoke(state, exception)}");
            });

        // Create service collection for dependency injection
        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();
    }

    /// <summary>
    /// Override this method to configure services for testing
    /// </summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
        // Add common test services
        services.AddSingleton<IAnsiConsole>(TestConsole);
        services.AddSingleton(MockLogger.Object);
    }

    /// <summary>
    /// Gets a service from the test service provider
    /// </summary>
    protected T GetService<T>() where T : class
    {
        return ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// Creates a mock of the specified type
    /// </summary>
    protected Mock<T> CreateMock<T>() where T : class
    {
        return new Mock<T>();
    }

    /// <summary>
    /// Asserts that the console output contains the expected text
    /// </summary>
    protected void AssertConsoleOutput(string expectedText)
    {
        TestConsole.Output.Should().Contain(expectedText);
    }

    /// <summary>
    /// Asserts that a log message with the specified level was written
    /// </summary>
    protected void AssertLogMessage(LogLevel level, string expectedMessage)
    {
        LogOutput.ToString().Should().Contain($"[{level}]").And.Contain(expectedMessage);
    }

    /// <summary>
    /// Clears the test console output
    /// </summary>
    protected void ClearConsoleOutput()
    {
        TestConsole.Clear();
    }

    /// <summary>
    /// Creates a temporary directory for test files
    /// </summary>
    protected string CreateTempDirectory()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"pks-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPath);
        return tempPath;
    }

    /// <summary>
    /// Creates a temporary file with the specified content
    /// </summary>
    protected string CreateTempFile(string content = "", string extension = ".txt")
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pks-cli-test-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(tempFile, content);
        return tempFile;
    }

    public virtual void Dispose()
    {
        if (ServiceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}