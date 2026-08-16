using System.Diagnostics;
using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Entra;
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
        => new(auth.Object, new AzureFoundryAuthConfig(), NoEntraApps, console);

    private static ManifestResolver Resolver(
        Mock<IAzureFoundryAuthService> auth,
        IEntraApplicationService entra,
        TestConsole console)
        => new(auth.Object, new AzureFoundryAuthConfig(), entra, console);

    /// <summary>Nothing provisioned — an `entra` provider is then simply not available.</summary>
    private static IEntraApplicationService NoEntraApps => NothingStored().Object;

    /// <summary>The same, kept as the mock, for asserting on what was or was not written.</summary>
    private static Mock<IEntraApplicationService> NothingStored()
    {
        var mock = new Mock<IEntraApplicationService>();
        mock.Setup(x => x.GetStoredAsync(It.IsAny<string>())).ReturnsAsync((EntraStoredApp?)null);
        mock.Setup(x => x.SaveAsync(It.IsAny<EntraManualApp>()))
            .ReturnsAsync((EntraManualApp a) => new EntraStoredApp { Alias = a.Alias, AppId = a.ClientId });
        return mock;
    }

    /// <summary>One app registration, stored under <paramref name="alias"/>.</summary>
    private static IEntraApplicationService EntraApp(
        string alias,
        string appId = "client-id-1",
        string tenantId = "tenant-1",
        string? secret = "client-secret-1")
    {
        var mock = new Mock<IEntraApplicationService>();
        mock.Setup(x => x.GetStoredAsync(It.IsAny<string>())).ReturnsAsync((string a) =>
            a == alias
                ? new EntraStoredApp
                {
                    Alias = alias,
                    DisplayName = alias,
                    AppId = appId,
                    ObjectId = "obj-1",
                    TenantId = tenantId,
                    SecretKeyId = "key-1",
                    SecretExpiresOn = DateTimeOffset.UtcNow.AddDays(90),
                    ClientSecret = SecretValue.From(secret),
                }
                : null);
        return mock.Object;
    }

    private static PksManifest OneEntraCapability(string capabilityId, Dictionary<string, string> env)
        => new()
        {
            ManifestVersion = "v1",
            Name = "test",
            Capabilities =
            {
                new PksCapabilityManifest
                {
                    Id = capabilityId,
                    Providers = { new PksProviderManifest { Kind = "entra", Env = env } },
                },
            },
        };

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

    // ─────────────────────────────────────────────
    //  Entra app registrations
    // ─────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_entra_capability_fills_the_three_parameters_a_sign_in_needs()
    {
        var console = new TestConsole();
        var resolver = Resolver(FoundrySignedOut(), EntraApp("margin-dev"), console);

        var resolved = await resolver.ResolveAsync(
            OneEntraCapability("margin-dev", new()
            {
                ["Parameters__entra-tenant-id"] = "{entra:tenantid}",
                ["Parameters__entra-client-id"] = "{entra:clientid}",
                ["Parameters__entra-client-secret"] = "{entra:clientsecret}",
            }),
            Unattended);

        resolved.Should().NotBeNull();
        resolved!.Describe().Should().ContainEquivalentOf(("Parameters__entra-client-id", "client-id-1"));
        resolved.Describe().Should().ContainEquivalentOf(("Parameters__entra-tenant-id", "tenant-1"));

        // The secret went in, and describing it says only that.
        resolved.Contains("Parameters__entra-client-secret").Should().BeTrue();
        resolved.Describe().Should().ContainEquivalentOf(("Parameters__entra-client-secret", "(set, hidden)"));

        var start = new ProcessStartInfo();
        resolved.ApplyTo(start);
        start.Environment["Parameters__entra-client-secret"].Should().Be("client-secret-1");

        console.Output.Should().NotContain("client-secret-1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task The_alias_defaults_to_the_capability_name_and_a_binding_can_override_it()
    {
        // The default is what keeps an AppHost from naming the alias twice; the override is what lets
        // one composition bind two registrations — a dev one and a customer's.
        var console = new TestConsole();
        var resolver = Resolver(FoundrySignedOut(), EntraApp("other-app", appId: "client-id-2"), console);

        var resolved = await resolver.ResolveAsync(
            OneEntraCapability("other-app", new()
            {
                ["BY_DEFAULT"] = "{entra:clientid}",
                ["BY_NAME"] = "{entra:clientid:other-app}",
                ["SOMEBODY_ELSE"] = "{entra:clientid:not-provisioned}",
            }),
            Unattended);

        resolved!.Describe().Should().ContainEquivalentOf(("BY_DEFAULT", "client-id-2"));
        resolved.Describe().Should().ContainEquivalentOf(("BY_NAME", "client-id-2"));
        resolved.Describe().Should().ContainEquivalentOf(("SOMEBODY_ELSE", ""));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_entra_capability_with_nothing_provisioned_is_skipped_not_guessed()
    {
        // Availability means "already provisioned". Writing an app registration into a company
        // directory is not something a run gets to decide on its way past.
        var console = new TestConsole();
        var resolver = Resolver(FoundrySignedOut(), NoEntraApps, console);

        var resolved = await resolver.ResolveAsync(
            OneEntraCapability("margin-dev", new() { ["Parameters__entra-client-id"] = "{entra:clientid}" }),
            Unattended);

        resolved.Should().NotBeNull();
        resolved!.Count.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_app_registration_can_be_typed_in_for_one_run_and_kept_nowhere()
    {
        // The answer to "I have the credential, I just do not want it on my disk yet". Aspire would ask
        // too, but its dialog offers to save into user secrets — plaintext under the project. Here the
        // default is no, and no means nothing is written at all.
        var console = new TestConsole();
        console.Interactive();
        console.Input.PushTextWithEnter("y");             // enter it now?
        console.Input.PushTextWithEnter("tenant-typed");
        console.Input.PushTextWithEnter("client-typed");
        console.Input.PushTextWithEnter("secret-typed");
        console.Input.PushTextWithEnter("n");             // keep it for next time?

        var entra = NothingStored();
        var resolver = Resolver(FoundrySignedOut(), entra.Object, console);

        var resolved = await resolver.ResolveAsync(
            OneEntraCapability("margin-dev", new()
            {
                ["Parameters__entra-tenant-id"] = "{entra:tenantid}",
                ["Parameters__entra-client-id"] = "{entra:clientid}",
                ["Parameters__entra-client-secret"] = "{entra:clientsecret}",
            }),
            new ManifestResolveOptions { AcceptOptional = true });

        resolved.Should().NotBeNull();
        resolved!.Describe().Should().ContainEquivalentOf(("Parameters__entra-tenant-id", "tenant-typed"));
        resolved.Describe().Should().ContainEquivalentOf(("Parameters__entra-client-id", "client-typed"));
        resolved.Describe().Should().ContainEquivalentOf(("Parameters__entra-client-secret", "(set, hidden)"));

        var start = new ProcessStartInfo();
        resolved.ApplyTo(start);
        start.Environment["Parameters__entra-client-secret"].Should().Be("secret-typed");

        // The whole point: nothing reached the store, and nothing reached the console.
        entra.Verify(x => x.SaveAsync(It.IsAny<EntraManualApp>()), Times.Never);
        console.Output.Should().NotContain("secret-typed");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Saying_yes_to_keeping_it_stores_it_under_the_capability_alias()
    {
        var console = new TestConsole();
        console.Interactive();
        console.Input.PushTextWithEnter("y");
        console.Input.PushTextWithEnter("tenant-typed");
        console.Input.PushTextWithEnter("client-typed");
        console.Input.PushTextWithEnter("secret-typed");
        console.Input.PushTextWithEnter("y");             // keep it

        var entra = NothingStored();
        var resolver = Resolver(FoundrySignedOut(), entra.Object, console);

        await resolver.ResolveAsync(
            OneEntraCapability("Margin Dev", new() { ["X"] = "{entra:clientid}" }),
            new ManifestResolveOptions { AcceptOptional = true });

        entra.Verify(
            x => x.SaveAsync(It.Is<EntraManualApp>(a =>
                a.Alias == "margin-dev" && a.ClientId == "client-typed" && a.TenantId == "tenant-typed")),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_unattended_run_is_never_asked_for_an_app_registration()
    {
        // A CI job has nobody to answer, and a prompt there is a hang rather than a question.
        var console = new TestConsole();
        var entra = NothingStored();
        var resolver = Resolver(FoundrySignedOut(), entra.Object, console);

        var resolved = await resolver.ResolveAsync(
            OneEntraCapability("margin-dev", new() { ["X"] = "{entra:clientid}" }),
            Unattended);

        resolved!.Count.Should().Be(0);
        entra.Verify(x => x.SaveAsync(It.IsAny<EntraManualApp>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_run_with_a_pipe_for_stdin_skips_rather_than_throws()
    {
        // Without --non-interactive but with nobody at the keyboard, Spectre's answer to a prompt is to
        // throw — which would turn "this capability was skipped" into "the run failed". The terminal is
        // asked before the question is.
        var console = new TestConsole();     // not .Interactive()
        var entra = NothingStored();
        var resolver = Resolver(FoundrySignedOut(), entra.Object, console);

        var resolved = await resolver.ResolveAsync(
            OneEntraCapability("margin-dev", new() { ["X"] = "{entra:clientid}" }),
            new ManifestResolveOptions { AcceptOptional = true });

        resolved.Should().NotBeNull();
        resolved!.Count.Should().Be(0);
    }
}
