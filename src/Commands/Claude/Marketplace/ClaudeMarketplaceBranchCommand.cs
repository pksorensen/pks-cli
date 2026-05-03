using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Claude.Marketplace;

[Description("Manage Claude Code plugin marketplaces")]
public class ClaudeMarketplaceBranchCommand : Command<ClaudeMarketplaceBranchCommand.Settings>
{
    public class Settings : CommandSettings { }

    public override int Execute(CommandContext context, Settings settings)
    {
        return 0;
    }
}
