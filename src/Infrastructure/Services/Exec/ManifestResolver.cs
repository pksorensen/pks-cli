using System.Security.Cryptography;
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
    private readonly IModelCatalogService _catalog;

    private ImdsProxy? _imds;

    /// <summary>Credential proxies started for this run, one per provider kind, and the one throwaway
    /// token all of them accept. See <see cref="EnsureCredentialProxyAsync"/>.</summary>
    private readonly Dictionary<string, RunningCredentialProxy> _proxies = new(StringComparer.OrdinalIgnoreCase);
    private string? _proxyToken;
    private CachedFoundryToken? _foundryToken;

    /// <summary>
    /// App registrations typed in during this run and deliberately not written anywhere. They live for
    /// as long as the resolver does — which is one <c>aspire run</c> — and go the same way every other
    /// resolved value goes: into the child process's environment and nowhere else.
    /// </summary>
    private readonly Dictionary<string, EntraStoredApp> _entered = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient _catalogHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <param name="catalog">
    /// The model catalog and chat routing table every credential proxy serves. Optional so a resolver
    /// built by hand (tests, scripts) still works: without it the catalog knows the providers that
    /// need no stored key beyond Foundry's — Foundry, and the environment-configured kinds.
    /// (<c>pks agent</c> resolves model→provider on its own in <c>AgentChatProviderFactory</c>; the
    /// two are deliberately separate, this one is the gateway a child process talks to.)
    /// </param>
    public ManifestResolver(
        IAzureFoundryAuthService foundryAuth,
        AzureFoundryAuthConfig foundryConfig,
        IEntraApplicationService entra,
        IAnsiConsole console,
        IModelCatalogService? catalog = null)
    {
        _foundryAuth = foundryAuth;
        _foundryConfig = foundryConfig;
        _entra = entra;
        _console = console;
        _catalog = catalog ?? new ModelCatalogService(_catalogHttp, foundryAuth, foundryConfig);
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
        foreach (var proxy in _proxies.Values)
        {
            proxy.Stop();
        }
        _proxies.Clear();
        _proxyToken = null;
        _foundryToken = null;
        _entered.Clear();
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

            // An app registration is the one kind whose values a person can simply *have* — in the
            // portal, in a password manager — without pks having provisioned anything. So ask, rather
            // than skip a capability the operator could fill in ten seconds. Asking is not writing:
            // the answer stays in memory unless they say to keep it.
            // The capability check is the flag; the console check is the terminal. A run whose stdin is
            // a pipe has nobody to ask either, and Spectre's answer to being asked anyway is to throw —
            // which would turn a skipped capability into a failed run.
            var entra = capability.Providers.FirstOrDefault(
                p => string.Equals(p.Kind, "entra", StringComparison.OrdinalIgnoreCase));
            if (entra is not null
                && !options.NonInteractive
                && _console.Profile.Capabilities.Interactive
                && await PromptForEntraAsync(capability.Id))
            {
                return entra;
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
        "gemini" => (await _catalog.CredentialAsync(ProviderCatalog.Gemini)).HasValue,
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
        "gemini" => "run [bold]pks google init[/] or set GEMINI_API_KEY",
        "openai-compatible" => "set OPENAI_BASE_URL (and OPENAI_API_KEY where the endpoint wants one)",
        "entra" => $"provision one with [bold]pks entra app init \"{capabilityId.EscapeMarkup()}\" --alias {capabilityId.EscapeMarkup()}[/], "
                 + "or add [bold]--manual[/] to store one that already exists",
        _ => $"nothing registered can provide [bold]{kind.EscapeMarkup()}[/]",
    };

    /// <summary>
    /// The stored app registration a capability means. The alias defaults to the capability's own id,
    /// which is what makes <c>AddAgenticsCapability("margin-dev")</c> + <c>pks entra app init --alias
    /// margin-dev</c> line up without either side naming the other twice; a binding can override it
    /// per placeholder with <c>{entra:clientid:some-alias}</c>.
    /// </summary>
    private async Task<EntraStoredApp?> FindEntraAppAsync(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        // What was typed in for this run wins over what is on disk, so an operator can try a rotated
        // secret without overwriting the stored one first.
        if (_entered.TryGetValue(EntraApplicationService.Slug(alias), out var entered))
        {
            return entered;
        }

        var app = await _entra.GetStoredAsync(alias);
        return app is not null && app.ClientSecret.HasValue ? app : null;
    }

    /// <summary>
    /// Asks for an app registration's three values at run time and keeps them in memory for this run.
    ///
    /// This is the answer to "I have the credential but I do not want it on my disk": Aspire would also
    /// ask — its parameter dialog has a *Save to user secret* checkbox — but what it saves is plaintext
    /// under the project, for as long as the project exists. Here the default is not to save, and a run
    /// that does not save asks again next time, which is the price of that and worth saying out loud.
    /// </summary>
    private async Task<bool> PromptForEntraAsync(string alias)
    {
        var key = EntraApplicationService.Slug(alias);
        if (!_console.Confirm($"  enter the app registration for [yellow]{key.EscapeMarkup()}[/] now?", false))
        {
            return false;
        }

        var tenantId = _console.Prompt(
            new TextPrompt<string>("    tenant id:")
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error("[red]required[/]")
                    : ValidationResult.Success()))
            .Trim();

        var clientId = _console.Prompt(
            new TextPrompt<string>("    client id:")
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error("[red]required[/]")
                    : ValidationResult.Success()))
            .Trim();

        var secret = _console.Prompt(
            new TextPrompt<string>("    client secret:")
                .Secret()
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error("[red]required[/]")
                    : ValidationResult.Success()))
            .Trim();

        _entered[key] = new EntraStoredApp
        {
            Alias = key,
            DisplayName = key,
            AppId = clientId,
            TenantId = tenantId,
            ClientSecret = SecretValue.From(secret),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Default no. Somebody who wanted it stored would have run `pks entra app init --manual`;
        // getting here usually means they specifically did not.
        if (_console.Confirm($"    keep it under alias [yellow]{key.EscapeMarkup()}[/] so the next run does not ask?", false))
        {
            await _entra.SaveAsync(new EntraManualApp
            {
                Alias = key,
                DisplayName = key,
                TenantId = tenantId,
                ClientId = clientId,
                ClientSecret = SecretValue.From(secret),
            });
            _console.MarkupLine("    [green]stored[/] [dim]— encrypted, and readable only on its way into a child process[/]");
        }
        else
        {
            _console.MarkupLine("    [dim]this run only — nothing written to disk[/]");
        }

        return true;
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
            case "proxy:endpoint":
                return ((await EnsureCredentialProxyAsync(providerKind))?.Endpoint ?? "", false);
            case "proxy:token":
                return (await EnsureCredentialProxyAsync(providerKind) is null ? "" : _proxyToken!, true);
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

    /// <summary>
    /// A loopback proxy in front of the provider's endpoint, started on first use and stopped by
    /// <see cref="Release"/>. The child gets its URL and a throwaway token; the proxy swaps that token
    /// for the real credential on every request and WebSocket upgrade, so the credential never enters
    /// the child's environment. For Foundry that credential is an Entra token for the Cognitive Services
    /// scope — the same one <see cref="ImdsProxy"/> mints — which works for every data-plane route on
    /// the resource, REST and realtime alike. Null when the kind has nothing to put behind a proxy.
    /// </summary>
    private async Task<RunningCredentialProxy?> EnsureCredentialProxyAsync(string kind)
    {
        if (_proxies.TryGetValue(kind, out var running))
        {
            return running;
        }

        string upstream;
        Func<Task<SecretValue>> credential;
        string missing;
        switch (kind.ToLowerInvariant())
        {
            case "foundry":
                upstream = (await _foundryAuth.GetStoredCredentialsAsync())?.SelectedResourceEndpoint ?? "";
                _foundryToken ??= new CachedFoundryToken(_foundryAuth, _foundryConfig.CognitiveScope);
                credential = _foundryToken.GetAsync;
                missing = "No Foundry token available — sign in again with pks foundry init";
                break;
            case "openai-compatible":
                upstream = Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "";
                credential = () => Task.FromResult(SecretValue.From(Environment.GetEnvironmentVariable("OPENAI_API_KEY")));
                missing = "OPENAI_API_KEY is not set";
                break;
            default:
                return null;
        }

        if (string.IsNullOrWhiteSpace(upstream))
        {
            return null;
        }

        _proxyToken ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        // pks is the gateway, whichever provider the child is bound to: /pks/v1/models is the union
        // across every registered provider, and /pks/v1/chat/completions goes wherever the requested
        // model lives — so a tool needs no model configuration and no second credential of its own.
        running = await LocalCredentialProxy.StartAsync(_proxyToken, upstream, credential, missing,
            catalog: UnionCatalogJsonAsync,
            chat: _catalog.RouteChatAsync);
        _proxies[kind] = running;
        return running;
    }

    /// <summary>The catalog the proxy serves: every provider's models, or a 502 with every reason when none could be listed.</summary>
    private async Task<string> UnionCatalogJsonAsync(CancellationToken ct)
    {
        var result = await _catalog.BuildAsync(null, ct);
        if (result.Catalog.Data.Count == 0 && result.Failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", result.Failures.Select(f => $"{f.Kind}: {f.Error}")));
        }
        return ProviderCatalog.ToJson(result.Catalog);
    }

    /// <summary>
    /// Holds a Foundry access token for a few minutes. <see cref="IAzureFoundryAuthService.GetAccessTokenAsync"/>
    /// redeems the refresh token and rewrites the credential store on every call, which is fine for
    /// <see cref="ImdsProxy"/> — a client asks it once and keeps the token — but not for a proxy that
    /// signs every request: a burst of parallel calls would race each other rotating the refresh token.
    /// Five minutes is far inside any Entra access-token lifetime, and a failed fetch is not cached.
    /// </summary>
    private sealed class CachedFoundryToken
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

        private readonly IAzureFoundryAuthService _auth;
        private readonly string _scope;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private SecretValue _token;
        private DateTimeOffset _expires;

        public CachedFoundryToken(IAzureFoundryAuthService auth, string scope)
        {
            _auth = auth;
            _scope = scope;
        }

        public async Task<SecretValue> GetAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (_token.HasValue && DateTimeOffset.UtcNow < _expires)
                {
                    return _token;
                }

                _token = SecretValue.From(await _auth.GetAccessTokenAsync(_scope));
                _expires = DateTimeOffset.UtcNow + Lifetime;
                return _token;
            }
            finally
            {
                _lock.Release();
            }
        }
    }

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
                // The key pks google init stored, else the environment — the same order the catalog uses.
                return (await _catalog.CredentialAsync(ProviderCatalog.Gemini)).Reveal() ?? "";
            case "openai-compatible":
                return Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
            default:
                return "";
        }
    }
}
