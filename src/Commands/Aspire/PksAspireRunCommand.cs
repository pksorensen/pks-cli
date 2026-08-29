using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services.Exec;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Aspire;

/// <summary>
/// `aspire run`, with the parameters already answered.
///
/// An Aspire AppHost that needs a model endpoint, a key and a deployment name asks for them by
/// declaring parameters, and Aspire's honest answer is to stop and prompt — every run, or once into
/// user secrets, where the key then lives in plaintext on a laptop for as long as the project does.
/// The way out is not to make the prompt nicer. It is for the composition to say what *kind* of thing
/// it needs, and for the tool that is already signed in to supply it.
///
/// So this runs in two passes, the same two `pks exec` uses. First `aspire do pks-declare`, which
/// builds the AppHost, executes one dependency-free pipeline step and writes a manifest — no container
/// starts, nothing listens. Then the real `aspire run`, with the resolved values in its environment as
/// `Parameters__&lt;name&gt;`, which is the first place Aspire looks. The parameters resolve silently,
/// no prompt appears, and nothing was written to disk.
///
/// An AppHost without the step still works: the first pass fails, this says so, and the run continues
/// exactly as `aspire run` would have. The point is to remove a chore, not to become a dependency.
/// </summary>
[Description("Run an Aspire AppHost with its parameters resolved from what you are already signed in to")]
public sealed class PksAspireRunCommand : AsyncCommand<PksAspireRunCommand.Settings>
{
    private readonly IManifestResolver _resolver;
    private readonly IAnsiConsole _console;

    public PksAspireRunCommand(IManifestResolver resolver, IAnsiConsole console)
    {
        _resolver = resolver;
        _console = console;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--apphost <PATH>")]
        [Description("AppHost project file or a directory to search (passed through to aspire)")]
        public string? AppHost { get; set; }

        [CommandOption("--environment <NAME>")]
        [Description("Environment for the declare pass (default: Development, which is what the run uses)")]
        public string? Environment { get; set; }

        [CommandOption("--provider <KIND>")]
        [Description("Skip the provider prompt and use this kind (foundry, gemini, openai-compatible)")]
        public string? Provider { get; set; }

        [CommandOption("--port <N>")]
        [Description("Bind the managed-identity proxy to this port (default: a free one)")]
        public int? Port { get; set; }

        [CommandOption("--non-interactive")]
        [Description("Take the default for every question — for CI, where there is nobody to ask")]
        public bool NonInteractive { get; set; }

        [CommandOption("--dry-run")]
        [Description("Declare and resolve, print what would be set, and do not start the AppHost")]
        public bool DryRun { get; set; }

        [CommandOption("--start")]
        [Description("Use `aspire start` (detached) instead of `aspire run`")]
        public bool Start { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // The AppHost's own arguments decide which parameters exist — Margin's model
        // parameters only appear with `--ai` — so both passes must be given the same
        // ones. Declaring against a different composition than the one about to run
        // is the failure that looks like "pks resolved nothing".
        var appHostArgs = context.Remaining.Raw.ToList();

        var manifestPath = Path.Combine(
            Path.GetTempPath(),
            $"pks-declare-{Guid.NewGuid():N}.json");

        PksManifest manifest;
        try
        {
            manifest = await DeclareAsync(settings, appHostArgs, manifestPath);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]could not ask the AppHost what it needs: {ex.Message.EscapeMarkup()}[/]");
            HintAfterFailedDeclare(settings);
            return await StartAspireAsync(settings, appHostArgs, environment: null);
        }
        finally
        {
            TryDelete(manifestPath);
        }

        Report(manifest);

        var resolved = await _resolver.ResolveAsync(manifest, new ManifestResolveOptions
        {
            PreferredProvider = settings.Provider,
            ImdsPort = settings.Port,
            NonInteractive = settings.NonInteractive,
            AcceptOptional = true,
        });

        if (resolved is null)
        {
            _resolver.Release();
            _console.MarkupLine("[red]a required capability could not be filled — not starting.[/]");
            return 1;
        }

        ReportStillMissing(manifest, resolved);

        if (settings.DryRun)
        {
            _console.MarkupLine("\n[yellow]--dry-run, would start with:[/]");
            foreach (var (name, display) in resolved.Describe())
            {
                _console.MarkupLine($"  {name.EscapeMarkup()} = {display.EscapeMarkup()}");
            }
            _resolver.Release();
            return 0;
        }

        try
        {
            return await StartAspireAsync(settings, appHostArgs, resolved);
        }
        finally
        {
            _resolver.Release();
        }
    }

    // ---------- pass one: what does this composition need? ----------

    private async Task<PksManifest> DeclareAsync(Settings settings, IReadOnlyList<string> appHostArgs, string manifestPath)
    {
        var psi = NewAspire();
        foreach (var argument in DeclareArguments(settings, appHostArgs))
        {
            psi.ArgumentList.Add(argument);
        }

        // The manifest goes to a file rather than stdout for one reason: this pass
        // builds the AppHost, and MSBuild's output would arrive interleaved with it.
        // Scanning stdout for JSON tolerates noise before the document and nothing
        // after it, and a build writes on both sides.
        psi.Environment["PKS_DECLARE_OUT"] = manifestPath;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("aspire did not start — is the CLI installed?");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        // No timeout worth having: this compiles a project. `pks exec` gives a tool ten
        // seconds because a tool that has to think before printing its manifest is
        // broken; an AppHost that has to be built first is normal.
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);

