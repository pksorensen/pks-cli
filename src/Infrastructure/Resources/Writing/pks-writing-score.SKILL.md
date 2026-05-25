---
name: pks-writing-score
description: |
  Score a Danish blog/LinkedIn/ADR post against the user's writer profile using
  the `pks writing` CLI. Use when the user asks you to score, review, critique,
  or grade a markdown post on agentics.dk (or any post tracked by pks-writing).
  Triggers: "score this post", "review my draft", "how does this rate against
  my voice", "run pks writing on X".
---

# pks writing score — agent-driven flow

You drive the LLM. The CLI is the data layer:
- gathers the writer profile, channel rubric, reference corpus, anglicism hints
- emits a structured prompt + a strict reply schema
- on accept: validates your reply against the schema and stores the report

If your reply doesn't match the schema, `accept` rejects it with field-level
errors and you correct + resubmit. Do not invent fields or skip dimensions.

## Step 1 — Get the prompt

```bash
pks writing prompt <path-to-post.md>
```

Outputs JSON on stdout:

```jsonc
{
  "system": "...full system prompt with rubric + profile + references...",
  "user":   "...numbered post body...",
  "schema": {...JSON schema for the expected reply...},
  "meta":   {"source": "...", "channel": "blog", "modelHint": "haiku", "maxFindings": 12}
}
```

`meta.modelHint` is **haiku** by design — this is BGA-style pattern matching, not
deliberation. A non-reasoning model applies the rubric strictly; a reasoning
model hedges. Use the fastest non-reasoning model you have unless told otherwise.

## Step 2 — Call your bound LLM

Send `system` as the system prompt and `user` as the user message. Get back
the reply. The reply MUST be valid JSON matching the schema:

```jsonc
{
  "dimensions": {
    "Naturalness": 1-5, "Tone": 1-5, "Terminology": 1-5, "Hook": 1-5, "Value": 1-5
  },
  "findings": [
    {
      "dimension": "Naturalness | Tone | Terminology | Hook | Value",
      "line": <1-indexed line in the source>,
      "match": "<exact phrase, <=120 chars>",
      "message": "<one-sentence why this is off>",
      "suggestions": ["<rewrite 1>", "<rewrite 2>"]
    }
  ],
  "notes": "<2-4 sentences: most important fix, why, what to try>"
}
```

Cap `findings` at `meta.maxFindings`. All five dimension scores are required.

## Step 3 — Submit the reply

```bash
pks writing accept <path-to-post.md> --from <reply.json> --model <your-model-id>
# or via stdin:
your-llm-call | pks writing accept <path-to-post.md> --model <your-model-id>
```

Exit codes:
- **0** — accepted. Last stdout line: `RESULT: {"ok": true, "score": N, ...}`.
  The report is written to `<post-dir>/_review/<stem>.WRITING-REPORT.{md,json}`.
- **1** — schema rejected. Stdout: `RESULT: {"ok": false, "errors": [...], "hint": "..."}`.
  Read the errors, correct your reply, resubmit. Common fixes:
  - `dimensions.<X>.missing` — add that dimension score (1-5).
  - `findings[i].line.out_of_range` — clamp to the source's line count.
  - `notes.missing` — add the notes field.
- **2** — argument error (path not found etc.). Not a retry condition.

## Step 4 — Continue the workflow

After accept succeeds, the standard pks-writing pipeline is available:

```bash
pks writing learn   <post>        # propose accept/reject actions from the report
pks writing apply   <proposal>    # commit accepted actions to global profile
pks writing corpus  <folder>      # roll up learn proposals across many posts
```

## Examples

**Single-post score** (one shot, no retry):
```bash
PROMPT=$(pks writing prompt blog-posts/x/da.md)
echo "$PROMPT" | jq -r '.system + "\n\n" + .user' | your-llm-cli > /tmp/reply.json
pks writing accept blog-posts/x/da.md --from /tmp/reply.json --model haiku
```

**With retry on schema failure:**
```bash
attempt=1
while [ $attempt -le 3 ]; do
  pks writing prompt blog-posts/x/da.md | your-llm > /tmp/reply.json
  if pks writing accept blog-posts/x/da.md --from /tmp/reply.json --model haiku; then
    break
  fi
  attempt=$((attempt + 1))
done
```

## Why this flow exists

- pks-cli stays free of LLM-vendor coupling (no auth, no version chasing).
- Any agent with any bound LLM can drive it — Claude Code, Cursor, MCP server, custom.
- Schema enforcement is explicit: bad reply → typed errors, not silent corruption.
- The prompt body is generated from current profile state, not stored in the skill — so it stays in sync as the profile evolves.
