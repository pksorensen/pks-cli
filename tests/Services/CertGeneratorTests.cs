using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using PKS.Infrastructure.Services;
using Xunit;

namespace PKS.CLI.Tests.Services;

/// <summary>Tests for self-signed code-signing certificate generation (pure, no I/O).</summary>
public class CertGeneratorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void CreateSelfSigned_HasCodeSigningEku()
    {
        var gen = CertGenerator.CreateSelfSigned("CN=Agentic Live (Self-Signed)", TimeSpan.FromDays(365), "pw");

        using var cert = X509CertificateLoader.LoadPkcs12(gen.Pkcs12, "pw");
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>()
            .Select(o => o.Value).Should().Contain(CertGenerator.CodeSigningEku);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void CreateSelfSigned_KeyUsageIsDigitalSignature_AndNotCa()
    {
        var gen = CertGenerator.CreateSelfSigned("CN=Test", TimeSpan.FromDays(30), "pw");
        using var cert = X509CertificateLoader.LoadPkcs12(gen.Pkcs12, "pw");

        var ku = cert.Extensions.OfType<X509KeyUsageExtension>().Single();
        ku.KeyUsages.Should().HaveFlag(X509KeyUsageFlags.DigitalSignature);

        var bc = cert.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        bc.CertificateAuthority.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void CreateSelfSigned_RespectsValidityWindow_AndHasThumbprint()
    {
        var gen = CertGenerator.CreateSelfSigned("CN=Test", TimeSpan.FromDays(365), "pw");

        gen.Thumbprint.Should().NotBeNullOrEmpty().And.HaveLength(40); // SHA-1 hex
        (gen.NotAfter - gen.NotBefore).TotalDays.Should().BeApproximately(365, 1);
        gen.Subject.Should().Contain("CN=Test");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void CreateSelfSigned_Pkcs12RoundTrips_WithPrivateKey()
    {
        var gen = CertGenerator.CreateSelfSigned("CN=Test", TimeSpan.FromDays(30), "secret");
        using var cert = X509CertificateLoader.LoadPkcs12(gen.Pkcs12, "secret");
        cert.HasPrivateKey.Should().BeTrue();
        cert.Thumbprint.Should().Be(gen.Thumbprint);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void CreateSelfSigned_PublicPem_HasNoPrivateKey()
    {
        var gen = CertGenerator.CreateSelfSigned("CN=Test", TimeSpan.FromDays(30), "pw");
        using var cert = X509Certificate2.CreateFromPem(gen.PublicCertPem);
        cert.HasPrivateKey.Should().BeFalse();
        cert.Thumbprint.Should().Be(gen.Thumbprint);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void CreateSelfSigned_EmptySubject_Throws()
    {
        var act = () => CertGenerator.CreateSelfSigned("  ", TimeSpan.FromDays(30), "pw");
        act.Should().Throw<ArgumentException>();
    }
}
