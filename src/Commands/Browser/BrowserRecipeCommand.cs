using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Browser;

/// <summary>
/// Kører en opskrift på pks-agent-browser og fortæller undervejs hvad der sker.
///
/// Der er ingen model i det her. Ingen planlægger, intet token-forbrug, intet der
/// kan finde på noget andet end det der står i filen — motoren i den anden ende er
/// en trin-eksekvering, og det her er en klient til den. Det er hele pointen:
/// når opskriften først er optaget, koster den at køre præcis hvad banken koster.
///
/// Det ene sted et menneske skal ind, siger den til: et `human`-trin parkerer
/// kørslen, og vi skriver prompten ud — og engangskoden i samme øjeblik MitID
/// viser den, uden at nogen skal kigge i browseren.
/// </summary>
[Description("Run a recipe on pks-agent-browser and stream its progress")]
public class BrowserRecipeCommand : AsyncCommand<BrowserRecipeCommand.Settings>
{
    private readonly IAnsiConsole _console;

    public BrowserRecipeCommand(IAnsiConsole console) => _console = console;

    public const string DefaultServer = "https://browser.agentics.dk";
    public const string LocalServer = "http://127.0.0.1:8099";

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<recipe>")]
        [Description("Path to the recipe JSON file")]
        public string Recipe { get; set; } = "";

        [CommandOption("--server")]
        [Description("Browser service base URL (default: https://browser.agentics.dk)")]
        public string? Server { get; set; }

        [CommandOption("--local")]
        [Description("Run against a local service instead (default http://127.0.0.1:8099)")]
        public bool Local { get; set; }

        [CommandOption("--token")]
        [Description("Bearer token. Falls back to BROWSER_API_TOKEN.")]
        public string? Token { get; set; }

        [CommandOption("-p|--param <KEY=VALUE>")]
        [Description("Run parameter. Repeatable. Never written into the recipe file.")]
        public string[] Params { get; set; } = [];

        [CommandOption("--profile")]
        [Description("Viewport profile (default: laptop)")]
        public string Profile { get; set; } = "laptop";

        [CommandOption("--persist-profile")]
        [Description("Named browser profile that survives the session, so the site remembers this device")]
        public string? PersistProfile { get; set; }

        [CommandOption("-o|--out")]
        [Description("Directory to write downloaded files into (default: ./downloads)")]
        public string Out { get; set; } = "downloads";

        [CommandOption("--record")]
        [Description("Record the session server-side and fetch the mp4 when it closes")]
        public bool Record { get; set; }

        [CommandOption("--keep")]
        [Description("Leave the session open when the run finishes, so it can be inspected or reused")]
        public bool Keep { get; set; }

        [CommandOption("--ttl-minutes")]
        [Description("Session lifetime (default 60)")]
        public int TtlMinutes { get; set; } = 60;

        [CommandOption("--json")]
        [Description("Print the final run status as JSON instead of a table")]
        public bool Json { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!File.Exists(settings.Recipe))
        {
            _console.MarkupLineInterpolated($"[red]No such recipe file:[/] {settings.Recipe}");
            return 1;
        }

        var baseUrl = (settings.Server ?? (settings.Local ? LocalServer : DefaultServer)).TrimEnd('/');
        var token = settings.Token ?? Environment.GetEnvironmentVariable("BROWSER_API_TOKEN");

        JsonNode body;
        try
        {
            body = JsonNode.Parse(await File.ReadAllTextAsync(settings.Recipe))
                   ?? throw new InvalidOperationException("empty file");
        }
        catch (Exception ex)
        {
            _console.MarkupLineInterpolated($"[red]Could not read the recipe:[/] {ex.Message}");
            return 1;
        }

        // Filen må gerne være enten `{recipe, params}` eller bare opskriften selv.
        // Det andet er den form man har i hånden når man lige har skrevet den.
        var payload = body["recipe"] is not null ? body : new JsonObject { ["recipe"] = body.DeepClone() };

