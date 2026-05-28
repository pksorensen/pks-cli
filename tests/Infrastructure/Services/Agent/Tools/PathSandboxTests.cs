using System;
using System.IO;
using FluentAssertions;
using PKS.Infrastructure.Services.Agent.Tools;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure.Services.Agent.Tools;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class PathSandboxTests
{
    [Fact]
    public void Resolve_RelativePath_StaysInsideSandbox()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "sandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var resolved = PathSandbox.Resolve(cwd, "foo/bar.txt");
            resolved.Should().StartWith(Path.GetFullPath(cwd));
            resolved.Should().EndWith("bar.txt");
        }
        finally { Directory.Delete(cwd, true); }
    }

    [Fact]
    public void Resolve_DotDotEscape_Throws()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "sandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            Action act = () => PathSandbox.Resolve(cwd, "../escape.txt");
            act.Should().Throw<ArgumentException>().WithMessage("*escapes sandbox*");
        }
        finally { Directory.Delete(cwd, true); }
    }

    [Fact]
    public void Resolve_AbsoluteOutsideSandbox_Throws()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "sandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            Action act = () => PathSandbox.Resolve(cwd, "/etc/passwd");
            act.Should().Throw<ArgumentException>().WithMessage("*escapes sandbox*");
        }
        finally { Directory.Delete(cwd, true); }
    }
}
