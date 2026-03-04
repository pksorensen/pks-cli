using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console.Cli;

namespace PKS.Commands.Ado;

/// <summary>
/// GIT_ASKPASS handler for Azure DevOps. Outputs credentials to stdout with zero Spectre markup.
/// Usage: GIT_ASKPASS="pks git askpass" git clone https://dev.azure.com/org/repo
/// </summary>
[Description("Git credential helper for Azure DevOps (used as GIT_ASKPASS)")]
public class GitAskPassCommand : Command<GitAskPassCommand.Settings>
{
    private readonly IAzureDevOpsAuthService _authService;

    public GitAskPassCommand(IAzureDevOpsAuthService authService)
    {
        _authService = authService;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[prompt]")]
        [Description("The credential prompt from Git")]
        public string? Prompt { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        try
        {
            var prompt = settings.Prompt ?? string.Empty;

            if (prompt.Contains("Username", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write("pks");
                return 0;
            }

            if (prompt.Contains("Password", StringComparison.OrdinalIgnoreCase))
            {
                var token = await _authService.RefreshAccessTokenAsync();
                if (string.IsNullOrEmpty(token))
                    return 1;

                Console.Write(token);
                return 0;
            }

            // Unknown prompt — fail silently
            return 1;
        }
        catch
        {
            // Silent failure — Git expects no stderr output
            return 1;
        }
    }
}
