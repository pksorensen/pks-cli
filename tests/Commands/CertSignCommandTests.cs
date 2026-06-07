using FluentAssertions;
using Moq;
using PKS.Commands.Cert;
using PKS.Commands.Sign;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Security;
using PKS.Infrastructure.Services.Signing;
using PKS.CLI.Tests.Infrastructure;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands;

public class CertSignCommandTests : TestBase
{
    private static CommandContext Ctx() =>
        new(Mock.Of<IRemainingArguments>(), "x", null);

    // --- cert init refusal paths ---

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void CertInit_RefusesSudoPath()
    {
        var prev = Environment.GetEnvironmentVariable("SUDO_USER");
        Environment.SetEnvironmentVariable("SUDO_USER", "node");
        try
        {
            var console = new TestConsole();
            var store = new CertStore(Path.Combine(CreateTempDirectory(), "certs"));
            var guard = new Mock<IActionGuard>();
            var cmd = new CertInitCommand(store, guard.Object, console);

            var rc = cmd.Execute(Ctx(), new CertInitCommand.Settings());

            rc.Should().Be(1);
            console.Output.Should().Contain("sudo");
        }
        finally { Environment.SetEnvironmentVariable("SUDO_USER", prev); }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void CertInit_RefusesRedirectedIo()
    {
        var prevU = Environment.GetEnvironmentVariable("SUDO_USER");
        var prevI = Environment.GetEnvironmentVariable("SUDO_UID");
        Environment.SetEnvironmentVariable("SUDO_USER", null);
        Environment.SetEnvironmentVariable("SUDO_UID", null);
        try
        {
            var console = new TestConsole();
            var store = new CertStore(Path.Combine(CreateTempDirectory(), "certs"));
            var guard = new Mock<IActionGuard>();
            var cmd = new CertInitCommand(store, guard.Object, console);

            // The xUnit runner redirects stdin/stdout, so the interactive guard trips.
            var rc = cmd.Execute(Ctx(), new CertInitCommand.Settings());

            rc.Should().Be(1);
            console.Output.Should().Contain("interactive terminal");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUDO_USER", prevU);
            Environment.SetEnvironmentVariable("SUDO_UID", prevI);
        }
    }

    // --- sign dispatch ---

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Sign_NoProviderForCert_Errors()
    {
        var store = new CertStore(Path.Combine(CreateTempDirectory(), "certs"));
        await store.CreateSelfSignedAsync("CN=Test", "agentics", TimeSpan.FromDays(30));
        var input = CreateTempFile("x", ".msix");

        var console = new TestConsole();
        var cmd = new SignCommand(store, Array.Empty<ISignProvider>(), console); // no providers registered

        var rc = cmd.Execute(Ctx(), new SignSettings { Input = input });

        rc.Should().Be(1);
        console.Output.Should().Contain("No signing provider");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Sign_MultipleCerts_NoSelection_Errors()
    {
        var store = new CertStore(Path.Combine(CreateTempDirectory(), "certs"));
        await store.CreateSelfSignedAsync("CN=A", "a", TimeSpan.FromDays(30));
        await store.CreateSelfSignedAsync("CN=B", "b", TimeSpan.FromDays(30));
        var input = CreateTempFile("x", ".msix");

        var console = new TestConsole();
        var ossl = new OsslSignProvider(Mock.Of<PKS.Infrastructure.Services.Runner.IProcessRunner>());
        var cmd = new SignCommand(store, new ISignProvider[] { ossl }, console);

        var rc = cmd.Execute(Ctx(), new SignSettings { Input = input });

        rc.Should().Be(1);
        console.Output.Should().Contain("Multiple certs");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Sign_MissingInput_Errors()
    {
        var store = new CertStore(Path.Combine(CreateTempDirectory(), "certs"));
        var console = new TestConsole();
        var cmd = new SignCommand(store, Array.Empty<ISignProvider>(), console);

        var rc = cmd.Execute(Ctx(), new SignSettings { Input = "/no/such/file.msix" });

        rc.Should().Be(1);
        console.Output.Should().Contain("Input not found");
    }
}
