using System;
using System.Text;
using FluentAssertions;
using PKS.Infrastructure.Services.Entra;
using PKS.CLI.Tests.Infrastructure;
using Xunit;

namespace PKS.CLI.Tests.Services.Entra;

/// <summary>
/// The tenant this resolves becomes the OIDC issuer on a box whose only front door is oauth2-proxy.
/// Getting it wrong does not look like a bug: the Entra sign-in succeeds and the callback then
/// rejects the token, which reads as a broken machine rather than a wrong URL.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class EntraTenantFromTokenTests
{
    private static string Jwt(string payloadJson)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{B64("{\"alg\":\"RS256\"}")}.{B64(payloadJson)}.signature";
    }

    [Fact]
    public void Reads_the_tid_claim()
    {
        var tid = "72f988bf-86f1-41af-91ab-2d7cd011db47";

        EntraApplicationService.TenantFromToken(Jwt($"{{\"tid\":\"{tid}\",\"upn\":\"a@b.dk\"}}"))
            .Should().Be(tid);
    }

    [Fact]
    public void Rejects_common_rather_than_passing_it_on()
    {
        // This is the actual failure: a Foundry sign-in through the multi-tenant endpoint stores
        // "common", and an issuer of .../common/v2.0 never matches the tenant GUID Entra puts in
        // the token it mints. Only a real GUID may reach the issuer URL.
        EntraApplicationService.TenantFromToken(Jwt("{\"tid\":\"common\"}")).Should().BeNull();
        EntraApplicationService.TenantFromToken(Jwt("{\"tid\":\"organizations\"}")).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.!!!not-base64!!!.c")]
    public void Never_throws_on_a_token_it_cannot_read(string token)
    {
        EntraApplicationService.TenantFromToken(token).Should().BeNull();
    }

    [Fact]
    public void Handles_payloads_of_every_padding_length()
    {
        // base64url drops the '=' padding, so a payload whose length mod 4 differs decodes only if
        // the padding is restored. A miss here throws on some tenants and not others.
        for (var pad = 0; pad < 6; pad++)
        {
            var filler = new string('x', pad);
            EntraApplicationService.TenantFromToken(
                    Jwt($"{{\"pad\":\"{filler}\",\"tid\":\"72f988bf-86f1-41af-91ab-2d7cd011db47\"}}"))
                .Should().Be("72f988bf-86f1-41af-91ab-2d7cd011db47", $"padding case {pad} must decode");
        }
    }
}
