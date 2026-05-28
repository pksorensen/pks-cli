using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.Infrastructure.Services.Agent.Tools;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure.Services.Agent.Tools;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class LsToolTests : IDisposable
{
    private readonly string _cwd;

    public LsToolTests()
    {
        _cwd = Path.Combine(Path.GetTempPath(), "ls-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cwd);
    }

    public void Dispose()
    {
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private static JsonElement Args(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    [Fact]
    public async Task ListsDirectoriesFirstThenFilesAlphabetically()
    {
        Directory.CreateDirectory(Path.Combine(_cwd, "zdir"));
        Directory.CreateDirectory(Path.Combine(_cwd, "adir"));
        File.WriteAllText(Path.Combine(_cwd, "zfile.txt"), "");
        File.WriteAllText(Path.Combine(_cwd, "afile.txt"), "");

        var tool = new LsTool(_cwd);
        var res = await tool.ExecuteAsync(Args(new { path = "." }), CancellationToken.None);
        res.IsError.Should().BeFalse();

        var lines = res.Output.TrimEnd('\n').Split('\n');
        lines.Should().Equal("adir/", "zdir/", "afile.txt", "zfile.txt");
    }
}
