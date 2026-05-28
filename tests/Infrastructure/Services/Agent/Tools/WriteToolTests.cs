using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.Infrastructure.Services.Agent.Tools;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure.Services.Agent.Tools;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class WriteToolTests : IDisposable
{
    private readonly string _cwd;

    public WriteToolTests()
    {
        _cwd = Path.Combine(Path.GetTempPath(), "write-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cwd);
    }

    public void Dispose()
    {
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private static JsonElement Args(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    [Fact]
    public async Task WritesFile_CreatesParentDirectories()
    {
        var tool = new WriteTool(_cwd);
        var res = await tool.ExecuteAsync(Args(new { path = "a/b/c.txt", content = "hi" }), CancellationToken.None);
        res.IsError.Should().BeFalse();
        File.Exists(Path.Combine(_cwd, "a", "b", "c.txt")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_cwd, "a", "b", "c.txt")).Should().Be("hi");
    }

    [Fact]
    public async Task OverwritesExistingFile()
    {
        var p = Path.Combine(_cwd, "x.txt");
        File.WriteAllText(p, "original content longer", new UTF8Encoding(false));
        var tool = new WriteTool(_cwd);
        var res = await tool.ExecuteAsync(Args(new { path = "x.txt", content = "new" }), CancellationToken.None);
        res.IsError.Should().BeFalse();
        File.ReadAllText(p).Should().Be("new");
    }
}