        if (process.ExitCode != 0)
        {
            var detail = FirstMeaningfulLine(await stderr) ?? FirstMeaningfulLine(await stdout) ?? $"aspire exited {process.ExitCode}";
            throw new InvalidOperationException(detail);
        }

        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException("the AppHost has no `pks-declare` step");
        }

        return PksManifest.Parse(await File.ReadAllTextAsync(manifestPath));
    }

    /// <summary>
    /// Why the declare pass came back with nothing, in the two shapes that actually happen.
    ///
    /// They look identical from here — the step is missing either way — so the discriminator is on
    /// disk: an AppHost that already carries AgenticsDeclare, as a package reference or as the copied
    /// file, has been through `pks aspire init`, and telling them to run it again is advice that
    /// cannot work. What is far more likely then is a composition whose capabilities all sit behind a
    /// flag, declaring none of them for these arguments and so never registering the step on first use.
    ///
    /// Not read from the failure text: the useful line is written to Aspire's own log file, and what
    /// arrives on stderr is the pointer to that file.
    /// </summary>
    private void HintAfterFailedDeclare(Settings settings)
    {
        if (AppHostDirectory(settings) is { } directory && CarriesAgenticsDeclare(directory))
        {
            _console.MarkupLine(
                "[dim]This AppHost already has AgenticsDeclare, so the step is probably missing because nothing[/]");
            _console.MarkupLine(
                "[dim]declared a capability for these arguments — they are usually behind a flag ([bold]-- --ai[/]).[/]");
            _console.MarkupLine(
                "[dim]Add [bold]builder.AddAgenticsDeclare();[/] near the top of AppHost.cs so it can also declare nothing.[/]");
            return;
        }

        _console.MarkupLine("[dim]Add the `pks-declare` step with [bold]pks aspire init[/], or run [bold]aspire run[/] and answer the prompts.[/]");
    }

    /// <summary>
    /// Whether the AppHost half is present at all. Two shapes since the package exists: the
    /// PackageReference (the default since 2026-08-29) and the copied file — plus the pre-rename
    /// `PksDeclare.cs`, which is still wired, just under the old names.
    /// </summary>
    private static bool CarriesAgenticsDeclare(string directory)
    {
        try
        {
            if (File.Exists(Path.Combine(directory, "AgenticsDeclare.cs"))
                || File.Exists(Path.Combine(directory, "PksDeclare.cs")))
            {
                return true;
            }

            // The reference lives in the project file, or — for a single-file AppHost — in a
            // `#:package` directive at the top of the .cs itself.
            foreach (var file in Directory.EnumerateFiles(directory, "*.csproj")
                         .Concat(Directory.EnumerateFiles(directory, "apphost.cs")))
            {
                if (File.ReadAllText(file).Contains(PksAspireInitCommand.PackageId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A directory that cannot be read just means the general advice below stands.
        }

        return false;
    }

    /// <summary>Where `--apphost` points, when it points somewhere this process can look at.</summary>
    private static string? AppHostDirectory(Settings settings)
    {
        // No `--apphost` means aspire searched for one, and repeating that search here would be a
        // second implementation of somebody else's rule. The working directory is the common case
        // and the only one worth guessing.
        var hint = string.IsNullOrWhiteSpace(settings.AppHost)
            ? Directory.GetCurrentDirectory()
            : settings.AppHost;

        try
        {
            if (File.Exists(hint)) return Path.GetDirectoryName(Path.GetFullPath(hint));
            return Directory.Exists(hint) ? Path.GetFullPath(hint) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A hint that cannot be looked at is not worth a second error message on top of the
            // first one; the general advice below is still correct.
            return null;
        }
    }

    private void Report(PksManifest manifest)
    {
        _console.MarkupLine($"[green]declared:[/] {manifest.Name.EscapeMarkup()}");

        var unfillable = manifest.Parameters
            .Where(p => !p.Bound && !p.Supplied)
            .Select(p => p.Name)
            .ToList();

        if (unfillable.Count > 0)
        {
            // Said now rather than discovered when the run stops on a prompt. These are
            // parameters nothing bound and nothing has answered — a tenant id, a
            // connection string — and pks has no business inventing them.
            _console.MarkupLine(
                $"[dim]not something pks can fill, and not yet answered: {string.Join(", ", unfillable).EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// The gap between what a capability promised and what resolution produced.
    ///
    /// A skipped optional capability is a legitimate outcome — nothing was registered that could fill
    /// it — but the parameters it bound are then unanswered, and the composition reported them as
    /// <c>Bound</c>, so the earlier line stayed silent about them. Without this the run just stops on
    /// a dashboard prompt with no explanation of which decision led there.
    /// </summary>
    private void ReportStillMissing(PksManifest manifest, ResolvedEnvironment resolved)
    {
        var missing = manifest.Parameters
            .Where(p => p.Bound && !p.Supplied && !resolved.Contains(EnvironmentVariableFor(p)))
            .Select(p => p.Name)
            .ToList();

        if (missing.Count > 0)
        {
            _console.MarkupLine(
                $"[dim]still unanswered, so the run will ask: {string.Join(", ", missing).EscapeMarkup()}[/]");
        }
    }

    /// <summary>The environment variable Aspire reads a parameter from — the same derivation the
    /// AppHost side does, and the one thing both halves have to agree on.</summary>
    private static string EnvironmentVariableFor(PksParameterManifest parameter)
        => parameter.ConfigurationKey.Replace(":", "__", StringComparison.Ordinal);

    /// <summary>
    /// The other thing both halves have to agree on: the marker that says this run was started by pks.
    /// AgenticsDeclare reads exactly this name, so changing it here silently turns the dashboard
    /// reminder back on for every project that carries the package.
    /// </summary>
    internal const string StartedByPksVariable = "PKS_ASPIRE_RUN";

    // ---------- pass two: the run itself ----------

    private async Task<int> StartAspireAsync(Settings settings, IReadOnlyList<string> appHostArgs, ResolvedEnvironment? environment)
    {
        var psi = NewAspire();
        psi.ArgumentList.Add(settings.Start ? "start" : "run");
        AddAppHost(psi, settings);
        AddAppHostArgs(psi, appHostArgs);

        environment?.ApplyTo(psi);

        // Says "pks started this one" to the AppHost side, which otherwise has no way to tell.
        // AgenticsDeclare uses it to keep quiet: an AppHost that carries the declare step and is
        // started with plain `aspire run` puts a reminder on the dashboard, and a run that came
        // through here must not get one. Set on every path into this method, including the one
        // after a failed declare pass — that run is still a pks run, it just had nothing to fill.
        psi.Environment[StartedByPksVariable] = "1";

        if (environment is { Count: > 0 })
        {
            _console.MarkupLine($"[green]resolved[/] {environment.Count} parameter(s); starting aspire.");
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]failed to start aspire: {ex.Message.EscapeMarkup()}[/]");
            return 127;
        }

        if (process is null)
        {
            return 127;
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { process.Kill(entireProcessTree: true); } catch { }
        };

        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    /// <summary>
    /// The declare pass, as a list of arguments — separated out because the interesting part of it is
    /// a decision, and a decision deserves a test that does not have to start a process to read it.
    ///
    /// The decision is <c>-e Development</c>. `aspire do` documents its default as `Production`, and
    /// `aspire run` has no such option at all because it applies the AppHost's launch profile, which
    /// every Aspire template writes as `DOTNET_ENVIRONMENT=Development`. The two passes therefore run
    /// in different environments by default — and .NET loads user secrets only in Development, so the
    /// declare pass cannot see them and reports parameters as unanswered that the run will answer
    /// without asking. Measured on Margin: `fabric-tenant-id` and `fabric-client-id` sat in user
    /// secrets and were reported missing; with `-e Development` only the one genuinely absent value,
    /// `fabric-client-secret`, is named.
    ///
    /// Not read out of `Properties/launchSettings.json`: `aspire do` does not apply launch profiles,
    /// so the profile would have to be parsed and replayed here, and the only entry in it that changes
    /// this answer is the environment name. `--environment` is there for the AppHost that runs as
    /// something else.
    /// </summary>
    internal static List<string> DeclareArguments(Settings settings, IReadOnlyList<string> appHostArgs)
    {
        var arguments = new List<string> { "do", "pks-declare" };

        if (!string.IsNullOrWhiteSpace(settings.AppHost))
        {
            arguments.Add("--apphost");
            arguments.Add(settings.AppHost);
        }

        arguments.Add("--environment");
        arguments.Add(string.IsNullOrWhiteSpace(settings.Environment) ? "Development" : settings.Environment);
        arguments.Add("--non-interactive");
        arguments.Add("--nologo");

        if (appHostArgs.Count > 0)
        {
            // Last, and everything after it belongs to the AppHost — including anything that looks
            // like one of the options above.
            arguments.Add("--");
            arguments.AddRange(appHostArgs);
        }

        return arguments;
    }

    // ---------- plumbing ----------

    private static ProcessStartInfo NewAspire() => new()
    {
        FileName = "aspire",
        UseShellExecute = false,
        CreateNoWindow = false,
    };

    private static void AddAppHost(ProcessStartInfo psi, Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.AppHost))
        {
            psi.ArgumentList.Add("--apphost");
            psi.ArgumentList.Add(settings.AppHost);
        }
    }

    private static void AddAppHostArgs(ProcessStartInfo psi, IReadOnlyList<string> appHostArgs)
    {
        if (appHostArgs.Count == 0)
        {
            return;
        }

        psi.ArgumentList.Add("--");
        foreach (var arg in appHostArgs)
        {
            psi.ArgumentList.Add(arg);
        }
    }

    private static string? FirstMeaningfulLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('-'))
            {
                return trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…";
            }
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
