using PKS.Commands.Runtime;
using Xunit;

namespace PKS.CLI.Tests.Commands.Runtime;

public class RuntimeHostOptionsTests
{
    [Fact]
    public void Defaults_To_Realtime_Speech_Capability()
    {
        var options = RuntimeHostOptions.FromEnvironment(_ => null);

        Assert.Equal(["speech.realtime.transcribe"], options.Capabilities);
    }

    [Fact]
    public void Reads_Multiple_Capabilities_Without_Duplicates()
    {
        var values = new Dictionary<string, string?>
        {
            ["PKS_RUNTIME_CAPABILITIES"] = "speech.realtime.transcribe, speech.realtime.transcribe",
            ["PKS_RUNTIME_TOKEN"] = "runtime-token",
        };

        var options = RuntimeHostOptions.FromEnvironment(key => values.GetValueOrDefault(key));

        Assert.Equal(["speech.realtime.transcribe"], options.Capabilities);
        Assert.Equal("runtime-token", options.ServiceToken);
    }

    [Fact]
    public void Accepts_Legacy_Speech_Token_During_Migration()
    {
        var values = new Dictionary<string, string?>
        {
            ["PKS_SPEECH_RUNTIME_TOKEN"] = "legacy-token",
        };

        var options = RuntimeHostOptions.FromEnvironment(key => values.GetValueOrDefault(key));

        Assert.Equal("legacy-token", options.ServiceToken);
    }

    [Fact]
    public void Rejects_Unknown_Capability()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RuntimeHostOptions.FromEnvironment(
            key => key == "PKS_RUNTIME_CAPABILITIES" ? "vision.generate" : null));

        Assert.Contains("vision.generate", error.Message);
    }
}
