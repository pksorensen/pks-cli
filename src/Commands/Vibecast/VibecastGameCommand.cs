using PKS.Commands.Devcontainer;
using PKS.Commands.Vm;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Vibecast;

[Description("Join a vibegame tournament match — code your bot and battle")]
public class VibecastGameCommand : VibecastCommand
{
    public new class Settings : VibecastCommand.Settings
    {
        [CommandArgument(0, "<gameId>")]
        [Description("Game room ID from the /vibe/[gameId] lobby")]
        public string GameId { get; set; } = "";

        // Shadow the parent's positional ProjectPath — vibecast game uses CWD, not a path arg.
        // Without this, Spectre binds the gameId positional to ProjectPath instead of GameId.
        public new string? ProjectPath { get; set; }
    }

    public VibecastGameCommand(
        IDevcontainerSpawnerService spawnerService,
        ISshTargetConfigurationService sshTargetService,
        INuGetTemplateDiscoveryService nugetTemplateService,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        VmInitCommand vmInitCommand,
        IAnsiConsole console)
        : base(spawnerService, sshTargetService, nugetTemplateService, vmMetadata, azureAuth, vmService, vmInitCommand, console)
    {
    }

    protected override string GetExtraVibecastArgs(VibecastCommand.Settings settings)
    {
        var gameId = (settings as Settings)?.GameId ?? "";
        return $"--attr game-id {gameId} --plugin vibegame";
    }

}
