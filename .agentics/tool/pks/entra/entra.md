---
title: "pks entra"
description: "Create or adopt an Entra ID app registration, keep its client secret in the encrypted store, and bind it into a run without anybody pasting a guid."
tags: [reference, cli, entra, identity, providers]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks entra app <init|list|forget> [options]"
examples:
  - command: "pks entra app init \"Margin v1 (dev)\" --alias margin-v1 --manual"
    description: "Store a registration somebody else made — typed in, no Graph, no permissions"
  - command: "pks entra app init \"Margin v1 (dev)\" --alias margin-v1"
    description: "Create or adopt the registration and store its secret out of reach"
  - command: "pks entra app list"
    description: "What pks holds, and when each secret expires"
  - command: "pks entra app init \"Margin v1 (dev)\" --alias margin-v1 --rotate"
    description: "Mint a new secret and remove the one it replaces"
---

`pks entra app` provisions the app registration an application signs in with, and puts the client
secret somewhere nothing reads it back out of.

The routine it replaces is the portal one: App registrations → New registration → Certificates &
secrets → New client secret → copy the blue box → `dotnet user-secrets set`. Six months later, again.
On the next machine, again. Every one of those copies is a live credential in a shell history, a chat
log or a settings file — which is how the one in this workspace leaked.

What comes out instead is an **alias**. `pks aspire run` binds the alias into the parameters an
AppHost declared; nothing prints the secret, and these commands could not print it if they tried — what
they hold is a `SecretValue`.

## Two ways in

**Manual — `--manual`.** The registration already exists; you have its ids and a secret. The command
asks for the three values, stores them under the alias, and calls nothing. No sign-in, no Graph
permissions, no directory write. Use this in a tenant you do not administer — a customer's production
directory — or whenever creating app registrations is somebody else's job. Everything downstream is
identical; the only difference is who made the registration.

**Provisioned — the default.** pks creates or adopts the registration itself, and mints the secret so
that nobody ever sees it. This is the convenience mode, and it belongs in tenants you own.

Either way the secret goes into the same encrypted store, and the same alias comes out.

## Sign-in

The provisioned path uses the Azure sign-in pks already has (`pks foundry init`), and asks Microsoft
Graph for a token with it. The account has to be allowed to register applications in that tenant; if it
is not, Graph says so in its own words and the command repeats them. `--manual` needs none of this and
is checked before the sign-in is, so it works for exactly the operator who cannot use the other path.

**Check which tenant you are about to write to.** The command prints it and asks before the first
write, because an app registration lands in a company directory and stays there.

## Commands

### `pks entra app init [NAME]`

Adopt-or-create, then make sure the registration is usable and its secret is stored.

| Option | Meaning |
| --- | --- |
| `--alias <ALIAS>` | What to call it locally — the name a capability binds (default: from NAME) |
| `--redirect-uri <URI>` | Web redirect URI; repeatable, **added** to whatever is already registered |
| `--spa-redirect-uri <URI>` | The same for a single-page app |
| `--audience <AUDIENCE>` | `AzureADMyOrg` (default), `AzureADMultipleOrgs`, `AzureADandPersonalMicrosoftAccount` |
| `--adopt <APPID>` | Adopt this exact registration instead of searching by display name |
| `--expires-days <DAYS>` | Secret lifetime, 1–730 (default 180) |
| `--rotate` | Mint a new secret even if a live one is stored, and remove the one it replaces |
| `--yes` | Skip the confirmation |
| `--manual` | Type in a registration that already exists; **no Graph call at all** |

With `--manual` the only options that apply are `--alias` and the name: it asks for the tenant id, the
client id and the secret (masked), stores them, and stops. It never warns before that secret expires,
because nothing told it when — and `--rotate` cannot replace it, because pks does not know which of the
registration's credentials it is. Both of those stay a portal visit.

Without it, what it does, in order: finds the registration by `--adopt` or by display name and creates one only if
there is none; makes sure it has a **service principal**, without which the registration is a
definition nothing can sign in against; adds any redirect URIs that are not there yet; and mints a
client secret unless a live one is already stored under the alias.

Running it twice is not two registrations and not two secrets. That is the whole point of the adopt
path — a second registration with the same name and none of the first one's redirect URIs looks like
success and then nothing can sign in.

### `pks entra app list`

What pks holds: alias, display name, client id, and when the secret expires — green, yellow inside 30
days, red past it. `--directory [PREFIX]` lists the tenant's registrations instead, which is how you
find the client id for `--adopt` without opening the portal.

### `pks entra app forget <ALIAS>`

Drops the local copy. Deliberately local only: the registration and the credential stay in the
directory, and the command says so.

## Binding it into a run

An AppHost declares which parameters the identity fills, exactly as it does for a model:

```csharp
builder.AddPksCapability("margin-v1", "The Entra app registration this app signs in with")
       .Offers("entra", "An app registration provisioned by `pks entra app init`")
       .Binds(entraTenantId,     "{entra:tenantid}")
       .Binds(entraClientId,     "{entra:clientid}")
       .Binds(entraClientSecret, "{entra:clientsecret}");
```

The alias defaults to the capability's own name, so `AddPksCapability("margin-v1")` and
`pks entra app init --alias margin-v1` line up without either side naming the other twice. A binding
can override it — `{entra:clientid:some-other-alias}` — which is how one composition binds two
registrations.

An `entra` provider counts as available only when the alias is **already stored**. Writing to a company
directory is not something a run gets to decide on its way past, so a missing alias never provisions
anything — it says which command would.

### Typing it in at run time, and keeping it nowhere

When the alias is unknown and the run is interactive, `pks aspire run` offers to take the three values
right there. They go into the child process's environment and nothing else — the follow-up question,
*keep it for the next run?*, defaults to **no**.

That is the mode for a credential you are not ready to put on disk: try it, see the app come up, decide
afterwards. The cost is that every run asks again until you answer yes. Aspire's own parameter dialog
will also ask — but its checkbox says *Save to user secret*, and what that writes is plaintext under
the project for as long as the project exists. This asks and forgets.

`--non-interactive` never asks; an unattended run with an unknown alias skips the capability, because a
prompt in CI is a hang rather than a question.

## Traps

- **A registration without a service principal cannot be signed in to.** `az ad app create` leaves
  exactly that state behind and the error arrives much later, somewhere unrelated. `init` creates it.
- **A PATCH replaces the whole redirect-URI collection.** Sending only the new URI unregisters
  everything else. `init` reads first and unions; if you do this by hand, do the same.
- **`--rotate` removes only the credential pks minted.** One somebody else added is theirs, and stays —
  including everything stored with `--manual`, which pks deliberately keeps no key id for. Replacing a
  hand-made secret means replacing it in the portal and storing the new one.
- **The stored secret expires quietly.** The first symptom is `AADSTS7000222` on a Tuesday morning;
  `pks entra app list` is the thing that says so a month early, and an expired one is replaced on the
  next `init` without being asked.

## See also

- [`pks aspire`](../aspire/aspire.md) — where the alias gets bound into a run
- [`pks exec`](../exec/exec.md) — the same handshake for a command-line tool
