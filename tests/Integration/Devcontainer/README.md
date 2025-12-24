# Devcontainer Integration Test Suite

This directory contains comprehensive end-to-end tests for the PKS CLI devcontainer initialization system. The test suite covers the complete workflow from empty project to fully initialized devcontainer configuration.

## Test Structure

### Core Test Classes

1. **`DevcontainerEndToEndTests.cs`** - Complete workflow testing
   - Full project initialization from empty directory to working devcontainer
   - Template mapping and feature resolution
   - Auto-enablement for appropriate project templates
   - Integration with MCP and GitHub features
   - Environment variable configuration

2. **`DevcontainerTemplateExtractionTests.cs`** - Template and file generation testing
   - Template extraction from various sources (built-in, NuGet packages)
   - Placeholder replacement in templates
   - File generation (devcontainer.json, Dockerfile, docker-compose.yml)
   - Custom settings and feature configuration
   - Concurrent generation handling

3. **`DevcontainerUniversalTemplateTests.cs`** - Universal template verification
   - Verifies pks-universal-devcontainer template creates identical .devcontainer to root
   - Structural comparison between root and generated devcontainers
   - Configuration section matching
   - Placeholder replacement validation
   - Multiple extraction consistency

4. **`DevcontainerErrorScenariosTests.cs`** - Error handling and edge cases
   - Non-existent template handling
   - Read-only path scenarios
   - Invalid feature configurations
   - Conflicting feature detection
   - Special character handling in project names
   - Concurrent access race conditions

5. **`DevcontainerNuGetTemplateTests.cs`** - NuGet template integration
   - Template discovery from NuGet packages
   - Template installation and usage
   - Version management and updates
   - Cache handling and performance
   - Error scenarios (network issues, invalid packages)

### Support Infrastructure

6. **`DevcontainerTestArtifactManager.cs`** - Test artifact management
   - Centralized test directory creation and cleanup
   - Test project generation with proper structure
   - Devcontainer validation utilities
   - Configuration comparison tools
   - Artifact preservation for debugging

7. **`DevcontainerTestRunner.cs`** - Comprehensive test orchestration
   - Executes all test categories in sequence
   - Performance and stress testing
   - Detailed reporting and artifact collection
   - Test result summarization

## Test Categories

### 1. End-to-End Workflow Tests
Tests the complete initialization process:
- ✅ Empty project → initialized devcontainer
- ✅ Template mapping (api → dotnet-web, console → dotnet-basic, etc.)
- ✅ Feature resolution and dependency handling
- ✅ Docker Compose integration
- ✅ Port forwarding configuration
- ✅ Post-create command setup
- ✅ Auto-enablement for appropriate templates
- ✅ MCP and GitHub integration
- ✅ Environment variable configuration

### 2. Template Extraction and File Generation
Tests template processing and file creation:
- ✅ Built-in template extraction (dotnet-basic, dotnet-web, pks-universal-devcontainer)
- ✅ Placeholder replacement (${projectName}, {{ProjectName}})
- ✅ devcontainer.json generation with proper JSON structure
- ✅ Dockerfile generation with appropriate base images
- ✅ docker-compose.yml generation for multi-container setups
- ✅ Custom settings application
- ✅ Feature configuration merging
- ✅ Concurrent generation handling

### 3. Universal Template Verification
Tests that pks-universal-devcontainer template creates identical structure to root:
- ✅ Structural comparison (file names, directory structure)
- ✅ Configuration section matching (build, runArgs, customizations, etc.)
- ✅ Placeholder replacement without affecting other content
- ✅ File permission preservation
- ✅ Multiple extraction consistency

### 4. Error Scenarios and Edge Cases
Tests proper error handling:
- ✅ Non-existent template graceful failure
- ✅ Read-only path error handling
- ✅ Invalid feature specification
- ✅ Conflicting feature detection
- ✅ Invalid port number handling
- ✅ Existing devcontainer without force flag
- ✅ Corrupted template handling
- ✅ Network unavailability scenarios
- ✅ Very long project names
- ✅ Special characters in project names
- ✅ Concurrent access race conditions

### 5. NuGet Template Integration
Tests NuGet package template system:
- ✅ PKS.Templates.DevContainer package discovery
- ✅ Template installation and extraction
- ✅ Custom feature application
- ✅ Version management and updates
- ✅ Template validation
- ✅ Cache performance
- ✅ Configuration management
- ✅ Error handling (invalid packages, network issues)
- ✅ Concurrent operations

### 6. Performance and Stress Testing
Tests system performance under load:
- ✅ Single initialization performance (< 30 seconds)
- ✅ Template extraction performance (< 10 seconds)
- ✅ Concurrent initialization handling
- ✅ High-volume operation stress testing
- ✅ Resource usage monitoring

## Test Artifacts Management

