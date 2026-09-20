# Azure resources: one sign-in per tenant, many enabled resources

`pks loganalytics`, `pks appinsights`, `pks fileshare`/`pks storage` and `pks acs` share one
model since 7.x: **sign in once per Azure tenant**, then tick which resources are enabled. The
consumer commands (`pks kusto`, `pks otel …`, `pks storage …`) run against every enabled
resource, not a single "current" one.

## The four `init` commands are the same command

```bash
pks loganalytics init        # checkbox list of workspaces      (space toggles, enter saves)
pks appinsights init         # checkbox list of App Insights resources
pks fileshare init           # checkbox list of storage accounts
pks acs init                 # checkbox list of SMS senders (Communication Services)
```

Each `init`:

1. **Ensures a sign-in.** No tenant known ⇒ browser login (email → tenant discovery →
   PKCE). A tenant already known ⇒ **no browser**. A tenant whose refresh token has died
   is offered a sign-in for that tenant only.
2. **Discovers** across every signed-in tenant × every subscription (`--tenant` /
   `--subscription` narrow it) and merges the result into the registry. Entries that are
   no longer discoverable stay, marked "(not found)".
3. **Shows the checkbox list** grouped by tenant → subscription, pre-ticked with what is
   enabled today. Unticking sets `enabled=false` and keeps the entry, so the next `init`
   shows it again without re-discovery.

Switching prod off is therefore `pks fileshare init`, untick `stprod`, enter.

| Option | Meaning |
| --- | --- |
| `-f, --force` | Kept for muscle memory. Same as plain `init`. **Never clears credentials.** |
| `--reauth [<tenant>]` | Browser login for that tenant (or every known tenant). The only way to get a browser when a tenant is already known. |
| `-t, --tenant <id\|email>` | Add or sign in to another tenant; also narrows discovery to it. |
| `-s, --subscription <id>` | Narrow discovery. |
| `--enable <name\|key>` / `--disable <name\|key>` | Non-interactive toggles, repeatable. No discovery, no prompt. |
| `--list` | Print the registry for that kind. No discovery, no prompt, no sign-in. |
| `-w, --workspace <guid>` (loganalytics only) | Register a workspace GUID directly, enabled, no ARM lookup. |

`status` for each vertical prints every entry with its enabled flag, a connection test per
enabled entry, and the sign-in state of every known tenant.

## What is stored where

| File | Contents | Secret? |
| --- | --- | --- |
| `~/.pks-cli/secrets.json` → key `azure.tenants.credentials` | One refresh token per tenant id (Azure CLI public client, scope `management.azure.com/.default offline_access`). The same token serves the storage, Log Analytics and Application Insights scopes. | Yes (AES-GCM, see `CREDENTIAL-QUARANTINE.md`) |
| `~/.pks-cli/azure-resources.json` | The registry: `{kind, tenantId, subscriptionId, subscriptionName, name, resourceId, key, resourceGroup, enabled, discoveredAt, endpoint?}` per resource. `key` is what consumers query with: workspace customer-id GUID, App Insights app id, storage account name, SMS sender (`+4566339237` or an alphanumeric id). `endpoint` is only set for SMS senders (the ACS host name). | No |
| `~/.pks-cli/config.json` → `acs.sms.from`, `acs.sms.recipient` | The default SMS sender (one of the enabled registry keys) and the phone number the runner's SMS notifications go to. Plain settings, not secrets. | No |
| `~/.pks-cli/azure-token-cache.json` + `.lock` | Access tokens keyed `(tenant\|scope)`, 50-minute lifetime, cross-process file lock. | Short-lived bearer, 0600 |

Why the token cache exists: Entra rotates the refresh token on every redemption. Fanning
out over N workspaces in one tenant, or running several `pks kusto` in parallel, would
otherwise redeem the same refresh token N times; the first wins and the rest fail with
`invalid_grant`. The cache collapses that to one refresh per `(tenant, scope)`.

## Consumers

