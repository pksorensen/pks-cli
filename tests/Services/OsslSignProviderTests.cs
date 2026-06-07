using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Runner;
using PKS.Infrastructure.Services.Signing;
using PKS.CLI.Tests.Infrastructure;
using Xunit;
using ProcessResult = PKS.Infrastructure.Services.Runner.ProcessResult;

namespace PKS.CLI.Tests.Services;

public class OsslSignProviderTests : TestBase
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildOsslArgs_Pkcs12Mode_Sha256()
    {
        var args = OsslSignProvider.BuildOsslArgs("/tmp/c.pfx", "pw", "in.msix", "out.msix", null);

        args.Should().ContainInOrder("sign", "-pkcs12", "/tmp/c.pfx", "-pass", "pw", "-h", "sha256", "-in", "in.msix", "-out", "out.msix");
        args.Should().NotContain("-t");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildOsslArgs_IncludesTimestamp_OnlyWhenProvided()
    {
        var args = OsslSignProvider.BuildOsslArgs("/tmp/c.pfx", "pw", "in.msix", "out.msix", "http://ts.example/rfc3161");
        args.Should().ContainInConsecutiveOrder("-t", "http://ts.example/rfc3161");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task SignWithPfx_Failure_PropagatesExitCode()
    {
        using var fakeTool = new FakeTool();
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(1, "", "bad cert"));

        var input = CreateTempFile("x", ".msix");
        var provider = new OsslSignProvider(runner.Object);
        var result = await provider.SignWithPfxAsync("/tmp/c.pfx", "pw", new SignRequest(input, input + ".out", null));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("exit 1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task SignWithPfx_Success_WhenOutputProduced()
    {
        using var fakeTool = new FakeTool();
        var input = CreateTempFile("x", ".msix");
        var output = input + ".out";

        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { await File.WriteAllTextAsync(output, "signed"); return new ProcessResult(0, "", ""); });

        var provider = new OsslSignProvider(runner.Object);
        var result = await provider.SignWithPfxAsync("/tmp/c.pfx", "pw", new SignRequest(input, output, null));

        result.Success.Should().BeTrue();
        File.Exists(output).Should().BeTrue();
    }

    /// <summary>Points the provider's PATH lookup at a present-but-fake "osslsigncode" via the
    /// OSSLSIGNCODE override env var, so process execution can be exercised under a mocked runner.</summary>
    private sealed class FakeTool : IDisposable
    {
        private readonly string _path;
        private readonly string? _prev;
        public FakeTool()
        {
            _path = Path.Combine(Path.GetTempPath(), $"osslsigncode-{Guid.NewGuid():n}");
            File.WriteAllText(_path, "#!/bin/sh\n");
            _prev = Environment.GetEnvironmentVariable("OSSLSIGNCODE");
            Environment.SetEnvironmentVariable("OSSLSIGNCODE", _path);
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("OSSLSIGNCODE", _prev);
            try { File.Delete(_path); } catch { }
        }
    }
}
