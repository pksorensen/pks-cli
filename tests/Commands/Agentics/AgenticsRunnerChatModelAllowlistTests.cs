using FluentAssertions;
using PKS.Commands.Agentics.Runner;
using Xunit;

namespace PKS.CLI.Tests.Commands.Agentics;

/// <summary>
/// Unit tests for the chat-model allowlist enforcement (Phase 3, docs/remote-runner-targets-plan.md
/// "Chat model exposure is an enforcement gap"): <see cref="AgenticsRunnerStartCommand.IsChatModelAllowed"/>
/// and <see cref="AgenticsRunnerStartCommand.FilterModelsByAllowlist"/>, the two <c>internal static</c>
/// pure functions that gate both the chat.models.request listing and the chat.completion.request
/// resolution path against a persisted <see cref="PKS.Infrastructure.Services.Models.RunnerProfile.ChatModels"/>
/// allowlist.
///
/// These are tested as pure functions rather than through a live WebSocket/AgentChatProviderFactory
/// harness (AgentChatProviderFactory is a sealed class with no interface, and this repo has no
/// WebSocket test server) -- but the production call sites in
/// <see cref="AgenticsRunnerStartCommand.RunChatLlmChannelSessionAsync"/> check
/// <see cref="AgenticsRunnerStartCommand.IsChatModelAllowed"/> BEFORE ever calling
/// ForwardChatCompletionRequestViaProviderAsync (which is what reaches
/// AgentChatProviderFactory.ResolveAsync).
///
/// Testing the pure predicates alone is NOT a guarantee about production behavior -- stubbing the
/// guard out at the call site used to leave this whole suite green. The DecideChatCompletionRoute
/// tests at the bottom pin the WIRING (which branch a request actually takes) by exercising the
/// same seam RunChatLlmChannelSessionAsync switches on.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AgenticsRunnerChatModelAllowlistTests
{
    // ── IsChatModelAllowed ──────────────────────────────────────────────────────────────

    [Fact]
    public void IsChatModelAllowed_NullAllowlist_AllowsAnyModel()
    {
        AgenticsRunnerStartCommand.IsChatModelAllowed("gpt-5.5", null).Should().BeTrue();
        AgenticsRunnerStartCommand.IsChatModelAllowed("claude-opus-4-7", null).Should().BeTrue();
    }

    [Fact]
    public void IsChatModelAllowed_EmptyAllowlist_AllowsAnyModel()
    {
        AgenticsRunnerStartCommand.IsChatModelAllowed("gpt-5.5", new List<string>()).Should().BeTrue();
    }

    [Fact]
    public void IsChatModelAllowed_ModelInAllowlist_ReturnsTrue()
    {
        var allowlist = new List<string> { "gpt-5.5" };
        AgenticsRunnerStartCommand.IsChatModelAllowed("gpt-5.5", allowlist).Should().BeTrue();
    }

    [Fact]
    public void IsChatModelAllowed_ModelNotInAllowlist_ReturnsFalse()
    {
        // The exact required scenario: ChatModels=['gpt-5.5'] configured, a chat.completion.request
        // names a different model -- must be rejected.
        var allowlist = new List<string> { "gpt-5.5" };
        AgenticsRunnerStartCommand.IsChatModelAllowed("claude-opus-4-7", allowlist).Should().BeFalse();
    }

    [Fact]
    public void IsChatModelAllowed_IsCaseInsensitive()
    {
        var allowlist = new List<string> { "gpt-5.5" };
        AgenticsRunnerStartCommand.IsChatModelAllowed("GPT-5.5", allowlist).Should().BeTrue();
    }

    [Fact]
    public void IsChatModelAllowed_NullOrWhitespaceModelId_WithNonEmptyAllowlist_ReturnsFalse()
    {
        var allowlist = new List<string> { "gpt-5.5" };
        AgenticsRunnerStartCommand.IsChatModelAllowed(null, allowlist).Should().BeFalse();
        AgenticsRunnerStartCommand.IsChatModelAllowed("   ", allowlist).Should().BeFalse();
    }

    // ── FilterModelsByAllowlist ─────────────────────────────────────────────────────────

    [Fact]
    public void FilterModelsByAllowlist_NullAllowlist_ReturnsAllModelsUnfiltered()
    {
        var models = new List<string> { "gpt-5.5", "claude-opus-4-7", "claude-sonnet-4-6" };
        var result = AgenticsRunnerStartCommand.FilterModelsByAllowlist(models, null);
        result.Should().BeEquivalentTo(models);
    }

    [Fact]
    public void FilterModelsByAllowlist_WithAllowlist_ChatModelsRequestReturnsOnlyAllowedModel()
    {
        // The exact required scenario: profile with ChatModels=['gpt-5.5'] -> chat.models.request
        // (i.e. the filter applied to whatever the factory resolved) returns only that model, even
        // though the factory itself resolved a broader set.
        var resolvedByFactory = new List<string> { "gpt-5.5", "claude-opus-4-7", "claude-sonnet-4-6" };
        var allowlist = new List<string> { "gpt-5.5" };

        var result = AgenticsRunnerStartCommand.FilterModelsByAllowlist(resolvedByFactory, allowlist);

        result.Should().BeEquivalentTo(new[] { "gpt-5.5" });
    }

    [Fact]
    public void FilterModelsByAllowlist_AllowlistEntryNotInResolvedModels_IsSilentlyDropped()
    {
        // An allowlist entry the factory can no longer actually resolve (e.g. a Foundry model that
        // was later disabled) must not be advertised as available just because it's on the allowlist
        // -- the allowlist can only narrow, never widen, what the factory says is real.
        var resolvedByFactory = new List<string> { "gpt-5.5" };
        var allowlist = new List<string> { "gpt-5.5", "claude-opus-4-7" };

        var result = AgenticsRunnerStartCommand.FilterModelsByAllowlist(resolvedByFactory, allowlist);

        result.Should().BeEquivalentTo(new[] { "gpt-5.5" });
    }

    // ── DecideChatCompletionRoute: the ENFORCEMENT WIRING, not just the truth table ──────
    //
    // These are the tests that actually fail if a future refactor of the
    // chat.completion.request switch case drops or inverts the allowlist guard. Verified by
    // mutation: stubbing the guard (returning Provider unconditionally) turns the two
    // "Rejected" cases below red, whereas the pure IsChatModelAllowed tests above stay green.

    [Fact]
    public void DecideChatCompletionRoute_DisallowedRequestedModel_IsRejected_AndNeverReachesProvider()
    {
        var allowlist = new List<string> { "gpt-5.5" };

        var decision = AgenticsRunnerStartCommand.DecideChatCompletionRoute(
            backendUrl: null, requestedModel: "claude-opus-4-7", defaultModelId: "gpt-5.5", allowlist: allowlist);

        decision.Route.Should().Be(AgenticsRunnerStartCommand.ChatCompletionRoute.Rejected);
        decision.ModelId.Should().Be("claude-opus-4-7");
    }

    [Fact]
    public void DecideChatCompletionRoute_AllowedRequestedModel_RoutesToProvider()
    {
        var allowlist = new List<string> { "gpt-5.5", "claude-opus-4-7" };

        var decision = AgenticsRunnerStartCommand.DecideChatCompletionRoute(
            backendUrl: null, requestedModel: "claude-opus-4-7", defaultModelId: "gpt-5.5", allowlist: allowlist);

        decision.Route.Should().Be(AgenticsRunnerStartCommand.ChatCompletionRoute.Provider);
        decision.ModelId.Should().Be("claude-opus-4-7");
    }

    [Fact]
    public void DecideChatCompletionRoute_NoRequestedModel_FallsBackToDefault_AndIsStillChecked()
    {
        // The runner default is itself subject to the allowlist -- an operator who narrowed
        // ChatModels without updating --chat-llm-model must not get a silent bypass.
        var decision = AgenticsRunnerStartCommand.DecideChatCompletionRoute(
            backendUrl: null, requestedModel: null, defaultModelId: "gpt-5.5",
            allowlist: new List<string> { "claude-opus-4-7" });

        decision.Route.Should().Be(AgenticsRunnerStartCommand.ChatCompletionRoute.Rejected);
        decision.ModelId.Should().Be("gpt-5.5");
    }

    [Fact]
    public void DecideChatCompletionRoute_NoAllowlist_RoutesToProvider()
    {
        var decision = AgenticsRunnerStartCommand.DecideChatCompletionRoute(
            backendUrl: null, requestedModel: "anything-at-all", defaultModelId: "gpt-5.5", allowlist: null);

        decision.Route.Should().Be(AgenticsRunnerStartCommand.ChatCompletionRoute.Provider);
        decision.ModelId.Should().Be("anything-at-all");
    }

    [Fact]
    public void DecideChatCompletionRoute_LiteralBackendUrl_Forwards_WithoutConsultingAllowlist()
    {
        // Literal-forward mode bypasses AgentChatProviderFactory entirely, so the allowlist (which
        // gates provider resolution) does not apply -- documented behavior, pinned so it is not
        // "fixed" into a rejection by accident.
        var decision = AgenticsRunnerStartCommand.DecideChatCompletionRoute(
            backendUrl: "http://localhost:11434/v1", requestedModel: "not-in-allowlist",
            defaultModelId: "gpt-5.5", allowlist: new List<string> { "gpt-5.5" });

        decision.Route.Should().Be(AgenticsRunnerStartCommand.ChatCompletionRoute.Forward);
    }
}
