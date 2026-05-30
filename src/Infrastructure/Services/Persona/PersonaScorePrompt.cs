using System.Text;
using PKS.Infrastructure.Services.Persona.Models;

namespace PKS.Infrastructure.Services.Persona;

/// <summary>
/// Composes the system + user prompts and accompanying JSON schema for a
/// persona × rubric × content scoring call. Mirrors the shape of
/// <c>WritingScorePrompt</c>: returns <c>{ system, user, schema, meta }</c>
/// so both the manual subscription path and the direct-run agent path emit
/// identical instructions to the LLM.
/// </summary>
public static class PersonaScorePrompt
{
    public sealed class Request
    {
        public string ContentPath { get; init; } = "";
        public string Content { get; init; } = "";
        public Models.Persona Persona { get; init; } = new();
        public Rubric Rubric { get; init; } = new();
        public string ModelHint { get; init; } = "claude-opus-4-7";
    }

    public sealed class Bundle
    {
        public string System { get; init; } = "";
        public string User { get; init; } = "";
        public object Schema { get; init; } = new { };
        public object Meta { get; init; } = new { };
    }

    public static Bundle Build(Request r)
    {
        var system = BuildSystem(r);
        var user = BuildUser(r);
        var schema = PersonaScoreSchema.SchemaObject(r.Rubric);
        var meta = new
        {
            source = r.ContentPath,
            personaId = r.Persona.Id,
            personaName = r.Persona.Name,
            rubric = r.Rubric.Id,
            modelHint = r.ModelHint,
        };
        return new Bundle { System = system, User = user, Schema = schema, Meta = meta };
    }

    private static string BuildSystem(Request r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an editorial scorer. Given a reader persona, a scoring rubric, and a piece of content, you rate how well the content serves the persona on the rubric's dimension.");
        sb.AppendLine();
        sb.AppendLine("Output a single JSON object matching the provided schema. No prose, no markdown, no preamble — only JSON. Score on the rubric's 1–5 scale.");
        sb.AppendLine();
        sb.AppendLine("The `evidence` field MUST be an array of objects shaped { \"quote\": \"<verbatim substring of the content>\", \"note\": \"<one sentence on what the quote shows>\" }. Do NOT emit `evidence` as an array of strings. Do NOT use keys other than `quote` and `note`.");
        sb.AppendLine();
        sb.AppendLine("Be honest. A high score is not the default. If the content is irrelevant or weak for this persona on this dimension, score it that way and explain why.");
        return sb.ToString();
    }

    private static string BuildUser(Request r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Persona");
        sb.AppendLine();
        sb.AppendLine($"- id: {r.Persona.Id}");
        sb.AppendLine($"- name: {r.Persona.Name}");
        sb.AppendLine($"- segment: {r.Persona.Segment}");
        sb.AppendLine($"- bucket: {r.Persona.Bucket}");
        sb.AppendLine();
        sb.AppendLine(r.Persona.Body);
        sb.AppendLine();
        sb.AppendLine($"## Rubric — {r.Rubric.Name} ({r.Rubric.Id})");
        sb.AppendLine();
        sb.AppendLine(r.Rubric.Body);
        sb.AppendLine();
        sb.AppendLine($"## Content (from {r.ContentPath})");
        sb.AppendLine();
        sb.AppendLine(r.Content);
        sb.AppendLine();
        sb.AppendLine("## Task");
        sb.AppendLine();
        sb.AppendLine($"Score the content above against this persona on the '{r.Rubric.Id}' rubric. Return the JSON object only.");
        return sb.ToString();
    }
}
