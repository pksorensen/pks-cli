namespace PKS.Infrastructure.Services.Writing;

/// Default content seeded into ~/.pks-cli/writing/ on `pks writing init`.
/// Kept as inline constants for v1; Task #2 will move these to embedded
/// resources under Infrastructure/Resources/Writing/ if/when they outgrow code.
internal static class DefaultSeeds
{
    /// Hidden marker on the seeded profile template. `pks writing profile ingest`
    /// uses this to detect "still the template, safe to overwrite without --force".
    public const string ProfileTemplateMarker = "<!-- pks-writing-profile:template -->";

    public const string Profile =
"""
<!-- pks-writing-profile:template -->
# Writer Profile

> Co-author this file with Claude in an interview-style session
> (`pks writing profile author`) — the LLM critic uses everything below
> as ground truth for what "you" sound like.

## 1. Who am I and who do I write for?
<!-- Role, expertise, audience. e.g. "engineer writing for other engineers
who already know the stack". -->

## 2. Tone and voice
<!-- 3–5 adjectives. Concrete examples > abstract labels. -->

## 3. Signature phrasings I actually use
<!-- Specific Danish phrases that sound like *you*, not a translation.
Critic should preserve these, not flag them. -->

## 4. Do
<!-- Bullet list. Specifics, not platitudes. -->

## 5. Don't
<!-- Bullet list. Anglicisms you hate, structures that feel translated,
clichés to avoid. -->

## 6. Reference sentences
<!-- 5–10 sentences you wrote yourself that capture your voice.
The critic uses these as in-context examples. -->
""";

    public const string Anglicisms =
"""
# pks writing — anglicism list
# Format: english → danish_alternative1, danish_alternative2  | optional note
# Comments start with '#'. Edit by hand or via `pks writing learn`.

deploye → udrulle, idriftsætte                      | verb form leaks through
deployment → udrulning, idriftsættelse
feature → funktion, egenskab
setuppe → opsætte, sætte op
setup → opsætning
tracke → spore, følge
tracking → sporing
update → opdater                                    | tag eller imperativ — som sub har 'opdatering'
updates → opdateringer
submitte → indsende
commit → indlevere, commit                          | tech-term — afhænger af kontekst
committe → indlevere
flow → forløb, arbejdsgang
issue → problem, sag
bug → fejl
backup → sikkerhedskopi
review → gennemgang
default → standard
feedback → tilbagemelding
implement → indføre, implementere                   | implementere er ok i tech-kontekst
implementere → indføre, realisere                   | overuse — variér
optimere → forbedre, finjustere                     | overuse
checke → tjekke
exception → undtagelse
queue → kø
release → udgivelse
feature flag → funktionsskifter
prompte → bede, spørge
performance → ydelse, ydeevne
performant → effektiv
scale → skalere
scope → omfang
endpoint → endepunkt
crash → nedbrud
fix → rette, løse
hack → genvej, kneb
mocking → at fake, mocking                          | mocking i test-kontekst er ok
random → tilfældig
output → resultat, output
input → input, inddata
ship → udgive, sende
ship it → send det, ud med det
high-level → overordnet
low-level → detaljeret, lavniveau
trade-off → afvejning
edge case → særtilfælde
""";

    public const string Allowlist =
"""
# pks writing — allowlist
# Terms here are never flagged as anglicisms (e.g. tech names).
# One term per line. Comments start with '#'.

# AI / tech vocabulary kept as-is (verb forms like "prompte" are still flagged)
agent
agents
prompt
prompts
LLM
LLMs

# Tech / product names
Agentic Live
agentics.dk
vibecast
vibecoding
AppHost
Aspire
.NET
Next.js
Tailwind
TypeScript
Claude Code
Claude
Anthropic
LLM
LLMs
SDK
CLI
API
APIs
JSON
YAML
MCP
ADR
ADRs
ws-relay
ttyd
tmux
Docker
Devcontainer
Keycloak
xUnit
Spectre
NuGet
GitHub
LinkedIn
Hafeok
pks-cli
""";

    public const string BlogRubric =
"""
# Channel: blog

Tone: explanatory but personal. Anecdote-first, then the lesson.
Length: 600–1500 ord. Korte afsnit (2–4 sætninger).
Code blocks are fine; explain *why* before *what*.
Avoid: clickbait headlines, "in this post we will…" openings,
trailing "what's next" cliffhangers.
""";

