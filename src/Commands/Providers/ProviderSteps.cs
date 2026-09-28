using PKS.Commands.Foundry;
using PKS.Commands.Google;
using PKS.Commands.Moonshot;
using PKS.Commands.Nvidia;
using PKS.Commands.OpenRouter;
using PKS.Commands.TypeSafe;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Providers;
using PKS.Infrastructure.Services.Security;
using PKS.Infrastructure.Services.TypeSafe;
using Spectre.Console;

namespace PKS.Commands.Providers;

/// <summary>
/// One model provider as `pks providers init` sees it. Every step is idempotent: it reports whether
/// the host is already set up, and it only writes when something is missing or changed — so the
/// wizard can be run again and again to open one more provider without disturbing the others.
///
/// The interactive path is the provider's own `pks &lt;provider&gt; init`, run in-process, so there is
/// one way to set each provider up and the wizard never drifts from it.
/// </summary>
public interface IProviderStep
{
    /// <summary>Short id used by <c>--only</c> and in the status table.</summary>
    string Id { get; }
    string DisplayName { get; }
    /// <summary>Who on this host can use the provider once it is set up.</summary>
    string Reach { get; }
    /// <summary>The variable `--from-env` reads, or null when the provider has no key (Foundry).</summary>
    string? EnvVar { get; }

    Task<bool> IsConfiguredAsync();

    /// <summary>Something worth telling the operator before asking, e.g. that a managed identity is
    /// available. Null for nothing.</summary>
    Task<string?> HintAsync();

    /// <summary>Runs the provider's own interactive init. True on success.</summary>
    Task<bool> RunInteractiveAsync(bool force);

    /// <summary>Non-interactive setup from <see cref="EnvVar"/>'s value (or, for Foundry, from the
    /// managed identity). Never prompts.</summary>
    Task<StepOutcome> ApplyUnattendedAsync(string? envValue, bool force);
}

public enum StepOutcome
{
    /// <summary>Nothing to do: no input for this provider.</summary>
    Skipped,
    /// <summary>Already set up with exactly this input.</summary>
    Unchanged,
    Stored,
    /// <summary>Stored, but the vendor could not be asked whether the key works.</summary>
    StoredUnverified,
    Failed,
}

/// <summary>Shared plumbing for the providers whose whole setup is "a key, validated, stored".</summary>
internal abstract class KeyStepBase : IProviderStep
{
    protected readonly IActionGuard Guard;
    protected readonly IAnsiConsole Console;

    protected KeyStepBase(IActionGuard guard, IAnsiConsole console)
    {
        Guard = guard;
        Console = console;
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Reach { get; }
    public abstract string? EnvVar { get; }
    public abstract Task<bool> IsConfiguredAsync();
    public virtual Task<string?> HintAsync() => Task.FromResult<string?>(null);
    public abstract Task<bool> RunInteractiveAsync(bool force);

    /// <summary>Validate; null = rejected, false = could not tell, true = valid.</summary>
    protected abstract Task<bool?> ValidateAsync(string apiKey);
    protected abstract Task StoreAsync(string apiKey);

    /// <summary>Whether <paramref name="apiKey"/> is already the stored key. Providers whose service
    /// cannot compare answer "configured at all", so a changed key needs <c>--force</c>.</summary>
    protected virtual async Task<bool> AlreadyStoredAsync(string apiKey) => await IsConfiguredAsync();

    public async Task<StepOutcome> ApplyUnattendedAsync(string? envValue, bool force)
    {
        if (string.IsNullOrWhiteSpace(envValue)) return StepOutcome.Skipped;
        var apiKey = envValue.Trim();
        if (!force && await AlreadyStoredAsync(apiKey)) return StepOutcome.Unchanged;

        var verdict = await ValidateAsync(apiKey);
        if (verdict is null)
        {
            Console.MarkupLine($"[red]{Markup.Escape(DisplayName)} refused the key in {EnvVar}.[/]");
            return StepOutcome.Failed;
        }

        try
        {
            await Guard.RequireAsync(new ActionRequest(ActionIds.CloudAuthWrite, $"Store {DisplayName} API credentials"));
        }
        catch (ActionGuardDeniedException ex)
        {
            Console.MarkupLine($"[red]Denied:[/] {Markup.Escape(ex.Message)}");
            return StepOutcome.Failed;
        }

        await StoreAsync(apiKey);
        return verdict.Value ? StepOutcome.Stored : StepOutcome.StoredUnverified;
    }
}

internal sealed class DirectApiKeyStep : KeyStepBase
{
    private readonly IProviderApiKeyService _keys;
    private readonly ApiKeyProvider _provider;

