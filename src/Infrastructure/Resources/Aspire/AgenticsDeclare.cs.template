// AgenticsDeclare — an AppHost says what it needs before it can run.
//
// An Aspire composition that wants a model endpoint, an API key or an app
// registration has, until it is handed one, exactly two moves: stop and ask a
// person to paste it, or find it already sitting in user secrets. This adds a
// third. The AppHost declares what it needs; a tool outside answers from what
// the developer is already signed in to; the values arrive in the environment
// and nothing is ever typed or written down.
//
// The tool that answers today is `pks aspire run` (`dotnet tool install -g
// pks-cli`), and the names on the wire below say so — `pks-declare`,
// PKS_DECLARE_OUT, PKS_ASPIRE_RUN. That split is deliberate: the API names what
// you declare, the wire names the tool that fills it. Nothing here references
// pks-cli or talks to it, and an AppHost carrying this package builds and runs
// exactly as before on a machine that has never heard of pks.
//
// What it adds is one pipeline step, `pks-declare`, with no dependencies and no
// resources behind it. `aspire do pks-declare` therefore builds the AppHost,
// walks the resource model and writes down which parameters this composition
// needs — and, for the ones that were bound below, which kind of credential
// would satisfy them. It starts nothing: no container, no app, no port.
//
// `pks aspire run` runs that step first, resolves the credentials on its own
// side, and then starts the real `aspire run` with the answers already in the
// environment as `Parameters__<name>`.
//
// Put one line near the top of AppHost.cs:
//
//     builder.AddAgenticsDeclare();
//
// It is not optional bookkeeping. The step registers itself the first time a
// capability is declared, and most compositions declare theirs behind a flag —
// `-- --ai`, `-- --real-entraid`. Run without those flags and there is no first
// time, so `aspire do pks-declare` fails with "step not found", which is exactly
// what an AppHost that was never wired says. The honest answer to "what does
// this run need?" is sometimes "nothing", and that answer needs a step to be
// given from.
//
// Declaring a capability is three kinds of line:
//
//     var chat = builder.AddAgenticsCapability("chat", "The model that answers on Overview");
//     chat.Offers("foundry", "Azure AI Foundry — sign in once with `pks foundry init`");
//     chat.Binds(aiBaseUrl, "{endpoint}");
//     chat.Binds(aiApiKey,  "{apikey}");
//     chat.Binds(aiModel,   "{model:default}");
//
// The placeholder vocabulary belongs to the answering tool, and it is
// deliberately small:
//
//     {endpoint}       the chosen provider's base URL
//     {endpoint:openai} the same, in the shape an OpenAI client can be pointed at
//     {apikey}         a key for that endpoint, if the provider has one
//     {model:<role>}   the model the developer picked for a named role
//     {imds:endpoint}  a loopback managed-identity proxy pks starts for the run
//     {imds:header}    that proxy's per-run secret
//     {entra:tenantid}     ┐ an app registration `pks entra app init`
//     {entra:clientid}     ├ provisioned, under the alias this capability
//     {entra:clientsecret} ┘ is named — or `{entra:clientid:other-alias}`
//
// A parameter that is never bound is still reported, as something the tool knows
// it cannot fill — which is the difference between "you have nothing configured"
// and "this run is going to stop and ask you".
//
// One more thing this package does, and it is the only thing it does that a
// person sees: an AppHost that carries the declare step and is started with
// plain `aspire run` puts a dismissible reminder on the dashboard saying it runs
// best with pks. A composition wired this way and started without it still
// works — it just stops and asks for the values pks would have supplied, and
// that prompt does not say why it appeared. The banner does. `pks aspire run`
// sets PKS_ASPIRE_RUN and the reminder stays away; so does a publish, a declare
// pass, and any host without a dashboard, which is what keeps it out of CI and
// out of `Aspire.Hosting.Testing`. Set PKS_ASPIRE_NO_REMINDER=1 to silence it on
// a machine that has no pks and does not want one.
//
// This shipped as a copied source file until 2026-08-29 (`pks aspire init` wrote
// PksDeclare.cs into the AppHost). An AppHost still carrying that copy must
// delete it before referencing this package: SuggestedValue would otherwise be
// defined twice and the step registered twice.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

