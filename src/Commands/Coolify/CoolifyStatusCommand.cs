using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Coolify;

/// <summary>
/// Display health status of all registered Coolify instances, their projects, and resources.
/// Usage: pks coolify status
/// </summary>
public class CoolifyStatusCommand : Command<CoolifySettings>
{
    private readonly ICoolifyConfigurationService _configService;
    private readonly ICoolifyApiService _apiService;
    private readonly IAnsiConsole _console;

    public CoolifyStatusCommand(
        ICoolifyConfigurationService configService,
        ICoolifyApiService apiService,
        IAnsiConsole console)
    {
        _configService = configService;
        _apiService = apiService;
        _console = console;
    }

    public override int Execute(CommandContext context, CoolifySettings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(CommandContext context, CoolifySettings settings)
    {
        var instances = await _configService.ListInstancesAsync();

        if (!instances.Any())
        {
            _console.MarkupLine("[yellow]No Coolify instances registered.[/]");
            _console.MarkupLine("[cyan]Use 'pks coolify register <url>' to add one.[/]");
            return 0;
        }

        foreach (var instance in instances)
        {
            var uri = new Uri(instance.Url);
            _console.MarkupLine($"[bold cyan]{uri.Host}[/]");

            var connectionResult = await _apiService.TestConnectionAsync(instance);

            if (!connectionResult.Success)
            {
                _console.MarkupLine($"[red]{connectionResult.Error?.EscapeMarkup()}[/]");
                continue;
            }

            if (!string.IsNullOrEmpty(connectionResult.Version))
            {
                _console.MarkupLine($"[green]Connected[/] — Coolify {connectionResult.Version}");
            }

            if (settings.Debug)
            {
                try
                {
                    var rawJson = await _apiService.GetRawProjectsJsonAsync(instance);
                    _console.MarkupLine("[dim]GET /api/v1/projects:[/]");
                    _console.WriteLine(rawJson);

                    // Dump first project detail + its first environment
                    var tmpDoc = System.Text.Json.JsonDocument.Parse(rawJson);
                    if (tmpDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var p in tmpDoc.RootElement.EnumerateArray())
                        {
                            if (p.TryGetProperty("uuid", out var u))
                            {
                                var puuid = u.GetString();
                                if (string.IsNullOrEmpty(puuid)) continue;

                                var detailJson = await _apiService.GetRawProjectsJsonAsync(instance, puuid);
                                _console.MarkupLine($"[dim]GET /api/v1/projects/{puuid}:[/]");
                                _console.WriteLine(detailJson);

                                // Try first environment
                                var detailDoc = System.Text.Json.JsonDocument.Parse(detailJson);
                                if (detailDoc.RootElement.TryGetProperty("environments", out var envs) &&
                                    envs.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var env in envs.EnumerateArray())
                                    {
                                        var envName = env.TryGetProperty("name", out var n) ? n.GetString() : null;
                                        if (string.IsNullOrEmpty(envName)) continue;

                                        try
                                        {
                                            var envJson = await _apiService.GetRawProjectsJsonAsync(instance, $"{puuid}/{envName}");
                                            _console.MarkupLine($"[dim]GET /api/v1/projects/{puuid}/{envName}:[/]");
                                            _console.WriteLine(envJson);
                                        }
                                        catch (Exception ex)
                                        {
                                            _console.MarkupLine($"[dim]GET /api/v1/projects/{puuid}/{envName}: {ex.Message.EscapeMarkup()}[/]");
                                        }
                                        break;
                                    }
                                }
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _console.MarkupLine($"[red]Debug dump failed: {ex.Message.EscapeMarkup()}[/]");
                }
            }

            List<CoolifyProject> projects;
            try
            {
                projects = await _apiService.GetProjectsWithResourcesAsync(instance);
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]Failed to fetch projects: {ex.Message.EscapeMarkup()}[/]");
                continue;
            }

            if (settings.Verbose)
            {
                _console.MarkupLine($"[dim]Found {projects.Count} projects, total resources: {projects.Sum(p => p.Resources.Count)}[/]");
            }

            if (!projects.Any())
            {
                _console.MarkupLine("[dim]No projects found.[/]");
                continue;
            }

            foreach (var project in projects)
            {
                _console.MarkupLine($"  [yellow]{project.Name.EscapeMarkup()}[/]");

                if (!project.Resources.Any())
                {
                    _console.MarkupLine("    [dim]No resources[/]");
                    continue;
                }

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Blue)
                    .AddColumn("[cyan]Name[/]")
                    .AddColumn("[cyan]Type[/]")
                    .AddColumn("[cyan]Status[/]")
                    .AddColumn("[cyan]FQDN[/]");

                foreach (var resource in project.Resources)
                {
                    var statusColor = resource.Status switch
                    {
                        "running" => "green",
                        "stopped" => "red",
                        "exited" => "red",
                        _ => "yellow"
                    };

                    table.AddRow(
                        resource.Name.EscapeMarkup(),
                        resource.Type.EscapeMarkup(),
                        $"[{statusColor}]{resource.Status.EscapeMarkup()}[/]",
                        resource.Fqdn?.EscapeMarkup() ?? "[dim]-[/]");
                }

                _console.Write(table);
            }

            _console.WriteLine();
        }

        return 0;
    }
}
