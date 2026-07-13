using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Agent;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure.Services.Agent.Chat;

/// <summary>
/// Unit tests for <see cref="AgentChatProviderFactory.ListAvailableModelsAsync"/> — the enumeration
/// of model ids the Runner can actually serve (used to populate a web UI /model picker).
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AgentChatProviderFactoryModelListTests
{
    private static AgentChatProviderFactory CreateFactory(
        Dictionary<string, string>? config = null,
        IAzureFoundryAuthService? foundryAuth = null)
    {
        var store = config ?? new Dictionary<string, string>();
        var configMock = new Mock<IConfigurationService>();
        configMock.Setup(x => x.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => store.TryGetValue(key, out var v) ? v : null);
        configMock.Setup(x => x.GetAllAsync())
            .ReturnsAsync(() => new Dictionary<string, string>(store));
        return new AgentChatProviderFactory(configMock.Object, new HttpClient(), foundryAuth);
    }

    private static IAzureFoundryAuthService FoundryWith(string? selectedResourceEndpoint, params string[] enabledModels)
    {
        var creds = new FoundryStoredCredentials
        {
            SelectedResourceEndpoint = selectedResourceEndpoint ?? string.Empty,
            EnabledModels = enabledModels.ToList(),
        };
        var mock = new Mock<IAzureFoundryAuthService>();
        mock.Setup(x => x.GetStoredCredentialsAsync()).ReturnsAsync(creds);
        return mock.Object;
    }

    private static IDisposable SetEnv(string name, string? value)
    {
        var original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return new EnvRestore(name, original);
    }

    private sealed class EnvRestore : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;
        public EnvRestore(string name, string? original) { _name = name; _original = original; }
        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }

    [Fact]
    public async Task BuiltIns_NoConfig_NoFoundry_NoEnv_ReturnsEmpty()
    {
        // gpt-5.5 has no default endpoint; the claude built-ins have no apiKey/env/Foundry route —
        // so nothing resolves right now.
        using var _ = SetEnv("ANTHROPIC_API_KEY", null);
        var factory = CreateFactory();

        var models = await factory.ListAvailableModelsAsync();

        models.Should().BeEmpty();
    }

    [Fact]
    public async Task ClaudeBuiltIn_WithConfiguredApiKey_Appears()
    {
        using var _ = SetEnv("ANTHROPIC_API_KEY", null);
        var factory = CreateFactory(new Dictionary<string, string>
        {
            ["agent.models.claude-opus-4-7.apiKey"] = "sk-test",
        });

        var models = await factory.ListAvailableModelsAsync();

        models.Should().Contain("claude-opus-4-7");
        // The other claude built-in has no key configured, so it stays hidden.
        models.Should().NotContain("claude-sonnet-4-6");
        models.Should().NotContain("gpt-5.5");
    }

    [Fact]
    public async Task ClaudeBuiltIns_WithAnthropicEnvVar_Appear()
    {
        using var _ = SetEnv("ANTHROPIC_API_KEY", "sk-env");
        var factory = CreateFactory();

        var models = await factory.ListAvailableModelsAsync();

        models.Should().Contain("claude-opus-4-7");
        models.Should().Contain("claude-sonnet-4-6");
        // gpt-5.5 still needs an azure endpoint we don't have.
        models.Should().NotContain("gpt-5.5");
    }

    [Fact]
    public async Task CustomAzureModel_Configured_Appears()
    {
        using var _ = SetEnv("ANTHROPIC_API_KEY", null);
        var factory = CreateFactory(new Dictionary<string, string>
        {
            ["agent.models.my-azure.provider"] = "azure-openai",
            ["agent.models.my-azure.endpoint"] = "https://my.openai.azure.com",
            ["agent.models.my-azure.apiKey"] = "k",
        });

        var models = await factory.ListAvailableModelsAsync();

        models.Should().Contain("my-azure");
    }

    [Fact]
    public async Task FoundrySelectedEndpoint_MakesBuiltInAzureModelResolve()
    {
        // gpt-5.5 (built-in azure-openai, no default endpoint) only resolves once Foundry supplies
        // a SelectedResourceEndpoint. Listing it as an EnabledModel too must not duplicate it.
        using var _ = SetEnv("ANTHROPIC_API_KEY", null);
        var foundry = FoundryWith("https://myfoundry.openai.azure.com", "gpt-5.5");
        var factory = CreateFactory(foundryAuth: foundry);

        var models = await factory.ListAvailableModelsAsync();

        models.Should().Contain("gpt-5.5");
        models.Count(m => string.Equals(m, "gpt-5.5", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
    }

    [Fact]
    public async Task WithoutFoundryEndpoint_BuiltInAzureModelStaysHidden()
    {
        using var _ = SetEnv("ANTHROPIC_API_KEY", null);
        var foundry = FoundryWith(selectedResourceEndpoint: null);
        var factory = CreateFactory(foundryAuth: foundry);

        var models = await factory.ListAvailableModelsAsync();

        models.Should().NotContain("gpt-5.5");
    }

    [Fact]
    public async Task DuplicateIdsAcrossSources_AreDedupedCaseInsensitively()
    {
        using var _ = SetEnv("ANTHROPIC_API_KEY", null);
        // gpt-5.5 arrives from three sources: built-in, an explicit config entry, and Foundry
        // EnabledModels (including a case variant) — it must appear exactly once.
        var foundry = FoundryWith("https://myfoundry.openai.azure.com", "gpt-5.5", "GPT-5.5");
        var factory = CreateFactory(
            new Dictionary<string, string>
            {
                ["agent.models.gpt-5.5.provider"] = "azure-openai",
                ["agent.models.gpt-5.5.endpoint"] = "https://explicit.openai.azure.com",
            },
            foundry);

        var models = await factory.ListAvailableModelsAsync();

        models.Count(m => string.Equals(m, "gpt-5.5", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
    }

    [Fact]
    public async Task FoundryEnabledModel_ThatCannotResolve_IsFilteredOut()
    {
        // An EnabledModel that is neither a built-in nor configured has no provider/endpoint to
        // resolve against, so CanResolveAsync drops it — only the resolvable gpt-5.5 survives.
        using var _ = SetEnv("ANTHROPIC_API_KEY", null);
        var foundry = FoundryWith("https://myfoundry.openai.azure.com", "gpt-5.5", "some-unknown-deployment");
        var factory = CreateFactory(foundryAuth: foundry);

        var models = await factory.ListAvailableModelsAsync();

        models.Should().Contain("gpt-5.5");
        models.Should().NotContain("some-unknown-deployment");
    }
}
