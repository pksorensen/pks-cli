# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Orchestrator Role

Work as a coordinator: assign sub-tasks/agents to do the work, don't implement everything yourself. Apply TDD — write tests first once a design is agreed, then implement.

## Build and Development Commands

```bash
# Build
dotnet build pks-cli.sln

# Run locally without installing (from repo root)
cd src && dotnet run -- [command] [options]
# Example: cd src && dotnet run -- init TestProject --template console

# Run all tests
cd tests && dotnet test

# Run a single test class
cd tests && dotnet test --filter "FullyQualifiedName~DevcontainerSpawnRemoteTests"

# Run tests by category/speed
cd tests && dotnet test --filter "Category=Unit&Speed=Fast"
cd tests && dotnet test --filter "Category=Integration"

# Run tests using the helper script (from tests/)
./run-tests.sh --category Unit --only-fast
./run-tests.sh --exclude-unstable --exclude-slow

# Install as global tool for manual end-to-end testing
./install.sh
# or force reinstall:
FORCE_INSTALL=true ./install.sh
```

## Architecture

### Entry Point and DI

`src/Program.cs` is the entry point. All DI registration happens there — no separate Startup class. Spectre.Console.Cli's `TypeRegistrar` and `TypeResolver` (`src/Infrastructure/TypeRegistrar.cs`) bridge Microsoft DI to Spectre's command resolution.

Commands that take many services use constructor injection; Spectre.Console.Cli resolves them automatically from the DI container.

### Command Pattern

All commands inherit `Command<TSettings>` (from Spectre.Console.Cli) where `TSettings` defines the CLI args/options. Long-running or async work uses `Execute()` calling `ExecuteAsync().GetAwaiter().GetResult()` — Spectre's `Execute` is synchronous.

Base classes:
- `DevcontainerCommand<T>` — shared display helpers (`DisplayBanner`, `DisplaySuccess`, etc.)
- `DevcontainerSpawnCommand` — base for remote devcontainer spawning; `ClaudeSpawnCommand` and `VibecastCommand` inherit from it and override `OnAfterRemoteSpawnAsync`

The inheritance chain for SSH-based commands:
```
DevcontainerSpawnCommand (DevcontainerSpawnCommand.cs)
  ├── ClaudeSpawnCommand (Commands/Claude/ClaudeSpawnCommand.cs)
  └── VibecastCommand   (Commands/Vibecast/VibecastCommand.cs)
```

`BuildFoundryEnvArgsAsync` (line 1554 of `DevcontainerSpawnCommand.cs`) is the key method that provisions Azure AI Foundry MSI token access for devcontainers.

### SSH and Remote Execution

`DevcontainerSpawnCommand` shells out directly to the `ssh` and `scp` system commands via `Process`/`ProcessStartInfo`. No SSH library is used. `RunSshCommandAsync` (line 1229) is the shared helper.

`PksSSHAgent` (`src/Infrastructure/Services/PksSSHAgent.cs`) is the pattern for long-lived background processes: `IAsyncDisposable`, `CancellationTokenSource`, `StartAsync()` that starts a background `Task`. Follow this pattern for any new Host-side infrastructure process.

`SshTarget` and `ISshTargetConfigurationService` (in `SshTargetConfigurationService.cs`) manage registered SSH targets stored in `~/.pks-cli/ssh-targets.json`.

### Services Layer

Services live in `src/Infrastructure/Services/` and follow the interface/implementation pattern. Complex services registered with `AddHttpClient<IFoo, Foo>()` get `HttpClient` via DI. Config-only objects (e.g., `AzureFoundryAuthConfig`, `AzureFoundryAuthConfig`) are registered with `AddSingleton<TConfig>()`.

Stored credentials (Foundry, ADO, Azure) persist to `~/.pks-cli/` via `IConfigurationService` (`src/Infrastructure/Services.cs`).

### Initializer System

`pks init` uses a pipeline of `IInitializer` implementations in `src/Infrastructure/Initializers/Implementations/`. Each initializer has an `Order` property; they run lowest-first. `ShouldRunAsync(context)` controls conditional execution. Initializers are auto-discovered via reflection — place new ones in `Implementations/` and they register automatically.

### Test Patterns

Tests use xUnit + Moq/NSubstitute + FluentAssertions. The `TestTraits` system in `tests/Infrastructure/TestTraits.cs` tags tests with `Category` (Unit/Integration/EndToEnd), `Speed` (Fast/Medium/Slow), and `Reliability` (Stable/Unstable/Experimental) via `[Trait]` attributes.

Shared mock helpers are defined as `private static` factory methods at the top of each test class. `Spectre.Console.Testing` provides `TestConsole` for capturing command output.

### Key Models

- `SshTarget` — SSH connection config (host, port, username, key path, label)
- `FoundryStoredCredentials` — Azure AI Foundry OAuth creds (`TenantId`, `RefreshToken`, `SelectedResourceName`, `EnabledModels`, `ApiKey`)
- `AzureFoundryAuthConfig` — OAuth2 client config (ClientId, tenant URLs)
- `DevcontainerSpawnOptions` / `DevcontainerSpawnResult` — devcontainer spawn parameters and results

## GitHub Integration

Scope all GitHub operations to `repo:pksorensen/pks-cli`.

## Releases

Release Please in manifest mode. Conventional commits are required. Each package (`src/`, `templates/devcontainer/`, etc.) has an independent version tracked in its `version.txt` and `.csproj`. Use `scripts/update-version.sh` and `scripts/get-package-version.sh` for version management. CI publishes preview packages on every push to `main`; stable releases require merging the Release PR.

## Planning

When asked to make a plan, use `EnterPlanMode` (the `/plan` tool) rather than writing inline plans.
