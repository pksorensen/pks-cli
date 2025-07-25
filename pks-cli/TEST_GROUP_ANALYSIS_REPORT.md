# PKS CLI Test Group Analysis Report

## Executive Summary

After analyzing the PKS CLI test suite, I've identified **407 potentially active tests** across 43 test files. However, a critical build error is blocking most tests from running. The tests can be organized into 6 logical groups for parallel fixing.

## Current State Overview

### Test Statistics
- **Total Test Files**: 43
- **Total Tests**: ~327 (279 Fact + 48 Theory tests)
- **Skipped Tests**: 112 (explicitly skipped)
- **Active Tests**: ~215 (potentially runnable if build passes)
- **Build-Blocked Tests**: ~70% (due to NuGetTemplateDiscoveryService.cs errors)

### Critical Blocker
**Build Error**: NuGetTemplateDiscoveryService.cs has API mismatches:
```csharp
// Lines 679-681 - Properties don't exist on TemplateInfo
Description = "A universal devcontainer template",  // ❌ Property doesn't exist
Identity = $"{packageId}.Universal",                // ❌ Property doesn't exist  
Tags = new[] { "devcontainer", "universal" }      // ❌ Type mismatch
```

## Test Groups by Domain and Complexity

### Group 1: Quick Wins - Validation Tests (15-20 tests)
**Complexity**: Low | **Fix Time**: 1-2 hours | **Value**: High

#### Files:
- `InitCommandValidationTests.cs` - 6 skipped tests (project name validation)
- `ProjectNameValidationTests.cs` - 1 test (validation rules)
- `DevcontainerValidationTests.cs` - 11 tests (configuration validation)
- `PrdSettingsParsingTests.cs` - 1 test (parsing logic)

#### Common Issues:
- Simple validation logic tests
- Mock setup improvements needed
- Clear pass/fail criteria

### Group 2: Command Execution Tests (50-60 tests)
**Complexity**: Medium | **Fix Time**: 3-4 hours | **Value**: High

#### Files:
- `DevcontainerInitCommandTests.cs` - 13 tests (ALL FAILING - console output capture)
- `DevcontainerWizardCommandTests.cs` - 19 tests (1 skipped)
- `InitCommandTests.cs` - 1 test
- `PrdCommandTests.cs` - 10 tests
- `HooksCommandTests.cs` - 10 tests

#### Common Issues:
- Console output mocking not working
- Command execution infrastructure problems
- Need better test harness for CLI commands

### Group 3: Service Layer Tests (60-70 tests)
**Complexity**: Medium | **Fix Time**: 4-5 hours | **Value**: High

#### Files:
- `PrdServiceTests.cs` - 12 tests (business logic)
- `HooksServiceTests.cs` - 14 tests (9 skipped, 13/14 passing when run)
- `DevcontainerServiceTests.cs` - 8 tests (3 skipped)
- `DevcontainerFeatureRegistryTests.cs` - 7 tests
- `DevcontainerFileGeneratorTests.cs` - 10 tests
- `DevcontainerTemplateServiceTests.cs` - 7 tests

#### Common Issues:
- Service layer mock improvements
- File system operation testing
- Async operation handling

### Group 4: Integration Tests (80-90 tests)
**Complexity**: High | **Fix Time**: 6-8 hours | **Value**: Critical

#### Files:
- `DevcontainerEndToEndTests.cs` - 11 tests
- `DevcontainerIntegrationTests.cs` - 9 tests
- `DevcontainerNuGetTemplateTests.cs` - 14 tests
- `TemplatePackagingTests.cs` - 6 tests
- `TemplateStructureTests.cs` - 10 tests
- `ClaudeCodeIntegrationTests.cs` - 5 tests

#### Common Issues:
- File system operations
- Template extraction and processing
- End-to-end workflow validation
- External dependency management

### Group 5: MCP (Model Context Protocol) Tests (100+ tests)
**Complexity**: High | **Fix Time**: 8-10 hours | **Value**: Medium

