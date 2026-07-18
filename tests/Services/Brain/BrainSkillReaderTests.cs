using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Brain;
using Xunit;

namespace PKS.CLI.Tests.Services.Brain;

public class BrainSkillReaderTests : TestBase
{
    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task Reader_prefers_project_agent_skill_so_Claude_and_Codex_share_one_source()
    {
        var root = CreateTempDirectory();
        var nested = Path.Combine(root, "src", "feature");
        var skillDir = Path.Combine(root, ".agents", "skills", "brain-extract");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), "shared agent skill");

        var reader = new BrainSkillReader(Path.Combine(root, "fake-home"), nested);
        var result = await reader.ReadAsync("brain-extract", null);

        result.Body.Should().Be("shared agent skill");
        result.Source.Should().Be(Path.Combine(skillDir, "SKILL.md"));
    }

    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task Reader_supports_user_agent_skills_before_legacy_Claude_copy()
    {
        var root = CreateTempDirectory();
        var home = Path.Combine(root, "home");
        var agentsDir = Path.Combine(home, ".agents", "skills", "brain-extract");
        var claudeDir = Path.Combine(home, ".claude", "skills", "brain-extract");
        Directory.CreateDirectory(agentsDir);
        Directory.CreateDirectory(claudeDir);
        await File.WriteAllTextAsync(Path.Combine(agentsDir, "SKILL.md"), "agents version");
        await File.WriteAllTextAsync(Path.Combine(claudeDir, "SKILL.md"), "claude version");

        var reader = new BrainSkillReader(home, root);
        var result = await reader.ReadAsync("brain-extract", null);

        result.Body.Should().Be("agents version");
    }
}
