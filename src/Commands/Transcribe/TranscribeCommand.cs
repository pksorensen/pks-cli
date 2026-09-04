using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using PKS.Infrastructure.Services.Transcription;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Transcribe;

/// <summary>
/// <c>pks transcribe &lt;media&gt;</c> — a recording in, a meeting folder out.
///
/// BREAKING, ON PURPOSE. This used to shell out to heypoul, which transcribed blind
/// 60-second chunks with no speaker labels. It is now native: two engines, merged word by
/// word, written in the format <c>docs/meeting-folder-format.md</c> describes. heypoul is
/// still here — it is a push-to-talk dictation daemon and good at that — but under
/// <c>pks voice transcribe</c>, where the rest of heypoul lives.
///
/// The audio never leaves this machine except to go to the speech model the caller
/// configured. There is no upload to an Agentics service, and there is no place in this
/// command where one could be added without it being obvious.
/// </summary>
[Description("Transcribe a recording into a meeting folder (verbatim text + speaker labels)")]
public sealed class TranscribeCommand : AsyncCommand<TranscribeCommand.Settings>
{
    private readonly TranscriptionPipeline _pipeline;
    private readonly TranscriptionProviderRegistry _providers;
    private readonly MeetingFolderWriter _writer;
    private readonly IAnsiConsole _console;

    public TranscribeCommand(
        TranscriptionPipeline pipeline,
        TranscriptionProviderRegistry providers,
        MeetingFolderWriter writer,
        IAnsiConsole console)
    {
        _pipeline = pipeline;
        _providers = providers;
        _writer = writer;
        _console = console;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<media>")]
        [Description("Recording to transcribe (mp4, m4a, mp3, wav, …)")]
        public string Media { get; set; } = "";

        [CommandOption("--out|-o")]
        [Description("Meeting folder to write. Default: the folder the recording sits in, when it is a raw/ folder")]
        public string? Out { get; set; }

        [CommandOption("--speakers")]
        [Description("Name the diariser's labels: \"1=Poul,2=Jakob\". Unnamed labels stay \"Taler N\"")]
        public string? Speakers { get; set; }

        [CommandOption("--max-speakers")]
        [Description("Upper bound for the diariser (2-12, default 6)")]
        public int MaxSpeakers { get; set; } = 6;

        [CommandOption("--words-only")]
        [Description("Skip speaker recognition: verbatim text, no names, one API call per part")]
        public bool WordsOnly { get; set; }

        [CommandOption("--method")]
        [Description("two-engine (default) or foundry-diarize")]
        public string? Method { get; set; }

        [CommandOption("--provider")]
        [Description("Override the provider that produces the words")]
        public string? Provider { get; set; }

        [CommandOption("--labels-provider")]
        [Description("Override the provider that produces the speaker labels")]
        public string? LabelsProvider { get; set; }

        [CommandOption("--locale|-l")]
        [Description("BCP-47 locale (default da-DK)")]
        public string Locale { get; set; } = "da-DK";

        [CommandOption("--phrases")]
        [Description("Comma-separated names and terms to bias the model towards")]
        public string? Phrases { get; set; }

        [CommandOption("--title")]
        [Description("Meeting title (default: derived from the folder or file name)")]
        public string? Title { get; set; }

        [CommandOption("--date")]
        [Description("Meeting date, yyyy-MM-dd (default: from the folder name, else the file's timestamp)")]
        public string? Date { get; set; }

        [CommandOption("--part-seconds")]
        [Description("Length of each transcribed part (default 900)")]
        public int PartSeconds { get; set; } = AudioPreparation.DefaultPartSeconds;

        [CommandOption("--compare")]
        [Description("Run the same audio through several words engines and report how far they agree")]
        public string? Compare { get; set; }

        [CommandOption("--replay")]
        [Description("Re-run the merge over the engine responses already in raw/engine/ — no API calls")]
        public bool Replay { get; set; }

        [CommandOption("--overwrite-readme")]
        [Description("Rewrite README.md. Off by default — it holds judgement the CLI did not produce")]
        public bool OverwriteReadme { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!File.Exists(settings.Media))
        {
            _console.MarkupLine($"[red]Not found:[/] {Markup.Escape(settings.Media)}");

            return 1;
        }

        var media = Path.GetFullPath(settings.Media);
        var outputDirectory = settings.Out is { Length: > 0 }
            ? Path.GetFullPath(settings.Out)
            : DefaultOutputDirectory(media);

        var options = new TranscriptionPipelineOptions
        {
            MediaPath = media,
            OutputDirectory = outputDirectory,
            Locale = settings.Locale,
            MaxSpeakers = settings.MaxSpeakers,
            MethodId = settings.Method ?? TranscriptionMethods.Default,
            PhraseList = Split(settings.Phrases),
            PartSeconds = settings.PartSeconds,
            WordsOnly = settings.WordsOnly,
            WordsProvider = settings.Provider,
            LabelsProvider = settings.LabelsProvider,
            Replay = settings.Replay,
        };

        var speakers = ParseSpeakers(settings.Speakers);

        if (Split(settings.Compare) is { Count: > 0 } comparing)
        {
            return await CompareAsync(options, comparing, settings.Replay);
        }

        var method = TranscriptionMethods.Resolve(options.MethodId);
        _console.MarkupLine($"[grey]{Markup.Escape(method.Name)} — {Markup.Escape(method.Tagline)}[/]");
        _console.MarkupLine($"[grey]→ {Markup.Escape(outputDirectory)}[/]");

        // Replay reads saved engine output; a credential it will never use must not block it.
        if (!settings.Replay && await ReportMissingCredentialsAsync(options, method) is { } credentialFailure)
        {
            return credentialFailure;
        }

        TranscriptResult transcript;
        try
        {
            transcript = await _pipeline.RunAsync(
                options,
                line => _console.MarkupLine($"[grey]{Markup.Escape(line)}[/]"));
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Transcription failed:[/] {Markup.Escape(ex.Message)}");

            return 1;
        }

        var written = _writer.Write(transcript, new MeetingFolderWriter.Options
        {
            Directory = outputDirectory,
            Title = settings.Title ?? DeriveTitle(media, outputDirectory),
            Date = ParseDate(settings.Date, media, outputDirectory),
            Speakers = speakers,
            SourcePath = media,
            OverwriteReadme = settings.OverwriteReadme,
        });

        _console.WriteLine();
        _console.MarkupLine($"[green]{transcript.Turns.Count}[/] turns · "
            + $"[green]{transcript.SpeakerCount}[/] speaker(s) · "
            + $"{transcript.ApiCalls} API call(s) over {transcript.PartCount} part(s)");
        if (transcript.LabelsProviderKey is not null)
        {
            _console.MarkupLine($"[grey]{transcript.AnchoredRatio * 100:0.0} % of words anchored to the "
                + $"diarised track · {transcript.SnappedWords} pulled back to their sentence[/]");
        }
        else
        {
            _console.MarkupLine("[yellow]No speaker labels.[/] [grey]The words are all there; the names are not.[/]");
        }

        foreach (var path in written)
        {
            _console.MarkupLine($"  [grey]{Markup.Escape(Path.GetRelativePath(outputDirectory, path))}[/]");
        }

        if (speakers.Count == 0 && transcript.LabelsProviderKey is not null)
        {
            _console.WriteLine();
            _console.MarkupLine("[grey]Speakers are numbered. Re-run with[/] "
                + "[white]--speakers \"1=Name,2=Name\"[/] [grey]to name them — "
                + "no new API calls are needed for the naming itself.[/]");
        }

        return 0;
    }

