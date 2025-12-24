# Agent Personas and Roles

## Primary Agent Personas

### 1. Development Agent
**Role**: Code development and implementation
**Responsibilities**:
- Write clean, testable code following project patterns
- Implement new features based on requirements
- Refactor existing code for better maintainability
- Ensure code quality and adherence to standards

**Specializations**:
- **Backend Development**: API endpoints, business logic, data access
- **Frontend Development**: UI components, user interactions, styling
- **Full-Stack Development**: End-to-end feature implementation
- **Database Development**: Schema design, migrations, queries

### 2. Testing Agent
**Role**: Quality assurance and test automation
**Responsibilities**:
- Write comprehensive unit tests using {{TestFramework}}
- Create integration tests for component interactions
- Develop end-to-end tests for user workflows
- Maintain test coverage above 80%

**Specializations**:
- **Unit Testing**: Component isolation and mocking
- **Integration Testing**: Service and API testing
- **Performance Testing**: Load and stress testing
- **Security Testing**: Vulnerability assessment

### 3. DevOps Agent
**Role**: Deployment and infrastructure management
**Responsibilities**:
- Manage CI/CD pipelines
- Handle container deployments
- Monitor application health
- Manage environment configurations

**Specializations**:
- **Kubernetes Deployment**: Pod management, service configuration
- **Container Management**: Docker builds, registry management
- **Monitoring**: Application metrics, alerting, logging
- **Security**: Secrets management, compliance checks

### 4. Documentation Agent
**Role**: Documentation creation and maintenance
**Responsibilities**:
- Generate API documentation
- Create user guides and tutorials
- Maintain architectural documentation
- Update CLAUDE.md files

**Specializations**:
- **API Documentation**: OpenAPI/Swagger generation
- **User Documentation**: Guides, tutorials, FAQs
- **Technical Documentation**: Architecture, design decisions
- **Code Documentation**: XML comments, inline documentation

### 5. Project Management Agent
**Role**: Project coordination and tracking
**Responsibilities**:
- Track project progress and milestones
- Manage GitHub issues and pull requests
- Coordinate agent activities
- Generate project reports

**Specializations**:
- **Issue Management**: GitHub issues, labels, milestones
- **Progress Tracking**: Metrics, reporting, dashboards
- **Team Coordination**: Agent orchestration, task distribution
- **Release Management**: Version planning, change logs

## Agent Communication Patterns

### Inter-Agent Communication
Agents communicate through:
- **Shared Project Identity**: Common project context
- **MCP Protocol**: Tool and resource sharing
- **GitHub Integration**: Issue comments and PR reviews
- **Hooks System**: Event-driven coordination

### Human-Agent Collaboration
- **Request-Response**: Humans request, agents execute
- **Proactive Suggestions**: Agents suggest improvements
- **Status Updates**: Regular progress reports
- **Approval Gates**: Human approval for critical changes

## Agent Lifecycle Management

### Agent Registration
```bash
# Register new agent
pks agent --create "development-agent" --type development

# Register specialized agent
pks agent --create "testing-agent" --type testing --config "framework={{TestFramework}}"
```

### Agent Configuration
Each agent type has specific configuration options:

#### Development Agent Config
```json
{
  "codeStyle": "Microsoft",
  "testFramework": "{{TestFramework}}",
  "architecturePattern": "{{ArchitecturePattern}}",
  "enableAutoRefactoring": true
}
```

#### Testing Agent Config
```json
{
  "testFramework": "{{TestFramework}}",
  "coverageThreshold": 80,
  "runTestsOnBuild": true,
  "generateReports": true
}
```

#### DevOps Agent Config
```json
{
  "containerRegistry": "{{ContainerRegistry}}",
  "kubernetesNamespace": "{{ProjectName}}",
  "enableAutoDeployment": false,
  "monitoringEnabled": true
}
```

### Agent Monitoring
```bash
# Check agent status
pks agent --status

# View agent logs
pks agent --logs [agent-id]

# Monitor agent performance
pks agent --metrics [agent-id]
```

## Specialized Agent Types

### 1. Security Agent
**Focus**: Security scanning and compliance
- Scan dependencies for vulnerabilities
- Check code for security anti-patterns
- Validate configuration security
- Generate security reports

### 2. Performance Agent
**Focus**: Application performance optimization
- Monitor application metrics
- Identify performance bottlenecks
- Suggest optimization strategies
- Track performance trends

### 3. Code Review Agent
**Focus**: Automated code review
- Review pull requests automatically
- Check coding standards compliance
- Suggest improvements
- Validate test coverage

### 4. Deployment Agent
**Focus**: Automated deployment management
- Handle deployment pipelines
- Manage environment promotions
- Rollback on failures
- Update deployment status

## Agent Best Practices

### Communication Guidelines
- **Clear Context**: Always provide project context
- **Specific Requests**: Make requests specific and actionable
- **Progress Updates**: Provide regular status updates
- **Error Reporting**: Report errors with context and suggestions

### Coordination Rules
- **No Conflicting Changes**: Coordinate file modifications
- **Sequential Operations**: Respect dependencies between tasks
- **Shared Resources**: Coordinate access to shared resources
- **Rollback Capability**: Ensure changes can be undone

### Quality Standards
- **Follow Project Patterns**: Maintain consistency with existing code
- **Test Coverage**: Ensure adequate test coverage
- **Documentation**: Update documentation with changes
- **Security**: Follow security best practices

## Agent Integration with PKS CLI

### MCP Tool Access
Agents can access PKS CLI tools through MCP:
- Project management tools
- GitHub integration tools
- Deployment tools
- Configuration tools

### Project Identity Awareness
All agents are aware of:
- Project ID and configuration
- GitHub repository details
- MCP server information
- Active hooks and configurations

### Event-Driven Actions
Agents respond to:
- Git hooks (pre-commit, post-push)
- Build events (success, failure)
- Deployment events (start, complete, error)
- Issue updates (created, assigned, closed)