using System.ComponentModel;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Sql;

/// <summary>
/// Opens an Azure SQL server's firewall to this machine, or closes it again with --remove.
///
/// This is a change to a live server, so it names the rule after the machine rather than something
/// anonymous: a stale rule you can't attribute is a rule nobody dares delete.
/// </summary>
[Description("Allow this machine's IP address through an Azure SQL server firewall")]
public class SqlAllowIpCommand : AsyncCommand<SqlAllowIpCommand.Settings>
{
    private readonly IAzureAuthService _authService;
    private readonly IAzureSqlDiscoveryService _discovery;
    private readonly IConfigurationService _configuration;
    private readonly IAnsiConsole _console;

    public SqlAllowIpCommand(
        IAzureAuthService authService,
        IAzureSqlDiscoveryService discovery,
        IConfigurationService configuration,
        IAnsiConsole console)
    {
        _authService = authService;
        _discovery = discovery;
        _configuration = configuration;
        _console = console;
    }

    public class Settings : SqlSettings
    {
        [CommandArgument(0, "[server]")]
        [Description("Server name; omit to use the one from `pks sqlserver init`")]
        public string? Server { get; set; }

        [CommandOption("--ip")]
        [Description("IP address to allow (detected when omitted)")]
        public string? IpAddress { get; set; }

        [CommandOption("--name")]
        [Description("Firewall rule name (defaults to pks-<hostname>)")]
        public string? RuleName { get; set; }

        [CommandOption("--remove")]
        [Description("Delete the rule instead of creating it")]
        public bool Remove { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var defaults = await SqlDefaults.LoadAsync(_configuration);
        var serverName = settings.Server ?? defaults?.Server;

        if (string.IsNullOrWhiteSpace(serverName))
        {
            _console.MarkupLine("[red]No server given, and none stored.[/]");
            return 1;
        }

        var host = SqlAuth.ResolveServer(serverName);
        var shortName = host.Split('.')[0];

        var managementToken = await _authService.GetAccessTokenAsync("https://management.azure.com/.default", SqlAuth.StorageKey);
        if (string.IsNullOrEmpty(managementToken))
        {
            _console.MarkupLine("[red]Not signed in — run [bold]pks sqlserver init[/] first.[/]");
            return 1;
        }

        AzureSqlServerRef? server = null;
        await _console.Status().StartAsync($"Looking for {shortName}...", async _ =>
        {
            List<AzureSubscription> subscriptions;
            try
            {
                subscriptions = await _authService.ListSubscriptionsAsync(managementToken);
            }
            catch (Exception)
            {
                subscriptions = new List<AzureSubscription>();
            }

            foreach (var subscription in subscriptions)
            {
                var found = await _discovery.ListServersAsync(managementToken, subscription.SubscriptionId, subscription.DisplayName);
                server = found.FirstOrDefault(s =>
                    string.Equals(s.Name, shortName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.FullyQualifiedDomainName, host, StringComparison.OrdinalIgnoreCase));
                if (server != null)
                    break;
            }
        });

        if (server == null)
        {
            _console.MarkupLine($"[red]{Markup.Escape(shortName)} is not on any subscription this account can manage.[/]");
            _console.MarkupLine("[dim]Whoever owns the server has to add the rule — the address is printed by the failing query.[/]");
            return 1;
        }

        var ruleName = settings.RuleName ?? $"pks-{Environment.MachineName.ToLowerInvariant()}";

        if (settings.Remove)
        {
            var removeError = await _discovery.DeleteFirewallRuleAsync(managementToken, server.Id, ruleName);
            if (removeError != null)
            {
                _console.MarkupLine($"[red]Could not delete the rule: {Markup.Escape(removeError)}[/]");
                return 1;
            }

            _console.MarkupLine($"[green]Removed firewall rule [bold]{Markup.Escape(ruleName)}[/] from {Markup.Escape(server.Name)}.[/]");
            return 0;
        }

        var ip = settings.IpAddress ?? await _discovery.DetectPublicIpAsync();
        if (string.IsNullOrWhiteSpace(ip))
        {
            _console.MarkupLine("[red]Could not work out this machine's public IP — pass it with --ip.[/]");
            return 1;
        }

        var error = await _discovery.SetFirewallRuleAsync(managementToken, server.Id, ruleName, ip);
        if (error != null)
        {
            _console.MarkupLine($"[red]Could not add the rule: {Markup.Escape(error)}[/]");
            _console.MarkupLine("[dim]Writing firewall rules needs Contributor on the server, not just read access.[/]");
            return 1;
        }

        _console.MarkupLine($"[green]{Markup.Escape(server.Name)} now accepts [bold]{Markup.Escape(ip)}[/] as rule [bold]{Markup.Escape(ruleName)}[/].[/]");
        _console.MarkupLine($"[dim]Resource group {Markup.Escape(server.ResourceGroup)} · subscription {Markup.Escape(server.SubscriptionName)}[/]");
        _console.MarkupLine($"[dim]Undo with: pks sqlserver allow-ip --remove[/]");
        return 0;
    }

}
