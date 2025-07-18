using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Base class for all PKS CLI tests providing common setup and utilities
/// </summary>
public abstract class TestBase : IDisposable
{
    protected readonly ITestOutputHelper Output;
    protected readonly MockFileSystem FileSystem;
    protected readonly ServiceCollection Services;
    protected ServiceProvider? ServiceProvider;
    protected readonly string TestDirectory;

    protected TestBase(ITestOutputHelper output)
    {
        Output = output;
        FileSystem = new MockFileSystem();
        Services = new ServiceCollection();
        TestDirectory = $"/test/{Guid.NewGuid()}";
        
        SetupBaseServices();
    }

    protected virtual void SetupBaseServices()
    {
        Services.AddSingleton<IFileSystem>(FileSystem);
        Services.AddLogging();
    }

    protected ServiceProvider BuildServiceProvider()
    {
        ServiceProvider?.Dispose();
        ServiceProvider = Services.BuildServiceProvider();
        return ServiceProvider;
    }

    protected T GetService<T>() where T : notnull
    {
        return (ServiceProvider ?? BuildServiceProvider()).GetRequiredService<T>();
    }

    protected void CreateTestDirectory(string path)
    {
        FileSystem.AddDirectory(path);
    }

    protected void CreateTestFile(string path, string content = "")
    {
        FileSystem.AddFile(path, new MockFileData(content));
    }

    protected void AssertFileExists(string path)
    {
        Assert.True(FileSystem.FileExists(path), $"Expected file to exist: {path}");
    }

    protected void AssertDirectoryExists(string path)
    {
        Assert.True(FileSystem.Directory.Exists(path), $"Expected directory to exist: {path}");
    }

    protected string GetFileContent(string path)
    {
        return FileSystem.File.ReadAllText(path);
    }

    public virtual void Dispose()
    {
        ServiceProvider?.Dispose();
        GC.SuppressFinalize(this);
    }
}