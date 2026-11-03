using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Pure, I/O-free certificate authority for <c>pks proxy</c>: a root the machine trusts once, and
/// short-lived server certificates for whichever hostnames the proxy answers on. A .NET or Vite
/// developer certificate only covers <c>localhost</c>; this is what lets a real-looking name such as
/// <c>platform.example.com</c>, pointed at 127.0.0.1 in the hosts file, open with no browser warning.
/// </summary>
public static class LocalCertificateAuthority
{
    public const string RootSubject = "CN=pks proxy local CA, O=pks-cli";
    private const string ServerAuthEku = "1.3.6.1.5.5.7.3.1";

    /// <summary>Create the root. It may only sign end-entity certificates (path length 0).</summary>
    public static X509Certificate2 CreateRoot(TimeSpan validity)
    {
        using var key = RSA.Create(3072);
        var req = new CertificateRequest(RootSubject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        return req.CreateSelfSigned(now.AddDays(-1), now.Add(validity));
    }

    /// <summary>
    /// Issue a server certificate for <paramref name="hostnames"/>, signed by <paramref name="root"/>.
    /// The result is re-imported from PKCS#12, because SslStream on Windows cannot use the ephemeral
    /// key <see cref="CertificateRequest.Create(X509Certificate2, DateTimeOffset, DateTimeOffset, byte[])"/> returns.
    /// </summary>
    public static X509Certificate2 IssueServerCertificate(X509Certificate2 root, IReadOnlyList<string> hostnames, TimeSpan validity)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (hostnames.Count == 0)
            throw new ArgumentException("At least one hostname is required.", nameof(hostnames));

        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={hostnames[0]}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        foreach (var host in hostnames)
            san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build(true));
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuthEku)], false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));

        var now = DateTimeOffset.UtcNow;
        var notAfter = now.Add(validity) < root.NotAfter ? now.Add(validity) : new DateTimeOffset(root.NotAfter);
        using var issued = req.Create(root, now.AddDays(-1), notAfter, RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }
}