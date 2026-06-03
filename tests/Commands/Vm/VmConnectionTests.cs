using Xunit;
using FluentAssertions;
using PKS.Commands.Vm;
using Spectre.Console.Testing;

namespace PKS.CLI.Tests.Commands.Vm;

public class VmConnectionTests
{
    private static Func<string?> Lines(params string[] lines)
    {
        var q = new Queue<string?>(lines);
        return () => q.Count > 0 ? q.Dequeue() : null;
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ReadPrivateKey_StopsAtEndMarker_IgnoresLeadingJunk()
    {
        var key = SshKeyText.ReadPrivateKey(Lines(
            "ignored prompt echo",
            "-----BEGIN OPENSSH PRIVATE KEY-----",
            "b3BlbnNzaC1rZXktdjEAAAA",
            "-----END OPENSSH PRIVATE KEY-----",
            "trailing junk that should not be read"));

        key.Should().NotBeNull();
        key!.Should().StartWith("-----BEGIN OPENSSH PRIVATE KEY-----");
        key.TrimEnd().Should().EndWith("-----END OPENSSH PRIVATE KEY-----");
        key.Should().NotContain("trailing junk");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ReadPrivateKey_ReturnsNull_WhenNoKeyBlock()
    {
        SshKeyText.ReadPrivateKey(Lines("just", "some", "text")).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void RenderConnectionPanel_ContainsHostUserAndSshCommand()
    {
        var console = new TestConsole();
        var info = new VmConnectionInfo("Scaleway", "si14x-h100", "51.159.10.20", 22, "root", "/no/such/key", "100.64.0.5");

        VmConnection.RenderConnectionPanel(console, info);

        var output = console.Output;
        output.Should().Contain("si14x-h100");
        output.Should().Contain("51.159.10.20");
        output.Should().Contain("root");
        output.Should().Contain("100.64.0.5");
        output.Should().Contain("pks claude --ssh-target si14x-h100");
    }
}