        var runParams = payload["params"] as JsonObject ?? [];
        foreach (var pair in settings.Params)
        {
            var i = pair.IndexOf('=');
            if (i <= 0)
            {
                _console.MarkupLineInterpolated($"[red]--param wants KEY=VALUE, got:[/] {pair}");
                return 1;
            }
            runParams[pair[..i]] = pair[(i + 1)..];
        }
        payload["params"] = runParams;

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrEmpty(token))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var sessionRequest = new JsonObject
        {
            ["profile"] = settings.Profile,
            ["ttlMs"] = settings.TtlMinutes * 60_000,
        };
        if (!string.IsNullOrEmpty(settings.PersistProfile)) sessionRequest["persistProfile"] = settings.PersistProfile;
        if (settings.Record) sessionRequest["record"] = true;

        _console.MarkupLineInterpolated($"[dim]{baseUrl}[/]");

        JsonNode? session;
        try
        {
            session = await PostAsync(http, $"{baseUrl}/v1/sessions", sessionRequest);
        }
        catch (Exception ex)
        {
            _console.MarkupLineInterpolated($"[red]Could not open a session:[/] {ex.Message}");
            return 1;
        }

        var sessionId = session?["id"]?.GetValue<string>();
        if (sessionId is null)
        {
            _console.MarkupLine("[red]The service opened no session.[/]");
            return 1;
        }
        _console.MarkupLineInterpolated($"[dim]session {sessionId}[/]");

        var exitCode = 0;
        try
        {
            await PostAsync(http, $"{baseUrl}/v1/sessions/{sessionId}/run", payload);
            var run = await FollowAsync(http, $"{baseUrl}/v1/sessions/{sessionId}/run");

            if (settings.Json)
            {
                _console.WriteLine(run?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}");
            }

            var state = run?["state"]?.GetValue<string>();
            if (state != "done")
            {
                _console.MarkupLineInterpolated($"[red]Run {state}:[/] {Markup.Escape(run?["error"]?.GetValue<string>() ?? "unknown")}");
                // Sessionen bliver stående ved en fejl uanset --keep: et knækket
                // trin er præcis det øjeblik hvor nogen skal kunne kigge i den
                // browser der knækkede.
                _console.MarkupLineInterpolated($"[dim]session {sessionId} left open for inspection[/]");
                return 2;
            }

            await SaveArtifactsAsync(http, baseUrl, run, settings.Out);
        }
        catch (Exception ex)
        {
            _console.MarkupLineInterpolated($"[red]Run failed:[/] {ex.Message}");
            return 1;
        }

        if (!settings.Keep)
        {
            var closed = await DeleteAsync(http, $"{baseUrl}/v1/sessions/{sessionId}");
            // Videoen findes først når sessionen lukker — optageren stitcher
            // billederne til mp4 på vejen ud.
            if (settings.Record) await SaveArtifactsAsync(http, baseUrl, closed, settings.Out, videoOnly: true);
        }
        else
        {
            _console.MarkupLineInterpolated($"[dim]session {sessionId} kept open[/]");
        }

        return exitCode;
    }

    /// <summary>
    /// Følger kørslen og skriver hvert trin ud efterhånden som det bliver færdigt.
    /// Der er ingen strøm at abonnere på i dag, så det er en pollefrekvens — men
    /// den er kun til fremvisningen: motoren venter ikke på os.
    /// </summary>
    private async Task<JsonNode?> FollowAsync(HttpClient http, string url)
    {
        var printed = 0;
        string? lastPrompt = null;
        var shown = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonNode? run = null;

        while (true)
        {
            run = await GetAsync(http, url);
            var steps = run?["steps"] as JsonArray ?? [];

            for (; printed < steps.Count; printed++)
            {
                var s = steps[printed]!;
                var id = s["stepId"]?.GetValue<string>() ?? "?";
                var st = s["state"]?.GetValue<string>() ?? "?";
                var ms = s["ms"]?.GetValue<double>() ?? 0;
                var note = (s["note"]?.GetValue<string>() ?? "").Split('\n')[0];
                var (glyph, colour) = st switch
                {
                    "ok" => ("[green]✔[/]", "white"),
                    "skipped" => ("[grey]•[/]", "grey"),
                    _ => ("[red]✘[/]", "red"),
                };
                var suffix = st == "skipped" ? "skipped" : Markup.Escape(Trim(note, 60));
                _console.MarkupLine($"{glyph} [{colour}]{Markup.Escape(id.PadRight(20))}[/] [dim]{ms / 1000:F1}s[/]  [dim]{suffix}[/]");
            }

            var waiting = run?["waiting"];
            if (waiting is not null)
            {
                // Prompten skrives én gang; hver ny værdi skrives i det øjeblik
                // den dukker op — det er sådan engangskoden når frem uden at
                // nogen skal spørge om den.
                var prompt = waiting["prompt"]?.GetValue<string>() ?? "Action required";
                if (prompt != lastPrompt)
                {
                    lastPrompt = prompt;
                    _console.MarkupLineInterpolated($"[yellow]⏸  {prompt}[/]");
                }

                if (waiting["data"] is JsonObject data)
                {
                    foreach (var (key, value) in data)
                    {
                        var text = value?.ToString();
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        if (shown.TryGetValue(key, out var was) && was == text) continue;
                        shown[key] = text;
                        _console.MarkupLineInterpolated($"   [bold yellow]{key}: {text}[/]");
                    }
                }
            }

            var state = run?["state"]?.GetValue<string>();
            if (state is "done" or "failed") return run;
            await Task.Delay(750);
        }
    }

    private async Task SaveArtifactsAsync(HttpClient http, string baseUrl, JsonNode? source, string outDir, bool videoOnly = false)
    {
        var list = (source?["artifacts"] as JsonArray) ?? [];
        var saved = 0;
        foreach (var a in list)
        {
            var kind = a?["kind"]?.GetValue<string>();
            if (videoOnly ? kind != "video" : kind != "download") continue;

            var url = a?["url"]?.GetValue<string>();
            if (url is null) continue;
            if (url.StartsWith('/')) url = baseUrl + url;

            Directory.CreateDirectory(outDir);
            var name = NameFor(a!, kind!);
            var path = Path.Combine(outDir, name);
            await using var stream = await http.GetStreamAsync(url);
            await using var file = File.Create(path);
            await stream.CopyToAsync(file);
            saved++;
            _console.MarkupLineInterpolated($"[green]↓[/] {path} [dim]{a?["bytes"]?.GetValue<long>() ?? 0} B[/]");
        }
        if (saved > 0 && !videoOnly) _console.MarkupLineInterpolated($"[green]{saved} file(s) in {outDir}[/]");
    }

    /// <summary>
    /// Bankernes egne filnavne er `eksport.csv` for hver eneste konto, så mærkatet
    /// fra opskriften er det eneste der skiller dem ad. Uden det her overskriver
    /// konto to konto et.
    /// </summary>
    private static string NameFor(JsonNode artifact, string kind)
    {
        var labels = artifact["labels"] as JsonObject;
        var filename = labels?["filename"]?.GetValue<string>() ?? $"{kind}.bin";
        var label = labels?["label"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(label)) return Safe(filename);

        var ext = Path.GetExtension(filename);
        return Safe(label) + ext;
    }

    private static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-');
        return string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static async Task<JsonNode?> GetAsync(HttpClient http, string url)
    {
        var res = await http.GetAsync(url);
        await ThrowIfBad(res);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync());
    }

    private static async Task<JsonNode?> PostAsync(HttpClient http, string url, JsonNode body)
    {
        var res = await http.PostAsJsonAsync(url, body);
        await ThrowIfBad(res);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync());
    }

    private static async Task<JsonNode?> DeleteAsync(HttpClient http, string url)
    {
        var res = await http.DeleteAsync(url);
        await ThrowIfBad(res);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync());
    }

    private static async Task ThrowIfBad(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        var text = await res.Content.ReadAsStringAsync();
        var message = JsonNode.Parse(text)?["error"]?.GetValue<string>() ?? text;
        throw new HttpRequestException($"HTTP {(int)res.StatusCode} — {Trim(message, 200)}");
    }
}
