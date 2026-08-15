using System.Diagnostics;
using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Exec;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Services.Exec;

/// <summary>
/// The discovery protocol's resolver — the half that turns "this tool needs a chat model" into an
/// environment a child process can start with. FT-010 shipped with no tests at all, and these are the
/// ones that matter: a manifest is parsed from output that has other things in it, a placeholder is
/// expanded against whatever the machine is signed in to, and a credential comes out somewhere a
/// caller cannot read it.
/// </summary>
public class ManifestResolverTests
{
    // ─────────────────────────────────────────────
    //  Shared helpers
    // ─────────────────────────────────────────────

    private static Mock<IAzureFoundryAuthService> FoundrySignedIn(
        string endpoint = "https://test.services.ai.azure.com",
        string? apiKey = "foundry-key-abc",
        string defaultModel = "claude-sonnet-4-6")
    {
        var mock = new Mock<IAzureFoundryAuthService>();
        mock.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(true);
        mock.Setup(x => x.GetStoredCredentialsAsync()).ReturnsAsync(new FoundryStoredCredentials
        {
            TenantId = "test-tenant",
            RefreshToken = SecretValue.From("refresh"),
            SelectedResourceEndpoint = endpoint,
            SelectedResourceName = "test-foundry",
            DefaultModel = defaultModel,
            ApiKey = SecretValue.From(apiKey),
        });
        return mock;
    }

    private static Mock<IAzureFoundryAuthService> FoundrySignedOut()
    {
        var mock = new Mock<IAzureFoundryAuthService>();
        mock.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(false);
        mock.Setup(x => x.GetStoredCredentialsAsync()).ReturnsAsync((FoundryStoredCredentials?)null);
        return mock;
    }

    private static ManifestResolver Resolver(Mock<IAzureFoundryAuthService> auth, TestConsole console)
        => new(auth.Object, new AzureFoundryAuthConfig(), console);

    private static PksManifest OneFoundryCapability(Dictionary<string, string> env, bool required = false)
        => new()
        {
            ManifestVersion = "v1",
            Name = "test",
            Capabilities =
            {
                new PksCapabilityManifest
                {
                    Id = "chat",
                    Required = required,
                    Providers =
                    {
                        new PksProviderManifest
                        {
                            Kind = "foundry",
                            Models = { new PksModelManifest { Role = "default" } },
                            Env = env,
                        },
                    },
                },
            },
        };

    private static ManifestResolveOptions Unattended => new() { NonInteractive = true, AcceptOptional = true };

