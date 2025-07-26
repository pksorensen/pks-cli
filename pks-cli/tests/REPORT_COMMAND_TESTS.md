# Report Command Test Suite

This document describes the comprehensive test suite for the PKS CLI `report` command, which enables users to create GitHub issues directly from the command line.

## Test Structure

### Unit Tests
Located in `/tests/Commands/`, these tests use extensive mocking to verify command behavior in isolation.

#### ReportCommandTests.cs
- **Purpose**: Core functionality testing with mocked GitHub API
- **Coverage**: 
  - Happy path scenarios
  - Authentication validation
  - Repository access checks
  - Issue creation workflows
  - Interactive prompts
  - System information inclusion
  - Label and priority handling

#### AuthCommandTests.cs
- **Purpose**: GitHub authentication flow testing
- **Coverage**:
  - Token-based authentication
  - Device code flow (OAuth)
  - Token validation and storage
  - Configuration management
  - Status checking
  - Multiple authentication providers

#### ReportCommandErrorTests.cs
- **Purpose**: Comprehensive error scenario testing
- **Coverage**:
  - Network failures and timeouts
  - Authentication failures
  - Permission and authorization issues
  - Rate limiting
  - Repository state issues (archived, private)
  - Configuration errors
  - System resource exhaustion
  - Malformed API responses

#### ReportCommandExecutionTests.cs
- **Purpose**: Command execution and CLI integration
- **Coverage**:
  - Parameter parsing and validation
  - Interactive mode operation
  - Progress indication
  - Output formatting
  - Help system integration
  - Tag handling
  - Dry run functionality

### Integration Tests
Located in `/tests/Integration/GitHub/`, these tests interact with actual GitHub APIs when properly configured.

#### GitHubReportIntegrationTests.cs
- **Purpose**: End-to-end testing with real GitHub API
- **Coverage**:
  - Authentication with real tokens
  - Repository access verification
  - Issue creation (when allowed)
  - Rate limit handling
  - Network resilience
  - Configuration management
  - System environment data collection

## Test Configuration

### Environment Variables
```bash
# Required for integration tests
GITHUB_TEST_TOKEN=your_github_token_here
GITHUB_TEST_REPOSITORY=https://github.com/owner/repo

# Optional - allows actual issue creation (use with caution)
GITHUB_ALLOW_ISSUE_CREATION=true
```

### Test Configuration File
`testconfig.json` contains test-specific settings:
- Valid command parameters
- Timeouts and retry settings
- Integration test behavior
- Validation rules

## Test Categories and Traits

### Categories
- **Unit**: Isolated unit tests with mocks
- **Integration**: Tests with external dependencies
- **Command**: Command-specific tests

### Test Types
- **Happy Path**: Normal operation scenarios
- **Authentication**: Auth-related scenarios
- **Authorization**: Permission-related scenarios
- **Validation**: Input validation scenarios
- **Error Handling**: Error and failure scenarios
- **Network**: Network-related scenarios
- **Configuration**: Configuration-related scenarios

### Priority Levels
- **Critical**: Core functionality that must work
- **High**: Important features
- **Medium**: Nice-to-have features
- **Low**: Edge cases and optimizations

## Mock Strategy

### GitHub Service Mocking
The test suite extensively mocks `IGitHubService` to simulate various GitHub API responses:

```csharp
// Successful authentication
_mockGitHubService.Setup(x => x.ValidateTokenAsync(It.IsAny<string>()))
    .ReturnsAsync(new GitHubTokenValidation
    {
        IsValid = true,
        Scopes = new[] { "repo", "issues" },
        ValidatedAt = DateTime.UtcNow
    });

// Repository access
_mockGitHubService.Setup(x => x.CheckRepositoryAccessAsync(It.IsAny<string>()))
    .ReturnsAsync(new GitHubAccessLevel
    {
        HasAccess = true,
        CanWrite = true,
        AccessLevel = "write"
    });

// Issue creation
_mockGitHubService.Setup(x => x.CreateIssueAsync(...))
    .ReturnsAsync(new GitHubIssue { ... });
```

### Configuration Service Mocking
Configuration storage and retrieval is mocked to test various configuration states:

```csharp
_mockConfigurationService.Setup(x => x.GetAsync("github.token"))
    .ReturnsAsync("test-token");
    
_mockConfigurationService.Setup(x => x.SetAsync(...))
    .Returns(Task.CompletedTask);
```

## Test Scenarios

### Authentication Scenarios
1. **Valid Token**: Token validation succeeds, all operations work
2. **Invalid Token**: Token validation fails, operations are blocked
3. **Expired Token**: Token was valid but has expired
4. **Missing Token**: No token configured
5. **Device Code Flow**: OAuth device code authentication
6. **Token Revocation**: Token was revoked after configuration

### Repository Access Scenarios
1. **Full Access**: User has admin/write access to repository
2. **Read Only**: User has read-only access
3. **No Access**: Repository is private or doesn't exist
4. **Archived Repository**: Repository exists but is archived

### Issue Creation Scenarios
1. **Standard Issue**: Create issue with title, description, and type
2. **With Priority**: Issue includes priority level
3. **With Tags**: Issue includes custom tags
4. **With System Info**: Issue includes system information
5. **Interactive Mode**: User is prompted for missing information

### Error Scenarios
1. **Network Timeout**: GitHub API doesn't respond in time
2. **Rate Limiting**: API rate limit exceeded
3. **Service Unavailable**: GitHub API returns 503
4. **Malformed Response**: API returns invalid JSON
5. **Configuration Corruption**: Local config file is corrupted

## Running Tests

### All Tests
```bash
dotnet test
```

### Unit Tests Only
```bash
dotnet test --filter "Category=Unit"
```

### Integration Tests Only
```bash
dotnet test --filter "Category=Integration"
```

### Specific Command Tests
```bash
dotnet test --filter "Command=Report"
dotnet test --filter "Command=Auth"
```

### By Test Type
```bash
dotnet test --filter "TestType=Authentication"
dotnet test --filter "TestType=ErrorScenarios"
```

### Critical Tests Only
```bash
dotnet test --filter "Priority=Critical"
```

## Test Data Management

### Test Isolation
- Each test creates its own temporary directories
- Mocks are reset between tests
- No shared state between tests

### Cleanup
- Temporary files and directories are cleaned up after each test
- Integration tests can optionally clean up created issues
- Background processes are terminated

### Test Artifacts
- Test results are stored in `/test-artifacts/results/`
- Code coverage reports in `/test-artifacts/coverage/`
- Test logs captured for debugging

## Continuous Integration

### GitHub Actions Integration
The test suite is designed to run in CI/CD environments:

```yaml
- name: Run Unit Tests
  run: dotnet test --filter "Category=Unit" --logger trx

- name: Run Integration Tests
  run: dotnet test --filter "Category=Integration" --logger trx
  env:
    GITHUB_TEST_TOKEN: ${{ secrets.GITHUB_TEST_TOKEN }}
    GITHUB_TEST_REPOSITORY: ${{ github.repository }}
```

### Test Parallelization
- Unit tests run in parallel for speed
- Integration tests may be serialized to avoid rate limiting
- Test collections are used to group related tests

## Coverage Goals

### Code Coverage Targets
- Unit Tests: >90% coverage of command logic
- Integration Tests: >80% coverage of GitHub service integration
- Overall: >85% total coverage

### Scenario Coverage
- All happy path scenarios must be tested
- All error conditions should have tests
- Edge cases and boundary conditions covered
- Interactive flows tested with simulated input

## Contributing to Tests

### Adding New Tests
1. Follow existing naming conventions
2. Use appropriate traits and categories
3. Include both positive and negative test cases
4. Mock external dependencies appropriately
5. Clean up resources in test disposal

### Test Naming Convention
```csharp
[Fact]
public async Task Execute_ShouldCreateIssue_WhenValidParametersProvided()
{
    // Method_ShouldExpectedBehavior_WhenCondition
}
```

### Best Practices
1. **Arrange-Act-Assert**: Clear test structure
2. **Single Responsibility**: One concept per test
3. **Descriptive Names**: Self-documenting test names
4. **Mock Verification**: Verify expected interactions
5. **Error Testing**: Test failure scenarios
6. **Resource Cleanup**: Always dispose properly

This comprehensive test suite ensures the report command is robust, reliable, and handles edge cases gracefully while providing excellent developer experience through clear error messages and helpful guidance.