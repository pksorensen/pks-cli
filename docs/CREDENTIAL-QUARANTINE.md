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
  getter) may not be named anywhere under `src/Commands/` or `src/Infrastructure/Services/MCP/` — a
  build-failing test enforces it, because inside one assembly the type system cannot express
  "reachable but not printable".
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
| `tests/Services/Security/SecretResolverGateTests.cs` | The gate. Fails the build if a command or MCP tool names the plaintext API. |

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

## Adding a new credential

Name the key so the classifier catches it — `*.token`, `*.credentials`, `*api_key*`, `*.secret`,
`*password*` and friends. Then add it to the inventory in `SecretKeysTests`, which asserts every real
credential key classifies as secret and every ordinary setting key does not. A miss on the first list
leaks a credential; a false positive on the second silently breaks a feature.