    /// <summary>
    /// Benchmark mode: the same audio through several engines, then a table. Writes no
    /// meeting folder — a comparison is a measurement, not a transcript, and the folder
    /// format has exactly one method's output in it by design.
    /// </summary>
    private async Task<int> CompareAsync(
        TranscriptionPipelineOptions options, List<string> keys, bool replay)
    {
        foreach (var key in keys)
        {
            var provider = _providers.Get(key);
            if (provider is null)
            {
                _console.MarkupLine($"[red]Unknown provider[/] {Markup.Escape(key)}. Known: "
                    + string.Join(", ", _providers.GetAllProviders().Select(p => p.ProviderKey)));

                return 1;
            }

            if (!replay && !await provider.IsAuthenticatedAsync())
            {
                _console.MarkupLine($"[red]{Markup.Escape(provider.DisplayName)}[/] "
                    + $"([grey]{Markup.Escape(key)}[/]) has no credentials — nothing to compare it with.");

                return 1;
            }
        }

        List<TranscriptionPipeline.ComparisonRun> runs;
        try
        {
            runs = await _pipeline.CompareAsync(
                options, keys, line => _console.MarkupLine($"[grey]{Markup.Escape(line)}[/]"));
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Comparison failed:[/] {Markup.Escape(ex.Message)}");

            return 1;
        }

        var directory = Path.Combine(options.OutputDirectory, "compare");
        Directory.CreateDirectory(directory);
        foreach (var run in runs.Where(r => r.Failure is null))
        {
            File.WriteAllText(
                Path.Combine(directory, $"{run.ProviderKey}.txt"), string.Join(" ", run.Words) + "\n");
        }

        _console.WriteLine();
        ReportComparison(_console, runs);
        _console.MarkupLine($"[grey]→ {Markup.Escape(directory)}[/]");

        return runs.All(r => r.Failure is not null) ? 1 : 0;
    }