- `pks kusto "<kql>"` — every enabled workspace in parallel. Table: one section per
  workspace. Json: still one flat array, each row gets a first `"workspace"` property.
  Csv: leading `workspace` column. `--workspace <name>` resolves among **enabled** entries
  only; a bare GUID is an explicit override and bypasses the registry. Exit 1 only when
  every workspace failed. Details in `loganalytics-kusto.md`.
- `pks otel errors|traces|logs|spans` — every enabled App Insights resource, rows merged
  and stamped with `Resource`; `--resource <name|appid>` (repeatable) narrows.
- `pks storage list|ls|sync|rm` — every enabled storage account. `--account` disambiguates
  a share name that exists in more than one account; `rm` still binds consent to
  `azure-fileshare:{account}/{share}`.

- `pks acs sms send [MESSAGE]` — one SMS from the default sender to the default recipient
  (message from the argument or stdin; the prompts let you pick another sender/recipient).
  Messages are cut at 306 characters (two GSM-7 segments) and capped at 30 per hour per
  process. The message text is never logged — only its length.

No browser is ever opened from a consumer command. An expired tenant fails with
`Run 'pks <vertical> init --reauth <tenant>'`.

## Upgrading from the single-resource stores

The first command that touches the registry migrates once:

- `loganalytics.*` and `appinsights.*` keys ⇒ one enabled entry each, then the keys are deleted.
- `fileshare.azure.credentials` ⇒ its refresh token is **moved** into the tenant store
  (never copied — a copied refresh token dies on the next rotation) and its selected
  storage account becomes an enabled entry.
- The `pks foundry` credential is left alone: `foundry proxy/select/token/usage` and the
  remote-settings export depend on its exact shape. A tenant known only to Foundry needs
  one browser login (`pks loganalytics init --reauth`) after the upgrade; until then the
  query services fall back to the Foundry token when the tenant store is empty.

Legacy entries migrate with `tenantId = null`; consumers then use the single known tenant,
and `init`'s next discovery fills the tenant in.

## What `--force` used to do, and why it changed

`pks appinsights init --force` deleted `foundry.auth.credentials` — the credential shared
by `foundry`, `loganalytics` and `appinsights` — which is why switching environments
forced a browser login and logged the other two verticals out. `loganalytics init --force`
never touched credentials, so the two felt inconsistent. Credential clearing is now only
`--reauth`, and it re-logs in rather than leaving a hole.

## `pks acs`: SMS senders and the runner's `sms` capability

Communication Services is the odd one out: **one registry entry is one sender**, not one
resource. `pks acs init` lists every `Microsoft.Communication/communicationServices`
resource per subscription (ARM), then asks each resource's data plane
(`GET https://{hostName}/phoneNumbers`) for its numbers and keeps the ones whose
`capabilities.sms` allows outbound. Voice-only numbers never show up. The data plane is
called with the same tenant sign-in and the scope `https://communication.azure.com//.default`;
a 401/403 there means the signed-in identity lacks a role (Contributor) on that resource,
and `init` says so and moves on.

Denmark accepts SMS only from **Mobile numbers** and **alphanumeric sender ids** (≤ 11
characters, one-way). Toll-free and geographic numbers cannot send here, so `init` also
offers to add an alphanumeric id by hand; it is stored as a sender entry on the resource
you pick and survives re-discovery.

After the checkbox list, `init` asks for the default sender (`acs.sms.from`), the
recipient of the runner's notifications (`acs.sms.recipient`, E.164) and offers a test
send. `pks acs status` prints the entries, the two defaults and whether the runner would
advertise `sms`.

**Runner capability.** `pks agentics runner run` advertises `sms` in its poll when an
enabled default sender and a recipient are configured on the host — nothing else. The
platform then queues SMS deliveries for a station whose notify channels include `sms`;
the runner drains `GET …/runners/deliveries` on its own 5-second loop (independent of the
job poll, which is blocked while a station job runs), sends each through ACS and posts
`sent`/`failed` back to `…/runners/deliveries/{id}`. The platform never holds ACS
credentials, the line's tool never knows a channel exists, and the recipient lives on the
runner host rather than on the station, so no phone number ends up in an exported line repo.