    // ─────────────────────────────────────────────
    //  Parsing
    // ─────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Parse_ignores_log_lines_written_before_the_manifest()
    {
        var text = "loading config\nwarning: no cache\n{\"manifestVersion\":\"v1\",\"name\":\"photographer\"}";

        var manifest = PksManifest.Parse(text);

        manifest.Name.Should().Be("photographer");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Parse_cannot_survive_a_line_written_after_the_manifest()
    {
        // The known limitation, pinned so nobody assumes otherwise: everything from the
        // first brace on goes to the JSON reader. It is why the Aspire side writes to a
        // file — a build talks on both sides of the document.
        var text = "{\"manifestVersion\":\"v1\"}\nDone in 1.2s\n";

        var parse = () => PksManifest.Parse(text);

        parse.Should().Throw<Exception>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Parse_refuses_a_version_this_pks_does_not_speak()
    {
        var parse = () => PksManifest.Parse("{\"manifestVersion\":\"v2\"}");

        parse.Should().Throw<InvalidOperationException>().WithMessage("*v2*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Parse_refuses_output_with_no_document_in_it()
    {
        var parse = () => PksManifest.Parse("command not found\n");

        parse.Should().Throw<InvalidOperationException>();
    }

    // ─────────────────────────────────────────────
    //  Placeholder expansion
    // ─────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Endpoint_openai_lands_on_the_url_an_openai_client_can_be_pointed_at()
    {
        var console = new TestConsole();
        var resolver = Resolver(FoundrySignedIn(), console);

        var resolved = await resolver.ResolveAsync(
            OneFoundryCapability(new() { ["BASE_URL"] = "{endpoint:openai}" }),
            Unattended);

        // Foundry's resource endpoint is the root of several APIs; the OpenAI-compatible
        // one hangs off /openai/v1, and a base URL without it 404s on the first call.
        resolved!.Describe().Should().ContainEquivalentOf(
            ("BASE_URL", "https://test.services.ai.azure.com/openai/v1"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Endpoint_openai_does_not_append_the_suffix_twice()
    {
        var console = new TestConsole();
        var resolver = Resolver(FoundrySignedIn(endpoint: "https://test.services.ai.azure.com/openai/v1"), console);

        var resolved = await resolver.ResolveAsync(
            OneFoundryCapability(new() { ["BASE_URL"] = "{endpoint:openai}" }),
            Unattended);

        resolved!.Describe().Should().ContainEquivalentOf(
            ("BASE_URL", "https://test.services.ai.azure.com/openai/v1"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_model_role_resolves_to_the_signed_in_default_when_nobody_is_asked()
    {
        var console = new TestConsole();
        var resolver = Resolver(FoundrySignedIn(defaultModel: "gpt-5-mini"), console);

        var resolved = await resolver.ResolveAsync(
            OneFoundryCapability(new() { ["MODEL"] = "{model:default}" }),
            Unattended);

        resolved!.Describe().Should().ContainEquivalentOf(("MODEL", "gpt-5-mini"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_placeholder_nothing_answers_becomes_empty_rather_than_the_literal_text()
    {
        var console = new TestConsole();
        var resolver = Resolver(FoundrySignedIn(), console);

        var resolved = await resolver.ResolveAsync(
            OneFoundryCapability(new() { ["WAT"] = "{nonsense}" }),
            Unattended);

        // A literal `{nonsense}` arriving in a child's environment would be read as a
        // configured value and fail somewhere far away from here.
        resolved!.Describe().Should().ContainEquivalentOf(("WAT", ""));
    }

    // ─────────────────────────────────────────────
    //  Credentials
    // ─────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_api_key_reaches_the_child_process_but_never_the_caller()
    {
        var console = new TestConsole();
        var resolver = Resolver(FoundrySignedIn(apiKey: "foundry-key-abc"), console);

        var resolved = await resolver.ResolveAsync(
            OneFoundryCapability(new() { ["API_KEY"] = "{apikey}", ["MODEL"] = "{model:default}" }),
            Unattended);

        // What a caller can see: that it is set, and nothing more.
        resolved!.Describe().Should().ContainEquivalentOf(("API_KEY", "(set, hidden)"));
        console.Output.Should().NotContain("foundry-key-abc");

        // What the child gets: the credential itself.
        var startInfo = new ProcessStartInfo();
        resolved.ApplyTo(startInfo);
        startInfo.Environment["API_KEY"].Should().Be("foundry-key-abc");
        startInfo.Environment["MODEL"].Should().Be("claude-sonnet-4-6");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_secret_that_was_never_issued_is_reported_as_missing_rather_than_hidden()
    {
        var console = new TestConsole();
        var resolver = Resolver(FoundrySignedIn(apiKey: null), console);

        var resolved = await resolver.ResolveAsync(
            OneFoundryCapability(new() { ["API_KEY"] = "{apikey}" }),
            Unattended);

        // "(set, hidden)" for something that is not set is how a missing credential turns
        // into an hour of debugging a 401 that was never going to work.
        resolved!.Describe().Should().ContainEquivalentOf(("API_KEY", "(not set)"));

        var startInfo = new ProcessStartInfo();
        resolved.ApplyTo(startInfo);
        startInfo.Environment.Should().NotContainKey("API_KEY");
    }

    // ─────────────────────────────────────────────
    //  Nothing to resolve with
    // ─────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_optional_capability_with_nothing_behind_it_is_skipped_and_the_run_goes_on()
    {
        var console = new TestConsole();
        var resolver = Resolver(FoundrySignedOut(), console);

        var resolved = await resolver.ResolveAsync(
            OneFoundryCapability(new() { ["API_KEY"] = "{apikey}" }, required: false),
            Unattended);

        // Margin's model is behind a flag for exactly this reason: no model configured is
        // a state the screen is built for, not a failure to start.
        resolved.Should().NotBeNull();
        resolved!.Count.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_required_capability_with_nothing_behind_it_stops_the_run()
    {
        var console = new TestConsole();
        var resolver = Resolver(FoundrySignedOut(), console);

        var resolved = await resolver.ResolveAsync(
            OneFoundryCapability(new() { ["API_KEY"] = "{apikey}" }, required: true),
            Unattended);

        resolved.Should().BeNull();
    }

    // ─────────────────────────────────────────────
    //  The environment itself
    // ─────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void A_later_capability_overrides_an_earlier_one()
    {
        var first = new ResolvedEnvironment();
        first.Set("BASE_URL", "http://first/v1", secret: false);

        var second = new ResolvedEnvironment();
        second.Set("BASE_URL", "http://second/v1", secret: false);

        first.MergeFrom(second);

        first.Describe().Should().ContainEquivalentOf(("BASE_URL", "http://second/v1"));
    }
}
