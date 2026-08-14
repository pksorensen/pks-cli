# Credential quarantine

## The failure this exists to stop

An agent wanted a workspace id, ran `cat ~/.pks-cli/settings.json`, and put a Jira API token, GitHub
access and refresh tokens, a Google API key and refresh tokens for ADO, Foundry and fileshare into a
transcript. Nothing was misconfigured — every one of those credentials was sitting in plaintext in a
file that any command, any support bundle, any MCP status tool and any `cat` would print in full.

The fix is not masking and not a confirmation prompt. There is no read path.

## What holds now

- **`settings.json` never contains credential material.** Anything whose key classifies as secret is
  moved out on load and stripped again before every save.
- **The configuration surface cannot return a secret.** `IConfigurationService.GetAsync` returns
  `null` for a secret-classified key and `GetAllAsync` omits it. No flag, no override, no "just this
  once". A config dump is therefore harmless by construction.
- **The plaintext API is off-limits where output is seen.** `ISecretResolver` (the one type with a
  getter), `Reveal`/`RevealAsync` and `SecretJson` may not be named anywhere under `src/Commands/` or
  `src/Infrastructure/Services/MCP/` — a build-failing test enforces it, because inside one assembly
  the type system cannot express "reachable but not printable".
- **A stored credential is not a `string`.** The auth services' credential DTOs carry `SecretValue`,
  a struct with no conversion to `string`, a `ToString()` of `"***"`, a `GetHashCode` that does not
  depend on the credential, and masked-by-default serialization. A command can test `HasValue`, pass
  it along, and put it where it needs to go through `SecretSink` — an environment dictionary, a
  `ProcessStartInfo`, a `docker -e` argument, an `Authorization` header, an OAuth form field — but
  cannot obtain the text. Changing a field to `SecretValue` turns every careless read into a compile
  error rather than silent masking, which is why there is deliberately no implicit conversion.
- **No service API returns a credential-bearing plain string to the command layer.** This is the rule
  the source scan can never enforce, so the API surface has to: a method like
  `ITailscaleService.BuildUpArgs`, whose result embeds an auth key, returns `SecretValue`, and the
  work that needs the plaintext (`JoinTailnetAsync`) lives in the service with the command supplying
  only the runner. A helper that hands back a string containing a credential is `Reveal()` with extra
  steps, and the gate cannot see it.
- **Nothing falls back to the real store.** The services that read credentials take `ISecretResolver`
  as a required constructor parameter. They used to default it to `new SecretStore()`, which meant a
  test that forgot the argument silently authenticated as whoever was logged in on that machine — and
  an assertion comparing the token to an expected literal printed the real credential in the failure
  message. That happened twice before it was caught, the second time putting a live Scaleway secret
  key into an agent transcript. Tests use `FakeSecretResolver`
  (`tests/Infrastructure/Security/FakeSecretResolver.cs`); forgetting it is now a compile error.
- **Storage is encrypted at rest.** AES-GCM per value in `~/.pks-cli/secrets.json`, keyed by a 32-byte
  KEK sidecar at `~/.pks-cli/.secrets-kek`, everything 0600. This does not stop a same-UID attacker —
  it stops the stray `cat`, the synced dotfile, the backup and the support bundle from yielding a
  usable token.

## The pieces

| File | Role |
| --- | --- |
| `src/Infrastructure/Services/Security/SecretKeys.cs` | The single classifier. One regex decides what counts as a credential, and both migration and `SetAsync` use it. |
| `src/Infrastructure/Services/Security/SecretStore.cs` | `ISecretStore` (write-only: set/has/describe/delete/list) and `ISecretResolver` (`RevealAsync`), both implemented by the encrypted store. |
| `src/Infrastructure/Services/Security/SecretSeedingService.cs` | Copies one named credential into another HOME's store, re-encrypted. |
| `src/Infrastructure/Services.cs` | `ConfigurationService` — routes secret writes to the store, hides them from reads, and migrates on load. |
| `src/Infrastructure/Services/Security/SecretValue.cs` | The credential type: no string conversion, masked `ToString`, masked serialization, plus `SecretJson.Persistence` for the one place a credential is written unmasked. |
| `src/Infrastructure/Services/Security/SecretSink.cs` | The sanctioned egress points. Each is a no-op when the credential is absent and returns `bool`, so a missing credential shows up as "not configured" rather than an empty `Authorization` header that reads like a broken one. |
| `tests/Services/Security/SecretResolverGateTests.cs` | The gate. Fails the build if a command or MCP tool names the plaintext API. |
| `tests/Infrastructure/Security/FakeSecretResolver.cs` | What a test injects instead of the real store. Empty by default; `BackedBy(…)` routes at a fixture's own configuration mock. |

