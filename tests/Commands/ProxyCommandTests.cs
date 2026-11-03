using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using PKS.Commands.Proxy;
using PKS.Infrastructure.Services;
using Xunit;

namespace PKS.CLI.Tests.Commands;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class ProxyCommandTests
{
    [Fact]
    public void Server_certificate_chains_to_the_root_and_names_the_host()
    {
        using var root = LocalCertificateAuthority.CreateRoot(TimeSpan.FromDays(10));
        using var server = LocalCertificateAuthority.IssueServerCertificate(root, ["platform.example.test"], TimeSpan.FromDays(30));

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));

        chain.Build(server).Should().BeTrue(string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation)));
        server.HasPrivateKey.Should().BeTrue();
        server.MatchesHostname("platform.example.test").Should().BeTrue();
        server.MatchesHostname("other.example.test").Should().BeFalse();
    }

    [Fact]
    public void Root_may_only_sign_end_entity_certificates()
    {
        using var root = LocalCertificateAuthority.CreateRoot(TimeSpan.FromDays(10));

        var constraints = root.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        constraints.CertificateAuthority.Should().BeTrue();
        constraints.HasPathLengthConstraint.Should().BeTrue();
        constraints.PathLengthConstraint.Should().Be(0);
    }

    [Fact]
    public void Server_certificate_never_outlives_the_root()
    {
        using var root = LocalCertificateAuthority.CreateRoot(TimeSpan.FromDays(5));
        using var server = LocalCertificateAuthority.IssueServerCertificate(root, ["a.example.test"], TimeSpan.FromDays(30));

        server.NotAfter.Should().BeOnOrBefore(root.NotAfter);
    }

    [Fact]
    public void Routes_combine_the_positional_host_and_route_pairs()
    {
        var routes = ProxyCommand.ParseRoutes("Platform.Example.Test", "http://localhost:5173",
            ["api.example.test=http://localhost:5000/"]);

        routes.Select(r => r.Host).Should().Equal("platform.example.test", "api.example.test");
        routes[0].Upstream.Should().Be(new Uri("http://localhost:5173"));
    }

    [Theory]
    [InlineData("platform.example.test", null)]
    [InlineData(null, "http://localhost:5173")]
    [InlineData("platform.example.test", "ftp://localhost")]
    [InlineData("not a host", "http://localhost:5173")]
    public void Incomplete_or_invalid_routes_are_refused(string? host, string? to)
    {
        var act = () => ProxyCommand.ParseRoutes(host, to, null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_host_routed_twice_is_refused()
    {
        var act = () => ProxyCommand.ParseRoutes("a.example.test", "http://localhost:1", ["A.example.test=http://localhost:2"]);
        act.Should().Throw<ArgumentException>().WithMessage("*routed twice*");
    }

    [Fact]
    public void Nothing_to_proxy_is_refused()
    {
        var act = () => ProxyCommand.ParseRoutes(null, null, []);
        act.Should().Throw<ArgumentException>();
    }
}