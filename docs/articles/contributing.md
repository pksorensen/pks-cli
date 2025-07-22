# Contributing to PKS CLI

Thank you for your interest in contributing to PKS CLI! This guide will help you get started with contributing to the project.

## Getting Started

### Development Setup

1. **Fork the Repository**
   ```bash
   # Fork on GitHub: https://github.com/pksorensen/pks-cli/fork
   git clone https://github.com/YOUR_USERNAME/pks-cli.git
   cd pks-cli
   ```

2. **Install Prerequisites**
   - .NET 8.0 SDK
   - Git
   - Your favorite code editor

3. **Build the Project**
   ```bash
   cd pks-cli/src
   dotnet restore
   dotnet build
   ```

4. **Run Tests**
   ```bash
   dotnet test
   ```

### Development Workflow

1. **Create a Feature Branch**
   ```bash
   git checkout -b feature/your-feature-name
   ```

2. **Make Your Changes**
   - Follow the coding standards
   - Add tests for new functionality
   - Update documentation as needed

3. **Test Your Changes**
   ```bash
   # Build and test
   dotnet build
   dotnet test
   
   # Test the CLI locally
   dotnet run -- --help
   ```

4. **Submit a Pull Request**
   - Push your branch to your fork
   - Create a PR with a clear description
   - Link to any related issues

## Code Standards

### Coding Style
- Follow standard .NET conventions
- Use meaningful variable and method names
- Add XML documentation for public APIs
- Keep methods focused and concise

### Spectre.Console Usage
- Use Spectre.Console for all terminal output
- Maintain consistent color schemes (cyan for primary)
- Include progress indicators for long operations
- Use tables with rounded borders for data display

### Command Pattern
- Inherit from `Command<T>` for new commands
- Create settings classes with proper attributes
- Implement interactive prompts for missing parameters
- Include comprehensive help text

## Project Structure

```
pks-cli/
├── src/
│   ├── Commands/          # Command implementations
│   ├── Infrastructure/    # Services and DI setup
│   ├── Templates/         # Template files
│   └── Program.cs         # Entry point
├── tests/                 # Test projects
├── docs/                  # Documentation
└── README.md             # Project overview
```

## Adding New Features

### Creating a New Command

1. **Create the Command Class**
   ```csharp
   public class MyCommand : Command<MyCommand.Settings>
   {
       public class Settings : CommandSettings
       {
           [CommandArgument(0, "<NAME>")]
           public string? Name { get; init; }
           
           [CommandOption("--option")]
           public string? Option { get; init; }
       }
       
       public override int Execute(CommandContext context, Settings settings)
       {
           // Implementation
           return 0;
       }
   }
   ```

2. **Register the Command**
   ```csharp
   // In Program.cs
   app.Configure(config =>
   {
       config.AddCommand<MyCommand>("my-command");
   });
   ```

### Creating an Initializer

1. **Create the Initializer Class**
   ```csharp
   public class MyInitializer : CodeInitializer
   {
       public override string Id => "my-initializer";
       public override string Name => "My Initializer";
       public override int Order => 50;
       
       protected override async Task ExecuteCodeLogicAsync(
           InitializationContext context, 
           InitializationResult result)
       {
           // Implementation
       }
   }
   ```

2. **Register the Initializer**
   Initializers are auto-discovered via reflection.

## Testing

### Unit Tests
- Write unit tests for all public methods
- Use xUnit testing framework
- Mock external dependencies
- Test both success and failure scenarios

### Integration Tests
- Test command execution end-to-end
- Verify file generation and structure
- Test error scenarios and recovery

### Example Test
```csharp
[Fact]
public void Should_Create_Project_With_Correct_Structure()
{
    // Arrange
    var tempDir = CreateTempDirectory();
    var settings = new InitCommand.Settings
    {
        ProjectName = "test-project",
        Template = "console"
    };
    
    // Act
    var result = ExecuteCommand(settings);
    
    // Assert
    Assert.Equal(0, result);
    Assert.True(File.Exists(Path.Join(tempDir, "test-project.csproj")));
}
```

## Documentation

### API Documentation
- Use XML documentation comments
- Include parameter descriptions
- Provide usage examples
- Document exceptions

### User Documentation
- Update relevant documentation pages
- Add examples for new features
- Include troubleshooting information
- Keep language clear and accessible

## Pull Request Process

### Before Submitting

1. **Ensure all tests pass**
   ```bash
   dotnet test
   ```

2. **Run code formatting**
   ```bash
   dotnet format
   ```

3. **Update documentation**
   - Add/update XML comments
   - Update user documentation
   - Include examples

4. **Test manually**
   ```bash
   # Install and test locally
   dotnet pack
   dotnet tool install -g --add-source ./bin/Debug pks-cli --force
   pks --help
   ```

### PR Description

Include in your PR description:
- **What**: Brief description of changes
- **Why**: Reason for the change
- **How**: Implementation approach
- **Testing**: How you tested the changes
- **Breaking Changes**: Any breaking changes (if applicable)

### Review Process

1. **Automated Checks**: All CI checks must pass
2. **Code Review**: Core maintainers will review
3. **Testing**: Manual testing by reviewers
4. **Approval**: At least one approval required
5. **Merge**: Squash and merge to main branch

## Issue Reporting

### Bug Reports

When reporting bugs, include:
- **Environment**: OS, .NET version, PKS CLI version
- **Steps to Reproduce**: Clear, numbered steps
- **Expected Behavior**: What should happen
- **Actual Behavior**: What actually happens
- **Additional Context**: Screenshots, logs, etc.

### Feature Requests

For new features, provide:
- **Problem Statement**: What problem does this solve?
- **Proposed Solution**: How should it work?
- **Alternatives**: Other approaches considered
- **Examples**: Usage examples and scenarios

## Community Guidelines

### Code of Conduct

- Be respectful and inclusive
- Focus on constructive feedback
- Help others learn and grow
- Assume positive intent

### Communication

- **GitHub Issues**: Bug reports and feature requests
- **GitHub Discussions**: Questions and general discussion
- **Pull Requests**: Code contributions and reviews

## Recognition

Contributors are recognized in:
- **CONTRIBUTORS.md**: All contributors listed
- **Release Notes**: Major contributions highlighted
- **GitHub**: Contributor badge and statistics

## Getting Help

Need help contributing?

- **Documentation**: Start with this guide
- **Issues**: Check existing issues for similar questions
- **Discussions**: Ask questions in GitHub Discussions
- **Code**: Look at existing implementations for patterns

## License

By contributing to PKS CLI, you agree that your contributions will be licensed under the MIT License.

---

Thank you for contributing to PKS CLI! Every contribution, whether code, documentation, or feedback, helps make the project better for everyone. 🙏