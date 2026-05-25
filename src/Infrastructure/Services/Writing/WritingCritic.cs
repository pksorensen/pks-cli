using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public sealed class WritingCritic : IWritingCritic
{
    private readonly IClaudeRunner _claude;

    public WritingCritic(IClaudeRunner claude)
    {
        _claude = claude;
    }

    public async Task<CritiqueResult> CritiqueAsync(CritiqueRequest request, CancellationToken ct = default)
    {
        var systemPrompt = BuildSystemPrompt(request);
        var userPrompt = BuildUserPrompt(request);

        var run = await _claude.RunAsync(new ClaudeRunRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Model = request.Model,
            MaxBudgetUsd = request.MaxBudgetUsd,
            Timeout = TimeSpan.FromMinutes(3),
        }, ct);

        if (!run.Success)
        {
            return new CritiqueResult
            {
                Success = false,
                ErrorKind = run.ErrorKind,
                ErrorMessage = string.IsNullOrWhiteSpace(run.Stderr) ? run.RawStdout : run.Stderr,
                Model = run.Model,
                CostUsd = run.CostUsd,
                Duration = run.Duration,
            };
        }

        if (!TryParseResponse(run.ResponseText, out var dims, out var findings, out var notes, out var parseErr))
        {
            return new CritiqueResult
            {
                Success = false,
                ErrorKind = "parse",
                ErrorMessage = parseErr + "\n--- raw response ---\n" + Truncate(run.ResponseText, 2000),
                Model = run.Model,
                CostUsd = run.CostUsd,
                Duration = run.Duration,
            };
        }

        return new CritiqueResult
        {
            Success = true,
            ErrorKind = null,
            ErrorMessage = null,
            DimensionScores = dims,
            Findings = findings,
            Notes = notes,
            Model = run.Model,
            CostUsd = run.CostUsd,
            Duration = run.Duration,
        };
    }

    // ── prompt construction ────────────────────────────────────────────────────

    internal static string BuildSystemPrompt(CritiqueRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a strict but kind Danish-writing critic for the `pks writing` tool.");
        sb.AppendLine("You apply a fixed rubric to one piece of writing and return ONE JSON object.");
        sb.AppendLine("You do NOT rewrite the post. You flag and grade. The author rewrites.");
        sb.AppendLine();
        sb.AppendLine("# Rubric — score each dimension 1 (worst) to 5 (best)");
        sb.AppendLine();
        sb.AppendLine("- **Naturalness** — does the Danish read native, or like a translation from English?");
        sb.AppendLine("- **Tone**        — does the voice match the writer profile below?");
        sb.AppendLine("- **Terminology** — anglicisms, kalkerede vendinger, on-brand vocabulary.");
        sb.AppendLine("- **Hook**        — does the opening sentence earn the read?");
        sb.AppendLine("- **Value**       — does the post deliver what the headline/opening promises?");
        sb.AppendLine();
        sb.AppendLine("Be honest. A 3 is competent. A 5 is rare. Never give all 5s out of politeness.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(r.Profile))
        {
            sb.AppendLine("# Writer profile");
            sb.AppendLine();
            sb.AppendLine(r.Profile.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(r.ChannelRubric))
        {
            sb.AppendLine($"# Channel rubric ({r.Channel})");
            sb.AppendLine();
            sb.AppendLine(r.ChannelRubric.Trim());
            sb.AppendLine();
        }

        if (r.References.Count > 0)
        {
            sb.AppendLine("# Reference samples — this is what the author actually sounds like");
            sb.AppendLine();
            foreach (var sample in r.References.Take(10))
            {
                sb.AppendLine($"## reference: {sample.Id}");
                sb.AppendLine();
                sb.AppendLine(Truncate(sample.Content.Trim(), 1500));
                sb.AppendLine();
            }
        }

        if (r.Anglicisms.Count > 0)
        {
            sb.AppendLine("# Known anglicisms (terminology hint — the linter already flags these)");
            sb.AppendLine();
            foreach (var a in r.Anglicisms.Take(60))
            {
                var alts = string.Join(", ", a.DanishAlternatives);
                sb.AppendLine($"- {a.English} → {alts}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("# Output format — return ONLY this JSON, no prose around it");
        sb.AppendLine();
        sb.AppendLine("""
            {
              "dimensions": {
                "Naturalness": 1-5,
                "Tone": 1-5,
                "Terminology": 1-5,
                "Hook": 1-5,
                "Value": 1-5
              },
              "findings": [
                {
                  "dimension": "Naturalness|Tone|Terminology|Hook|Value",
                  "line": <1-indexed line in the source>,
                  "match": "<the exact phrase or sentence flagged, ≤120 chars>",
                  "message": "<one-sentence why this is off>",
                  "suggestions": ["<concrete rewrite 1>", "<concrete rewrite 2>"]
                }
              ],
              "notes": "<2-4 sentences: the single most important thing to fix, why, what to try>"
            }
            """);
        sb.AppendLine();
        sb.AppendLine("Cap findings at 12. Pick the ones that move the needle most. Skip nitpicks.");
        return sb.ToString();
    }

    internal static string BuildUserPrompt(CritiqueRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Source: {Path.GetFileName(r.SourcePath)}  (channel: {r.Channel})");
        sb.AppendLine();
        sb.AppendLine("Apply the rubric to the post below and return the JSON.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        // Number lines so the model can reference them accurately.
        var lines = r.Content.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append((i + 1).ToString().PadLeft(4));
            sb.Append("  ");
            sb.AppendLine(lines[i]);
        }
        return sb.ToString();
    }

    // ── response parsing ───────────────────────────────────────────────────────

    internal static bool TryParseResponse(
        string responseText,
        out Dictionary<string, int> dimensions,
        out List<WritingFinding> findings,
        out string? notes,
        out string? errorReason)
    {
        dimensions = new();
        findings = new();
        notes = null;
        errorReason = null;

        // Strip ``` fences if the model wraps the JSON.
        var trimmed = responseText.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNl = trimmed.IndexOf('\n');
            if (firstNl >= 0) trimmed = trimmed[(firstNl + 1)..];
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) trimmed = trimmed[..lastFence];
        }

        // Grab the outermost JSON object if there's stray prose.
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            errorReason = "No JSON object found in response.";
            return false;
        }
        var json = trimmed[firstBrace..(lastBrace + 1)];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("dimensions", out var dimsEl) && dimsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in dimsEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var v))
                        dimensions[prop.Name] = Math.Clamp(v, 1, 5);
                }
            }

            if (root.TryGetProperty("findings", out var findEl) && findEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in findEl.EnumerateArray())
                {
                    var dimension = f.TryGetProperty("dimension", out var d) ? d.GetString() ?? "" : "";
                    var line = f.TryGetProperty("line", out var l) && l.TryGetInt32(out var li) ? li : 0;
                    var match = f.TryGetProperty("match", out var m) ? m.GetString() ?? "" : "";
                    var message = f.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                    var suggestions = new List<string>();
                    if (f.TryGetProperty("suggestions", out var s) && s.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sug in s.EnumerateArray())
                        {
                            var txt = sug.GetString();
                            if (!string.IsNullOrWhiteSpace(txt)) suggestions.Add(txt);
                        }
                    }

                    findings.Add(new WritingFinding
                    {
                        RuleId = $"Critic.{dimension}",
                        Severity = WritingSeverity.Suggestion,
                        Line = line,
                        Column = 1,
                        Match = match,
                        Message = message,
                        Suggestions = suggestions,
                    });
                }
            }

            if (root.TryGetProperty("notes", out var notesEl) && notesEl.ValueKind == JsonValueKind.String)
            {
                notes = notesEl.GetString();
            }
            return true;
        }
        catch (JsonException jx)
        {
            errorReason = "JSON parse error: " + jx.Message;
            return false;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
