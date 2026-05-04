using PKS.Infrastructure.Services.Claude;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Marketplace;

[Description("Add a plugin marketplace and apply its policy")]
public class MarketplaceAddCommand : AsyncCommand<MarketplaceAddCommand.Settings>
{
    private readonly IClaudeMarketplaceConfigurationService _configService;
    private readonly IClaudeMarketplaceFetcher _fetcher;
    private readonly IAnsiConsole _console;

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<SOURCE>")]
        [Description("Marketplace source (URL, github:owner/repo, github:owner/repo@ref)")]
        public string Source { get; set; } = "";

        [CommandOption("--label <LABEL>")]
        [Description("Optional display label for the marketplace")]
        public string? Label { get; set; }

        [CommandOption("--non-interactive")]
        [Description("Skip interactive prompts")]
        public bool NonInteractive { get; set; }

        [CommandOption("--enable-all")]
        [Description("Enable all plugins when adding (only used with --non-interactive)")]
        public bool EnableAll { get; set; }
    }

    public MarketplaceAddCommand(
        IClaudeMarketplaceConfigurationService configService,
        IClaudeMarketplaceFetcher fetcher,
        IAnsiConsole console)
    {
        _configService = configService;
        _fetcher = fetcher;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            _console.MarkupLine($"[cyan]Fetching marketplace from:[/] {settings.Source.EscapeMarkup()}");

            var source = _fetcher.ParseSource(settings.Source);
            var marketplaceJson = await _fetcher.FetchAsync(source);

            var plugins = marketplaceJson.Plugins.Select(p => new ClaudeMarketplacePluginSnapshot
            {
                Name = p.Name,
                Version = p.Version,
                Description = p.Description,
                Enabled = settings.NonInteractive ? settings.EnableAll : false
            }).ToList();

            // Fetch and apply policy (URL sources only)
            MarketplacePolicy? policy = null;
            if (source.SourceType == "url" && !string.IsNullOrWhiteSpace(source.Url))
            {
                policy = await FetchPolicyAsync(source.Url);
                if (policy != null)
                    ApplyPolicy(plugins, policy);
            }

            // Interactive plugin selection (skip policy-forced ones)
            if (!settings.NonInteractive && plugins.Count > 0)
            {
                var forcedNames = policy?.Plugins
                    .Where(p => p.Policy == "required" || p.Policy == "installed-default")
                    .Select(p => p.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();

                var selectable = plugins.Where(p => !forcedNames.Contains(p.Name)).ToList();
                if (selectable.Count > 0)
                {
                    var selected = _console.Prompt(
                        new MultiSelectionPrompt<string>()
                            .Title("Select plugins to enable:")
                            .NotRequired()
                            .AddChoices(selectable.Select(p => p.Name)));

                    foreach (var plugin in selectable)
                        plugin.Enabled = selected.Contains(plugin.Name);
                }
            }

            if (string.IsNullOrWhiteSpace(marketplaceJson.Name))
            {
                _console.MarkupLine("[red]Error: fetched marketplace.json has no `name` field.[/]");
                _console.MarkupLine("[dim]Anthropic's schema requires a top-level `name`. Without it the marketplace cannot be referenced from `enabledPlugins`. Refusing to write a stub entry — fix the marketplace source and re-run.[/]");
                return 1;
            }

            var marketplace = new ClaudeMarketplace
            {
                Id = marketplaceJson.Name,
                Label = settings.Label ?? marketplaceJson.Label,
                Source = source,
                AddedAt = DateTime.UtcNow,
                LastFetchedAt = DateTime.UtcNow,
                Plugins = plugins
            };

            await _configService.AddOrUpdateMarketplaceAsync(marketplace);

            _console.MarkupLine($"[green]Marketplace '{marketplace.Id.EscapeMarkup()}' added successfully.[/]");
            _console.MarkupLine($"[dim]Plugins: {plugins.Count} total, {plugins.Count(p => p.Enabled)} enabled[/]");

            if (policy != null)
            {
                var installedDefault = policy.Plugins.Count(p => p.Policy == "installed-default");
                var required = policy.Plugins.Count(p => p.Policy == "required");
                _console.MarkupLine($"[dim]Policy applied: {installedDefault} installed by default, {required} required.[/]");

                foreach (var p in policy.Plugins.Where(p => p.Policy == "required"))
                    _console.MarkupLine($"[yellow]⚑ {p.Name.EscapeMarkup()} is required by policy and cannot be disabled.[/]");
            }

            return 0;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Error adding marketplace: {ex.Message.EscapeMarkup()}[/]");
            return 1;
        }
    }

    private async Task<MarketplacePolicy?> FetchPolicyAsync(string marketplaceUrl)
    {
        try
        {
            var policyUrl = marketplaceUrl.TrimEnd('/') + "/policy";
            using var http = new System.Net.Http.HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var response = await http.GetAsync(policyUrl);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            return System.Text.Json.JsonSerializer.Deserialize<MarketplacePolicy>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyPolicy(List<ClaudeMarketplacePluginSnapshot> plugins, MarketplacePolicy policy)
    {
        foreach (var entry in policy.Plugins)
        {
            var plugin = plugins.FirstOrDefault(p =>
                string.Equals(p.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (plugin == null) continue;

            if (entry.Policy == "required" || entry.Policy == "installed-default")
                plugin.Enabled = true;

            if (entry.Policy == "required")
                plugin.Required = true;
        }
    }

    private record MarketplacePolicy(
        string Version = "1",
        string MarketplaceId = "",
        List<MarketplacePolicyPlugin> Plugins = null!)
    {
        public List<MarketplacePolicyPlugin> Plugins { get; init; } = Plugins ?? new();
    }

    private record MarketplacePolicyPlugin(string Name = "", string Policy = "available");
}
