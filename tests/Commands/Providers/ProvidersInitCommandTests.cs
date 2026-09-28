using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.Commands.Foundry;
using PKS.Commands.Providers;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Providers;
using PKS.Infrastructure.Services.Security;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Providers;

/// <summary>
/// `pks providers init` is run again and again on the same host — by hand to open one more provider,
/// and by the self-host installer on every re-run. These pin that a second run changes nothing it
/// does not have to, and that the unattended path never prompts.
/// </summary>
public class ProvidersInitCommandTests
{
    private static Mock<IActionGuard> Guard()
    {
        var guard = new Mock<IActionGuard>();
        guard.Setup(g => g.RequireAsync(It.IsAny<ActionRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return guard;
    }

    // ── DirectApiKeyStep (Anthropic / OpenAI) ──────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Unattended_SameKeyAsStored_IsUnchanged_AndNeitherValidatesNorWrites()
    {
        var keys = new Mock<IProviderApiKeyService>();
        keys.Setup(k => k.IsStoredKeyAsync(ApiKeyProvider.Anthropic, "sk-ant-1")).ReturnsAsync(true);
        var step = new DirectApiKeyStep(ApiKeyProvider.Anthropic, keys.Object, Guard().Object, new TestConsole());

        var outcome = await step.ApplyUnattendedAsync("sk-ant-1", force: false);

        outcome.Should().Be(StepOutcome.Unchanged);
        keys.Verify(k => k.ValidateAsync(It.IsAny<ApiKeyProvider>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        keys.Verify(k => k.StoreAsync(It.IsAny<ApiKeyProvider>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Unattended_NewValidKey_IsStored()
    {
        var keys = new Mock<IProviderApiKeyService>();
        keys.Setup(k => k.IsStoredKeyAsync(ApiKeyProvider.OpenAI, It.IsAny<string>())).ReturnsAsync(false);
        keys.Setup(k => k.ValidateAsync(ApiKeyProvider.OpenAI, "sk-new", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyCheck(ApiKeyVerdict.Valid, 200, null));
        var step = new DirectApiKeyStep(ApiKeyProvider.OpenAI, keys.Object, Guard().Object, new TestConsole());

        var outcome = await step.ApplyUnattendedAsync("  sk-new ", force: false);

        outcome.Should().Be(StepOutcome.Stored);
        keys.Verify(k => k.StoreAsync(ApiKeyProvider.OpenAI, "sk-new"), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Unattended_RejectedKey_IsNeverStored()
    {
        var keys = new Mock<IProviderApiKeyService>();
        keys.Setup(k => k.ValidateAsync(ApiKeyProvider.Anthropic, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyCheck(ApiKeyVerdict.Rejected, 401, "refused"));
        var step = new DirectApiKeyStep(ApiKeyProvider.Anthropic, keys.Object, Guard().Object, new TestConsole());

        var outcome = await step.ApplyUnattendedAsync("sk-bad", force: false);

        outcome.Should().Be(StepOutcome.Failed);
        keys.Verify(k => k.StoreAsync(It.IsAny<ApiKeyProvider>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Unattended_InconclusiveValidation_StoresButSaysSo()
    {
        var keys = new Mock<IProviderApiKeyService>();
        keys.Setup(k => k.ValidateAsync(ApiKeyProvider.Anthropic, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyCheck(ApiKeyVerdict.Inconclusive, 503, "HTTP 503"));
        var step = new DirectApiKeyStep(ApiKeyProvider.Anthropic, keys.Object, Guard().Object, new TestConsole());

        (await step.ApplyUnattendedAsync("sk-maybe", force: false)).Should().Be(StepOutcome.StoredUnverified);
        keys.Verify(k => k.StoreAsync(ApiKeyProvider.Anthropic, "sk-maybe"), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Unattended_NoValue_IsSkipped()
    {
        var keys = new Mock<IProviderApiKeyService>(MockBehavior.Strict);
        var step = new DirectApiKeyStep(ApiKeyProvider.Anthropic, keys.Object, Guard().Object, new TestConsole());

        (await step.ApplyUnattendedAsync(null, force: false)).Should().Be(StepOutcome.Skipped);
    }

    // ── The wizard loop ────────────────────────────────────────────────────

    private sealed class FakeStep : IProviderStep
    {
        public FakeStep(string id, bool configured) { Id = id; Configured = configured; }
        public string Id { get; }
        public bool Configured { get; set; }
        public int InteractiveRuns { get; private set; }
        public string DisplayName => Id;
        public string Reach => "tests";
        public string? EnvVar => null;
        public Task<bool> IsConfiguredAsync() => Task.FromResult(Configured);
        public Task<string?> HintAsync() => Task.FromResult<string?>(null);
        public Task<bool> RunInteractiveAsync(bool force) { InteractiveRuns++; Configured = true; return Task.FromResult(true); }
        public Task<StepOutcome> ApplyUnattendedAsync(string? envValue, bool force) => Task.FromResult(StepOutcome.Skipped);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Interactive_RunsOnlyTheTickedProviders()
    {
        var first = new FakeStep("first", configured: false);
        var second = new FakeStep("second", configured: true);
        var third = new FakeStep("third", configured: false);
        var console = new TestConsole().Interactive();
        // ↓ past "first", tick "second" (a configured one: re-setup), ↓, tick "third", enter.
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Spacebar);
        console.Input.PushKey(ConsoleKey.Enter);
        var command = new ProvidersInitCommand(new ProviderStepCatalog(new IProviderStep[] { first, second, third }), console)
        {
            InputRedirected = () => false,
        };

        var result = await command.ExecuteAsync(null!, new ProvidersInitCommand.Settings());

        result.Should().Be(0);
        first.InteractiveRuns.Should().Be(0);
        second.InteractiveRuns.Should().Be(1);
        third.InteractiveRuns.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Interactive_EnterAlone_ChangesNothing()
    {
        var done = new FakeStep("done", configured: true);
        var open = new FakeStep("open", configured: false);
        var console = new TestConsole().Interactive();
        console.Input.PushKey(ConsoleKey.Enter);
        var command = new ProvidersInitCommand(new ProviderStepCatalog(new IProviderStep[] { done, open }), console)
        {
            InputRedirected = () => false,
        };

        (await command.ExecuteAsync(null!, new ProvidersInitCommand.Settings())).Should().Be(0);
        done.InteractiveRuns.Should().Be(0);
        open.InteractiveRuns.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RedirectedStdin_RunsUnattended_EvenOnAnInteractiveConsole()
    {
        // `curl … | bash` hands the script to stdin; a prompt there would eat the rest of it.
        var open = new FakeStep("open", configured: false);
        var command = new ProvidersInitCommand(new ProviderStepCatalog(new IProviderStep[] { open }), new TestConsole().Interactive())
        {
            InputRedirected = () => true,
        };

        (await command.ExecuteAsync(null!, new ProvidersInitCommand.Settings())).Should().Be(0);
        open.InteractiveRuns.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnknownProviderInOnly_Fails_ListingTheKnownOnes()
    {
        var console = new TestConsole();
        var command = new ProvidersInitCommand(new ProviderStepCatalog(new IProviderStep[] { new FakeStep("foundry", false) }), console);

        var result = await command.ExecuteAsync(null!, new ProvidersInitCommand.Settings { Only = "fondry", FromEnv = true });

        result.Should().Be(1);
        console.Output.Should().Contain("fondry").And.Contain("foundry");
    }

    // ── Foundry via managed identity ───────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FoundryInit_ManagedIdentityUnattended_StoresNoTokenAndEnablesEveryDeployment()
    {
        var mi = new Mock<IManagedIdentityClient>();
        mi.Setup(m => m.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mi.Setup(m => m.GetTokenAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(FakeJwt("tenant-42"));

        var stored = new List<FoundryStoredCredentials>();
        var auth = new Mock<IAzureFoundryAuthService>();
        auth.Setup(a => a.IsAuthenticatedAsync()).ReturnsAsync(false);
        auth.Setup(a => a.StoreCredentialsAsync(It.IsAny<FoundryStoredCredentials>()))
            .Callback<FoundryStoredCredentials>(stored.Add).Returns(Task.CompletedTask);
        auth.Setup(a => a.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("arm-token");
        auth.Setup(a => a.ListSubscriptionsAsync("arm-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AzureSubscription> { new() { SubscriptionId = "sub-1", DisplayName = "Sandbox" } });
        auth.Setup(a => a.ListFoundryResourcesAsync("arm-token", "sub-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CognitiveServicesAccount>
            {
                new() { Name = "fabric-ai", Id = "/subscriptions/sub-1/resourceGroups/rg-ai/providers/Microsoft.CognitiveServices/accounts/fabric-ai" },
                new() { Name = "other-ai", Id = "/subscriptions/sub-1/resourceGroups/rg-x/providers/Microsoft.CognitiveServices/accounts/other-ai" },
            });
        auth.Setup(a => a.ListDeploymentsAsync("arm-token", "sub-1", "rg-ai", "fabric-ai", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FoundryDeployment> { new() { Name = "gpt-5.5" }, new() { Name = "claude-sonnet-5" } });

        var console = new TestConsole();
        var command = new FoundryInitCommand(auth.Object, new AzureFoundryAuthConfig(), console, mi.Object);

        var result = await command.RunAsync(new FoundryInitCommand.Settings { ManagedIdentity = true, Yes = true, Resource = "fabric-ai" });

        result.Should().Be(0, console.Output);
        var final = stored.Last();
        final.IsManagedIdentity.Should().BeTrue();
        final.RefreshToken.HasValue.Should().BeFalse("a managed identity leaves nothing long-lived on the box");
        final.ApiKey.HasValue.Should().BeFalse();
        final.TenantId.Should().Be("tenant-42");
        final.SelectedResourceName.Should().Be("fabric-ai");
        final.EnabledModels.Should().Equal("gpt-5.5", "claude-sonnet-5");
        final.DefaultModel.Should().Be("gpt-5.5");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FoundryInit_YesWithoutManagedIdentity_RefusesInsteadOfOpeningABrowser()
    {
        var auth = new Mock<IAzureFoundryAuthService>();
        auth.Setup(a => a.IsAuthenticatedAsync()).ReturnsAsync(false);
        var command = new FoundryInitCommand(auth.Object, new AzureFoundryAuthConfig(), new TestConsole());

        (await command.RunAsync(new FoundryInitCommand.Settings { Yes = true })).Should().Be(1);
        auth.Verify(a => a.InitiateLoginAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FoundryAuth_ManagedIdentityMode_TakesTokensFromImds_NotTheRefreshFlow()
    {
        var creds = new FoundryStoredCredentials { AuthMode = FoundryStoredCredentials.ManagedIdentityMode, ManagedIdentityClientId = "uami-1" };
        var resolver = new Mock<ISecretResolver>();
        resolver.Setup(r => r.RevealAsync("foundry.auth.credentials")).ReturnsAsync(JsonSerializer.Serialize(creds));
        var mi = new Mock<IManagedIdentityClient>();
        mi.Setup(m => m.GetTokenAsync("https://management.azure.com/.default", "uami-1", It.IsAny<CancellationToken>())).ReturnsAsync("mi-token");

        var service = new AzureFoundryAuthService(
            new HttpClient(new ThrowingHandler()),
            new Mock<IConfigurationService>().Object,
            new Mock<ILogger<AzureFoundryAuthService>>().Object,
            resolver.Object,
            new AzureFoundryAuthConfig(),
            mi.Object);

        (await service.IsAuthenticatedAsync()).Should().BeTrue();
        (await service.GetAccessTokenAsync("https://management.azure.com/.default")).Should().Be("mi-token");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("https://management.azure.com/.default", "https://management.azure.com")]
    [InlineData("https://cognitiveservices.azure.com/.default offline_access", "https://cognitiveservices.azure.com")]
    [InlineData("https://ai.azure.com", "https://ai.azure.com")]
    public void ManagedIdentity_ScopeBecomesImdsResource(string scope, string resource) =>
        ManagedIdentityClient.ResourceFromScope(scope).Should().Be(resource);

    [Fact]
    [Trait("Category", "Unit")]
    public void SecretSink_ShellExport_QuotesTheValue()
    {
        var script = new StringBuilder();
        SecretSink.AppendShellExport(script, "ANTHROPIC_API_KEY", SecretValue.From("a'b$c")).Should().BeTrue();
        SecretSink.AppendShellExport(script, "OPENAI_API_KEY", SecretValue.None).Should().BeFalse();

        script.ToString().Should().Be("export ANTHROPIC_API_KEY='a'\\''b$c'" + Environment.NewLine);
    }

    private static string FakeJwt(string tenant)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"tid\":\"{tenant}\"}}")}.sig";
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"unexpected HTTP call to {request.RequestUri}");
    }
}
