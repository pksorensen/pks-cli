using System.ComponentModel;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Sql;

/// <summary>
/// Signs in to Azure for database access and picks a default server and database.
///
/// The refresh token is kept under <see cref="SqlAuth.StorageKey"/>, separate from the subscription
/// login `pks azure init` stores, because the database you need to reach is usually in a different
/// tenant than the subscription you deploy to — and there is only one slot for each.
/// </summary>
[Description("Sign in to Azure SQL and choose a default server and database")]
public class SqlInitCommand : AsyncCommand<SqlInitCommand.Settings>
{
    private const string EnterManually = "Type a server name myself";
    private const string SkipSelection = "Skip — I'll name the server on every query";

    private readonly IAzureAuthService _authService;
    private readonly IAzureSqlDiscoveryService _discovery;
    private readonly IConfigurationService _configuration;
    private readonly IAnsiConsole _console;

    public SqlInitCommand(
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
        [CommandOption("-t|--tenant")]
        [Description("Azure AD tenant ID (discovered from --email, or 'common' when that fails)")]
        public string? TenantId { get; set; }

        [CommandOption("-e|--email")]
        [Description("Sign in as this account; also used to discover the tenant")]
        public string? Email { get; set; }

        [CommandOption("-f|--force")]
        [Description("Sign in again even if a SQL login is already stored")]
        public bool Force { get; set; }

        [CommandOption("--no-select")]
        [Description("Sign in only; don't go looking for servers")]
        public bool NoSelect { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var stored = await _authService.GetStoredCredentialsAsync(SqlAuth.StorageKey);
        var signedIn = stored != null && !string.IsNullOrEmpty(stored.RefreshToken);

        string? managementToken = null;

        if (signedIn && !settings.Force)
        {
            _console.MarkupLine("[green]Already signed in for Azure SQL.[/]");
            _console.MarkupLine($"[green]Tenant: [bold]{Markup.Escape(stored!.TenantId)}[/][/]");
            _console.MarkupLine("[dim]Use [bold]--force[/] to sign in as someone else.[/]");
            managementToken = await _authService.GetAccessTokenAsync("https://management.azure.com/.default", SqlAuth.StorageKey);
        }
        else
        {
            managementToken = await SignInAsync(settings);
            if (managementToken == null)
                return 1;
        }

        if (settings.NoSelect)
            return await ReportSqlTokenAsync();

        await SelectServerAsync(managementToken);
        return await ReportSqlTokenAsync();
    }

    /// <summary>
    /// Browser sign-in. Tenant discovery is a convenience, not a gate: when the mail domain has no
    /// Entra tenant — which is the common case for a mail-only domain — we fall through to 'common'
    /// and let the sign-in page work out where the account lives.
    /// </summary>
    private async Task<string?> SignInAsync(Settings settings)
    {
        var loginHint = string.IsNullOrWhiteSpace(settings.Email) ? null : settings.Email.Trim();
        string tenantId;

        if (!string.IsNullOrWhiteSpace(settings.TenantId))
        {
            tenantId = settings.TenantId.Trim();
        }
        else
        {
            if (loginHint == null)
            {
                var typed = _console.Prompt(
                    new TextPrompt<string>("[cyan]Email address to sign in with[/] [dim](or press Enter to choose in the browser)[/]:")
                        .AllowEmpty());
                loginHint = string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();
            }

            tenantId = "common";

            if (loginHint != null)
            {
                _console.MarkupLine("[dim]Discovering tenant...[/]");
                var discovered = await _authService.DiscoverTenantAsync(loginHint);
                if (!string.IsNullOrEmpty(discovered))
                {
                    tenantId = discovered;
                    _console.MarkupLine($"[green]Found tenant: [bold]{Markup.Escape(tenantId)}[/][/]");
                }
                else
                {
                    _console.MarkupLine($"[yellow]No tenant registered for {Markup.Escape(loginHint)} — signing in against 'common' instead.[/]");
                    _console.MarkupLine("[dim]The sign-in page will find the right one. The mail domain is often not the sign-in domain.[/]");
                }
            }
        }

        _console.MarkupLine("[cyan]Signing in to Azure...[/]");
        _console.MarkupLine("[dim]A browser window will open. If it doesn't, use the URL printed below.[/]");
        _console.WriteLine();

        AzureAuthResult result;
        try
        {
            result = await _authService.InitiateLoginAsync(tenantId, loginHint, SqlAuth.LoginScope);
        }
        catch (OperationCanceledException)
        {
            _console.MarkupLine("[red]Sign-in timed out.[/]");
            return null;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Sign-in failed: {Markup.Escape(ex.Message)}[/]");
            return null;
        }

        if (string.IsNullOrEmpty(result.RefreshToken))
        {
            _console.MarkupLine("[red]Azure returned no refresh token — cannot keep the session.[/]");
            return null;
        }

        // 'common' is a routing alias, not a tenant. Store the one the token was actually issued in,
        // so every later refresh goes straight to the right endpoint.
        var actualTenant = JwtClaims.Read(result.AccessToken, "tid") ?? tenantId;
        var account = JwtClaims.Read(result.AccessToken, "upn")
            ?? JwtClaims.Read(result.AccessToken, "preferred_username");

        await _authService.StoreCredentialsAsync(new AzureStoredCredentials
        {
            TenantId = actualTenant,
            RefreshToken = result.RefreshToken,
            CreatedAt = DateTime.UtcNow,
            LastRefreshedAt = DateTime.UtcNow,
        }, SqlAuth.StorageKey);

        _console.MarkupLine($"[green]Signed in{(account == null ? string.Empty : $" as [bold]{Markup.Escape(account)}[/]")}.[/]");
        _console.MarkupLine($"[dim]Tenant {Markup.Escape(actualTenant)}[/]");

        return result.AccessToken;
    }

    /// <summary>
    /// Offers the servers the account can see through ARM. A server in someone else's tenant will
    /// not be in that list — hence the manual entry, which is a normal outcome, not a failure.
    /// </summary>
    private async Task SelectServerAsync(string? managementToken)
    {
        var servers = new List<AzureSqlServerRef>();

        if (!string.IsNullOrEmpty(managementToken))
        {
            await _console.Status().StartAsync("Looking for SQL servers...", async _ =>
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
                    servers.AddRange(found);
                }
            });
        }

