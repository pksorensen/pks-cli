using FluentAssertions;
using PKS.Commands.Codex;
using PKS.Infrastructure.Services.Agent.Codex;
using Spectre.Console.Cli;
using Xunit;

namespace PKS.CLI.Tests.Commands.Codex;

public class CodexCommandParsingTests
{
    [Fact]
    public void DefaultCodexCommand_BindsModelOptionAtBranchLevel()
    {
        CaptureCodexCommand.LastSettings = null;
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.AddBranch<CodexBranchSettings>("codex", codex =>
            {
                codex.SetDefaultCommand<CaptureCodexCommand>();
            });
        });

        var exitCode = app.Run(["codex", "-m", "gpt-6-sol"]);

        exitCode.Should().Be(0);
        CaptureCodexCommand.LastSettings.Should().NotBeNull();
        CaptureCodexCommand.LastSettings!.Model.Should().Be("gpt-6-sol");
        CodexCliConfig.NormalizeDeploymentName(CaptureCodexCommand.LastSettings.Model).Should().Be("gpt-5.6-sol");
        CaptureCodexCommand.LastSettings.Args.Should().BeEmpty();
    }

    [Fact]
    public void NativeCodexSubcommand_PreservesRawArgumentsForPassthrough()
    {
        CaptureCodexCommand.LastContextName = null;
        CaptureCodexCommand.LastSettings = null;
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.AddBranch<CodexBranchSettings>("codex", codex =>
            {
                codex.AddCommand<CaptureCodexCommand>("resume");
            });
        });

        var exitCode = app.Run(["codex", "-m", "gpt-6-sol", "resume", "--last"]);

        exitCode.Should().Be(0);
        CaptureCodexCommand.LastContextName.Should().Be("resume");
        CaptureCodexCommand.LastSettings.Should().NotBeNull();
        CaptureCodexCommand.LastSettings!.Model.Should().Be("gpt-6-sol");
        CaptureCodexCommand.LastSettings!.Args.Should().BeEmpty();
        CaptureCodexCommand.LastRemainingRaw.Should().BeEmpty();
        CodexCommand.GetNativeArgsFromArgv("resume", ["codex", "-m", "gpt-5.6-sol", "resume", "--last"])
            .Should().Equal("resume", "--last");
    }

    [Fact]
    public void BuildCodexCommandLine_DisablesFoundryIncompatibleFeatureGroups()
    {
        var commandLine = CodexCommand.BuildCodexCommandLine(
            "gpt-5.6-sol",
            "medium",
            ["resume", "--last"],
            bypass: true);

        commandLine.Should().Contain("--disable collaboration_modes");
        commandLine.Should().Contain("--disable apps");
        commandLine.Should().Contain("--disable multi_agent_v2");
        commandLine.Should().Contain("--disable multi_agent");
        commandLine.Should().Contain("--dangerously-bypass-approvals-and-sandbox");
        commandLine.Should().StartWith("codex resume ");
        commandLine.Should().EndWith("--last");
    }

    private sealed class CaptureCodexCommand : Command<CodexSettings>
    {
        public static string? LastContextName { get; set; }
        public static IReadOnlyList<string>? LastRemainingRaw { get; set; }
        public static CodexSettings? LastSettings { get; set; }

        public override int Execute(CommandContext context, CodexSettings settings)
        {
            LastContextName = context.Name;
            LastRemainingRaw = context.Remaining.Raw;
            LastSettings = settings;
            return 0;
        }
    }
}
