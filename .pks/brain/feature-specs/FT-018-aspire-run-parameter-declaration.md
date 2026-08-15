---
id: FT-018
title: Aspire run — an AppHost declares its parameters, pks fills them
domain: agentic-runtime
status: implemented
adrs: []
tests: [tests/Services/Exec/ManifestResolverTests.cs, tests/Commands/Aspire/PksAspireInitCommandTests.cs]
source-files: [src/Commands/Aspire/PksAspireRunCommand.cs, src/Commands/Aspire/PksAspireInitCommand.cs, src/Infrastructure/Services/Exec/ManifestResolver.cs, src/Infrastructure/Services/Exec/PksManifest.cs, src/Infrastructure/Services/Exec/ResolvedEnvironment.cs, src/Infrastructure/Resources/Aspire/PksDeclare.cs.template]
sessions: [5870168d-2595-4db1-88bb-3706fa630fea]
---

## Description
`pks aspire run [-- <apphost args>]` is FT-010's two-phase handshake applied to a
.NET Aspire AppHost. Pass one runs `aspire do pks-declare`, a dependency-free
pipeline step the AppHost carries in a copied `PksDeclare.cs`; it walks the
resource model and writes a v1 manifest to the file named by `PKS_DECLARE_OUT`,
starting nothing. pks resolves that manifest with the same provider/model/
placeholder machinery `pks exec` uses, then pass two runs the real `aspire run`
with the answers injected as `Parameters__<name>` environment variables — the
first place Aspire looks for a parameter value. The developer is never prompted
for an endpoint or a key, and none is written to user secrets, a launch profile
or a shell history. `pks aspire init` writes the AppHost half into a project.

## Intent
> From session 5870168d (2026-08-15), prompt:
> "kunne vi ikke blot lave \"pks aspire run\" og vi har en appbuilder.AddAgenticsGateway() som betyder at den kan startes med pks aspire run hvor pks cli så starter aspire run -- --xxxx et eller andet der trigger at apphost svare tilbage med de parameter som den har brug for fra pks cli og så starter pks aspire run bager efter igen med de rigtige […] så aspire får sat sine paramters den skal bruge uden at vi skal pass secrets ind og rundt manuelt?"

> From session 5870168d (2026-08-15), prompt:
> "ja til pks aspire run og det kan være det er den der sørger for at det virker med aspire runs output? men hvis vi skal se på andre måder at få info ud omkring hvilke paramtere den har behov for så kan vi jo lidt ala aspire publish vel lave noget der hoooker ind i aspire så den køre noget andet end aspire run første gang. Du kan jo bare undersøge hvordan asprie virker og finde en løsning."

## Key decisions
- **A pipeline step, not stdout.** Aspire 13 replaced publishing callbacks with
  `builder.Pipeline.AddStep` and `aspire do <step>`. A step with no
  `dependsOn`/`requiredBy` runs in publish mode without pulling in
  `parameter-prompt` or building any resource image, so asking what a run needs
  cannot start the run. It also avoids FT-010's known limitation: the declare
  pass builds the AppHost, and MSBuild writes on both sides of the document,
  which stdout scanning cannot survive.
- **Injection needs no protocol.** Aspire resolves `Parameters__{name}`
  environment variables ahead of configuration files and ahead of prompting, so
  pass two is an ordinary `aspire run` with a larger environment. Verified
  empirically against Margin v1: the dash survives (`Parameters__ai-base-url`),
  and a shell cannot export that name — only a `ProcessStartInfo` environment
  can, which is also the sanctioned `SecretSink` egress.
- **The AppHost's declarations are the manifest.** `PipelineStepContext.Model`
  exposes every `ParameterResource` with its name, secret flag and description,
  so the step reports all of them and marks the ones a capability binds. Nothing
  is hand-authored twice.
- **The contract travels as source.** `PksDeclare.cs` is an embedded resource
  written into the AppHost by `pks aspire init` — no package reference, no
  project reference. Same choice as the Go side's `internal/pksmanifest`, and
  the only one available to trees that may not reach pks-cli at all.
- **Resolver extracted to a service.** `IManifestResolver` in
  `Infrastructure/Services/Exec/` now backs both `pks exec` and `pks aspire run`.
  It had to be: resolving `{apikey}` reads a quarantined credential, which the
  command layer is forbidden from doing. It returns a `ResolvedEnvironment` —
  applicable to a `ProcessStartInfo`, not readable as a string.
- **`{endpoint:openai}` added to the vocabulary.** Foundry's resource endpoint is
  the root of several APIs and the OpenAI-compatible one hangs off `/openai/v1`,
  while an `OPENAI_BASE_URL` already is that URL. One placeholder that is correct
  whichever provider the operator picks.
- **An optional capability with no provider is skipped, not fatal.** Margin's
  concierge is built for "no model configured"; refusing to start would take that
  state away.

## Gotchas / known issues
- **`AddParameter(name, "literal")` cannot be filled by anything.** That overload
  pins the value and stops consulting configuration, so the environment variable
  is ignored silently and the parameter keeps its old value while every step
  reports success. Cost an hour on Margin's `ai-model`. `PksDeclare.cs` ships
  `SuggestedValue : ParameterDefault` for the case that reads the same and
  behaves as intended.
- **The declare pass runs in publish mode.** An AppHost that branches on
  `ExecutionContext.IsPublishMode` declares a different set of parameters than
  the run will. Register parameters in both modes and gate only the wiring.
- **Both passes need the same `--` arguments.** Margin's model parameters exist
  only with `--ai`; declaring against a different composition than the one about
  to run is the failure that looks like "pks resolved nothing".
- **Not a timeout.** Discovery here compiles a project, so unlike FT-010's ten
  seconds there is no deadline on the declare pass.
- **`PksDeclare.cs.template` was embedded as Czech.** MSBuild's `AssignCulture`
  reads the middle extension of a resource file name as a culture, `cs` is a real
  one, and the resource went into a `cs/` satellite assembly — where
  `GetManifestResourceStream("PksDeclare.cs")` cannot see it. Build green, no
  warning, `pks aspire init` dead on first use. The fix is `WithCulture="false"`
  metadata on the item; `tests/Commands/Aspire/PksAspireInitCommandTests.cs` asks
  the assembly the same question the command does, so it cannot come back quietly.
