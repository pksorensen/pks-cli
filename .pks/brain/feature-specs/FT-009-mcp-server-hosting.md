---
id: FT-009
title: MCP server hosting (stdio + HTTP)
domain: agentic-runtime
status: draft
adrs: []
tests: [tests/Commands/McpServerCommandTests.cs, tests/Commands/StdioMcpServerTests.cs, tests/Commands/Mcp/McpServerTests.cs]
source-files: [src/Commands/Mcp/McpCommand.cs, src/Infrastructure/Services/MCP/IMcpHostingService.cs, src/Infrastructure/Services/MCP/McpHostingService.cs, src/Infrastructure/Services/MCP/McpConfiguration.cs, src/Infrastructure/Services/MCP/McpResourceService.cs, src/Infrastructure/Services/MCP/McpToolService.cs, src/Infrastructure/Services/MCP/Tools/]
sessions: [02f82b87-a9e8-43d2-a2f7-c2e9088bf9c8, b9878de6-3dd9-4ab0-99d2-88c597a7beb1, 1c38b37f-2e00-4c39-9cce-cde9ab532cb9, d30fe666-0be4-4876-81bb-9446cb02a914, 5d817688-c7ef-4497-988e-53d257d792db]
---

## Description
`pks mcp` is the entry point that exposes the broader pks-cli surface as a Model
Context Protocol server so Claude Code, the agentic runner, and external clients can
call PKS capabilities as tools instead of shelling out. The implementation is
SDK-based — `McpHostingService` boots a `Microsoft.Extensions.Hosting` host wired
with `AddMcpServer()` and `WithToolsFromAssembly()`, picking up every `[McpServerTool]`
attribute under `Infrastructure/Services/MCP/Tools/` (Agent, Deployment, Devcontainer,
GitHub, Hooks, PRD, Project, Report, Status, Swarm, Template, Utility…). The transport
is selected at runtime: stdio for Claude Code's default `.mcp.json` spawn pattern, HTTP
for cases where a long-lived process (the runner) needs a stable URL it can publish via
a generated plugin directory. `McpCommand` deliberately suppresses Spectre banner/log
output when stdio is selected so the JSON-RPC framing on stdout is not corrupted —
the same banner-suppression pattern is shared with `isGitAskPass`, `isHooksCommand`,
and `isFoundryProxy`. A `MIGRATION-PLAN.md` in the service folder records the move
from an earlier 819-line custom `McpServerService` to this SDK-based hosting.

## Intent

> From session 02f82b87 (2026-04-18), prompt:
> "I also realize that we actually need a way for the runner to show its capabilities
> if we dont already and if the runner is setup with foundry it need to expose the
> resource url and that means we properly need to look into that resource registry
> anyhow. Does this mean that the runner actually need to expose a mcp server with a
> tool list foundry resoruces and models and then provide a plugin dir to vibecast
> that it expsoes to the agent telling it about this mcp server."

> From session b9878de6 (2026-04-19), prompt:
> "Aspire CLI commands: `aspire run`, `aspire agent mcp` (stdio MCP transport — NOT
> HTTP URL) … MCP connection chain: `Claude → stdio → aspire agent mcp → http →
> localhost:18240 → Aspire Dashboard` … `.mcp.json` correct format:
> `{ \"mcpServers\": { \"aspire\": { \"command\": \"aspire\", \"args\": [\"agent\", \"mcp\"] } } }`"

> From session 1c38b37f (2026-03-28), prompt:
> "lets make a new plan for the mcp.json - we can actually provide the mcp as part of
> the plugin we give to claude when vibecast starts it using the plugin folder argument
> thing, so we dont need to add to mcp files in the repo. Analyse and make a plan for
> this"

## Key decisions
- **SDK hosting over custom JSON-RPC** — `AddMcpServer().WithToolsFromAssembly()` replaces
  the legacy hand-rolled `McpServerService`. Tools are declared with `[McpServerTool]`
  attributes per service class; reflection wires schema + dispatch so adding a tool is a
  matter of dropping a method into `Infrastructure/Services/MCP/Tools/`.
- **Transport chosen by flag, not by separate binary** — one `pks mcp` command accepts
  `--transport stdio|http|sse`. Stdio is the canonical Claude Code path (spawned as a
  child); HTTP is reserved for the runner and external clients that need a URL. Mirrors
  the `aspire agent mcp → http://localhost:18240` bridge pattern referenced by
  [[FT-007-agentics-runner]].
- **Stdout discipline for stdio** — when `Transport=stdio`, all logger and Spectre
  banner output is suppressed so the JSON-RPC byte stream is clean. This shares the
  same `isMcpStdio` flag family used by foundry-proxy and git-askpass in
  [[FT-001-cli-core-and-utilities]].
- **Selective tool exposure for the runner** — companion runner work in session
  02f82b87 chose `WithTools<RunnerMcpTools>()` instead of `WithToolsFromAssembly()`
  to avoid leaking the full pks tool surface into vibecast/Claude; the pks-cli server
  itself opts into the broad surface because it is the developer-facing host.
- **Resources alongside tools** — `McpResourceService` is registered next to the tool
  services so MCP `resources` (not just tools) can be served, leaving room for things
  like exposing the foundry resource registry mentioned in 02f82b87.

## Gotchas / known issues
- Log output to stdout will corrupt stdio JSON-RPC framing; the `isStdioTransport`
  branch in `McpCommand.ExecuteAsync` is load-bearing — do not "clean up" the
  duplicated banner suppression without checking [[FT-005-foundry-proxy-substrate-boundary]]
  which relies on the same pattern.
- Coexistence of legacy `McpServerService` and the new `McpHostingService` is tracked
  in `Infrastructure/Services/MCP/MIGRATION-PLAN.md`; until the legacy path is deleted,
  changes that add tools must be made in the SDK-based `Tools/*` files, not the old
  hardcoded list.
- Session corpus for this feature is thin — most prompts that mention MCP discuss
  *consumers* (vibecast plugin injection, runner MCP, aspire agent mcp) rather than the
  pks-cli server itself, because the SDK migration predates the brain ingest window.
  Treat the linked sessions as adjacent context, not direct design intent.
