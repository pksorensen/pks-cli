# report-dependencies

Reports a repository's dependencies into [Agentics Exposure](https://exposure.agentics.dk)
so the registry can answer "does today's CVE hit us?" as a lookup instead of an
afternoon.

```yaml
permissions:
  contents: read
  id-token: write          # required — the action mints an OIDC token

steps:
  - uses: actions/checkout@v4
  - uses: pksorensen/pks-cli/.github/actions/report-dependencies@main
```

That is the whole integration. No secret, no PAT: the server verifies a GitHub
OIDC token and binds the inventory to the `repository` and numeric
`repository_id` claims, so a repo can only report as itself and renaming it does
not create a second system.

## Reporting a built image instead of the checkout

A checkout tells you what the source depends on; an image tells you what is
actually going to run, including its base layer's OS packages. Report both if the
repo builds a container — they are different systems with different exposure.

```yaml
  - uses: pksorensen/pks-cli/.github/actions/report-dependencies@main
    with:
      system: myapp-image
      image: ghcr.io/pksorensen/myapp@${{ steps.build.outputs.digest }}
```

Pass the **digest**, not the tag. A tag is a moving pointer: scanning `:latest`
tells you about whatever it points at now, which is not necessarily what the
running container was started from.

## Inputs

| Input | Default | Notes |
| --- | --- | --- |
| `server` | `https://exposure.agentics.dk` | |
| `audience` | same as `server` | Must match the server's `EXPOSURE_GITHUB_AUDIENCE` exactly |
| `system` | the repository name | Lowercased and normalised to a path segment |
| `path` | `.` | Directory to scan |
| `image` | — | Scan an image instead of the checkout |
| `sbom` | — | Use an SBOM you already generated; skips syft |
| `syft-version` | `1.33.0` | Only used when syft is not already on PATH |
| `fail-on-actionable` | `false` | See below |

## Why `fail-on-actionable` defaults to off

A dependency can publish an advisory overnight. With this on, that breaks the
next unrelated build in a repository nobody changed — and the reliable outcome
of a security check that breaks unrelated builds is that people stop running it.
The report still lands, the registry still pushes to your phone, and the job
summary still shows the counts.

Turn it on for a repository where you want the gate.

## What it does with secrets

Property and reference keys matching `token|password|secret|apikey|credential|authorization|passphrase`
are stripped from the document before it is transmitted. A CycloneDX assembled
from a checkout can carry `.npmrc` tokens and registry credentials, and the right
place to drop a credential is before it leaves the runner — not in the service
that receives it.

## Order of operations

The SBOM is generated **before** the OIDC token is minted. GitHub's assertion is
short-lived and an image scan is slow; minting first is how a long build arrives
at the upload with an expired token.
