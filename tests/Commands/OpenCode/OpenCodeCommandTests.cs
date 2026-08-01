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
    public void ResolveProvider_automatically_selects_the_only_configured_provider_with_the_model()
    {
        var provider = OpenCodeCommand.ResolveProvider(
            "kimi-k3",
            requestedProvider: null,
            configuredProviderIds: ["scaleway", "moonshot"],
            providers: OpenCodeCommand.Providers);

        provider.Id.Should().Be("moonshot");
    }

    [Fact]
    public void ResolveProvider_preserves_automatic_scaleway_selection_for_glm()
    {
        var provider = OpenCodeCommand.ResolveProvider(
            "glm-5.2",
            requestedProvider: null,
            configuredProviderIds: ["scaleway", "moonshot"],
            providers: OpenCodeCommand.Providers);

        provider.Id.Should().Be("scaleway");
    }

    [Fact]
    public void ResolveProvider_requires_provider_only_when_a_model_is_ambiguous()
    {
        var providers = new[]
        {
            new OpenCodeProvider("one", "One", "https://one.test/v1", "ONE_KEY", ["shared"]),
            new OpenCodeProvider("two", "Two", "https://two.test/v1", "TWO_KEY", ["shared"]),
        };

        var act = () => OpenCodeCommand.ResolveProvider(
            "shared", null, ["one", "two"], providers);

        act.Should().Throw<OpenCodeProviderException>()
            .WithMessage("*multiple configured providers*--provider one*--provider two*");
    }

    [Fact]
    public void ResolveProvider_accepts_a_provider_prefixed_model()
    {
        var provider = OpenCodeCommand.ResolveProvider(
            "moonshot/kimi-k3", null, ["moonshot"], OpenCodeCommand.Providers);

        provider.Id.Should().Be("moonshot");
    }

    [Fact]
    public void BuildInlineConfig_registers_moonshot_without_embedding_the_secret()
    {
        var moonshot = OpenCodeCommand.Providers.Single(x => x.Id == "moonshot");
        var json = OpenCodeCommand.BuildInlineConfig(moonshot, "kimi-k3");
        using var document = JsonDocument.Parse(json);

        document.RootElement.GetProperty("$schema").GetString()
            .Should().Be("https://opencode.ai/config.json");
        var provider = document.RootElement.GetProperty("provider").GetProperty("moonshot");
        provider.GetProperty("npm").GetString().Should().Be("@ai-sdk/openai-compatible");
        provider.GetProperty("options").GetProperty("baseURL").GetString()
            .Should().Be("https://api.moonshot.ai/v1");
        provider.GetProperty("options").GetProperty("apiKey").GetString()
            .Should().Be("{env:MOONSHOT_API_KEY}");
        provider.GetProperty("models").TryGetProperty("kimi-k3", out _).Should().BeTrue();
        json.Should().NotContain("secret-value");
    }

    [Fact]
    public void BuildInlineConfig_preserves_the_scaleway_glm_flow()
    {
        var scaleway = OpenCodeCommand.Providers.Single(x => x.Id == "scaleway");
        var json = OpenCodeCommand.BuildInlineConfig(scaleway, "glm-5.2");
        using var document = JsonDocument.Parse(json);

        var provider = document.RootElement.GetProperty("provider").GetProperty("scaleway");
        provider.GetProperty("options").GetProperty("baseURL").GetString()
            .Should().Be("https://api.scaleway.ai/v1");
        provider.GetProperty("options").GetProperty("apiKey").GetString()
            .Should().Be("{env:PKS_SCALEWAY_API_KEY}");
        provider.GetProperty("models").TryGetProperty("glm-5.2", out _).Should().BeTrue();
    }

    [Fact]
    public void BuildStartInfo_selects_provider_model_and_passes_native_arguments()
    {
        var moonshot = OpenCodeCommand.Providers.Single(x => x.Id == "moonshot");
        var startInfo = OpenCodeCommand.BuildStartInfo(
            moonshot,
            "kimi-k3",
            "secret-value",
            ["--continue"]);

        startInfo.FileName.Should().Be("opencode");
        startInfo.ArgumentList.Should().Equal("--model", "moonshot/kimi-k3", "--continue");
        startInfo.Environment["MOONSHOT_API_KEY"].Should().Be("secret-value");
        startInfo.Environment["OPENCODE_CONFIG_CONTENT"].Should().Contain("kimi-k3");
        startInfo.Environment["OPENCODE_CONFIG_CONTENT"].Should().NotContain("secret-value");
        startInfo.ArgumentList.Should().NotContain(a => a.Contains("secret-value"));
    }

    [Fact]
    public async Task ExecuteAsync_without_matching_provider_auth_explains_how_to_authenticate()
    {
        var scaleway = new Mock<IScalewayService>();
        scaleway.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(false);
        var moonshot = new Mock<IMoonshotService>();
        moonshot.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(false);
        var console = new TestConsole();
        var command = new OpenCodeCommand(scaleway.Object, moonshot.Object, console);

        var exitCode = await command.ExecuteAsync(null!, new OpenCodeSettings { Model = "kimi-k3" });

        exitCode.Should().Be(1);
        console.Output.Should().Contain("pks moonshot init");
    }
}
