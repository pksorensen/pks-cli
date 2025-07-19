# Test-Driven Development Guidelines for PKS CLI

## Overview

PKS CLI follows strict Test-Driven Development (TDD) methodology to ensure high code quality, comprehensive test coverage, and reliable functionality. This document outlines the TDD process and guidelines for all contributors.

## TDD Workflow

### Red-Green-Refactor Cycle

#### 1. RED Phase - Write Failing Tests
- **Write the test first** before any implementation
- **Define expected behavior** through test assertions
- **Ensure the test fails** for the right reason (not due to compilation errors)
- **Write minimal test code** that clearly expresses the requirement

#### 2. GREEN Phase - Make Tests Pass
- **Write minimal implementation** to make the test pass
- **Don't optimize yet** - focus on making it work
- **Avoid over-engineering** - implement only what the test requires
- **Ensure all tests pass** before moving to refactor

#### 3. REFACTOR Phase - Improve Code Quality
- **Improve code structure** while keeping tests green
- **Optimize performance** if needed
- **Remove duplication** and improve readability
- **Run tests frequently** to ensure no regressions

## Test Structure Standards

### Test Class Organization
```csharp
public class ComponentNameTests : TestBase
{
    private readonly Mock<IDependency> _mockDependency;
    private readonly ComponentName _systemUnderTest;

    public ComponentNameTests()
    {
        _mockDependency = ServiceMockFactory.CreateDependency();
        _systemUnderTest = new ComponentName(_mockDependency.Object);
    }

    // Test methods follow AAA pattern
}
```

### Test Method Naming
Follow the pattern: `MethodName_ShouldExpectedBehavior_WhenCondition`

Examples:
- `Execute_ShouldReturnSuccess_WhenValidInputProvided`
- `Validate_ShouldThrowException_WhenInputIsNull`
- `Process_ShouldReturnEmptyList_WhenNoDataExists`

### Test Method Structure (AAA Pattern)
```csharp
[Fact]
public async Task Method_ShouldBehavior_WhenCondition()
{
    // Arrange - Set up test data, mocks, and expected results
    var input = TestDataGenerator.GenerateValidInput();
    var expectedResult = new ExpectedResult();
    _mockDependency.Setup(x => x.Method(input)).ReturnsAsync(expectedResult);

    // Act - Execute the method under test
    var result = await _systemUnderTest.Method(input);

    // Assert - Verify the results and interactions
    result.Should().Be(expectedResult);
    _mockDependency.Verify(x => x.Method(input), Times.Once);
}
```

## Test Categories and Scope

### 1. Unit Tests
- **Scope**: Single class or method
- **Dependencies**: All external dependencies mocked
- **Speed**: Fast execution (< 100ms per test)
- **Purpose**: Verify isolated functionality

### 2. Integration Tests
- **Scope**: Multiple components working together
- **Dependencies**: Real implementations where appropriate
- **Speed**: Moderate execution (< 1s per test)
- **Purpose**: Verify component interactions

### 3. Command Tests
- **Scope**: Complete command execution
- **Dependencies**: Mock external services, real command infrastructure
- **Speed**: Moderate execution
- **Purpose**: Verify command behavior and user experience

## Mock Strategy

### Use ServiceMockFactory
Always use the centralized mock factory for consistent mock setup:
```csharp
var mockService = ServiceMockFactory.CreateServiceName();
```

### Mock Behavior Guidelines
- **Setup only what's needed** for the specific test
- **Verify interactions** that are important to the test
- **Use realistic return values** from TestDataGenerator
- **Reset mocks** between tests (handled by TestBase)

### When to Mock vs Real Objects
- **Mock external dependencies**: File system, network, databases
- **Mock complex services**: Heavy computation, slow operations
- **Use real objects for**: Simple DTOs, value objects, pure functions

## Test Data Management

### Use TestDataGenerator
Generate consistent, realistic test data:
```csharp
var options = TestDataGenerator.GenerateInitializationOptions();
var config = TestDataGenerator.GenerateMcpServerConfig();
var agent = TestDataGenerator.GenerateAgentConfiguration();
```

### Test Data Principles
- **Deterministic**: Same input should always produce same output
- **Realistic**: Data should represent real-world scenarios
- **Minimal**: Include only necessary data for the test
- **Isolated**: Each test should use independent data

## Assertion Standards

### Use FluentAssertions
Prefer FluentAssertions for readable test assertions:
```csharp
// Preferred
result.Should().BeTrue();
result.Should().Be(expectedValue);
items.Should().HaveCount(3);
items.Should().Contain(x => x.Name == "test");

// Avoid
Assert.True(result);
Assert.Equal(expectedValue, result);
```

