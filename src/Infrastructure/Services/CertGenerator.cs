using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Pure, I/O-free generation of self-signed code-signing certificates. Produces a PKCS#12 blob
/// (cert + private key, encrypted with an ephemeral password) plus the public certificate PEM.
/// The PKCS#12 is what <see cref="CertStore"/> encrypts at rest; the public PEM is exported as a
/// <c>.cer</c> so consumers can trust the (stable) signing cert once.
/// </summary>
public static class CertGenerator
{
    /// <summary>Code Signing EKU OID — what makes a cert valid for Authenticode/MSIX signing.</summary>
    public const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";

    public sealed record GeneratedCert(
        byte[] Pkcs12,
        string PublicCertPem,
        string Thumbprint,
        string Subject,
        DateTime NotBefore,
        DateTime NotAfter);

    /// <summary>
    /// Generate a self-signed code-signing certificate.
    /// </summary>
    /// <param name="subject">X.500 subject, e.g. <c>CN=Agentic Live (Self-Signed)</c>. For MSIX this
    /// MUST equal the <c>Publisher</c> in the appxmanifest or the package will not install.</param>
    /// <param name="validity">How long the cert is valid for (from ~now).</param>
    /// <param name="pkcs12Password">Ephemeral password used to encrypt the exported PKCS#12. The
    /// store re-encrypts the blob with its KEK; this password is never persisted in cleartext.</param>
    public static GeneratedCert CreateSelfSigned(string subject, TimeSpan validity, string pkcs12Password)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("Subject is required.", nameof(subject));

        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest(
            new X500DistinguishedName(subject),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // End-entity cert (not a CA).
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));

        // Signing only.
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));

        // Code signing EKU — the bit Authenticode/osslsigncode checks for.
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(CodeSigningEku) }, critical: true));

        req.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.Add(validity);

        using var cert = req.CreateSelfSigned(notBefore, notAfter);

        var pkcs12 = cert.Export(X509ContentType.Pkcs12, pkcs12Password);
        var pem = new string(PemEncoding.Write("CERTIFICATE", cert.RawData));

        return new GeneratedCert(
            Pkcs12: pkcs12,
            PublicCertPem: pem,
            Thumbprint: cert.Thumbprint,
            Subject: cert.Subject,
            NotBefore: cert.NotBefore.ToUniversalTime(),
            NotAfter: cert.NotAfter.ToUniversalTime());
    }
}