    public const string LinkedInRubric =
"""
# Channel: linkedin

Tone: punchy, first-person, single insight per post.
Length: 80–200 ord. First sentence MUST earn the click — no warm-up.
No hashtag spam. Max 3 hashtags, all lowercase, all relevant.
Avoid: "I'm excited to announce…", emoji bullet lists, humble-brag intros.
""";

    public const string AdrRubric =
"""
# Channel: adr

Tone: terse, factual, decision-record. Past/present tense.
Sections: Context, Decision, Consequences (positive + negative).
No first-person "I", no anecdotes, no marketing language.
Avoid: hedging ("might", "could possibly"), passive voice in the Decision.
""";

    public const string AuthoringPrompt =
"""
# Cowork authoring prompt — pks writing profile

> Paste this whole document into a Claude session that has access to your
> existing writing (claude.ai chat with attached posts, a project you've
> uploaded, or memory of past work). That session has NO filesystem access,
> so it produces a single JSON bundle which you then save and pass to
> `pks writing profile ingest <path>`.

---

I'm building a writer profile for a tool called `pks writing`. I want you
to produce ONE JSON object I can save and ingest. Do not write files —
just emit the JSON in a single ```json … ``` fenced block at the end.

## Bundle schema (v1)

```jsonc
{
  "version": 1,
  "generatedBy": "claude-cowork",
  "generatedAt": "<ISO-8601 UTC timestamp>",
  "profile": "<full markdown body of the writer profile — see sections below>",
  "anglicisms": [
    { "english": "deploye", "danishAlternatives": ["udrulle"], "note": "verb form" }
  ],
  "allowlist": ["AppHost", "vibecast"],
  "references": {
    "blog":     [ { "id": "post-01", "content": "<full markdown of one of MY hand-written posts>" } ],
    "linkedin": [ { "id": "post-01", "content": "..." } ]
  },
  "lessons": []
}
```

Every top-level field except `version` is optional. Empty/missing means
"don't touch that part of my profile."

## profile.md sections to fill (concatenate into the `profile` string)

1. **Who I write for** — audience, expected expertise.
2. **Tone and voice** — 3–5 concrete adjectives with examples, not platitudes.
3. **Signature phrasings I actually use** — specific Danish phrases that
   sound like ME, not a translation. Quote me.
4. **Do** — bullet list, specifics not platitudes.
5. **Don't** — bullets: anglicisms I hate, translated-feeling structures,
   clichés to avoid.
6. **Reference sentences** — 5–10 sentences I wrote myself that capture
   my voice. (The `references` section below carries the long-form corpus.)

## How to behave

- **Interview first.** Ask me one question at a time. Use what you already
  know (memory + attached writing) to draft a candidate answer, let me
  approve or correct, only THEN commit it to the bundle JSON.
- For `references.blog`: pick 10–20 of my own hand-written posts. Verbatim.
  Filename-safe id per post (`post-01`, `post-02`, …).
- For `anglicisms`: add only patterns you can demonstrate I overuse.
  Don't fabricate. Don't restate the tool's defaults — they're already loaded.
- For `allowlist`: tech/product names + jargon I use that should never be
  flagged (e.g. `AppHost`, `vibecast`, `Aspire`, `Hafeok`, etc.).
- **NEVER include AI-written drafts in `references`.** Anything co-authored
  with Claude/ChatGPT poisons the corpus. Hand-written only.
- Empty section > made-up section. The tool degrades fine with partial bundles.

## Hand-off

When the interview ends and I say "produce the bundle", reply with exactly
ONE fenced ```json … ``` block containing the full bundle and nothing else
after it. I'll save your reply to a file and run:

    pks writing profile ingest ~/Downloads/<your-file>.md
""";

    public const string ReferenceReadme =
"""
# Reference corpus

Drop `*.md` files into the per-channel subfolders (e.g. `blog/`, `linkedin/`,
`adr/`) — each file is one piece of writing that sounds like *you*.

The LLM critic injects these as few-shot examples when scoring, so it learns
your voice instead of relying on the generic rubric. Aim for 10–30 samples
per channel, 200–800 words each.

Filename is used as a stable id (e.g. `post-12.md` → "post-12"). Order is
alphabetical, so prefix numerically if you want a specific load order.

⚠ Don't drop AI-written drafts here — that's circular. Only your own writing.
""";

    public const string ValeIni =
"""
StylesPath = styles
MinAlertLevel = suggestion

Packages =

[*.md]
BasedOnStyles = Writing
""";
}
