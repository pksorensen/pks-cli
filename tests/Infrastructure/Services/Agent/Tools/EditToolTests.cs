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
public class EditToolTests : IDisposable
{
    private readonly string _cwd;

    public EditToolTests()
    {
        _cwd = Path.Combine(Path.GetTempPath(), "edit-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cwd);
    }

    public void Dispose()
    {
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private static JsonElement Args(object o) =>
        JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

    private string Write(string name, string content)
    {
        var p = Path.Combine(_cwd, name);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    [Fact]
    public async Task UniqueMatch_Replaces()
    {
        var p = Write("a.txt", "hello world");
        var tool = new EditTool(_cwd);
        var res = await tool.ExecuteAsync(Args(new { path = "a.txt", old_string = "world", new_string = "there" }), CancellationToken.None);
        res.IsError.Should().BeFalse();
        File.ReadAllText(p).Should().Be("hello there");
    }

    [Fact]
    public async Task ZeroMatches_ReturnsError()
    {
        Write("a.txt", "hello");
        var tool = new EditTool(_cwd);
        var res = await tool.ExecuteAsync(Args(new { path = "a.txt", old_string = "missing", new_string = "x" }), CancellationToken.None);
        res.IsError.Should().BeTrue();
        res.Output.Should().Contain("not found");
    }

    [Fact]
    public async Task MultipleMatchesWithoutReplaceAll_ReturnsErrorWithCount()
    {
        Write("a.txt", "x x x");
        var tool = new EditTool(_cwd);
        var res = await tool.ExecuteAsync(Args(new { path = "a.txt", old_string = "x", new_string = "y" }), CancellationToken.None);
        res.IsError.Should().BeTrue();
        res.Output.Should().Contain("3 matches");
    }

    [Fact]
    public async Task ReplaceAll_ReplacesAll()
    {
        var p = Write("a.txt", "x x x");
        var tool = new EditTool(_cwd);
        var res = await tool.ExecuteAsync(Args(new { path = "a.txt", old_string = "x", new_string = "y", replace_all = true }), CancellationToken.None);
        res.IsError.Should().BeFalse();
        res.Output.Should().Contain("3 occurrences");
        File.ReadAllText(p).Should().Be("y y y");
    }
}
