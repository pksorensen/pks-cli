using System.Text;
using FluentAssertions;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services.Security;

public class TotpServiceTests
{
    [Theory]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    [InlineData("")]
    [InlineData("f")]
    [InlineData("fo")]
    [InlineData("foo")]
    [InlineData("foobar")]
    [InlineData("The quick brown fox 1234567890")]
    public void Base32_RoundTrips(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var encoded = Base32.Encode(bytes);
        Base32.Decode(encoded).Should().Equal(bytes);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public void Base32_Decode_IgnoresSpacesDashesAndCase()
    {
        var encoded = Base32.Encode(Encoding.ASCII.GetBytes("hello"));
        var noisy = encoded.ToLowerInvariant().Insert(2, " ").Insert(5, "-");
        Base32.Decode(noisy).Should().Equal(Encoding.ASCII.GetBytes("hello"));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public void ComputeCode_MatchesRfc6238Vector()
    {
        // RFC 6238 Appendix B: secret "12345678901234567890" (SHA1), T=59 → step 1 → 8-digit 94287082.
        var secret = Base32.Encode(Encoding.ASCII.GetBytes("12345678901234567890"));
        TotpService.ComputeCode(secret, 1).Should().Be("287082"); // last 6 digits of 94287082
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public void RecoveryCode_HashVerifies_AndRejectsWrong()
    {
        var code = TotpService.GenerateRecoveryCodes(1)[0];
        var (hash, salt) = TotpService.HashRecoveryCode(code);

        TotpService.VerifyRecoveryCode(code, hash, salt).Should().BeTrue();
        TotpService.VerifyRecoveryCode(code.ToLowerInvariant(), hash, salt).Should().BeTrue(); // normalized
        TotpService.VerifyRecoveryCode("AAAAA-BBBBB", hash, salt).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public void GenerateSecret_IsDecodableBase32_20Bytes()
    {
        var secret = TotpService.GenerateSecretBase32();
        Base32.Decode(secret).Should().HaveCount(20);
    }
}
