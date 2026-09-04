using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure.Services.Transcription;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Transcribe;

/// <summary>
/// <c>pks diarize &lt;media&gt;</c> — who spoke when, and nothing else.
///
/// This is a separate verb because it is a separate request. Diarisation and the verbatim
/// enhanced model cannot be asked for together: the API answers 200 to a request carrying
/// both and silently drops the diarisation, returning one unlabelled phrase. Two calls is
/// not an implementation detail we could optimise away — it is the shape of the service.
///
/// Reach for it when the speakers in a transcript look wrong. It is one fast call, it prints
/// how the talk time divides, and it names the failure mode that actually happens: a
/// "speaker" made entirely of sub-second fragments is crosstalk, not a person.
/// </summary>
[Description("Detect who spoke when in a recording, without transcribing the words")]
public sealed class DiarizeCommand : AsyncCommand<DiarizeCommand.Settings>
{
    private readonly TranscriptionPipeline _pipeline;
    private readonly TranscriptionProviderRegistry _providers;
    private readonly IAnsiConsole _console;

    public DiarizeCommand(
        TranscriptionPipeline pipeline, TranscriptionProviderRegistry providers, IAnsiConsole console)
    {
        _pipeline = pipeline;
        _providers = providers;
        _console = console;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<media>")]
        [Description("Recording to analyse (mp4, m4a, mp3, wav, …)")]
        public string Media { get; set; } = "";

        [CommandOption("--out|-o")]
        [Description("Folder to write diarization.json into (default: beside the recording)")]
        public string? Out { get; set; }

        [CommandOption("--max-speakers")]
        [Description("Upper bound for the diariser (2-12, default 6)")]
        public int MaxSpeakers { get; set; } = 6;

        [CommandOption("--locale|-l")]
        [Description("BCP-47 locale (default da-DK). Diarisation needs the region: \"da-DK\", not \"da\"")]
        public string Locale { get; set; } = "da-DK";

        [CommandOption("--provider")]
        [Description("Diarisation provider (default foundry-fast)")]
        public string? Provider { get; set; }

        [CommandOption("--part-seconds")]
        [Description("Length of each analysed part (default 900)")]
        public int PartSeconds { get; set; } = AudioPreparation.DefaultPartSeconds;
    }

    /// <summary>
    /// A "speaker" whose typical contribution is this short never said a sentence.
    ///
    /// MEASURED, and the reason this is the median and not the maximum: the crosstalk label on
    /// the museliving status call was 29 phrases totalling 7.7 s, median 200 ms — and one
    /// stray 1240 ms phrase, which is all it took for a "longest phrase under a second" test
    /// to call it a person. One outlier must not outvote twenty-eight fragments.
    /// </summary>
    private const long FragmentMs = 1000;

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
            : Path.GetDirectoryName(media)!;

        var providerKey = settings.Provider ?? "foundry-fast";
        var provider = _providers.Get(providerKey);
        if (provider is null)
        {
            _console.MarkupLine($"[red]Unknown provider[/] {Markup.Escape(providerKey)}. Known: "
                + string.Join(", ", _providers.GetAllProviders()
                    .Where(p => p.ProvidesSpeakerLabels).Select(p => p.ProviderKey)));

            return 1;
        }

        if (!await provider.IsAuthenticatedAsync())
        {
            _console.MarkupLine($"[red]{Markup.Escape(provider.DisplayName)}[/] has no credentials. "
                + "Run [white]pks foundry init[/], or export PKS_FOUNDRY_ENDPOINT and PKS_FOUNDRY_API_KEY.");

            return 1;
        }

        SpeechResult result;
        try
        {
            result = await _pipeline.DiarizeAsync(
                new TranscriptionPipelineOptions
                {
                    MediaPath = media,
                    OutputDirectory = outputDirectory,
                    Locale = settings.Locale,
                    MaxSpeakers = settings.MaxSpeakers,
                    LabelsProvider = providerKey,
                    PartSeconds = settings.PartSeconds,
                },
                line => _console.MarkupLine($"[grey]{Markup.Escape(line)}[/]"));
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Diarisation failed:[/] {Markup.Escape(ex.Message)}");

            return 1;
        }

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "diarization.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));

        Report(_console, result.Phrases ?? []);
        _console.MarkupLine($"[grey]→ {Markup.Escape(path)}[/]");

        return 0;
    }

    /// <summary>
    /// How the talk time divides, and which "speakers" are not people. Static and internal so
    /// the rendering can be tested without spending an API call on real audio.
    /// </summary>
    internal static void Report(IAnsiConsole console, List<SpeechPhrase> phrases)
    {
        if (phrases.Count == 0)
        {
            console.MarkupLine("[yellow]No speech found.[/] Check that the recording has audio on it.");

            return;
        }

        var speakers = phrases
            .GroupBy(p => p.Speaker ?? "?")
            .Select(g => new
            {
                Label = g.Key,
                Phrases = g.Count(),
                TalkMs = g.Sum(p => p.DurationMilliseconds),
                LongestMs = g.Max(p => p.DurationMilliseconds),
                MedianMs = Median([.. g.Select(p => p.DurationMilliseconds)]),
            })
            .OrderByDescending(s => s.TalkMs)
            .ToList();
        var totalMs = Math.Max(1, speakers.Sum(s => s.TalkMs));

        // Every column carries a header and every cell has content. A column that is empty in
        // all rows gets no width, and Spectre renders the whole table as a single "…".
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Speaker");
        table.AddColumn(new TableColumn("Phrases").RightAligned());
        table.AddColumn(new TableColumn("Talk time").RightAligned());
        table.AddColumn(new TableColumn("Share").RightAligned());
        table.AddColumn("Looks like");

        foreach (var speaker in speakers)
        {
            var fragment = speaker.MedianMs < FragmentMs;
            table.AddRow(
                fragment ? $"[yellow]{speaker.Label}[/]" : speaker.Label,
                speaker.Phrases.ToString(),
                Hms(speaker.TalkMs),
                $"{100.0 * speaker.TalkMs / totalMs:0.0} %",
                fragment ? "[yellow]crosstalk[/]" : "a person");
        }

        console.Write(table);

        var suspects = speakers.Where(s => s.MedianMs < FragmentMs).ToList();
        if (suspects.Count > 0)
        {
            console.MarkupLine($"[yellow]{suspects.Count} \"speaker\"(s) say nothing longer than a second, "
                + "typically.[/] [grey]That is usually what the diariser does with people talking over each "
                + $"other. Check before naming them: map crosstalk to \"{MeetingFolderWriter.OverlapSpeaker}\", "
                + "not to a person.[/]");
        }
    }

    private static long Median(List<long> values)
    {
        values.Sort();

        return values.Count == 0 ? 0 : values[values.Count / 2];
    }

    private static string Hms(long ms)
        => $"{ms / 3_600_000}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}";
}