#### Files:
- `McpServerCommandTests.cs` - 11 skipped
- `StdioMcpServerTests.cs` - 13 skipped
- `McpIntegrationTests.cs` - 9 skipped
- `McpSdkIntegrationTests.cs` - 10 skipped
- `McpSdkComplianceTests.cs` - 8 skipped
- `McpSdkErrorHandlingTests.cs` - 10 skipped
- `McpSdkToolValidationTests.cs` - 7 skipped

#### Common Issues:
- Entire MCP feature set appears disabled
- Complex protocol implementation tests
- May need complete reimplementation

### Group 6: PRD (Product Requirements Document) Tests (60-70 tests)
**Complexity**: Medium-High | **Fix Time**: 5-6 hours | **Value**: High

#### Files:
- `PrdBranchCommandSimpleTests.cs` - 4 tests
- `PrdCommandRegistrationTests.cs` - 4 skipped (low value)
- `PrdErrorHandlingTests.cs` - 19 tests
- `PrdHelpSystemTests.cs` - 14 tests
- `PrdIntegrationTests.cs` - 14 tests

#### Common Issues:
- Command registration and DI setup
- Error handling scenarios
- Help system functionality

## Root Cause Analysis

### 1. Build Errors (Critical)
- **NuGetTemplateDiscoveryService.cs** - Property mismatches on TemplateInfo class
- Blocks ~70% of tests from running

### 2. Console Output Capture (High Impact)
- All DevcontainerInitCommandTests failing due to empty console output
- Test infrastructure issue affecting all command tests

### 3. Disabled Feature Sets (Medium Impact)
- MCP tests: 68 tests skipped (entire feature disabled)
- Agent tests: 10 tests skipped

### 4. Mock-Heavy Tests (Low Impact)
- Many tests only verify mock interactions
- Already disabled 8 low-value tests

## Fix Priority Roadmap

### Phase 1: Unblock Build (1 hour)
**Agent 1**: Fix NuGetTemplateDiscoveryService.cs
- Remove Description and Identity properties usage
- Fix Tags array type mismatch
- Ensure build passes

### Phase 2: Quick Wins (2-3 hours parallel)
**Agent 2**: Fix validation tests (Group 1)
- Focus on simple validation logic
- Clear pass/fail criteria
- ~20 tests fixed quickly

**Agent 3**: Fix console output infrastructure
- Resolve DevcontainerInitCommandTests console capture
- Will unblock many command tests

### Phase 3: Core Functionality (4-6 hours parallel)
**Agent 4**: Service layer tests (Group 3)
- Business logic tests
- File operations
- ~70 tests

**Agent 5**: Command execution tests (Group 2)
- After console fix from Agent 3
- ~60 tests

**Agent 6**: PRD tests (Group 6)
- Error handling and help system
- ~70 tests

### Phase 4: Integration Tests (6-8 hours)
**All Agents**: Tackle integration tests together
- Complex file operations
- End-to-end workflows
- ~90 tests

### Phase 5: Feature Completion (Optional)
**Specialized Team**: MCP tests
- Entire feature implementation
- ~100 tests
- May be deferred if not critical

## Expected Outcomes

### After Phase 1-3 (8-10 hours total):
- **~220 tests passing** (from current ~13)
- Core functionality validated
- Major features tested

### After Phase 4 (14-16 hours total):
- **~310 tests passing**
- Full integration coverage
- Production-ready test suite

### With Phase 5 (22-26 hours total):
- **~410 tests passing**
- Complete feature coverage
- All capabilities tested

## Recommendations

1. **Immediate Action**: Fix build error in NuGetTemplateDiscoveryService.cs
2. **Parallel Execution**: Deploy 5-6 agents on different test groups
3. **Focus Areas**: Prioritize Groups 1-4 (core functionality)
4. **Defer if Needed**: MCP tests can be deferred if feature not critical
5. **Test Infrastructure**: Invest in better console output capture for command tests

## Success Metrics

- **Phase 1**: Build passes, can run tests
- **Phase 2**: 40+ tests passing (validation + console fix)
- **Phase 3**: 200+ tests passing (core services + commands)
- **Phase 4**: 300+ tests passing (integration complete)
- **Phase 5**: 400+ tests passing (full coverage)