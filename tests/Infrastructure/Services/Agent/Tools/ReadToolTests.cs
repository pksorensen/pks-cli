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
public class ReadToolTests : IDisposable
{
    private readonly string _cwd;

    public ReadToolTests()
    {
        _cwd = Path.Combine(Path.GetTempPath(), "read-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cwd);
    }

    public void Dispose()
    {
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private static JsonElement Args(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    [Fact]
    public async Task ReadsFile_WithCatNStylePrefix()
    {
        File.WriteAllText(Path.Combine(_cwd, "hello.txt"), "Hello\nWorld\n", new UTF8Encoding(false));
        var tool = new ReadTool(_cwd);
        var res = await tool.ExecuteAsync(Args(new { path = "hello.txt" }), CancellationToken.None);
        res.IsError.Should().BeFalse();
        res.Output.Should().Contain("     1\tHello\n");
        res.Output.Should().Contain("     2\tWorld\n");
    }

    [Fact]
    public async Task MissingFile_ReturnsError()
    {
        var tool = new ReadTool(_cwd);
        var res = await tool.ExecuteAsync(Args(new { path = "nope.txt" }), CancellationToken.None);
        res.IsError.Should().BeTrue();
        res.Output.Should().Contain("file not found");
    }

    [Fact]
    public async Task OffsetAndLimit_TruncatesProperly()
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= 10; i++) sb.Append("line").Append(i).Append('\n');
        File.WriteAllText(Path.Combine(_cwd, "many.txt"), sb.ToString(), new UTF8Encoding(false));
        var tool = new ReadTool(_cwd);
        var res = await tool.ExecuteAsync(Args(new { path = "many.txt", offset = 3, limit = 2 }), CancellationToken.None);
        res.IsError.Should().BeFalse();
        res.Output.Should().Contain("     3\tline3");
        res.Output.Should().Contain("     4\tline4");
        res.Output.Should().NotContain("line5");
        res.Output.Should().NotContain("line2\n");
    }
}
