# Development Rules

## Mandatory Rules

### File Creation Rules
- **NEVER create files unless absolutely necessary** for achieving the goal
- **ALWAYS prefer editing existing files** to creating new ones
- **NEVER proactively create documentation files** (*.md) or README files unless explicitly requested
- **Follow existing code patterns** and architecture decisions
- **Use established naming conventions** and folder structure

### Code Quality Rules
- **Write unit tests for new functionality** using {{TestFramework}}
- **Update this CLAUDE.md file** when making architectural changes
- **Follow C# coding conventions** and StyleCop rules
- **Maintain minimum 80% code coverage**
- **Use meaningful variable and method names**
- **Write XML documentation** for public APIs
- **Implement proper async/await patterns**
- **Use dependency injection** for loose coupling

### Security Rules
- **Never hardcode secrets** in code
- **Use HTTPS** for all external communications
- **Implement proper authentication** and authorization
- **Validate all inputs** and sanitize outputs
- **Follow OWASP security guidelines**
- **Store sensitive data** in Azure Key Vault or environment variables

### Performance Rules
- **Use async/await** for I/O operations
- **Implement proper caching** strategies
- **Monitor memory usage** and implement proper disposal patterns
- **Use efficient LINQ operations** and avoid N+1 queries
- **Profile performance** critical paths

## PKS CLI Specific Rules

### Project Identity Rules
- **Every project must have a unique project ID**
- **Project configuration must be stored in .pks folder**
- **Project identity must be validated before operations**
- **Cross-component integration requires project ID**

### GitHub Integration Rules
- **Personal access tokens must be encrypted**
- **Repository URLs must be validated**
- **GitHub operations require proper authentication**
- **Repository creation must include proper description**

### MCP Server Rules
- **MCP servers must be project-specific**
- **Follow MCP protocol specification exactly**
- **Transport configuration must be validated**
- **Server lifecycle must be properly managed**

### Agent Framework Rules
- **Agents must be registered with project**
- **Agent types must be clearly defined**
- **Agent configuration must be persisted**
- **Agent lifecycle must be trackable**

### Hooks System Rules
- **Hooks must be executable and secure**
- **Hook configuration must be validated**
- **Hook execution must be logged**
- **Failed hooks must not break the pipeline**

### Initializer Rules
- **Initializers must implement proper interfaces**
- **Execution order must be respected**
- **Conditional execution must be implemented**
- **Error handling must be comprehensive**

## Testing Rules

### Unit Testing Rules
- **Use AAA pattern** (Arrange, Act, Assert)
- **Test one thing at a time**
- **Use descriptive test names**
- **Mock external dependencies**
- **Test both happy path and error cases**

### Integration Testing Rules
- **Test component interactions**
- **Use realistic test data**
- **Clean up test resources**
- **Test configuration variations**
- **Validate end-to-end workflows**

### Test Organization Rules
```
tests/
├── Unit/                 # Unit tests for individual components
├── Integration/          # Integration tests for component interactions
├── EndToEnd/            # End-to-end tests for complete workflows
└── Common/              # Shared test utilities and helpers
```

## Error Handling Rules

### Exception Handling
- **Use custom exceptions** for business logic errors
- **Implement global exception handling** middleware
- **Log errors with structured logging**
- **Provide meaningful error messages**
- **Don't expose internal details** in error responses

### Validation Rules
- **Validate inputs at boundaries**
- **Use data annotations** for model validation
- **Implement business rule validation**
- **Return specific validation errors**
- **Log validation failures**

## Logging Rules

### Structured Logging
- **Use structured logging** with Serilog or ILogger
- **Include correlation IDs** for request tracking
- **Log at appropriate levels** (Debug, Info, Warning, Error)
- **Don't log sensitive information**
- **Include context** in log messages

### Performance Logging
- **Log slow operations**
- **Track key metrics**
- **Monitor resource usage**
- **Alert on performance degradation**

## Configuration Rules

### Configuration Management
- **Use strongly-typed configuration** with IOptions<T>
- **Separate configuration by environment**
- **Validate configuration on startup**
- **Document configuration options**
- **Use secrets management** for sensitive values

### Environment-Specific Rules
- **Development**: Detailed logging, development tools enabled
- **Staging**: Production-like, but with debugging capabilities
- **Production**: Optimized performance, minimal logging, security hardened