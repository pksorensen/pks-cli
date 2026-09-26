# pks-cli runtime

`pks-cli-runtime` is the long-running capability adapter host for pks-cli. It
implements Agentics Gateway capability contracts and translates them to cloud,
model, tool, or local provider protocols.

It is separate from the ordinary `pks-cli` runner process:

```text
pks-cli runner       executes finite ALP tasks and stations in sandboxes
pks-cli-runtime      serves long-lived streaming capabilities behind the Gateway
```

The runtime is not a public application endpoint. Clients call
`pks-agent-gateway`; the Gateway authenticates and routes the connection to a
compatible runtime.

## Current capability

The first built-in capability is:

```text
speech.realtime.transcribe
```

It currently adapts the Agentics realtime speech protocol to Azure OpenAI
Realtime Transcription using Azure Managed Identity.

## Run

```bash
PKS_RUNTIME_CAPABILITIES=speech.realtime.transcribe \
PKS_FOUNDRY_ENDPOINT=https://<foundry>.services.ai.azure.com \
PKS_FOUNDRY_TRANSCRIPTION_MODEL=gpt-4o-transcribe-diarize \
dotnet run --project src/pks-cli-runtime
```

The container image is:

```text
registry.agentics.dk/agentics/pks-cli-runtime:<version>
```

`GET /healthz` reports runtime health and enabled capability identifiers.
`GET /v1/capabilities` reports the concrete capability descriptors used by the
Gateway.

## Configuration

| Variable | Purpose |
|---|---|
| `PKS_RUNTIME_CAPABILITIES` | Comma-separated enabled capability identifiers. Defaults to `speech.realtime.transcribe`. |
| `PKS_RUNTIME_TOKEN` | Bearer token accepted from the Gateway. |
| `PKS_SPEECH_PROVIDER` | Speech provider adapter. |
| `PKS_FOUNDRY_ENDPOINT` | Azure AI Foundry account endpoint. |
| `PKS_FOUNDRY_TRANSCRIPTION_MODEL` | Foundry transcription deployment name. |
| `AZURE_CLIENT_ID` | Optional user-assigned managed identity client ID. |

`PKS_SPEECH_RUNTIME_TOKEN` remains accepted as a migration alias for
`PKS_RUNTIME_TOKEN`.

## Adding a capability

1. Implement `IRuntimeCapability` under `src/Commands/Runtime/`.
2. Give it a stable capability identifier owned by the Gateway specification.
3. Register it in `PksRuntimeHost.CreateCapability`.
4. Add protocol and provider-translation tests.
5. Document the wire contract in the `pks-agent-gateway` repository.

Provider-specific configuration and credentials belong in the adapter. They
must not leak into the Gateway client contract.
