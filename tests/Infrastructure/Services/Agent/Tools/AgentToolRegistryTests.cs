using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using PKS.Infrastructure.Services.Agent;
using PKS.Infrastructure.Services.Agent.Tools;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure.Services.Agent.Tools;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AgentToolRegistryTests
{
    private static string Cwd()
    {
        var p = Path.Combine(Path.GetTempPath(), "reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    [Fact]
    public void RegistersToolsByName()
    {
        var cwd = Cwd();
        var reg = new AgentToolRegistry(new IAgentTool[] { new ReadTool(cwd), new WriteTool(cwd), new LsTool(cwd) });
        reg.GetByName("read").Should().BeOfType<ReadTool>();
        reg.GetByName("write").Should().BeOfType<WriteTool>();
        reg.GetByName("ls").Should().BeOfType<LsTool>();
        reg.All.Should().HaveCount(3);
    }

    [Fact]
    public void GetByName_UnknownTool_Throws()
    {
        var cwd = Cwd();
        var reg = new AgentToolRegistry(new IAgentTool[] { new ReadTool(cwd) });
        Action act = () => reg.GetByName("nope");
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void FilterTo_ReturnsSubset()
    {
        var cwd = Cwd();
        var reg = new AgentToolRegistry(new IAgentTool[] { new ReadTool(cwd), new WriteTool(cwd), new LsTool(cwd) });
        var filtered = reg.FilterTo(new[] { "read", "ls" });
        filtered.All.Should().HaveCount(2);
        filtered.GetByName("read").Should().NotBeNull();
        filtered.GetByName("ls").Should().NotBeNull();
        Action act = () => filtered.GetByName("write");
        act.Should().Throw<KeyNotFoundException>();
    }
}

// TODO: bash/grep/find tests
