using System.Text;
using System.Text.RegularExpressions;
using PKS.Infrastructure.Services.Entra;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;

namespace PKS.Infrastructure.Services.Exec;

/// <inheritdoc />
public sealed class ManifestResolver : IManifestResolver
{
    private readonly IAzureFoundryAuthService _foundryAuth;
    private readonly AzureFoundryAuthConfig _foundryConfig;
    private readonly IEntraApplicationService _entra;
    private readonly IAnsiConsole _console;

    private ImdsProxy? _imds;

    public ManifestResolver(
        IAzureFoundryAuthService foundryAuth,
        AzureFoundryAuthConfig foundryConfig,
        IEntraApplicationService entra,
        IAnsiConsole console)
    {
        _foundryAuth = foundryAuth;
        _foundryConfig = foundryConfig;
        _entra = entra;
        _console = console;
    }

    public async Task<ResolvedEnvironment?> ResolveAsync(PksManifest manifest, ManifestResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);

        var resolved = new ResolvedEnvironment();

        foreach (var capability in manifest.Capabilities)
        {
            if (!capability.Required && !options.AcceptOptional && !options.NonInteractive)
            {
                if (!_console.Confirm($"Configure optional capability [yellow]{capability.Id.EscapeMarkup()}[/]?", false))
                {
                    continue;
                }
            }

            _console.MarkupLine($"\n[cyan]── {capability.Id.EscapeMarkup()}: {capability.Description.EscapeMarkup()} ──[/]");

            var provider = await ChooseProviderAsync(capability, options);
            if (provider is null)
            {
                // An optional capability with nothing behind it is a run without a
                // model, not a failed run — the composition said so by marking it
                // optional, and stopping here would take that choice away.
                if (!capability.Required)
                {
                    _console.MarkupLine($"  [dim]skipping {capability.Id.EscapeMarkup()} — nothing registered that can fill it.[/]");
                    continue;
                }

                _console.MarkupLine($"[red]no usable provider for {capability.Id.EscapeMarkup()}, which is required.[/]");
                return null;
            }

            var models = PromptModels(provider, options);
            await ResolveEnvironmentAsync(capability, provider, models, options, resolved);
        }

