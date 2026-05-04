using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Marketplace;

[Description("Manage plugin marketplaces")]
public class MarketplaceBranchCommand : Command<MarketplaceBranchCommand.Settings>
{
    public class Settings : CommandSettings { }

    public override int Execute(CommandContext context, Settings settings)
    {
        return 0;
    }
}
