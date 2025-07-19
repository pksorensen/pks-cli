# PKS CLI Tests

This directory contains comprehensive test suites for the PKS CLI application following Test-Driven Development (TDD) principles.

## Test Structure

The test project is organized to mirror the source code structure:

```
PKS.CLI.Tests/
├── Commands/                     # Command-specific tests
│   ├── Hooks/                   # Hooks command tests
│   ├── Mcp/                     # MCP server command tests
│   └── Agent/                   # Agent framework command tests
├── Infrastructure/               # Infrastructure and service tests
│   ├── Initializers/            # Initializer system tests
│   ├── Mocks/                   # Mock factories and utilities
│   ├── Fixtures/                # Test data generators
│   └── Utilities/               # Test utilities
└── Services/                    # Service layer tests
```

## Test Categories

### 1. Command Tests
- **HooksCommandTests**: Tests for hooks management commands
- **McpServerTests**: Tests for MCP server lifecycle management
- **AgentFrameworkTests**: Tests for AI agent management

### 2. Infrastructure Tests
- **HooksInitializerTests**: Tests for hooks system initialization
- **InitializationServiceTests**: Tests for project initialization orchestration
- **InitializerRegistryTests**: Tests for initializer discovery and management

### 3. Service Tests
- Tests for core services (KubernetesService, ConfigurationService, etc.)
- Integration tests for service interactions

## Test Infrastructure

### TestBase Class
All test classes inherit from `TestBase` which provides:
- Dependency injection setup
- Mock service factories
- Console output testing utilities
- Temporary file/directory management
- Logging capture and assertions

### Mock Factories
The `ServiceMockFactory` provides pre-configured mocks for all services:
- `CreateHooksService()` - Mock hooks management service
- `CreateMcpServerService()` - Mock MCP server service  
- `CreateAgentFrameworkService()` - Mock agent framework service
- `CreateInitializationService()` - Mock initialization orchestration service

### Test Data Generators
The `TestDataGenerator` creates realistic test data:
- Project configurations
- Hook definitions and contexts
- MCP server configurations
- Agent configurations
- File content for various types

## Running Tests

### Command Line
```bash
# Run all tests
dotnet test

# Run tests with detailed output
dotnet test --verbosity detailed

# Run specific test class
dotnet test --filter "ClassName=HooksCommandTests"

# Run tests with code coverage
dotnet test --collect:"XPlat Code Coverage"
```

### IDE Integration
Tests are compatible with:
- Visual Studio Test Explorer
- VS Code Test Explorer
- JetBrains Rider
- Any IDE with xUnit support

## Test Conventions

### Naming
- Test methods: `MethodName_ShouldExpectedBehavior_WhenCondition`
- Test classes: `{ClassUnderTest}Tests`
- Mock variables: `_mock{ServiceName}`

### Structure (AAA Pattern)
```csharp
[Fact]
public async Task Method_ShouldBehavior_WhenCondition()
{
    // Arrange - Set up test data and mocks
    var input = TestDataGenerator.GenerateInput();
    _mockService.Setup(x => x.Method()).ReturnsAsync(expectedResult);

    // Act - Execute the method under test
    var result = await _systemUnderTest.Method(input);

    // Assert - Verify the results
    result.Should().Be(expectedValue);
    _mockService.Verify(x => x.Method(), Times.Once);
}
```

### Assertions
We use FluentAssertions for readable test assertions:
```csharp
// Simple assertions
result.Should().BeTrue();
result.Should().Be(expectedValue);

// Collection assertions
items.Should().HaveCount(3);
items.Should().Contain(x => x.Name == "test");

// Exception assertions
await Assert.ThrowsAsync<ArgumentException>(() => method());

// Console output assertions (from TestBase)
AssertConsoleOutput("Expected output text");
AssertLogMessage(LogLevel.Information, "Expected log message");
```

## TDD Workflow

### Red-Green-Refactor Cycle

1. **Red Phase**: Write failing tests that define expected behavior
   - All current tests are in this phase (failing by design)
   - Tests define the interface and behavior contracts

2. **Green Phase**: Implement minimal code to make tests pass
   - Implement the actual command classes
   - Implement the service interfaces
   - Make tests pass with simplest possible implementation

3. **Refactor Phase**: Improve code quality while keeping tests green
   - Optimize performance
   - Improve code structure
   - Add additional features

### Test-First Development

1. **Write the test** defining expected behavior
2. **Run the test** and verify it fails for the right reason
3. **Write minimal code** to make the test pass
4. **Run tests** to verify they pass
5. **Refactor** both test and implementation code
6. **Repeat** for next piece of functionality

## Current Test Status

### Failing Tests (Red Phase)
All tests are currently failing by design as they define the expected behavior for components that need to be implemented:

- ✅ **Test Infrastructure**: Complete and working
- ❌ **HooksCommandTests**: 8 failing tests defining hooks command behavior
- ❌ **HooksInitializerTests**: 10 failing tests defining hooks initialization
- ❌ **McpServerTests**: 9 failing tests defining MCP server management
- ❌ **AgentFrameworkTests**: 10 failing tests defining agent framework

### Implementation Status
- ✅ Test project setup
- ✅ Mock factories and test utilities
- ✅ Test data generators
- ❌ HooksCommand (to be implemented)
- ❌ HooksInitializer (to be implemented)
- ❌ McpCommand (to be implemented)
- ❌ AgentCommand (to be implemented)
- ❌ Service implementations (to be implemented)

## Contributing

### Adding New Tests
1. Follow the established naming conventions
2. Use the TestBase class for common functionality
3. Leverage existing mock factories and test data generators
4. Write tests that define clear behavior expectations
5. Use FluentAssertions for readable assertions

### Test Categories
Use Xunit categories to organize tests:
```csharp
[Fact]
[Trait("Category", "Unit")]
public void UnitTest() { }

[Fact]
[Trait("Category", "Integration")]
public void IntegrationTest() { }
```

### Test Data
- Use TestDataGenerator for consistent test data
- Create realistic but deterministic test scenarios
- Avoid hardcoded values in test assertions

## Coverage Goals

We aim for:
- **90%+ Code Coverage** on core functionality
- **100% Coverage** on critical paths (commands, initialization)
- **Comprehensive Edge Cases** for error conditions
- **Integration Tests** for cross-service interactions

## Dependencies

The test project uses:
- **xUnit** (v2.6.2) - Test framework
- **FluentAssertions** (v6.12.0) - Assertion library
- **Moq** (v4.20.69) - Mocking framework
- **Spectre.Console.Testing** (v0.47.0) - Console testing utilities
- **Microsoft.NET.Test.Sdk** (v17.8.0) - Test SDK
- **coverlet.collector** (v6.0.0) - Code coverage