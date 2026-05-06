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
    // Each run gets a unique suffix so two players joining the same game get independent containers.
    private readonly string _playerSessionId = Guid.NewGuid().ToString("N")[..8];

    public VibecastGameCommand(
        IDevcontainerSpawnerService spawnerService,
        ISshTargetConfigurationService sshTargetService,
        INuGetTemplateDiscoveryService nugetTemplateService,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        VmInitCommand vmInitCommand,
        IAzureFoundryAuthService foundryAuthService,
        IAnsiConsole console)
        : base(spawnerService, sshTargetService, nugetTemplateService, vmMetadata, azureAuth, vmService, vmInitCommand, foundryAuthService, console)
    {
    }

    protected override string AdjustProjectName(string projectName, PKS.Commands.Devcontainer.DevcontainerSpawnCommand.Settings settings)
    {
        var gameId = settings.ProjectPath ?? projectName;
        // Prefix with "game-" so discovery filters don't mix game containers with regular dev containers.
        // The unique _playerSessionId ensures two players in the same game get separate containers/volumes.
        return $"game-{gameId[..Math.Min(8, gameId.Length)]}-{_playerSessionId}";
    }

    protected override string GetExtraVibecastArgs(VibecastCommand.Settings settings)
    {
        var gameId = settings.ProjectPath ?? "";
        return $"--attr game-id {gameId} --plugin vibegame";
    }

    protected override string GetPreVibecastSetupScript(VibecastCommand.Settings settings)
    {
        var gameId = settings.ProjectPath ?? "";
        var prompt = BuildGamePrompt(gameId);
        var promptB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(prompt));

        const string monitorScript = """
            #!/bin/bash
            # Only output lines that are meaningful events — status lines are filtered out so
            # Monitor only wakes Claude when something actionable arrives (countdown, game_start, game_end).
            SCHEME=https
            [[ "$VIBEGAME_SERVER" == *localhost* ]] && SCHEME=http
            while true; do
              curl -N -s -f "$SCHEME://$VIBEGAME_SERVER/api/vibegame/games/$VIBEGAME_GAME_ID/events" \
                | grep --line-buffered -v '"type":"status"'
              sleep 2
            done
            """;
        var scriptB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(monitorScript));

        // Write both files then prefix vibecast with the prompt env var.
        return $"echo '{promptB64}' | base64 -d > /tmp/vibegame-prompt.txt; " +
               $"echo '{scriptB64}' | base64 -d > /tmp/game_events.sh; chmod +x /tmp/game_events.sh; " +
               $"VIBECAST_INITIAL_PROMPT_FILE=/tmp/vibegame-prompt.txt ";
    }

    private static string BuildGamePrompt(string gameId)
    {
        return """
            You are competing in a Vibegame coding tournament. Your task is to implement a bot for a competitive game.

            ## Immediate Action Required

            Use the Bash tool with `run_in_background: true` to start the event monitor, then use the Monitor tool to watch it:

            ```bash
            /tmp/game_events.sh
            ```

            Run that script with `run_in_background: true`, then call Monitor on the process — each SSE event line will be delivered to you as a message. React immediately when you see `game_start`.

            ## What Will Happen

            1. You will see the current game status (`{"type":"status",...}`)
            2. When both players have joined, a **countdown begins**: `{"type":"countdown","count":10}` ... down to 0
            3. **Do NOT start coding yet** — wait for `game_start`
            4. The `game_start` event contains all the rules, bot WebSocket API, and simulation endpoint

            ## When You Receive `game_start`

            1. Present the game overview clearly to the player (human) — make it exciting!
            2. Tell them they have **10 prompts** to implement and refine their bot
            3. Ask for their first prompt — what strategy should the bot use?
            4. Use the simulation endpoint to test each iteration before committing
            5. The real game starts automatically when both players exhaust their prompts or time runs out

            Begin monitoring now!
            """;
    }

}
