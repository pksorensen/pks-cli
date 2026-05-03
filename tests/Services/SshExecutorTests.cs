using System.Diagnostics;
using FluentAssertions;
using PKS.Infrastructure.Services;
using Xunit;

namespace PKS.CLI.Tests.Services;

public class SshExecutorTests
{
    [Fact]
    [Trait("Category", "SshExecutor")]
    public async Task RunAsync_ReturnsExitCodeAndStdout()
    {
        // Arrange — inject a fake process factory that runs /bin/sh -c 'echo hello'
        var executor = new SshExecutor(psi =>
        {
            var fakePsi = new ProcessStartInfo("/bin/sh")
            {
                Arguments = "-c \"echo hello\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            return Process.Start(fakePsi);
        });

        var target = new SshTarget
        {
            Host = "localhost",
            Port = 22,
            Username = "user",
            KeyPath = ""
        };

        // Act
        var result = await executor.RunAsync(target, "ignored-ssh-cmd", TimeSpan.FromSeconds(10));

        // Assert
        result.ExitCode.Should().Be(0);
        result.Stdout.Trim().Should().Be("hello");
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "SshExecutor")]
    public async Task RunAsync_TimedOut_ReturnsTrueWhenTimeout()
    {
        // Arrange — inject a fake process that sleeps longer than the timeout
        var executor = new SshExecutor(psi =>
        {
            var fakePsi = new ProcessStartInfo("/bin/sh")
            {
                Arguments = "-c \"sleep 60\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            return Process.Start(fakePsi);
        });

        var target = new SshTarget { Host = "localhost", Port = 22, Username = "user", KeyPath = "" };

        // Act
        var result = await executor.RunAsync(target, "ignored-ssh-cmd", TimeSpan.FromMilliseconds(200));

        // Assert
        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(-1);
    }
}
