using FluentAssertions;
using PKS.Infrastructure.Services.Agent.Setup;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// The casing tolerance that lets one client read both adopters.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public sealed class AspJsonNamesTests
{
    [Fact]
    public void ACamelCaseDocument_IsReadRatherThanSilentlyEmptied()
    {
        // Measured output from Home's BFF, whose generated DTOs carry one global naming policy.
        var json = AspJsonNames.Normalize("""
            {"v":1,"tools":["codex"],"loopbackRedirectUris":["http://localhost:51789"],
             "publishesProvider":true,"message":"hi"}
            """);

        json.Should().Contain("loopback_redirect_uris");
        json.Should().Contain("publishes_provider");

        // The whole point: the redirect host survives. A dropped one is not an error, it is the
        // client quietly using 127.0.0.1 against an issuer that registered localhost.
        var doc = System.Text.Json.JsonSerializer.Deserialize<AspServiceDocument>(json)!;
        doc.LoopbackRedirectUris.Should().ContainSingle().Which.Should().Be("http://localhost:51789");
        doc.PublishesProvider.Should().BeTrue();
    }

    [Fact]
    public void TheIndexesRelUriKeys_AreNotMangled()
    {
        // These keys are data, not field names, and a blind rewrite would break discovery for
        // every platform at once.
        const string rel = "https://agentics.dk/rel/agent-setup/v1";

        var json = AspJsonNames.Normalize(
            "{\"services\":{\"" + rel + "\":{\"href\":\"https://x/api/v1/setup\"}}}");

        json.Should().Contain(rel);
    }

    [Fact]
    public void AConformingDocument_IsLeftExactlyAsItCame()
    {
        const string json = """{"v":1,"loopback_redirect_uris":["a"],"publishes_provider":false}""";

        AspJsonNames.Normalize(json).Should().Be(json);
    }

    [Fact]
    public void AnAlreadyPresentSnakeCaseKey_WinsOverItsCamelCaseTwin()
    {
        var json = AspJsonNames.Normalize("""{"publishes_provider":true,"publishesProvider":false}""");

        System.Text.Json.JsonSerializer.Deserialize<AspServiceDocument>(json)!
            .PublishesProvider.Should().BeTrue();
    }

    [Fact]
    public void SomethingThatIsNotJson_IsReturnedUntouched()
    {
        AspJsonNames.Normalize("<html>nope</html>").Should().Be("<html>nope</html>");
    }
}
