# Devcontainer Test Suite

This directory contains comprehensive tests for the PKS CLI devcontainer functionality. The tests are designed using Test-Driven Development (TDD) principles and provide complete coverage for all devcontainer-related components.

## Test Structure

### Service Layer Tests (`/Services/Devcontainer/`)

#### DevcontainerServiceTests.cs
- Tests the main `IDevcontainerService` interface
- Configuration creation and validation
- Feature dependency resolution
- Configuration merging
- Error handling scenarios

#### DevcontainerFeatureRegistryTests.cs
- Tests the `IDevcontainerFeatureRegistry` interface
- Feature discovery and search
- Feature validation
- Category-based filtering
- Option validation

#### DevcontainerTemplateServiceTests.cs
- Tests the `IDevcontainerTemplateService` interface
- Template discovery and application
- Template customization
- Configuration generation from templates

#### DevcontainerFileGeneratorTests.cs
- Tests the `IDevcontainerFileGenerator` interface
- devcontainer.json generation
- Dockerfile generation
- docker-compose.yml generation
- Path validation and file system operations

#### DevcontainerValidationTests.cs
- Comprehensive validation testing
- Project name validation
- Configuration validation
- Feature compatibility checking
- Error message generation

### Command Layer Tests (`/Commands/Devcontainer/`)

#### DevcontainerInitCommandTests.cs
- Tests the `DevcontainerInitCommand` 
- Command-line argument parsing
- Non-interactive mode execution
- Force overwrite scenarios
- Error handling and validation

#### DevcontainerWizardCommandTests.cs
- Tests the `DevcontainerWizardCommand`
- Interactive wizard workflow
- User input validation
- Step-by-step configuration building
- Summary and confirmation dialogs

#### DevcontainerTestRunner.cs
- Comprehensive test suite runner
- Demonstrates all test scenarios
- Integration test coordination
- Test reporting and validation

### Integration Tests (`/Integration/Devcontainer/`)

#### DevcontainerIntegrationTests.cs
- End-to-end workflow testing
- Service integration verification
- Complete devcontainer creation workflows
- Error propagation testing
- Multi-service coordination

### Test Infrastructure (`/Infrastructure/`)

#### Fixtures/Devcontainer/DevcontainerTestData.cs
- Test data generators
- Sample configurations (basic and complex)
- Available features and extensions
- File content templates
- Validation test cases

#### Mocks/DevcontainerServiceMocks.cs
- Mock service implementations
- Behavior simulation
- Error scenario setup
- Test result generation

## Test Coverage

### Functionality Covered

1. **Configuration Management**
   - Basic and complex devcontainer configurations
   - Template-based configuration generation
   - Custom configuration merging
   - Validation and error checking

2. **Feature System**
   - Feature discovery and registry
   - Dependency resolution
   - Conflict detection
   - Option validation

3. **Template System**
   - Template discovery and selection
   - Template application and customization
   - Base image and feature integration

4. **File Generation**
   - devcontainer.json generation with proper formatting
   - Dockerfile creation with custom instructions
   - docker-compose.yml for multi-container setups
   - Path validation and file system safety

5. **Command Interface**
   - CLI argument parsing and validation
   - Interactive wizard workflows
   - Error handling and user feedback
   - Force overwrite and safety checks

6. **Integration Scenarios**
   - Complete end-to-end workflows
   - Multi-service coordination
   - Error propagation and handling
   - Performance and reliability testing

### Test Types

- **Unit Tests**: Individual component testing with mocks
- **Integration Tests**: Multi-component workflow testing
- **Validation Tests**: Input validation and error handling
- **Edge Case Tests**: Boundary conditions and error scenarios
- **Mock Tests**: Service behavior simulation and verification

## Running the Tests

### Individual Test Classes
```bash
# Run specific test class
dotnet test --filter "ClassName=DevcontainerServiceTests"
dotnet test --filter "ClassName=DevcontainerIntegrationTests"
```

### Test Categories
```bash
# Run all devcontainer tests
dotnet test --filter "FullyQualifiedName~Devcontainer"

# Run only service layer tests
dotnet test --filter "FullyQualifiedName~Services.Devcontainer"

# Run only command tests
dotnet test --filter "FullyQualifiedName~Commands.Devcontainer"

# Run only integration tests
dotnet test --filter "FullyQualifiedName~Integration.Devcontainer"
```

### Complete Test Suite
```bash
# Run the comprehensive test runner
dotnet test --filter "ClassName=DevcontainerTestRunner"
```

## Test Data and Fixtures

### Sample Configurations

The test suite includes pre-built configurations for various scenarios:

- **Basic Configuration**: Simple .NET development environment
- **Complex Configuration**: Multi-feature setup with Docker, Azure CLI, Kubernetes tools
- **Invalid Configuration**: For error testing and validation
- **Edge Cases**: Boundary conditions and unusual inputs

### Available Features

Test data includes common devcontainer features:
- .NET SDK and runtime
- Docker in Docker
- Azure CLI
- Kubernetes tools (kubectl, Helm, Minikube)
- Node.js and npm

### VS Code Extensions

Sample extensions for testing:
- C# language support
- Docker integration
- Kubernetes tools
- Azure account management

### File Templates

Pre-built file content for testing:
- devcontainer.json with various configurations
- Dockerfile with custom instructions
- docker-compose.yml for multi-container setups

## Mock Services

The test suite uses comprehensive mocks that simulate real service behavior:

### DevcontainerService Mock
- Configuration creation simulation
- Validation logic implementation
- Feature dependency resolution
- Error scenario simulation

### FeatureRegistry Mock
- Feature discovery simulation
- Search functionality
- Validation implementation
- Category filtering

### TemplateService Mock
- Template discovery
- Application logic
- Customization handling

### FileGenerator Mock
- File generation simulation
- Path validation
- Content generation
- Error handling

## Error Scenarios Tested

1. **Invalid Inputs**
   - Empty or null project names
   - Invalid image names
   - Malformed port numbers
   - Invalid feature configurations

2. **File System Issues**
   - Read-only directories
   - Permission errors
   - Non-existent paths
   - Disk space issues

3. **Service Failures**
   - Network connectivity issues
   - Registry unavailability
   - Template service errors
   - Validation failures

4. **Dependency Conflicts**
   - Incompatible features
   - Circular dependencies
   - Version conflicts
   - Resource constraints

## Test Best Practices

### Isolation
- Each test is completely isolated
- No shared state between tests
- Temporary directories for file operations
- Mock services prevent external dependencies

### Deterministic
- Predictable test outcomes
- No random data or timing dependencies
- Consistent mock behavior
- Repeatable test execution

### Comprehensive
- All code paths covered
- Error scenarios included
- Edge cases tested
- Integration workflows verified

### Maintainable
- Clear test names and descriptions
- Well-organized test structure
- Reusable test utilities
- Documentation and comments

## Future Enhancements

### Planned Test Additions
- Performance benchmarking tests
- Stress testing with large configurations
- Concurrent operation testing
- Memory usage validation

### Test Infrastructure Improvements
- Automated test data generation
- Property-based testing
- Mutation testing
- Code coverage reporting

### Integration Enhancements
- Real Docker integration tests
- VS Code extension testing
- GitHub Codespaces compatibility
- Multi-platform testing

This test suite provides a solid foundation for implementing the devcontainer functionality with confidence that all components work correctly together.