    /// <summary>
    /// The comparison table. Agreement is measured against the first engine listed, so the
    /// order of <c>--compare</c> chooses the reference — which is the honest framing when
    /// there is no gold transcript: this says how far the others differ from that one, not
    /// which of them is right.
    /// </summary>
    internal static void ReportComparison(
        IAnsiConsole console, IReadOnlyList<TranscriptionPipeline.ComparisonRun> runs)
    {
        var reference = runs.FirstOrDefault(r => r.Failure is null);
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Engine");
        table.AddColumn(new TableColumn("Words").RightAligned());
        table.AddColumn(new TableColumn("Seconds").RightAligned());
        table.AddColumn(new TableColumn(reference is null ? "Agreement" : $"vs {reference.ProviderKey}").RightAligned());

        foreach (var run in runs)
        {
            if (run.Failure is not null)
            {
                table.AddRow(
                    Markup.Escape(run.ProviderKey), "—", "—", $"[red]{Markup.Escape(Trim(run.Failure))}[/]");

                continue;
            }

            var agreement = reference is null || ReferenceEquals(run, reference)
                ? "reference"
                : $"{TranscriptionPipeline.Agreement(reference.Words, run.Words) * 100:0.0} %";
            table.AddRow(
                Markup.Escape(run.ProviderKey),
                run.Words.Count.ToString(CultureInfo.InvariantCulture),
                (run.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture),
                agreement);
        }

        console.Write(table);
    }

    private static string Trim(string message) => message.Length <= 60 ? message : message[..60] + "…";

    /// <summary>
    /// Say which credential is missing before spending minutes of audio finding out. A
    /// provider that cannot authenticate fails identically on part 1 and part 8; the
    /// difference is that part 8 costs seven parts of billing first.
    /// </summary>
    private async Task<int?> ReportMissingCredentialsAsync(
        TranscriptionPipelineOptions options, TranscriptionMethod method)
    {
        var keys = new List<string> { options.WordsProvider ?? method.WordsProvider };
        var labels = options.WordsOnly ? null : options.LabelsProvider ?? method.LabelsProvider;
        if (labels is not null) keys.Add(labels);

        foreach (var key in keys)
        {
            var provider = _providers.Get(key);
            if (provider is null)
            {
                _console.MarkupLine($"[red]Unknown provider[/] {Markup.Escape(key)}. Known: "
                    + string.Join(", ", _providers.GetAllProviders().Select(p => p.ProviderKey)));

                return 1;
            }

            if (!await provider.IsAuthenticatedAsync())
            {
                _console.MarkupLine($"[red]{Markup.Escape(provider.DisplayName)}[/] has no credentials. "
                    + "Run [white]pks foundry init[/], or export PKS_FOUNDRY_ENDPOINT and PKS_FOUNDRY_API_KEY.");

                return 1;
            }
        }

        return null;
    }

    // ── defaults derived from where the file sits ───────────────────────────────

    /// <summary>
    /// A recording at <c>&lt;meeting&gt;/raw/audio.wav</c> belongs to <c>&lt;meeting&gt;</c>, so
    /// re-transcribing writes back into the folder it came from instead of nesting a second
    /// one inside it. Anywhere else, a sibling folder.
    /// </summary>
    private static string DefaultOutputDirectory(string media)
    {
        var directory = Path.GetDirectoryName(media)!;
        if (string.Equals(Path.GetFileName(directory), "raw", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(directory)!;
        }

        return Path.Combine(directory, Path.GetFileNameWithoutExtension(media) + "-transcript");
    }

    private static readonly Regex DatePrefix = new(@"^(\d{4})-(\d{2})-(\d{2})[-_]?(.*)$", RegexOptions.Compiled);

    private static string DeriveTitle(string media, string outputDirectory)
    {
        var folder = Path.GetFileName(outputDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var match = folder is { Length: > 0 } ? DatePrefix.Match(folder) : Match.Empty;
        var stem = match.Success && match.Groups[4].Value.Length > 0
            ? match.Groups[4].Value
            : Path.GetFileNameWithoutExtension(media);

        var words = stem.Replace('-', ' ').Replace('_', ' ').Trim();

        return words.Length == 0 ? "Møde" : char.ToUpper(words[0], CultureInfo.CurrentCulture) + words[1..];
    }

    private static DateOnly ParseDate(string? given, string media, string outputDirectory)
    {
        if (given is { Length: > 0 } && DateOnly.TryParse(given, CultureInfo.InvariantCulture, out var explicitDate))
        {
            return explicitDate;
        }

        var folder = Path.GetFileName(outputDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var match = folder is { Length: > 0 } ? DatePrefix.Match(folder) : Match.Empty;
        if (match.Success && DateOnly.TryParse(
                $"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}",
                CultureInfo.InvariantCulture, out var fromFolder))
        {
            return fromFolder;
        }

        return DateOnly.FromDateTime(File.GetLastWriteTime(media));
    }

    private static List<string> Split(string? csv)
        => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    internal static Dictionary<string, string> ParseSpeakers(string? spec)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Split(spec))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            var label = entry[..eq].Trim();
            var name = entry[(eq + 1)..].Trim();
            if (label.Length > 0 && name.Length > 0) map[label] = name;
        }

        return map;
    }
}