All tests use the `DevcontainerTestArtifactManager` for proper artifact handling:

- **Test Directories**: Created in `%LOCALAPPDATA%/test-artifacts/pks-cli/devcontainer/`
- **Automatic Cleanup**: Old artifacts (>7 days) automatically cleaned
- **Artifact Preservation**: Test results and generated files preserved for debugging
- **Structured Organization**: Tests organized by category and timestamp

### Artifact Structure
```
test-artifacts/
├── pks-cli/
│   └── devcontainer/
│       ├── comprehensive-devcontainer-suite/
│       ├── template-extraction/
│       ├── universal-template/
│       ├── error-scenarios/
│       ├── nuget-templates/
│       ├── performance/
│       └── stress/
```

## Running the Tests

### Run All Tests
```bash
dotnet test --filter "DevcontainerEndToEndTests|DevcontainerTemplateExtractionTests|DevcontainerUniversalTemplateTests|DevcontainerErrorScenariosTests|DevcontainerNuGetTemplateTests"
```

### Run Comprehensive Test Suite
```bash
dotnet test --filter "DevcontainerTestRunner.RunAllDevcontainerTests"
```

### Run Performance Tests
```bash
dotnet test --filter "DevcontainerTestRunner.PerformanceTest"
```

### Run Stress Tests
```bash
dotnet test --filter "DevcontainerTestRunner.StressTest"
```

### Run Individual Categories
```bash
# End-to-end tests
dotnet test --filter "DevcontainerEndToEndTests"

# Template extraction
dotnet test --filter "DevcontainerTemplateExtractionTests"

# Universal template verification
dotnet test --filter "DevcontainerUniversalTemplateTests"

# Error scenarios
dotnet test --filter "DevcontainerErrorScenariosTests"

# NuGet integration
dotnet test --filter "DevcontainerNuGetTemplateTests"
```

## Test Results and Reporting

The `DevcontainerTestRunner` provides comprehensive reporting:

1. **Real-time Progress**: Console output with emoji indicators
2. **Performance Metrics**: Timing for all operations
3. **Artifact Summaries**: File counts, sizes, and locations
4. **Success Rates**: Pass/fail ratios by category
5. **JSON Results**: Detailed test results saved as JSON for analysis

### Sample Output
```
🔄 Running End-to-End Workflow Tests...
✅ End-to-End Tests: 4/4 passed
📦 Running Template Extraction Tests...
✅ Template Extraction Tests: 3/3 passed
🔍 Running Universal Template Comparison Tests...
✅ Universal Template Tests: 1/1 passed
⚠️ Running Error Scenario Tests...
✅ Error Scenario Tests: 4/4 passed
📋 Running NuGet Integration Tests...
✅ NuGet Integration Tests: 4/4 passed
⚡ Running Performance Tests...
✅ Performance Tests: 2/2 passed

📊 Test Suite Summary:
   Run ID: a1b2c3d4
   Duration: 45.2 seconds
   Overall Success: ✅ PASS
   Total Tests: 18/18 passed
```

## Integration with CI/CD

The test suite is designed for CI/CD integration:

- **Deterministic Results**: Tests produce consistent results across environments
- **Artifact Collection**: Test artifacts can be collected for build analysis
- **Performance Baselines**: Performance tests can detect regressions
- **Parallel Execution**: Tests support parallel execution for faster CI builds

## Dependencies

The test suite requires:

- ✅ .NET 8.0 SDK
- ✅ xUnit test framework
- ✅ FluentAssertions for readable assertions
- ✅ Microsoft.Extensions.DependencyInjection for service resolution
- ✅ Access to file system for artifact creation
- ✅ Network access for NuGet template tests (optional)

## Contributing

When adding new devcontainer tests:

1. Use `DevcontainerTestArtifactManager` for all file operations
2. Follow the established naming patterns for test methods
3. Include both positive and negative test cases
4. Add performance considerations for long-running operations
5. Document any special setup requirements
6. Update this README with new test categories

## Troubleshooting

### Common Issues

1. **Test Artifacts Not Cleaned Up**
   - Run `DevcontainerTestArtifactManager.CleanupOldArtifacts()` manually
   - Check disk space in `%LOCALAPPDATA%/test-artifacts/`

2. **Performance Tests Failing**
   - Check system load during test execution
   - Verify test timeouts are appropriate for the environment
   - Review performance baselines in `PerformanceTargets`

3. **NuGet Tests Failing**
   - Verify network connectivity
   - Check NuGet package availability
   - Validate NuGet source configuration

4. **Template Comparison Failures**
   - Ensure root `.devcontainer` exists and is valid
   - Check for recent changes to root devcontainer configuration
   - Verify template extraction is working correctly

For additional debugging, check the test artifacts directory for detailed logs and generated files.