        string? host = null;
        AzureSqlServerRef? selected = null;

        if (servers.Count > 0)
        {
            var choices = servers
                .Select(s => $"{s.Name}  [dim]{s.Location} · {s.SubscriptionName}[/]")
                .ToList();
            choices.Add(EnterManually);
            choices.Add(SkipSelection);

            var choice = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Which SQL server should [bold]pks sql[/] use by default?[/]")
                    .PageSize(15)
                    .AddChoices(choices));

            if (choice == SkipSelection)
                return;

            if (choice != EnterManually)
            {
                selected = servers[choices.IndexOf(choice)];
                host = selected.FullyQualifiedDomainName;
            }
        }
        else
        {
            _console.MarkupLine("[yellow]No SQL servers found on the subscriptions this account can see.[/]");
            _console.MarkupLine("[dim]That is expected when the server belongs to someone else's tenant — name it yourself.[/]");
        }

        if (host == null)
        {
            var typed = _console.Prompt(
                new TextPrompt<string>("[cyan]Server name[/] [dim](short name or full host, Enter to skip)[/]:")
                    .AllowEmpty());
            if (string.IsNullOrWhiteSpace(typed))
                return;
            host = SqlAuth.ResolveServer(typed);
        }

        var database = await SelectDatabaseAsync(managementToken, selected);

        await SqlDefaults.SaveAsync(_configuration, new SqlDefaults
        {
            Server = host,
            Database = database ?? string.Empty,
        });

        _console.MarkupLine($"[green]Default server: [bold]{Markup.Escape(host)}[/][/]");
        if (!string.IsNullOrEmpty(database))
            _console.MarkupLine($"[green]Default database: [bold]{Markup.Escape(database)}[/][/]");
    }

    private async Task<string?> SelectDatabaseAsync(string? managementToken, AzureSqlServerRef? server)
    {
        var databases = new List<string>();

        if (server != null && !string.IsNullOrEmpty(managementToken))
            databases = await _discovery.ListDatabasesAsync(managementToken, server.Id);

        if (databases.Count > 0)
        {
            var choices = new List<string>(databases) { SkipSelection };
            var choice = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Which database?[/]")
                    .PageSize(15)
                    .AddChoices(choices));
            return choice == SkipSelection ? null : choice;
        }

        var typed = _console.Prompt(
            new TextPrompt<string>("[cyan]Database name[/] [dim](Enter to skip)[/]:").AllowEmpty());
        return string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();
    }

    /// <summary>
    /// The management token proves the sign-in worked; this proves the same session can be exchanged
    /// for a database token, which is the one that actually matters.
    /// </summary>
    private async Task<int> ReportSqlTokenAsync()
    {
        var token = await _authService.GetAccessTokenAsync(SqlAuth.Scope, SqlAuth.StorageKey);
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Signed in, but Azure would not issue a database token for this account.[/]");
            return 1;
        }

        _console.MarkupLine("[green]Database token acquired.[/]");
        _console.MarkupLine("[dim]pks sql \"select top 10 * from sys.tables\"[/]");
        return 0;
    }
}
