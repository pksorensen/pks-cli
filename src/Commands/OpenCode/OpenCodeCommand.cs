using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Agent.Anthropic;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.OpenCode;

/// <summary>
/// Launches OpenCode against a configured OpenAI-compatible provider. Provider configuration and
/// credentials are process-local, so pks does not modify the user's OpenCode configuration.
/// </summary>
public sealed class OpenCodeCommand : AsyncCommand<OpenCodeSettings>
{
    private const string DefaultModel = "glm-5.2";
    private readonly IScalewayService _scaleway;
    private readonly IMoonshotService _moonshot;
    private readonly IAnsiConsole _console;

    public static IReadOnlyList<OpenCodeProvider> Providers { get; } =
    [
        new(
            "scaleway",
            "Scaleway serverless",
            GenerativeModelCatalog.ScalewayBaseUrl,
            "PKS_SCALEWAY_API_KEY",
            GenerativeModelCatalog.Scaleway.Select(model => model.Id)
                .Append("glm-5.2")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()),
        new(
            "moonshot",
            "Moonshot API",
            MoonshotService.BaseUrl,
            "MOONSHOT_API_KEY",
            ["kimi-k3"]),
    ];

    public OpenCodeCommand(
        IScalewayService scaleway,
        IMoonshotService moonshot,
        IAnsiConsole console)
    {
        _scaleway = scaleway;
        _moonshot = moonshot;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, OpenCodeSettings settings)
    {
        var configured = new List<string>();
        if (await _scaleway.IsAuthenticatedAsync()) configured.Add("scaleway");
        if (await _moonshot.IsAuthenticatedAsync()) configured.Add("moonshot");

        OpenCodeProvider provider;
        try
        {
            provider = ResolveProvider(settings.Model, settings.Provider, configured, Providers);
        }
        catch (OpenCodeProviderException exception)
        {
            _console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            return 1;
        }

        var model = NormalizeModel(settings.Model, Providers);
        var apiKey = await GetApiKeyAsync(provider.Id);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _console.MarkupLine($"[red]No {Markup.Escape(provider.DisplayName)} API key is configured.[/]");
            _console.MarkupLine($"[dim]Run [bold]pks {provider.Id} init[/] first.[/]");
            return 1;
        }

        var startInfo = BuildStartInfo(provider, model, apiKey, settings.Args);
        _console.MarkupLine(
            $"[green]Launching OpenCode on[/] [bold]{Markup.Escape(model)}[/] " +
            $"[dim]({Markup.Escape(provider.DisplayName)})[/]");

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

    public static OpenCodeProvider ResolveProvider(
        string? model,
        string? requestedProvider,
        IReadOnlyCollection<string> configuredProviderIds,
        IReadOnlyCollection<OpenCodeProvider> providers)
    {
        var normalizedModel = NormalizeModel(model, providers);
        var prefixedProvider = ExtractProviderPrefix(model, providers);
        var explicitProvider = string.IsNullOrWhiteSpace(requestedProvider)
            ? prefixedProvider
            : requestedProvider.Trim();

        if (prefixedProvider is not null && !string.IsNullOrWhiteSpace(requestedProvider) &&
            !string.Equals(prefixedProvider, requestedProvider.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenCodeProviderException(
                $"Model prefix '{prefixedProvider}/' conflicts with --provider {requestedProvider.Trim()}.");
        }

        if (explicitProvider is not null)
        {
            var selected = providers.FirstOrDefault(provider =>
                string.Equals(provider.Id, explicitProvider, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
                throw new OpenCodeProviderException($"Unknown provider '{explicitProvider}'.");
            if (!selected.Offers(normalizedModel))
                throw new OpenCodeProviderException(
                    $"Model '{normalizedModel}' is not available from provider '{selected.Id}'.");
            if (!Contains(configuredProviderIds, selected.Id))
                throw NotConfigured(selected, normalizedModel);
            return selected;
        }

        var catalogMatches = providers.Where(provider => provider.Offers(normalizedModel)).ToList();
        if (catalogMatches.Count == 0)
        {
            var known = string.Join(", ", providers.SelectMany(provider => provider.Models).Distinct());
            throw new OpenCodeProviderException(
                $"Unknown model '{normalizedModel}'. Known models: {known}.");
        }

        var configuredMatches = catalogMatches
            .Where(provider => Contains(configuredProviderIds, provider.Id))
            .ToList();
        if (configuredMatches.Count == 1) return configuredMatches[0];
        if (configuredMatches.Count == 0 && catalogMatches.Count == 1)
            throw NotConfigured(catalogMatches[0], normalizedModel);

        if (configuredMatches.Count > 1)
        {
            var choices = string.Join(", ", configuredMatches.Select(provider => $"--provider {provider.Id}"));
            throw new OpenCodeProviderException(
                $"Model '{normalizedModel}' is available from multiple configured providers. Choose {choices}.");
        }

        throw new OpenCodeProviderException(
            $"No configured provider offers model '{normalizedModel}'. Configure one with pks <provider> init.");
    }

    public static ProcessStartInfo BuildStartInfo(
        OpenCodeProvider provider,
        string model,
        string apiKey,
        IReadOnlyList<string> nativeArgs)
    {
        var normalizedModel = NormalizeModel(model, Providers);
        var startInfo = new ProcessStartInfo
        {
            FileName = "opencode",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add($"{provider.Id}/{normalizedModel}");
        foreach (var argument in nativeArgs) startInfo.ArgumentList.Add(argument);

        startInfo.Environment[provider.ApiKeyEnvironmentVariable] = apiKey;
        startInfo.Environment["OPENCODE_CONFIG_CONTENT"] = BuildInlineConfig(provider, normalizedModel);
        return startInfo;
    }

    public static string BuildInlineConfig(OpenCodeProvider provider, string model)
    {
        var normalizedModel = NormalizeModel(model, Providers);
        var config = new Dictionary<string, object>
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["provider"] = new Dictionary<string, object>
            {
                [provider.Id] = new
                {
                    npm = "@ai-sdk/openai-compatible",
                    name = provider.DisplayName,
                    options = new
                    {
                        baseURL = provider.BaseUrl,
                        apiKey = $"{{env:{provider.ApiKeyEnvironmentVariable}}}",
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

    public static string NormalizeModel(
        string? model,
        IReadOnlyCollection<OpenCodeProvider>? providers = null)
    {
        var value = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        var prefix = ExtractProviderPrefix(value, providers ?? Providers);
        return prefix is null ? value : value[(prefix.Length + 1)..];
    }

    private async Task<string?> GetApiKeyAsync(string providerId) => providerId switch
    {
        "scaleway" => (await _scaleway.GetStoredCredentialsAsync())?.SecretKey,
        "moonshot" => (await _moonshot.GetStoredCredentialsAsync())?.ApiKey,
        _ => null,
    };

    private static string? ExtractProviderPrefix(
        string? model,
        IReadOnlyCollection<OpenCodeProvider> providers)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var slash = model.IndexOf('/');
        if (slash <= 0) return null;
        var possibleProvider = model[..slash];
        return providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, possibleProvider, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private static bool Contains(IEnumerable<string> values, string value) =>
        values.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static OpenCodeProviderException NotConfigured(OpenCodeProvider provider, string model) =>
        new($"Model '{model}' is available from {provider.DisplayName}, but it is not configured. " +
            $"Run pks {provider.Id} init first.");
}
