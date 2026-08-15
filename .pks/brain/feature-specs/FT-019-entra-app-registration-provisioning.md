---
id: FT-019
title: Entra app registrations — pks provisions the identity, and keeps the secret
domain: security
status: implemented
adrs: []
tests: [tests/Services/Entra/EntraApplicationServiceTests.cs, tests/Services/Exec/ManifestResolverTests.cs]
source-files: [src/Infrastructure/Services/Entra/EntraApplicationService.cs, src/Infrastructure/Services/Entra/EntraModels.cs, src/Commands/Entra/EntraAppInitCommand.cs, src/Commands/Entra/EntraAppListCommand.cs, src/Commands/Entra/EntraAppForgetCommand.cs, src/Infrastructure/Services/Exec/ManifestResolver.cs]
sessions: [5870168d-2595-4db1-88bb-3706fa630fea]
---

## Description
`pks entra app init [NAME]` creates or adopts an Entra ID app registration through Microsoft Graph,
makes sure it has a service principal and the requested redirect URIs, mints a client secret and
writes it straight into the encrypted store under an alias. Nothing prints it and no command can:
what a command holds is a `SecretValue`. `pks aspire run` then fills an AppHost's tenant/client/secret
parameters from that alias through the `entra` provider kind and the `{entra:*}` placeholders, so the
composition never has the credential in source, in user secrets, or in a paste. `pks entra app list`
shows what is held and when each secret expires; `pks entra app forget` drops the local copy without
touching the directory.

## Intent
> From session 5870168d (2026-08-14), prompt:
> "det betyder også i margin projektet kunne vi have lavet pks-cli azure-app-reg init og sætte en credencials op som ikke kan tilgåes af agenten og fodre den ind i aspire som environment variable til parameter eller via args istedet for at copy paste hver gang?"

> From session 5870168d (2026-08-14), prompt:
> "og ja pks entra app init er fint at få tilføjet som en måde at registere en app reg der kan bruges til app adgang."

## Key decisions
- **The sign-in pks already has.** Graph is called with a token minted from the stored Azure refresh
  token (`IAzureFoundryAuthService.GetAccessTokenAsync("https://graph.microsoft.com/.default")`). The
  client is the Azure CLI's well-known public client, which pks already uses everywhere else, and a
  refresh token there is per user and per client rather than per resource — the same move `az` makes
  when it goes from ARM to Graph without asking you to sign in twice. No new app registration is
  needed to create app registrations.
- **Adopt-or-create, never create-only.** Margin's registration already existed, with the localhost
  redirect URI on it. A command that only mints new ones produces a twin with no redirect URI: it
  reports success and then nothing can sign in.
- **A service principal is part of "provisioned".** Without one the registration is a definition
  nothing can sign in against. `az ad app create` leaves that state behind and the error surfaces much
  later somewhere unrelated.
- **Redirect URIs are unioned, not set.** Graph's PATCH replaces the whole collection.
- **The secret never becomes a string a command can hold.** It goes from the Graph response into a
  `SecretValue`, into the encrypted store under `entra.app.{alias}.credentials` — the trailing word is
  what `SecretKeys` classifies as credential material — and out again only through the resolver, into a
  child process's environment via `SecretSink`.
- **An alias, not a guid.** The capability name is the default alias, so
  `AddPksCapability("margin-v1")` and `--alias margin-v1` line up without either side naming the other
  twice; `{entra:clientid:other-alias}` overrides it where one composition binds two registrations.
- **Availability means already provisioned.** The `entra` provider is unavailable when the alias is
  unknown, so a run skips the capability and prints the command that would create it. Writing to a
  company directory is not something a run decides on its way past.
- **Confirmed before the first write.** `init` prints the account and the tenant and asks, because the
  object lands in a directory somebody else administers and stays there. `--yes` for scripts.

## Gotchas / known issues
- **A registration with no service principal cannot be signed in to**, and the failure is far from the
  cause.
- **`--rotate` removes only the credential pks minted itself** (matched by stored keyId). One somebody
  else added is theirs. If removal fails the command still succeeds — the new secret is stored and
  working, and failing there would be worse than an old credential the operator can remove by hand.
- **The stored secret expires quietly**; the first symptom is `AADSTS7000222`. `list` colours it a
  month early and `init` replaces an expired one without being asked.
- **The declare pass sees a different composition than the run.** Margin declared
  `entra-client-secret` only when there was no Key Vault, and the vault exists in publish mode — which
  is the mode `aspire do` runs in — so the one parameter this feature exists to fill was absent from
  the manifest. `PksDeclareExtensions.IsDeclaring` is the fix; see FT-018.
- **Graph's own error text is the useful half.** `403` says nothing;
  `Authorization_RequestDenied: Insufficient privileges to complete the operation` is the difference
  between asking an admin and debugging for an hour. It is surfaced verbatim; the request body never
  is, because it carried a secret.
- **Not yet run against a real tenant.** The read path is proven live (listing the Context& tenant's
  registrations through Graph with the stored sign-in); every write path is proven against a scripted
  Graph in tests. The first real create/adopt is the operator's call, in a directory they own.