## Migration

Nobody re-authenticates anything. On every `LoadSettingsAsync`:

1. Entries whose value is the old `***encrypted***` sentinel are dropped — that credential was already
   destroyed, and migrating it would enshrine garbage as a working token.
2. Entries whose key classifies as secret are written into the encrypted store and removed from
   `settings.json`.
3. Everything else stays exactly where it was.

This runs on *every* load, not once, because `dotnet dnx pks-cli` means an older binary can still write
plaintext back during the rollout window. `SaveSettingsAsync` strips secret keys unconditionally as the
second half of that guard.

Every command that reads the store loads settings first for the same reason. Without it, the first
`pks secrets list` after an upgrade would print "no credentials stored" while every token still sat in
plaintext on disk, and `seed-home` would report "nothing to seed" for a credential that was right
there — leaving the ALP runner without a Foundry session. The store is only the truth *after* the
sweep has run.

## Working with it

```bash
pks secrets list                                    # what is stored, when, fingerprint
pks secrets status github.auth.token                # presence + fingerprint for one key
pks secrets delete github.auth.token                # forget it; re-run the login to restore
pks secrets seed-home foundry.auth.credentials --home /tmp/runner-home
```

Fingerprints are HMAC-SHA256 keyed by the local KEK, truncated. They prove two stores hold the same
value without being an offline oracle: the same credential on another machine fingerprints
differently, so holding the file does not let anyone confirm a guess.

`seed-home` is the sanctioned way to give an isolated HOME one credential — the Aspire AppHost uses it
to hand the ALP runner a Foundry session, replacing the `File.ReadAllText` on `settings.json` it used
to do. One key at a time, on purpose: "copy the credential store" is the wrong operation for a HOME
that exists to be isolated.

**There is no export command.** If you need a credential somewhere else, authorize it there or rotate
it — do not fetch it.

## If the gate test fails

Do not add an exception. Move the work that needs the credential into a service under
`src/Infrastructure/Services/` and have the command ask that service to do the thing. The worked
example is `AgenticsRunnerStartCommand`, which asks
`IAgenticsRunnerSshHandoffService.ForwardStoredSecretAsync` to forward a token the command itself
never sees, and `HasStoredSecretAsync` when it only needs to know whether one exists.

If the command only needs the credential to *arrive* somewhere, that already exists: `SecretSink`
performs the five sanctioned deliveries without handing anything back.

When you move the work into a service, do not let the credential come back out as a return value.
`ITailscaleService` is the shape to copy: `BuildUpArgs` returns `SecretValue` so cloud-init can carry
it, and `JoinTailnetAsync` composes the whole `tailscale up …` command line internally, taking the
caller's SSH runner as a delegate. The command supplies the *how to run*, never the *what to run*.

## What this does not stop

A determined caller inside `src/Infrastructure/Services/` can route a credential somewhere readable —
one assembly, no privilege boundary. The threat model is the careless leak: the `cat`, the config
dump, the interpolation into a log line, the assertion message. `SecretValue` makes those a compile
error, the gate makes reaching for plaintext a build failure in the layers whose output is seen, and
the encrypted store makes the file on disk useless on its own. Nothing here defends against code
written to exfiltrate.

## Adding a new credential

Name the key so the classifier catches it — `*.token`, `*.credentials`, `*api_key*`, `*.secret`,
`*password*` and friends. Then add it to the inventory in `SecretKeysTests`, which asserts every real
credential key classifies as secret and every ordinary setting key does not. A miss on the first list
leaks a credential; a false positive on the second silently breaks a feature.
