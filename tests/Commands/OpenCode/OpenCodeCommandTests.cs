using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.Commands.OpenCode;
using PKS.Infrastructure.Services;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.OpenCode;

public class OpenCodeCommandTests
{
    [Fact]
    public void BuildInlineConfig_registers_scaleway_without_embedding_the_secret()
    {
        var json = OpenCodeCommand.BuildInlineConfig("glm-5.2");
        using var document = JsonDocument.Parse(json);

        document.RootElement.GetProperty("$schema").GetString()
            .Should().Be("https://opencode.ai/config.json");
        var provider = document.RootElement.GetProperty("provider").GetProperty("scaleway");
        provider.GetProperty("npm").GetString().Should().Be("@ai-sdk/openai-compatible");
        provider.GetProperty("options").GetProperty("baseURL").GetString()
            .Should().Be("https://api.scaleway.ai/v1");
        provider.GetProperty("options").GetProperty("apiKey").GetString()
            .Should().Be("{env:PKS_SCALEWAY_API_KEY}");
        provider.GetProperty("models").TryGetProperty("glm-5.2", out _).Should().BeTrue();
    }

    [Fact]
    public void BuildStartInfo_selects_provider_model_and_passes_native_arguments()
    {
        var startInfo = OpenCodeCommand.BuildStartInfo(
            "glm-5.2",
            "secret-value",
            ["--continue"]);

        startInfo.FileName.Should().Be("opencode");
        startInfo.ArgumentList.Should().Equal("--model", "scaleway/glm-5.2", "--continue");
        startInfo.Environment["PKS_SCALEWAY_API_KEY"].Should().Be("secret-value");
        startInfo.Environment["OPENCODE_CONFIG_CONTENT"].Should().Contain("glm-5.2");
        startInfo.ArgumentList.Should().NotContain(a => a.Contains("secret-value"));
    }

    [Fact]
    public async Task ExecuteAsync_without_scaleway_auth_explains_how_to_authenticate()
    {
        var scaleway = new Mock<IScalewayService>();
        scaleway.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(false);
        var console = new TestConsole();
        var command = new OpenCodeCommand(scaleway.Object, console);

        var exitCode = await command.ExecuteAsync(null!, new OpenCodeSettings());

        exitCode.Should().Be(1);
        console.Output.Should().Contain("pks scaleway init");
    }
}