        return resolved;
    }

    public void Release()
    {
        _imds?.Stop();
        _imds = null;
    }

    // ---------- provider selection ----------

    private async Task<PksProviderManifest?> ChooseProviderAsync(PksCapabilityManifest capability, ManifestResolveOptions options)
    {
        var available = new List<PksProviderManifest>();
        foreach (var p in capability.Providers)
        {
            if (await IsAvailableAsync(p.Kind, capability.Id))
            {
                available.Add(p);
            }
        }

        if (available.Count == 0)
        {
            _console.MarkupLine("  [yellow]no registered provider matches this capability.[/]");
            // The hint is per offered kind, because "run pks foundry init" is useless advice to
            // somebody whose capability wanted an app registration.
            foreach (var hint in capability.Providers.Select(p => HintFor(p.Kind, capability.Id)).Distinct())
            {
                _console.MarkupLine($"  [dim]{hint}[/]");
            }
            return null;
        }

        if (!string.IsNullOrEmpty(options.PreferredProvider))
        {
            var match = available.FirstOrDefault(p =>
                string.Equals(p.Kind, options.PreferredProvider, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _console.MarkupLine($"  [red]--provider {options.PreferredProvider.EscapeMarkup()} is not registered, or not offered here.[/]");
            }
            return match;
        }

        if (available.Count == 1 || options.NonInteractive)
        {
            _console.MarkupLine($"  using [green]{available[0].Kind.EscapeMarkup()}[/]");
            return available[0];
        }

        return _console.Prompt(new SelectionPrompt<PksProviderManifest>()
            .Title("  select provider:")
            .UseConverter(p => string.IsNullOrEmpty(p.Description) ? p.Kind : $"{p.Kind} — {p.Description}")
            .AddChoices(available));
    }

    /// <summary>
    /// Whether the operator is signed in to this kind of provider. Deliberately a check and never a
    /// prompt: discovering what a tool could use must not turn into being asked to go and set up an
    /// account.
    /// </summary>
    private async Task<bool> IsAvailableAsync(string kind, string capabilityId) => kind.ToLowerInvariant() switch
    {
        "foundry" => await _foundryAuth.IsAuthenticatedAsync(),
        "gemini" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY")),
        "openai-compatible" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_BASE_URL"))
                             || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
        // An app registration pks provisioned, under the alias this capability is called. Available
        // means "already provisioned" — deliberately not "could be": writing to a company directory is
        // not something a run should decide to do on its way past.
        "entra" => await FindEntraAppAsync(capabilityId) is not null,
        _ => false,
    };

    /// <summary>What to do about a provider kind that is offered but not registered.</summary>
    private static string HintFor(string kind, string capabilityId) => kind.ToLowerInvariant() switch
    {
        "foundry" => "sign in with [bold]pks foundry init[/]",
        "gemini" => "set GEMINI_API_KEY",
        "openai-compatible" => "set OPENAI_BASE_URL (and OPENAI_API_KEY where the endpoint wants one)",
        "entra" => $"provision one with [bold]pks entra app init \"{capabilityId.EscapeMarkup()}\" --alias {capabilityId.EscapeMarkup()}[/]",
        _ => $"nothing registered can provide [bold]{kind.EscapeMarkup()}[/]",
    };

    /// <summary>
    /// The stored app registration a capability means. The alias defaults to the capability's own id,
    /// which is what makes <c>AddPksCapability("margin-dev")</c> + <c>pks entra app init --alias
    /// margin-dev</c> line up without either side naming the other twice; a binding can override it
    /// per placeholder with <c>{entra:clientid:some-alias}</c>.
    /// </summary>
    private async Task<EntraStoredApp?> FindEntraAppAsync(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        var app = await _entra.GetStoredAsync(alias);
        return app is not null && app.ClientSecret.HasValue ? app : null;
    }

    // ---------- model selection ----------

    private Dictionary<string, string> PromptModels(PksProviderManifest provider, ManifestResolveOptions options)
    {
        var choices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in provider.Models)
        {
            var suggestion = SuggestDefaultModel(provider.Kind, model.Role);
            if (options.NonInteractive)
            {
                choices[model.Role] = suggestion;
                continue;
            }

            // Spectre reads [foo] as markup, so a role has to arrive as [[foo]].
            var role = model.Role.EscapeMarkup();
            var label = string.IsNullOrEmpty(model.Description)
                ? $"  model[[{role}]]:"
                : $"  model[[{role}]] ({model.Description.EscapeMarkup()}):";
            choices[model.Role] = _console.Prompt(new TextPrompt<string>(label)
                .DefaultValue(suggestion)
                .ShowDefaultValue());
        }
        return choices;
    }

    private string SuggestDefaultModel(string kind, string role)
    {
        switch (kind.ToLowerInvariant())
        {
            case "foundry":
                var stored = _foundryAuth.GetStoredCredentialsAsync().GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(stored?.DefaultModel))
                {
                    return stored.DefaultModel;
                }
                return role == "fast" ? "claude-sonnet-4-6" : "claude-opus-4-7";
            case "gemini":
                return role == "fast" ? "gemini-2.5-flash" : "gemini-2.5-pro";
            case "openai-compatible":
                return role == "fast" ? "gpt-4o-mini" : "gpt-4o";
            default:
                return "";
        }
    }

    // ---------- placeholder expansion ----------

    private static readonly Regex PlaceholderRx =
        new(@"\{([a-z0-9:_\-]+)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private async Task ResolveEnvironmentAsync(
        PksCapabilityManifest capability,
        PksProviderManifest provider,
        IReadOnlyDictionary<string, string> models,
        ManifestResolveOptions options,
        ResolvedEnvironment into)
    {
        foreach (var (name, template) in provider.Env)
        {
            var (value, secret) = await ExpandAsync(template, provider.Kind, capability.Id, models, options);
            into.Set(name, value, secret);
        }
    }

    /// <summary>
    /// Expands one template. The secret flag travels with the value rather than being guessed from the
    /// variable's name later: <c>{apikey}</c> is a credential wherever it lands, and a variable called
    /// <c>Parameters__ai-api-key</c> would have been guessed right only by luck.
    /// </summary>
    private async Task<(string Value, bool Secret)> ExpandAsync(
        string template,
        string providerKind,
        string capabilityId,
        IReadOnlyDictionary<string, string> models,
        ManifestResolveOptions options)
    {
        if (!template.Contains('{'))
        {
            return (template, false);
        }

        var secret = false;
        var builder = new StringBuilder(template);

        // From the end, so the earlier match offsets stay valid.
        foreach (var match in PlaceholderRx.Matches(template).Reverse())
        {
            var name = match.Groups[1].Value;
            var (value, isSecret) = await ResolveOneAsync(name, providerKind, capabilityId, models, options);
            secret |= isSecret;
            builder.Remove(match.Index, match.Length);
            builder.Insert(match.Index, value);
        }

        return (builder.ToString(), secret);
    }

    private async Task<(string Value, bool Secret)> ResolveOneAsync(
        string name,
        string providerKind,
        string capabilityId,
        IReadOnlyDictionary<string, string> models,
        ManifestResolveOptions options)
    {
        if (name.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
        {
            return (models.TryGetValue(name[6..], out var m) ? m : "", false);
        }

        if (name.StartsWith("entra:", StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveEntraAsync(name[6..], capabilityId);
        }

        switch (name.ToLowerInvariant())
        {
            case "imds:endpoint":
                return (EnsureImds(options).Endpoint, false);
            case "imds:header":
                return (EnsureImds(options).Header, true);
            case "endpoint":
                return (await ResolveEndpointAsync(providerKind), false);
            case "endpoint:openai":
                return (await ResolveOpenAiEndpointAsync(providerKind), false);
            case "apikey":
                return (await ResolveApiKeyAsync(providerKind), true);
            default:
                return ("", false);
        }
    }

    private ImdsProxy EnsureImds(ManifestResolveOptions options)
        => _imds ??= ImdsProxy.Start(_foundryAuth, _foundryConfig, options.ImdsPort);

    private async Task<string> ResolveEndpointAsync(string kind) => kind.ToLowerInvariant() switch
    {
        "foundry" => (await _foundryAuth.GetStoredCredentialsAsync())?.SelectedResourceEndpoint ?? "",
        "openai-compatible" => Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "",
        _ => "",
    };

    /// <summary>
    /// The same endpoint, in the one shape an OpenAI client can be pointed at. Foundry's resource
    /// endpoint is the root of several APIs and the OpenAI-compatible one hangs off <c>/openai/v1</c>;
    /// an <c>OPENAI_BASE_URL</c> already is that URL and must not have anything appended. A composition
    /// that binds <c>{endpoint:openai}</c> is saying "point an OpenAI client at this", and stays
    /// correct whichever provider the operator picks.
    /// </summary>
    private async Task<string> ResolveOpenAiEndpointAsync(string kind)
    {
        switch (kind.ToLowerInvariant())
        {
            case "foundry":
                var endpoint = (await _foundryAuth.GetStoredCredentialsAsync())?.SelectedResourceEndpoint ?? "";
                if (string.IsNullOrEmpty(endpoint))
                {
                    return "";
                }
                endpoint = endpoint.TrimEnd('/');
                return endpoint.Contains("/openai", StringComparison.OrdinalIgnoreCase)
                    ? endpoint
                    : endpoint + "/openai/v1";
            default:
                return await ResolveEndpointAsync(kind);
        }
    }

    /// <summary>
    /// One field of a provisioned app registration. <c>field</c> is <c>tenantid</c>, <c>clientid</c> or
    /// <c>clientsecret</c>, optionally followed by <c>:alias</c> when the alias is not the capability's
    /// own name — <c>{entra:clientsecret:margin-dev}</c>.
    ///
    /// The secret leaves here as a plain string on its way into a <see cref="ResolvedEnvironment"/>,
    /// which is the one place that is allowed: from there it can only reach a child process's
    /// environment through <see cref="SecretSink"/>, and the flag travelling with it is what keeps it
    /// out of the console.
    /// </summary>
    private async Task<(string Value, bool Secret)> ResolveEntraAsync(string spec, string capabilityId)
    {
        var parts = spec.Split(':', 2);
        var field = parts[0].ToLowerInvariant();
        var alias = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : capabilityId;

        var app = await FindEntraAppAsync(alias);
        if (app is null)
        {
            return ("", field == "clientsecret");
        }

        return field switch
        {
            "tenantid" or "tenant" => (app.TenantId, false),
            "clientid" or "appid" => (app.AppId, false),
            "clientsecret" or "secret" => (app.ClientSecret.Reveal() ?? "", true),
            _ => ("", false),
        };
    }

    private async Task<string> ResolveApiKeyAsync(string kind)
    {
        switch (kind.ToLowerInvariant())
        {
            case "foundry":
                // The key the Foundry portal issues, when the operator saved one. Without it a Foundry
                // run has to go through the identity proxy instead, which is what `{imds:*}` is for.
                var stored = await _foundryAuth.GetStoredCredentialsAsync();
                return stored is null ? "" : stored.ApiKey.Reveal() ?? "";
            case "gemini":
                return Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
            case "openai-compatible":
                return Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
            default:
                return "";
        }
    }
}
