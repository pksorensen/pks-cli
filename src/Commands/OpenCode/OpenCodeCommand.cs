using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Agent.Anthropic;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.OpenCode;

/// <summary>
/// Launches OpenCode directly against Scaleway's OpenAI-compatible serverless API. The inline
/// provider config is process-local, so pks does not modify the user's OpenCode configuration or
/// duplicate the Scaleway secret in another credential store.
/// </summary>
public sealed class OpenCodeCommand : AsyncCommand<OpenCodeSettings>
{
    private const string DefaultModel = "glm-5.2";
    private readonly IScalewayService _scaleway;
    private readonly IAnsiConsole _console;

    public OpenCodeCommand(IScalewayService scaleway, IAnsiConsole console)
    {
        _scaleway = scaleway;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, OpenCodeSettings settings)
    {
        if (!await _scaleway.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]Not authenticated with Scaleway.[/]");
            _console.MarkupLine("[dim]Run [bold]pks scaleway init[/] first.[/]");
            return 1;
        }

        var credentials = await _scaleway.GetStoredCredentialsAsync();
        if (credentials is null || string.IsNullOrWhiteSpace(credentials.SecretKey))
        {
            _console.MarkupLine("[red]No Scaleway secret key configured — run [bold]pks scaleway init[/].[/]");
            return 1;
        }

        var model = NormalizeModel(settings.Model);
        var startInfo = BuildStartInfo(model, credentials.SecretKey, settings.Args);
        _console.MarkupLine($"[green]Launching OpenCode on[/] [bold]{Markup.Escape(model)}[/] [dim](Scaleway serverless)[/]");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _console.MarkupLine("[red]Failed to start the opencode CLI.[/]");
                return 1;
            }

            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception)
        {
            _console.MarkupLine("[red]Could not find the [bold]opencode[/] CLI on PATH.[/]");
            _console.MarkupLine("[dim]Install OpenCode from [link]https://opencode.ai/docs[/].[/]");
            return 127;
        }
    }

    public static ProcessStartInfo BuildStartInfo(
        string model,
        string secretKey,
        IReadOnlyList<string> nativeArgs)
    {
        var normalizedModel = NormalizeModel(model);
        var startInfo = new ProcessStartInfo
        {
            FileName = "opencode",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add($"scaleway/{normalizedModel}");
        foreach (var argument in nativeArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["PKS_SCALEWAY_API_KEY"] = secretKey;
        startInfo.Environment["OPENCODE_CONFIG_CONTENT"] = BuildInlineConfig(normalizedModel);
        return startInfo;
    }

    public static string BuildInlineConfig(string model)
    {
        var normalizedModel = NormalizeModel(model);
        var config = new Dictionary<string, object>
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["provider"] = new Dictionary<string, object>
            {
                ["scaleway"] = new
                {
                    npm = "@ai-sdk/openai-compatible",
                    name = "Scaleway Generative APIs",
                    options = new
                    {
                        baseURL = GenerativeModelCatalog.ScalewayBaseUrl,
                        apiKey = "{env:PKS_SCALEWAY_API_KEY}",
                    },
                    models = new Dictionary<string, object>
                    {
                        [normalizedModel] = new { name = normalizedModel },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(config);
    }

    private static string NormalizeModel(string? model)
    {
        var value = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        const string prefix = "scaleway/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
    }
}