#pragma warning disable ASPIREPIPELINES001 // Pipelines are experimental in Aspire 13.4.
#pragma warning disable ASPIREINTERACTION001 // Dashboard interactions are experimental in Aspire 13.4.

/// <summary>
/// Declares, to whoever is starting this AppHost, what it needs before it can run.
/// </summary>
public static class AgenticsDeclareExtensions
{
    /// <summary>The step name. `pks aspire run` looks for exactly this.</summary>
    public const string StepName = "pks-declare";

    /// <summary>Where the manifest is written when this environment variable is set. pks sets it;
    /// a person running the step by hand does not, and gets the manifest on the console instead.</summary>
    public const string OutputVariable = "PKS_DECLARE_OUT";

    /// <summary>Set by `pks aspire run` on the run it starts. Its absence is the whole signal behind
    /// the dashboard reminder, so the name has to match pks-cli's side exactly.</summary>
    public const string StartedByPksVariable = "PKS_ASPIRE_RUN";

    /// <summary>Opt out of the reminder on a machine that has no pks and does not want one.</summary>
    public const string SilenceReminderVariable = "PKS_ASPIRE_NO_REMINDER";

    private const string ManifestVersion = "v1";

    /// <summary>Whether this run came through `pks aspire run`.</summary>
    public static bool StartedByPks =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(StartedByPksVariable));

    /// <summary>
    /// Whether this process is the declare pass rather than a real run or a real publish.
    ///
    /// This matters more than it looks. `aspire do` executes in **publish** mode, so a composition
    /// that branches on <c>ExecutionContext.IsPublishMode</c> — a Key Vault instead of parameters, a
    /// real tenant instead of an emulator — describes the deployment while the run that follows is a
    /// local one, and the parameters pks was asked to fill are exactly the ones missing from the
    /// manifest. The symptom is a `pks aspire run` that resolves nothing and reports nothing wrong.
    ///
    /// So: fold this into the same condition. <c>IsPublishMode &amp;&amp; !IsDeclaring</c> reads as
    /// "actually publishing", and the declare pass then sees the composition the run will have.
    /// </summary>
    public static bool IsDeclaring =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OutputVariable));

    /// <summary>Builder-shaped spelling of <see cref="IsDeclaring"/>, for compositions that read
    /// better that way: <c>builder.ExecutionContext.IsPublishMode &amp;&amp; !builder.IsAgenticsDeclaring()</c>.</summary>
    public static bool IsAgenticsDeclaring(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return IsDeclaring;
    }

    /// <summary>
    /// Registers the `pks-declare` step, whether or not this composition ends up declaring anything.
    ///
    /// Worth one line in every AppHost that has this file. Capabilities usually sit behind a flag, so
    /// a run without that flag declares none, the step is never registered on first use, and the
    /// declare pass fails with "step not found" — indistinguishable from an AppHost pks was never
    /// added to. With this call the pass answers "nothing to fill" and the run carries on, which is
    /// both true and useful: the manifest still reports every parameter and whether it is already
    /// supplied.
    ///
    /// Idempotent, and safe to call alongside <see cref="AddAgenticsCapability"/> in either order.
    /// </summary>
    public static IDistributedApplicationBuilder AddAgenticsDeclare(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        _ = AgenticsDeclareState.For(builder);
        return builder;
    }

    /// <summary>
    /// Declares a capability this composition would like filled — a model to talk to, a registry to
    /// pull from — and registers the `pks-declare` step on first use.
    /// </summary>
    /// <param name="builder">The AppHost builder.</param>
    /// <param name="name">The capability's id, and the alias the answering tool resolves it under.</param>
    /// <param name="description">What this capability is for, shown when the developer is asked to choose.</param>
    /// <param name="required">
    /// A required capability is one the composition cannot run without, so pks refuses to start rather
    /// than leaving a parameter to be typed. The default is optional, which is the honest answer for
    /// anything sitting behind a flag.
    /// </param>
    public static AgenticsCapability AddAgenticsCapability(
        this IDistributedApplicationBuilder builder,
        string name,
        string description = "",
        bool required = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var state = AgenticsDeclareState.For(builder);
        var capability = new AgenticsCapability(name, description, required);
        state.Capabilities.Add(capability);
        return capability;
    }

    private sealed class AgenticsDeclareState
    {
        public List<AgenticsCapability> Capabilities { get; } = new();

        // Weak, so a builder that has been through Build() and gone out of scope goes with it. A
        // plain static dictionary would hold every composition the process ever made alive, which
        // is invisible in a CLI that builds one and fatal in a test run that builds hundreds.
        private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, AgenticsDeclareState> Registered = new();

        public static AgenticsDeclareState For(IDistributedApplicationBuilder builder)
        {
            // Not GetValue(builder, factory): that factory is allowed to run more than once under
            // contention and only one result is kept — and the one thing this factory does that
            // cannot be done twice is add the step.
            lock (Registered)
            {
                if (Registered.TryGetValue(builder, out var existing))
                {
                    return existing;
                }

                var state = new AgenticsDeclareState();
                Registered.Add(builder, state);

                // No dependencies and nothing requiring it: the step stands alone, so
                // `aspire do pks-declare` pulls in neither `parameter-prompt` nor a
                // build of any resource image. Asking what a run needs must not be
                // able to start the run.
                builder.Pipeline.AddStep(StepName, context => WriteManifestAsync(context, state));

                // Here rather than in AddAgenticsDeclare, so a composition that only declared a
                // capability is reminded too.
                AddRunReminder(builder);

                return state;
            }
        }
    }

    /// <summary>
    /// Puts a dismissible message bar on the dashboard when this AppHost was started the plain way.
    ///
    /// Three of the four guards are about not crying wolf: the declare pass is a pks run by
    /// definition, a publish has no dashboard to put anything on, and PKS_ASPIRE_RUN means pks
    /// already answered what it could. The fourth — <c>IsAvailable</c> — is the one that matters in
    /// CI and in <c>Aspire.Hosting.Testing</c>, where there is no dashboard client and a notification
    /// would be posted to nobody.
    /// </summary>
    private static void AddRunReminder(IDistributedApplicationBuilder builder)
    {
        if (IsDeclaring
            || StartedByPks
            || builder.ExecutionContext.IsPublishMode
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SilenceReminderVariable)))
        {
            return;
        }

        builder.Eventing.Subscribe<AfterResourcesCreatedEvent>((@event, cancellationToken) =>
        {
            var interactions = @event.Services.GetService<IInteractionService>();
            if (interactions is null || !interactions.IsAvailable)
            {
                return Task.CompletedTask;
            }

            // Not awaited, and deliberately: PromptNotificationAsync completes when the person
            // dismisses the bar, which may be never. Awaiting it here would hold the event and
            // the startup behind it for the lifetime of the run.
            _ = Task.Run(async () =>
            {
                try
                {
                    await interactions.PromptNotificationAsync(
                        "Started without pks",
                        "This AppHost can fill its own parameters. Start it with "
                            + "`dotnet dnx pks-cli aspire run` and the values come from what this "
                            + "machine is already signed in to, instead of being typed or stored.",
                        new NotificationInteractionOptions
                        {
                            Intent = MessageIntent.Information,
                            EnableMessageMarkdown = true,
                            LinkText = "pks-cli",
                            LinkUrl = "https://github.com/pksorensen/pks-cli",
                        },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // A reminder is the least important thing this process is doing.
                }
            });

            return Task.CompletedTask;
        });
    }

    private static async Task WriteManifestAsync(PipelineStepContext context, AgenticsDeclareState state)
    {
        var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var capability in state.Capabilities)
        {
            foreach (var binding in capability.Bindings)
            {
                bound.Add(binding.Parameter.Name);
            }
        }

        // Every parameter in the model, so the report covers the ones pks cannot
        // fill as well as the ones it can. Names and descriptions only — a value
        // here would be a credential on disk, which is the thing this exists to
        // avoid. `Supplied` is the nearest thing to a value that is safe to write
        // down: whether this parameter already resolves from the environment, a
        // user secret or a default, which is what turns "pks will fill three
        // parameters" into "and the other two are already answered".
        var parameters = new List<AgenticsParameterReport>();
        foreach (var p in context.Model.Resources.OfType<ParameterResource>().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            parameters.Add(new AgenticsParameterReport
            {
                Name = p.Name,
                ConfigurationKey = ConfigurationKeyOf(p),
                Secret = p.Secret,
                Description = p.Description ?? "",
                Bound = bound.Contains(p.Name),
                Supplied = await IsSuppliedAsync(p, context.CancellationToken).ConfigureAwait(false),
            });
        }

        var manifest = new AgenticsManifestReport
        {
            ManifestVersion = ManifestVersion,
            Name = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "apphost",
            Description = "Aspire AppHost",
            Capabilities = state.Capabilities.Select(c => c.ToReport()).ToList(),
            Parameters = parameters,
        };

        var json = JsonSerializer.Serialize(manifest, ManifestJson);

        var destination = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(destination))
        {
            // Run by hand. The console is the useful place for it, and it is also
            // the only place it can go without inventing a path in somebody's repo.
            context.Logger.LogInformation("{Manifest}", json);
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(destination, json, context.CancellationToken).ConfigureAwait(false);
        context.Logger.LogInformation("Declared {Count} parameter(s) to {Path}.", parameters.Count, destination);
    }

    /// <summary>
    /// Whether this parameter already has an answer. The value is asked for and immediately dropped:
    /// what comes back is a boolean, and the string it was derived from is never logged, serialized or
    /// returned. Anything that goes wrong — no value, no prompt available, a provider that throws —
    /// means "not supplied", because the only use of this is deciding what still needs asking.
    /// </summary>
    private static async Task<bool> IsSuppliedAsync(ParameterResource parameter, CancellationToken cancellationToken)
    {
        try
        {
            var value = await parameter.GetValueAsync(cancellationToken).ConfigureAwait(false);
            return !string.IsNullOrEmpty(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Where Aspire looks this parameter up. `ConfigurationKey` on the resource itself is internal, and
    /// the format it documents is stable and short enough to state here rather than reach for.
    /// </summary>
    internal static string ConfigurationKeyOf(ParameterResource parameter) =>
        parameter.IsConnectionString
            ? $"ConnectionStrings:{parameter.Name}"
            : $"Parameters:{parameter.Name}";

    /// <summary>
    /// The environment variable that fills that key. .NET's configuration reads `__` as the section
    /// separator, so `Parameters:ai-base-url` arrives as `Parameters__ai-base-url` — dash and all,
    /// because the name is not transformed on the way in.
    /// </summary>
    internal static string EnvironmentVariableFor(ParameterResource parameter) =>
        ConfigurationKeyOf(parameter).Replace(":", "__", StringComparison.Ordinal);

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    internal sealed class AgenticsManifestReport
    {
        public string ManifestVersion { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public List<AgenticsCapabilityReport> Capabilities { get; set; } = new();
        public List<AgenticsParameterReport> Parameters { get; set; } = new();
    }

    internal sealed class AgenticsCapabilityReport
    {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";
        public bool Required { get; set; }
        public List<AgenticsProviderReport> Providers { get; set; } = new();
    }

    internal sealed class AgenticsProviderReport
    {
        public string Kind { get; set; } = "";
        public string Description { get; set; } = "";
        public List<AgenticsModelReport> Models { get; set; } = new();
        public Dictionary<string, string> Env { get; set; } = new();
    }

    internal sealed class AgenticsModelReport
    {
        public string Role { get; set; } = "";
        public string Description { get; set; } = "";
    }

    internal sealed class AgenticsParameterReport
    {
        public string Name { get; set; } = "";
        public string ConfigurationKey { get; set; } = "";
        public bool Secret { get; set; }
        public string Description { get; set; } = "";
        public bool Bound { get; set; }
        public bool Supplied { get; set; }
    }
}

/// <summary>
/// One thing this composition wants — described by the providers that could supply it and the
/// parameters that receive it.
/// </summary>
public sealed class AgenticsCapability
{
    internal AgenticsCapability(string name, string description, bool required)
    {
        Name = name;
        Description = description;
        Required = required;
    }

    internal string Name { get; }
    internal string Description { get; }
    internal bool Required { get; }
    internal List<(string Kind, string Description)> Providers { get; } = new();
    internal List<AgenticsBinding> Bindings { get; } = new();

    /// <summary>
    /// Names a kind of provider that could fill this capability. pks offers the developer only the
    /// ones they are actually signed in to, so listing several is how a composition says "any of
    /// these will do" rather than committing to one.
    /// </summary>
    public AgenticsCapability Offers(string kind, string description = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        Providers.Add((kind, description));
        return this;
    }

    /// <summary>
    /// Sends one resolved value into one parameter. <paramref name="placeholder"/> is from pks-cli's
    /// vocabulary — <c>{endpoint}</c>, <c>{apikey}</c>, <c>{model:role}</c>, <c>{imds:endpoint}</c>,
    /// <c>{imds:header}</c> — and anything else is passed through as a literal.
    /// </summary>
    public AgenticsCapability Binds(
        IResourceBuilder<ParameterResource> parameter,
        string placeholder,
        string description = "")
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(placeholder);
        Bindings.Add(new AgenticsBinding(parameter.Resource, placeholder, description));
        return this;
    }

    internal AgenticsDeclareExtensions.AgenticsCapabilityReport ToReport()
    {
        // A model role is discovered from the bindings rather than declared twice: a
        // composition that binds `{model:default}` has, by saying so, told pks there is
        // a role called `default` to ask about.
        var roles = Bindings
            .Where(b => b.Placeholder.StartsWith("{model:", StringComparison.OrdinalIgnoreCase))
            .Select(b => new AgenticsDeclareExtensions.AgenticsModelReport
            {
                Role = b.Placeholder[7..].TrimEnd('}'),
                Description = string.IsNullOrEmpty(b.Description) ? b.Parameter.Description ?? "" : b.Description,
            })
            .GroupBy(m => m.Role, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // The same env map for every provider. Which provider was chosen changes what
        // `{endpoint}` and `{apikey}` resolve to, not where they land.
        var env = Bindings.ToDictionary(
            b => AgenticsDeclareExtensions.EnvironmentVariableFor(b.Parameter),
            b => b.Placeholder,
            StringComparer.Ordinal);

        var providers = Providers.Count > 0
            ? Providers
            : new List<(string Kind, string Description)> { ("openai-compatible", "") };

        return new AgenticsDeclareExtensions.AgenticsCapabilityReport
        {
            Id = Name,
            Description = Description,
            Required = Required,
            Providers = providers.Select(p => new AgenticsDeclareExtensions.AgenticsProviderReport
            {
                Kind = p.Kind,
                Description = p.Description,
                Models = roles,
                Env = env,
            }).ToList(),
        };
    }
}

internal sealed record AgenticsBinding(ParameterResource Parameter, string Placeholder, string Description);

/// <summary>
/// A starting value that anything else can still override.
///
/// <c>builder.AddParameter("ai-model", "gpt-4o-mini")</c> reads like a default and is not one. That
/// overload pins the value: configuration is not consulted, so an environment variable, a user secret
/// and anything pks resolves are all silently ignored, and the only symptom is that the parameter keeps
/// its old value while everything reports success. The overload that takes a
/// <see cref="ParameterDefault"/> is the one whose value is used <em>only</em> when nothing supplied
/// one, which is what a default means — so a parameter that both suggests something and can be filled
/// is written:
///
///     builder.AddParameter("ai-model", new SuggestedValue("gpt-4o-mini"))
/// </summary>
public sealed class SuggestedValue : ParameterDefault
{
    private readonly string _value;

    public SuggestedValue(string value) => _value = value;

    public override string GetDefaultValue() => _value;

    public override void WriteToManifest(Aspire.Hosting.Publishing.ManifestPublishingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Writer.WriteString("value", _value);
    }
}
