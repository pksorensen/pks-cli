# PKS CLI Test Suite

This directory contains the comprehensive test suite for PKS CLI with performance optimizations and timeout handling to prevent hanging tests.

## Test Configuration

### Performance Optimizations

The test suite has been optimized to prevent the 2-minute timeout hangs and improve execution speed:

- **Sequential Execution**: Tests run sequentially (`maxParallelThreads: 1`) to prevent resource conflicts
- **Timeout Controls**: Multiple timeout layers prevent hanging tests
- **Process Management**: Automatic cleanup of background processes
- **Resource Cleanup**: Proper disposal patterns and cleanup mechanisms

### Configuration Files

| File | Purpose |
|------|---------|
| `.runsettings` | MSTest configuration with timeouts and execution settings |
| `xunit.runner.json` | xUnit configuration for parallel execution control |
| `run-tests.sh` | Unix test runner with category filtering |
| `run-tests.ps1` | PowerShell test runner with category filtering |

## Test Categories

Tests are organized using trait attributes for better filtering and execution control:

### By Category
- **Unit**: Fast, isolated unit tests
- **Integration**: Component integration tests
- **EndToEnd**: Full application workflow tests
- **Performance**: Performance and load tests
- **Smoke**: Quick validation tests

### By Speed
- **Fast**: < 1 second execution time
- **Medium**: 1-10 seconds execution time
- **Slow**: > 10 seconds execution time

### By Reliability
- **Stable**: Consistent, reliable tests
- **Unstable**: Known flaky tests (often skipped in CI)
- **Experimental**: New tests with unknown reliability

## Running Tests

### Basic Test Execution

```bash
# Run all tests
dotnet test

# Run with specific configuration
dotnet test --settings .runsettings

# Run specific category
dotnet test --filter "Category=Unit"

# Run fast tests only
dotnet test --filter "Speed=Fast"

# Exclude unstable tests
dotnet test --filter "Reliability!=Unstable"
```

### Using Test Runner Scripts

#### Unix/Linux/macOS
```bash
# Run all stable, fast tests
./run-tests.sh --exclude-unstable --only-fast

# Run unit tests with 10-minute timeout
./run-tests.sh --category Unit --timeout-minutes 10

# Run integration tests excluding slow ones
./run-tests.sh --category Integration --exclude-slow
```

#### Windows PowerShell
```powershell
# Run all stable, fast tests
.\run-tests.ps1 -ExcludeUnstable -OnlyFast

# Run unit tests with 10-minute timeout
.\run-tests.ps1 -Category Unit -TimeoutMinutes 10

# Run integration tests excluding slow ones
.\run-tests.ps1 -Category Integration -ExcludeSlow
```

## Test Infrastructure

### Base Classes

#### TestBase
- Standard base for unit tests
- Mock service registration
- Console and logging capture
- Proper disposal and cleanup

#### IntegrationTestBase
- Base for integration tests
- Real service implementations
- Test artifact management
- Enhanced cleanup mechanisms

### Helper Classes

#### TestTimeoutHelper
- Timeout management for async operations
- Configurable timeout values by test category
- Cancellation token support

#### TestProcessHelper
- Safe external process management
- Automatic process cleanup
- Timeout handling for process operations

#### TestTraits
- Attribute system for test categorization
- Standardized trait names and values

### Collections

Tests are organized into collections to control parallel execution:

- **Sequential**: Tests that must run one at a time
- **Parallel**: Tests that can run in parallel within their group
- **FileSystem**: Tests involving file operations
- **Process**: Tests that start external processes
- **Network**: Tests with network dependencies

## Timeout Configuration

### Timeout Layers

1. **Test Session Timeout**: 180 seconds (3 minutes) - prevents entire test run from hanging
2. **Individual Test Timeout**: 30 seconds - prevents single tests from hanging
3. **Operation Timeouts**: 5-30 seconds - prevents individual operations from hanging

### Timeout Values by Category

| Category | Default Timeout |
|----------|----------------|
| Unit | 5 seconds |
| Integration | 15 seconds |
| EndToEnd | 30 seconds |
| Performance | 30 seconds |

## CI/CD Integration

### GitHub Actions

The test workflow is configured to:
- Run only stable and fast tests in CI
- Use proper timeout settings
- Generate test reports and coverage
- Fail fast on timeout or error

```yaml
- name: Test - Stable and Fast Tests Only
  run: |
    cd tests
    dotnet test --settings .runsettings \
      --filter "Reliability!=Unstable&Speed!=Slow" \
      --timeout 180000
```

## Troubleshooting

### Common Issues

#### Tests Hang or Timeout
1. Check for infinite loops or blocking operations
2. Ensure proper use of cancellation tokens
3. Use TestTimeoutHelper for async operations
4. Verify process cleanup in test disposal

#### Resource Conflicts
1. Use appropriate test collections
2. Run tests sequentially if needed
3. Ensure proper cleanup in test base classes
4. Check for file locking issues

#### Flaky Tests
1. Mark unstable tests with `[UnstableTest]` attribute
2. Use proper wait conditions instead of fixed delays
3. Implement retry logic where appropriate
4. Consider test isolation improvements

### Debug Mode

Enable debug output to troubleshoot test execution:

```bash
# Unix
./run-tests.sh --debug --verbose

# PowerShell
.\run-tests.ps1 -Debug -Verbose
```

## Performance Monitoring

### Metrics to Watch

- **Test Execution Time**: Should complete within timeout limits
- **Memory Usage**: Monitor for memory leaks in long-running tests
- **Process Count**: Ensure no leaked processes after test completion
- **File Handles**: Check for proper file cleanup

### Performance Improvements

1. **Process Management**: Automatic cleanup prevents resource leaks
2. **Sequential Execution**: Eliminates race conditions and resource conflicts
3. **Timeout Controls**: Prevents hanging tests from blocking CI/CD
4. **Selective Test Running**: Filter tests by category, speed, and reliability

## Best Practices

### Writing Tests

1. **Use Appropriate Attributes**: Mark tests with category, speed, and reliability traits
2. **Implement Timeout Handling**: Use TestTimeoutHelper for async operations
3. **Proper Cleanup**: Ensure resources are disposed in test base classes
4. **Avoid External Dependencies**: Use mocks for external services in unit tests

### Test Organization

1. **Group Related Tests**: Use test collections for related functionality
2. **Separate Fast and Slow Tests**: Allow selective execution based on speed
3. **Mark Unstable Tests**: Prevent flaky tests from breaking CI/CD
4. **Use Descriptive Names**: Make test intent clear from method names

### Maintenance

1. **Monitor Test Performance**: Track execution times and success rates
2. **Update Timeout Values**: Adjust based on actual execution patterns
3. **Clean Up Skipped Tests**: Regularly review and fix skipped tests
4. **Review Unstable Tests**: Work to stabilize flaky tests over time