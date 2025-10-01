# PKS CLI Test Fix - Agent Assignments

## Overview
This document assigns specific test groups to specialized agents for parallel execution. Each agent has clear objectives, files to fix, and success criteria.

## Agent 1: Build Fix Specialist
**Priority**: CRITICAL - Must complete first
**Timeline**: 1 hour
**Blocking**: All other agents

### Assignment
Fix the build error in `NuGetTemplateDiscoveryService.cs` that's preventing 70% of tests from running.

### Specific Issues
```csharp
// Line 679: Remove - Description property doesn't exist
Description = "A universal devcontainer template",

// Line 680: Remove - Identity property doesn't exist  
Identity = $"{packageId}.Universal",

// Line 681: Fix type mismatch - Tags should be List<string>
Tags = new[] { "devcontainer", "universal" }  // Change to List<string>
```

### Success Criteria
- Build passes without errors
- Can run `dotnet test --no-build` successfully

---

## Agent 2: Validation Test Specialist
**Priority**: HIGH - Quick wins
**Timeline**: 2-3 hours
**Dependencies**: Agent 1 completion

### Assignment
Fix all validation-related tests that have simple pass/fail criteria.

### Files to Fix
1. `tests/Commands/InitCommandValidationTests.cs` (6 skipped tests)
   - Project name validation rules
   - Path validation logic
   
2. `tests/Commands/ProjectNameValidationTests.cs` (1 test)
   - Business rule validation
   
3. `tests/Services/Devcontainer/DevcontainerValidationTests.cs` (11 tests)
   - Configuration validation
   - Feature validation
   
4. `tests/Commands/Prd/PrdSettingsParsingTests.cs` (1 test)
   - Settings parsing logic

### Common Fixes Needed
- Improve mock setup for validation services
- Ensure proper test data setup
- Fix assertion logic

### Success Criteria
- All 19 validation tests passing
- No skipped tests in these files

---

## Agent 3: Console Infrastructure Specialist
**Priority**: HIGH - Unblocks many tests
**Timeline**: 2-3 hours
**Dependencies**: Agent 1 completion

### Assignment
Fix the console output capture infrastructure that's causing all DevcontainerInitCommandTests to fail.

### Primary Issue
All tests show: `Expected string "" to contain "X" because Expected text 'X' not found in console output. Raw output: '', Cleaned output: ''.`

### Files to Fix
1. `tests/Infrastructure/TestBase.cs`
   - Fix `AssertConsoleOutput` method
   - Improve console capture mechanism
   
2. `tests/Commands/Devcontainer/DevcontainerInitCommandTests.cs` (13 tests)
   - All failing due to console capture issue

### Investigation Steps
1. Check how `IAnsiConsole` is mocked in TestBase
2. Verify console output is being captured correctly
3. Ensure Spectre.Console output is redirected properly

### Success Criteria
- Console output properly captured in tests
- All 13 DevcontainerInitCommandTests passing

---

## Agent 4: Service Layer Specialist
**Priority**: MEDIUM
**Timeline**: 4-5 hours
**Dependencies**: Agent 1 completion

### Assignment
Fix service layer tests focusing on business logic and file operations.

### Files to Fix
1. `tests/Services/HooksServiceTests.cs` (14 tests, 9 skipped)
   - Already 13/14 passing when run individually
   - Fix file permission issue on test #14
   
2. `tests/Services/Devcontainer/DevcontainerServiceTests.cs` (8 tests, 3 skipped)
   - Remove low-value mock verification tests
   - Focus on business logic tests
   
3. `tests/Services/Devcontainer/DevcontainerFeatureRegistryTests.cs` (7 tests)
   - Feature discovery and registration
   
4. `tests/Services/Devcontainer/DevcontainerFileGeneratorTests.cs` (10 tests)
   - File generation logic
   - Template processing
   
5. `tests/Commands/Prd/PrdServiceTests.cs` (12 tests)
   - PRD generation business logic

### Common Fixes Needed
- Improve file system mocking
- Fix async operation handling
- Better error scenario testing

### Success Criteria
- 50+ service tests passing
- Reduced reliance on mock verification

---

## Agent 5: Command Execution Specialist
**Priority**: MEDIUM
**Timeline**: 3-4 hours
**Dependencies**: Agent 1 & Agent 3 completion

### Assignment
Fix command execution tests after console infrastructure is repaired.

### Files to Fix
1. `tests/Commands/Devcontainer/DevcontainerWizardCommandTests.cs` (19 tests, 1 skipped)
   - Interactive wizard testing
   - User input simulation
   
2. `tests/Commands/Prd/PrdCommandTests.cs` (10 tests)
   - Command execution flow
   - Parameter handling
   
3. `tests/Commands/Hooks/HooksCommandTests.cs` (10 tests)
   - Hook command execution
   - Event handling
   
4. `tests/Commands/Hooks/HookEventCommandsTests.cs` (10 tests, 1 skipped)
   - Event-specific commands

### Common Fixes Needed
- Proper command context setup
- Input/output mocking
- Command pipeline testing

### Success Criteria
- 45+ command tests passing
- Interactive commands properly tested

---

## Agent 6: Integration Test Specialist
**Priority**: HIGH
**Timeline**: 6-8 hours
**Dependencies**: Agents 1-5 completion

### Assignment
Fix complex integration tests that validate end-to-end workflows.

### Files to Fix
1. `tests/Integration/Devcontainer/DevcontainerEndToEndTests.cs` (11 tests)
   - Full workflow validation
   - File system operations
   
2. `tests/Integration/Devcontainer/DevcontainerIntegrationTests.cs` (9 tests)
   - Cross-component integration
   
3. `tests/Integration/Templates/TemplatePackagingTests.cs` (6 tests)
   - NuGet packaging workflows
   
4. `tests/Integration/Templates/TemplateStructureTests.cs` (10 tests)
   - Template file validation
   
5. `tests/Integration/Hooks/ClaudeCodeIntegrationTests.cs` (5 tests)
   - Claude integration scenarios

### Common Fixes Needed
- Temporary directory management
- File system cleanup
- External dependency mocking
- Timeout handling

### Success Criteria
- 40+ integration tests passing
- Reliable file system operations
- Proper cleanup after tests

---

## Coordination Strategy

### Phase 1 (Hour 1)
- **Agent 1**: Fix build errors
- **Other Agents**: Review assigned files, prepare fix strategies

### Phase 2 (Hours 2-4)
- **Agent 2**: Validation tests (parallel)
- **Agent 3**: Console infrastructure (parallel)
- **Agent 4**: Begin service tests (parallel)

### Phase 3 (Hours 5-8)
- **Agent 5**: Command tests (depends on Agent 3)
- **Agent 4**: Continue service tests
- **Agent 6**: Begin integration tests

### Phase 4 (Hours 9-12)
- **All Agents**: Focus on remaining integration tests
- **Review**: Verify all fixes are working together

## Communication Protocol

1. **Hourly Check-ins**: Report progress and blockers
2. **Shared Issues**: Post in common channel if discovering infrastructure problems
3. **Dependencies**: Alert dependent agents when prerequisites complete
4. **Success Notifications**: Announce when test groups are fully passing

## Expected Outcomes

- **Hour 1**: Build passing
- **Hour 4**: 100+ tests passing
- **Hour 8**: 250+ tests passing
- **Hour 12**: 350+ tests passing

## Notes

- MCP tests (100+ tests) are deferred as entire feature appears disabled
- Focus on core functionality first
- Document any discovered infrastructure issues for future reference