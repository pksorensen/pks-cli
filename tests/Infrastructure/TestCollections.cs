using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Test collection definitions for controlling parallel execution
/// </summary>

/// <summary>
/// Tests that must run sequentially to avoid resource conflicts
/// </summary>
[CollectionDefinition("Sequential")]
public class SequentialTestCollection : ICollectionFixture<SequentialTestFixture>
{
}

/// <summary>
/// Tests that can run in parallel but within their own group
/// </summary>
[CollectionDefinition("Parallel")]
public class ParallelTestCollection : ICollectionFixture<ParallelTestFixture>
{
}

/// <summary>
/// Tests that involve file system operations
/// </summary>
[CollectionDefinition("FileSystem")]
public class FileSystemTestCollection : ICollectionFixture<FileSystemTestFixture>
{
}

/// <summary>
/// Tests that involve external processes
/// </summary>
[CollectionDefinition("Process")]
public class ProcessTestCollection : ICollectionFixture<ProcessTestFixture>
{
}

/// <summary>
/// Tests that involve network operations
/// </summary>
[CollectionDefinition("Network")]
public class NetworkTestCollection : ICollectionFixture<NetworkTestFixture>
{
}

/// <summary>
/// Fixture for sequential test collection
/// </summary>
public class SequentialTestFixture : IDisposable
{
    public void Dispose()
    {
        // Cleanup any shared resources
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}

/// <summary>
/// Fixture for parallel test collection
/// </summary>
public class ParallelTestFixture : IDisposable
{
    public void Dispose()
    {
        // Cleanup any shared resources
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}

/// <summary>
/// Fixture for file system test collection
/// </summary>
public class FileSystemTestFixture : IDisposable
{
    public void Dispose()
    {
        // Cleanup any temporary files or directories
        CleanupTempFiles();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private void CleanupTempFiles()
    {
        try
        {
            var tempPath = Path.GetTempPath();
            var pksTestDirs = Directory.GetDirectories(tempPath, "pks-cli-test-*");

            foreach (var dir in pksTestDirs)
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}

/// <summary>
/// Fixture for process test collection
/// </summary>
public class ProcessTestFixture : IDisposable
{
    public void Dispose()
    {
        // Kill any leftover test processes
        KillTestProcesses();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private void KillTestProcesses()
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("dotnet")
                .Where(p => p.ProcessName.Contains("pks") ||
                           p.StartInfo.Arguments?.Contains("pks") == true)
                .ToArray();

            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(1000);
                    }
                }
                catch
                {
                    // Ignore process cleanup errors
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Ignore process enumeration errors
        }
    }
}

/// <summary>
/// Fixture for network test collection
/// </summary>
public class NetworkTestFixture : IDisposable
{
    public void Dispose()
    {
        // Cleanup any network resources
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}