    public DirectApiKeyStep(ApiKeyProvider provider, IProviderApiKeyService keys, IActionGuard guard, IAnsiConsole console)
        : base(guard, console)
    {
        _provider = provider;
        _keys = keys;
    }

    public override string Id => _provider == ApiKeyProvider.Anthropic ? "anthropic" : "openai";
    public override string DisplayName => _provider == ApiKeyProvider.Anthropic ? "Anthropic Console API" : "OpenAI API";
    public override string Reach => _provider == ApiKeyProvider.Anthropic
        ? "runner jobs (claude), pks agent"
        : "runner jobs (codex)";
    public override string? EnvVar => _provider == ApiKeyProvider.Anthropic ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY";

    private string KeyUrl => _provider == ApiKeyProvider.Anthropic
        ? "https://console.anthropic.com/settings/keys"
        : "https://platform.openai.com/api-keys";

    public override Task<bool> IsConfiguredAsync() => _keys.HasKeyAsync(_provider);

    protected override Task<bool> AlreadyStoredAsync(string apiKey) => _keys.IsStoredKeyAsync(_provider, apiKey);

    protected override async Task<bool?> ValidateAsync(string apiKey)
    {
        var check = await _keys.ValidateAsync(_provider, apiKey);
        return check.Verdict switch
        {
            ApiKeyVerdict.Valid => true,
            ApiKeyVerdict.Rejected => null,
            _ => false,
        };
    }

    protected override Task StoreAsync(string apiKey) => _keys.StoreAsync(_provider, apiKey);

    public override async Task<bool> RunInteractiveAsync(bool force)
    {
        if (!force && await IsConfiguredAsync())
        {
            Console.MarkupLine($"[green]{Markup.Escape(DisplayName)} key is already registered.[/]");
            return true;
        }

        Console.MarkupLine($"[dim]Create a key at [link]{KeyUrl}[/]. It is billed per token, separately from any subscription.[/]");
        var apiKey = Console.Prompt(
            new TextPrompt<string>("[cyan]API key:[/]")
                .Secret()
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error("[red]API key is required.[/]")
                    : ValidationResult.Success()))
            .Trim();

        var check = await Console.Status().Spinner(Spinner.Known.Dots)
            .StartAsync("Validating API key...", _ => _keys.ValidateAsync(_provider, apiKey));
        if (check.Verdict == ApiKeyVerdict.Rejected)
        {
            Console.MarkupLine($"[red]{Markup.Escape(DisplayName)} refused the key.[/]");
            return false;
        }
        if (check.Verdict == ApiKeyVerdict.Inconclusive)
            Console.MarkupLine($"[yellow]Could not verify the key ({Markup.Escape(check.Detail ?? "no answer")}); storing it anyway.[/]");

        try
        {
            await Guard.RequireAsync(new ActionRequest(ActionIds.CloudAuthWrite, $"Store {DisplayName} API credentials"));
        }
        catch (ActionGuardDeniedException ex)
        {
            Console.MarkupLine($"[red]Denied:[/] {Markup.Escape(ex.Message)}");
            return false;
        }

        await _keys.StoreAsync(_provider, apiKey);
        Console.MarkupLine($"[green]{Markup.Escape(DisplayName)} key registered.[/]");
        return true;
    }
}

internal sealed class FoundryStep : IProviderStep
{
    // Read by --from-env: the machine's managed identity is the only unattended Foundry sign-in, and
    // these say which of the resources it can see to use when it sees several.
    internal const string ResourceEnv = "AZURE_FOUNDRY_RESOURCE";
    internal const string SubscriptionEnv = "AZURE_FOUNDRY_SUBSCRIPTION";
    internal const string ClientIdEnv = "AZURE_FOUNDRY_CLIENT_ID";