### Exception Testing
```csharp
// Async methods
await Assert.ThrowsAsync<ArgumentException>(() => 
    _systemUnderTest.MethodAsync(invalidInput));

// Sync methods
Action action = () => _systemUnderTest.Method(invalidInput);
action.Should().Throw<ArgumentException>()
    .WithMessage("Expected error message");
```

## Coverage Requirements

### Minimum Coverage Targets
- **Unit Tests**: 90% code coverage
- **Critical Paths**: 100% coverage (commands, initialization)
- **Edge Cases**: All error conditions tested
- **Public APIs**: All public methods tested

### Coverage Exclusions
Exclude from coverage requirements:
- Generated code
- Simple property getters/setters
- Constructor parameter validation
- Logging-only methods

## Test Organization

### Directory Structure
```
tests/PKS.CLI.Tests/
├── Commands/           # Command-specific tests
├── Infrastructure/     # Infrastructure component tests
├── Services/          # Service layer tests
├── Integration/       # Integration tests
└── TestHelpers/       # Shared test utilities
```

### Test File Naming
- Test files should end with `Tests.cs`
- Match the namespace structure of source code
- One test class per source class

## Continuous Integration

### Pre-commit Requirements
- All tests must pass
- Code coverage must meet minimum thresholds
- No test should be marked as `[Skip]` without justification

### Pull Request Requirements
- New features must include comprehensive tests
- Bug fixes must include regression tests
- Test coverage should not decrease

## Common Anti-Patterns to Avoid

### ❌ Testing Implementation Details
```csharp
// Don't test private methods directly
// Don't assert on internal state that users can't observe
```

### ❌ Fragile Tests
```csharp
// Don't rely on specific timing
// Don't hardcode file paths or external dependencies
// Don't use Thread.Sleep in tests
```

### ❌ Test Interdependence
```csharp
// Each test should be independent
// Tests should not rely on execution order
// Shared state between tests should be avoided
```

### ❌ Magic Numbers and Strings
```csharp
// Use TestDataGenerator or constants
// Make test data meaningful and clear
```

## Best Practices

### ✅ Test Behavior, Not Implementation
Focus on what the code should do, not how it does it.

### ✅ One Assertion Per Test
Each test should verify one specific behavior or outcome.

### ✅ Descriptive Test Names
Test names should clearly describe the scenario and expected outcome.

### ✅ Fast Test Execution
Keep tests fast to encourage frequent execution.

### ✅ Isolated Tests
Each test should set up its own data and clean up after itself.

## Testing Complex Scenarios

### Testing Async Code
```csharp
[Fact]
public async Task ProcessAsync_ShouldHandleConcurrency_WhenMultipleRequestsReceived()
{
    // Arrange
    var tasks = Enumerable.Range(0, 10)
        .Select(_ => _systemUnderTest.ProcessAsync())
        .ToArray();

    // Act
    var results = await Task.WhenAll(tasks);

    // Assert
    results.Should().AllSatisfy(r => r.Should().NotBeNull());
}
```

### Testing Error Conditions
```csharp
[Theory]
[InlineData(null)]
[InlineData("")]
[InlineData("   ")]
public async Task Method_ShouldThrowArgumentException_WhenInputIsInvalid(string input)
{
    // Act & Assert
    await Assert.ThrowsAsync<ArgumentException>(() => 
        _systemUnderTest.Method(input));
}
```

## Test Debugging

### Debugging Failed Tests
1. **Read the error message carefully**
2. **Check test setup and arrange phase**
3. **Verify mock configurations**
4. **Use debugger to step through test execution**
5. **Check for test data issues**

### Common Test Failures
- **NullReferenceException**: Check mock setups and test data
- **MockException**: Verify mock expectations and setups
- **TimeoutException**: Check for infinite loops or deadlocks
- **FileNotFoundException**: Ensure test files are properly created

## Performance Testing

### Load Testing Guidelines
```csharp
[Fact]
public async Task Process_ShouldCompleteWithinTimeout_WhenProcessingLargeDataset()
{
    // Arrange
    var largeDataset = TestDataGenerator.GenerateLargeDataset(10000);
    var timeout = TimeSpan.FromSeconds(5);

    // Act
    var stopwatch = Stopwatch.StartNew();
    await _systemUnderTest.ProcessAsync(largeDataset);
    stopwatch.Stop();

    // Assert
    stopwatch.Elapsed.Should().BeLessThan(timeout);
}
```

## Conclusion

Following these TDD guidelines ensures:
- **High code quality** through comprehensive testing
- **Reliable functionality** with confidence in changes
- **Clear requirements** expressed through tests
- **Maintainable codebase** with good test coverage
- **Fast feedback** during development

Remember: **Tests are not just verification tools - they are design tools that help you write better code.**