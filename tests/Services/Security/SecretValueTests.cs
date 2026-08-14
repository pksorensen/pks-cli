using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services.Security;

/// <summary>
/// <see cref="SecretValue"/> is the half of the quarantine the gate test cannot express: the gate
/// stops a command from *asking* for plaintext, this stops it from getting plaintext by accident. The
/// tests below are the properties the rest of the design leans on — no implicit string conversion, a
/// masked <c>ToString</c>, masked serialization by default, and a mask that never round-trips back
/// into a credential.
/// </summary>
public class SecretValueTests
{
    private sealed class Holder
    {
        public string Tenant { get; set; } = "contoso";
        public SecretValue RefreshToken { get; set; }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void NullAndEmpty_CollapseToAbsent()
    {
        // "Present but empty" would give every presence check two answers, and the call sites this
        // replaces were split between `== null` and `IsNullOrEmpty` on exactly that ambiguity.
        SecretValue.From(null).HasValue.Should().BeFalse();
        SecretValue.From("").HasValue.Should().BeFalse();
        SecretValue.From("rt_live").HasValue.Should().BeTrue();
        default(SecretValue).Should().Be(SecretValue.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ToString_NeverYieldsTheCredential()
    {
        // The failure this whole exercise exists to stop is a token reaching a transcript, and string
        // interpolation is how it would get there.
        $"token: {SecretValue.From("rt_live_secret")}".Should().NotContain("rt_live_secret");
        SecretValue.From("rt_live_secret").ToString().Should().Be("***");
        SecretValue.None.ToString().Should().Be("(none)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void HasNoImplicitConversionToString()
    {
        // Load-bearing: with an implicit conversion, every one of the ~60 call sites this type was
        // introduced to surface would have kept compiling and started masking silently instead.
        typeof(SecretValue).GetMethods()
            .Where(m => m.Name is "op_Implicit" or "op_Explicit")
            .Should().BeEmpty("a credential must not be convertible to a string by the compiler");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void GetHashCode_DoesNotDependOnTheCredential()
    {
        // A hash of the plaintext in a dictionary dump or a diagnostic listing is an offline oracle
        // against any guessable token.
        SecretValue.From("one").GetHashCode().Should().Be(SecretValue.From("two").GetHashCode());
        SecretValue.None.GetHashCode().Should().NotBe(SecretValue.From("one").GetHashCode());
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Equality_ComparesCredentialsWithoutRevealingThem()
    {
        // The ADO proxy needs exactly this: "did the token endpoint hand me a new refresh token?"
        SecretValue.From("rt").Should().Be(SecretValue.From("rt"));
        (SecretValue.From("rt") != SecretValue.From("rt2")).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void DefaultSerialization_Masks()
    {
        var json = JsonSerializer.Serialize(new Holder { RefreshToken = SecretValue.From("rt_live_secret") });

        json.Should().NotContain("rt_live_secret");
        json.Should().Contain("***");
        json.Should().Contain("contoso", "masking a credential must not blank out the metadata beside it");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void PersistenceSerialization_KeepsTheCredential()
    {
        var holder = new Holder { RefreshToken = SecretValue.From("rt_live_secret") };

        var json = JsonSerializer.Serialize(holder, SecretJson.Persistence);
        json.Should().Contain("rt_live_secret");

        var restored = JsonSerializer.Deserialize<Holder>(json, SecretJson.Persistence)!;
        restored.RefreshToken.Should().Be(SecretValue.From("rt_live_secret"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ForPersistence_PreservesTheServicesOwnOptions()
    {
        // Auth services each carry their own naming policy; persistence must add a converter, not
        // replace the options and silently change every property name in the stored payload.
        var basedOn = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        var json = JsonSerializer.Serialize(
            new Holder { RefreshToken = SecretValue.From("rt_live_secret") },
            SecretJson.ForPersistence(basedOn));

        json.Should().Contain("refresh_token").And.Contain("rt_live_secret");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void AMaskedCredentialNeverRoundTripsBackIntoOne()
    {
        // If a masked DTO is ever persisted by mistake, the credential must come back absent so the
        // user is told to log in again. The alternative — a token that is present and equal to "***" —
        // is the `***encrypted***` sentinel bug the migration already had to clean up once.
        var masked = JsonSerializer.Serialize(new Holder { RefreshToken = SecretValue.From("rt_live_secret") });

        JsonSerializer.Deserialize<Holder>(masked)!.RefreshToken.HasValue.Should().BeFalse();
        JsonSerializer.Deserialize<Holder>(masked, SecretJson.Persistence)!.RefreshToken.HasValue.Should().BeFalse();
    }
}

/// <summary>
/// The sinks exist so a command can get a credential to where it is needed without naming
/// <c>Reveal</c>. Their contract is "absent means untouched" — an empty <c>Authorization</c> header or
/// a blank environment variable turns a missing credential into a 401 that reads like a broken one.
/// </summary>
public class SecretSinkTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void EnvironmentVariable_IsSetWhenPresentAndUntouchedWhenAbsent()
    {
        var env = new Dictionary<string, string>();

        SecretSink.SetEnvironmentVariable(env, "ANTHROPIC_FOUNDRY_API_KEY", SecretValue.From("ak_live")).Should().BeTrue();
        env["ANTHROPIC_FOUNDRY_API_KEY"].Should().Be("ak_live");

        SecretSink.SetEnvironmentVariable(env, "HEYPOUL_API_KEY", SecretValue.None).Should().BeFalse();
        env.Should().NotContainKey("HEYPOUL_API_KEY");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ProcessStartInfo_GetsTheCredentialInItsEnvironment()
    {
        var psi = new ProcessStartInfo("heypoul");

        SecretSink.SetEnvironmentVariable(psi, "HEYPOUL_API_KEY", SecretValue.From("ak_live")).Should().BeTrue();

        psi.Environment["HEYPOUL_API_KEY"].Should().Be("ak_live");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void DockerEnvArgument_QuotesTheCredential()
    {
        // This one goes through a remote shell. Unquoted, a credential containing a space splits into
        // extra docker arguments and one containing `$` gets expanded — both leave fragments behind.
        var line = new StringBuilder();

        SecretSink.AppendDockerEnvArgument(line, "TOKEN", SecretValue.From("a b$c'd"));

        line.ToString().Should().Be("-e TOKEN='a b$c'\\''d' ");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BearerToken_IsOmittedEntirelyWhenAbsent()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid");

        SecretSink.SetBearerToken(request, SecretValue.None).Should().BeFalse();
        request.Headers.Authorization.Should().BeNull("an empty Authorization header reads as a broken credential, not a missing one");

        SecretSink.SetBearerToken(request, SecretValue.From("at_live")).Should().BeTrue();
        request.Headers.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "at_live"));
    }
}