    private readonly IAzureFoundryAuthService _auth;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IManagedIdentityClient _managedIdentity;
    private readonly IAnsiConsole _console;

    public FoundryStep(IAzureFoundryAuthService auth, AzureFoundryAuthConfig config, IManagedIdentityClient managedIdentity, IAnsiConsole console)
    {
        _auth = auth;
        _config = config;
        _managedIdentity = managedIdentity;
        _console = console;
    }

    public string Id => "foundry";
    public string DisplayName => "Azure AI Foundry";
    public string Reach => "runner chat (chat-llm), pks agent, claude/codex via proxy";
    public string? EnvVar => null;

    public Task<bool> IsConfiguredAsync() => _auth.IsAuthenticatedAsync();

    public async Task<string?> HintAsync() =>
        await _managedIdentity.IsAvailableAsync()
            ? "this machine has an Azure managed identity — no sign-in needed"
            : null;

    private FoundryInitCommand Command() => new(_auth, _config, _console, _managedIdentity);

    public async Task<bool> RunInteractiveAsync(bool force) =>
        await Command().RunAsync(new FoundryInitCommand.Settings { Force = force }) == 0;

    public async Task<StepOutcome> ApplyUnattendedAsync(string? envValue, bool force)
    {
        if (!force && await IsConfiguredAsync()) return StepOutcome.Unchanged;
        // Unattended Foundry means managed identity, and only when asked for: a box that merely
        // happens to run on Azure should not start calling models on its identity's say-so.
        var resource = Environment.GetEnvironmentVariable(ResourceEnv);
        if (string.IsNullOrWhiteSpace(resource)) return StepOutcome.Skipped;
        if (!await _managedIdentity.IsAvailableAsync())
        {
            _console.MarkupLine($"[yellow]{ResourceEnv} is set, but this machine has no Azure managed identity.[/]");
            return StepOutcome.Failed;
        }

        var result = await Command().RunAsync(new FoundryInitCommand.Settings
        {
            Force = true,
            ManagedIdentity = true,
            Yes = true,
            Resource = resource,
            Subscription = Environment.GetEnvironmentVariable(SubscriptionEnv),
            ManagedIdentityClientId = Environment.GetEnvironmentVariable(ClientIdEnv),
        });
        return result == 0 ? StepOutcome.Stored : StepOutcome.Failed;
    }
}

internal sealed class GoogleStep : KeyStepBase
{
    private readonly IGoogleAiService _google;

    public GoogleStep(IGoogleAiService google, IActionGuard guard, IAnsiConsole console) : base(guard, console) => _google = google;

    public override string Id => "google";
    public override string DisplayName => "Google AI Studio";
    public override string Reach => "pks image, host tools";
    public override string? EnvVar => "GEMINI_API_KEY";
    public override Task<bool> IsConfiguredAsync() => _google.IsAuthenticatedAsync();
    public override Task<bool> RunInteractiveAsync(bool force) =>
        Task.FromResult(new GoogleInitCommand(_google, Console).Execute(null!, new GoogleInitCommand.Settings { Force = force }) == 0);
    protected override async Task<bool?> ValidateAsync(string apiKey) => await _google.ValidateApiKeyAsync(apiKey) ? true : null;
    protected override Task StoreAsync(string apiKey) => _google.StoreApiKeyAsync(apiKey);
}

internal sealed class OpenRouterStep : KeyStepBase
{
    private readonly IOpenRouterService _openRouter;

    public OpenRouterStep(IOpenRouterService openRouter, IActionGuard guard, IAnsiConsole console) : base(guard, console) => _openRouter = openRouter;

    public override string Id => "openrouter";
    public override string DisplayName => "OpenRouter";
    public override string Reach => "host tools via `pks openrouter proxy`";
    public override string? EnvVar => "OPENROUTER_API_KEY";
    public override Task<bool> IsConfiguredAsync() => _openRouter.IsAuthenticatedAsync();
    public override async Task<bool> RunInteractiveAsync(bool force) =>
        await new OpenRouterInitCommand(_openRouter, Guard, Console).ExecuteAsync(null!, new OpenRouterInitCommand.Settings { Force = force }) == 0;
    protected override async Task<bool?> ValidateAsync(string apiKey) => await _openRouter.ValidateApiKeyAsync(apiKey) is null ? null : true;
    protected override Task StoreAsync(string apiKey) => _openRouter.StoreCredentialsAsync(new OpenRouterStoredCredentials
    {
        ApiKey = SecretValue.From(apiKey),
        CreatedAt = DateTime.UtcNow,
    });
}

internal sealed class NvidiaStep : KeyStepBase
{
    private readonly INvidiaService _nvidia;

    public NvidiaStep(INvidiaService nvidia, IActionGuard guard, IAnsiConsole console) : base(guard, console) => _nvidia = nvidia;

    public override string Id => "nvidia";
    public override string DisplayName => "NVIDIA NIM";
    public override string Reach => "host tools via `pks nvidia proxy`";
    public override string? EnvVar => "NVIDIA_API_KEY";
    public override Task<bool> IsConfiguredAsync() => _nvidia.IsAuthenticatedAsync();
    public override async Task<bool> RunInteractiveAsync(bool force) =>
        await new NvidiaInitCommand(_nvidia, Guard, Console).ExecuteAsync(null!, new NvidiaInitCommand.Settings { Force = force }) == 0;
    protected override async Task<bool?> ValidateAsync(string apiKey)
    {
        var result = await _nvidia.ValidateApiKeyAsync(apiKey);
        return result.Verdict switch
        {
            NvidiaKeyVerdict.Valid => true,
            NvidiaKeyVerdict.Rejected => null,
            _ => false,
        };
    }
    protected override Task StoreAsync(string apiKey) => _nvidia.StoreCredentialsAsync(new NvidiaStoredCredentials
    {
        ApiKey = SecretValue.From(apiKey),
        CreatedAt = DateTime.UtcNow,
    });
}

internal sealed class MoonshotStep : KeyStepBase
{
    private readonly IMoonshotService _moonshot;

    public MoonshotStep(IMoonshotService moonshot, IActionGuard guard, IAnsiConsole console) : base(guard, console) => _moonshot = moonshot;

    public override string Id => "moonshot";
    public override string DisplayName => "Moonshot (Kimi)";
    public override string Reach => "host tools";
    public override string? EnvVar => "MOONSHOT_API_KEY";
    public override Task<bool> IsConfiguredAsync() => _moonshot.IsAuthenticatedAsync();
    public override async Task<bool> RunInteractiveAsync(bool force) =>
        await new MoonshotInitCommand(_moonshot, Guard, Console).ExecuteAsync(null!, new MoonshotInitCommand.Settings { Force = force }) == 0;
    protected override async Task<bool?> ValidateAsync(string apiKey) => await _moonshot.ValidateApiKeyAsync(apiKey) ? true : null;
    protected override Task StoreAsync(string apiKey) => _moonshot.StoreCredentialsAsync(new MoonshotStoredCredentials
    {
        ApiKey = SecretValue.From(apiKey),
        CreatedAt = DateTime.UtcNow,
    });
}

internal sealed class TypeSafeStep : KeyStepBase
{
    private readonly ITypeSafeCredentialService _typeSafe;
    private readonly ISecretStore _secrets;

    public TypeSafeStep(ITypeSafeCredentialService typeSafe, ISecretStore secrets, IActionGuard guard, IAnsiConsole console)
        : base(guard, console)
    {
        _typeSafe = typeSafe;
        _secrets = secrets;
    }

    public override string Id => "typesafe";
    public override string DisplayName => "TypeSafe (Jev)";
    public override string Reach => "host tools, `pks typesafe ask`";
    public override string? EnvVar => "TYPESAFE_API_KEY";
    public override Task<bool> IsConfiguredAsync() => _typeSafe.HasApiKeyAsync();
    public override async Task<bool> RunInteractiveAsync(bool force) =>
        await new TypeSafeInitCommand(_typeSafe, _secrets, Guard, Console).ExecuteAsync(null!, new TypeSafeInitCommand.Settings { Force = force }) == 0;
    protected override async Task<bool?> ValidateAsync(string apiKey) => await _typeSafe.ValidateApiKeyAsync(apiKey) is null ? null : true;
    protected override Task StoreAsync(string apiKey) => _secrets.SetAsync(TypeSafeCredentialService.ApiKeyKey, apiKey);
}
