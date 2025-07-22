# pks agent - AI Agent Management

The `pks agent` command enables you to create, manage, and coordinate AI agents that assist with various development tasks.

## Overview

AI agents in PKS CLI are specialized assistants that help with:
- **Code Generation** - Writing boilerplate and complex logic
- **Testing** - Creating and maintaining test suites
- **Documentation** - Generating and updating docs  
- **Architecture** - Designing system components
- **DevOps** - Managing deployments and infrastructure

## Syntax

```bash
pks agent <SUBCOMMAND> [OPTIONS]
```

## Subcommands

### create
Create a new AI agent with specified capabilities.

```bash
pks agent create --name <NAME> --type <TYPE> [OPTIONS]
```

**Options:**
- `--name <NAME>` - Agent name (required)
- `--type <TYPE>` - Agent type: `developer`, `testing`, `documentation`, `architecture`, `devops`
- `--description <TEXT>` - Agent description
- `--specialization <AREA>` - Specific area of focus
- `--shared` - Make agent available to team

**Examples:**
```bash
# Create a development agent
pks agent create --name DevBot --type developer

# Create a specialized testing agent
pks agent create --name TestMaster --type testing --specialization "API testing"

# Create a shared documentation agent
pks agent create --name DocsBot --type documentation --shared --description "Technical writing expert"
```

### list
Display all available agents and their status.

```bash
pks agent list [OPTIONS]
```

**Options:**
- `--format <FORMAT>` - Output format: `table`, `json`, `minimal`
- `--status <STATUS>` - Filter by status: `active`, `idle`, `stopped`
- `--type <TYPE>` - Filter by agent type

**Examples:**
```bash
# List all agents
pks agent list

# List only active agents
pks agent list --status active

# JSON output for scripts
pks agent list --format json
```

### start
Start an agent to begin accepting tasks.

```bash
pks agent start <NAME> [OPTIONS]
```

**Options:**
- `--background` - Run agent in background
- `--auto-accept` - Automatically accept compatible tasks
- `--max-tasks <NUM>` - Maximum concurrent tasks (default: 3)

**Examples:**
```bash
# Start an agent interactively
pks agent start DevBot

# Start agent in background
pks agent start TestBot --background --auto-accept
```

### stop
Stop a running agent.

```bash
pks agent stop <NAME> [OPTIONS]
```

**Options:**
- `--force` - Force stop without completing current tasks
- `--save-state` - Save current progress before stopping

### status
Show detailed status information for an agent.

```bash
pks agent status <NAME> [OPTIONS]
```

**Options:**
- `--detailed` - Show extended information
- `--logs` - Include recent log entries
- `--format <FORMAT>` - Output format

### remove
Remove an agent permanently.

```bash
pks agent remove <NAME> [OPTIONS]
```

**Options:**
- `--force` - Remove without confirmation
- `--backup` - Create backup before removal

## Agent Types

### Developer Agent
Specializes in code-related tasks:
- Code generation and refactoring
- Bug fixing and optimization
- Code review and analysis
- Implementation of features

```bash
pks agent create --name CodeMaster --type developer --specialization "API development"
```

### Testing Agent  
Focuses on quality assurance:
- Unit test creation
- Integration test design
- Test automation
- Performance testing

```bash
pks agent create --name TestBot --type testing --specialization "automated testing"
```

### Documentation Agent
Handles documentation tasks:
- API documentation generation
- User guide creation  
- Technical writing
- Documentation maintenance

```bash
pks agent create --name DocsExpert --type documentation
```

### Architecture Agent
Provides system design guidance:
- System architecture design
- Design pattern recommendations
- Performance optimization
- Scalability planning

```bash
pks agent create --name ArchBot --type architecture --specialization "microservices"
```

### DevOps Agent
Manages deployment and operations:
- CI/CD pipeline creation
- Infrastructure management
- Monitoring setup
- Deployment automation

```bash
pks agent create --name DeployBot --type devops --specialization "Kubernetes"
```

## Agent Coordination

### Task Assignment
Agents can work together on complex tasks:

```bash
# Create a team of agents
pks agent create --name DevLead --type developer --specialization "coordination"
pks agent create --name BackendDev --type developer --specialization "APIs"  
pks agent create --name FrontendDev --type developer --specialization "UI/UX"
pks agent create --name QAEngineer --type testing

# Start the team
pks agent start DevLead --auto-accept
pks agent start BackendDev --background
pks agent start FrontendDev --background
pks agent start QAEngineer --background
```

### Communication
Agents can communicate through the PKS message system:

```bash
# View agent messages
pks agent messages DevBot

# Send message to agent
pks agent send DevBot "Focus on performance optimization"
```

## Configuration

### Agent Configuration File
Agents are configured in `~/.pks/agents/<name>/config.json`:

```json
{
  "name": "DevBot",
  "type": "developer", 
  "specialization": "API development",
  "capabilities": [
    "code-generation",
    "refactoring", 
    "testing",
    "documentation"
  ],
  "preferences": {
    "codeStyle": "standard",
    "testFramework": "xUnit",
    "maxConcurrentTasks": 3
  },
  "learning": {
    "enabled": true,
    "feedback": true,
    "adaptation": true
  }
}
```

### Global Agent Settings
Configure agent behavior in `pks.config.json`:

```json
{
  "agents": {
    "autoSpawn": true,
    "defaultType": "developer",
    "maxConcurrentAgents": 5,
    "learningEnabled": true,
    "communicationEnabled": true,
    "taskTimeout": 1800
  }
}
```

## Examples

### Development Workflow
```bash
# Setup development team
pks agent create --name TeamLead --type developer --specialization "coordination"
pks agent create --name APIBot --type developer --specialization "REST APIs"
pks agent create --name DBBot --type developer --specialization "database design"

# Start the agents
pks agent start TeamLead --background --auto-accept
pks agent start APIBot --background
pks agent start DBBot --background

# Monitor progress
pks agent status TeamLead --detailed
pks agent list --status active
```

### Testing Pipeline
```bash
# Create testing specialists
pks agent create --name UnitTester --type testing --specialization "unit tests"
pks agent create --name IntegrationTester --type testing --specialization "integration tests"
pks agent create --name E2ETester --type testing --specialization "end-to-end tests"

# Run comprehensive testing
pks agent start UnitTester --auto-accept
pks agent start IntegrationTester --auto-accept  
pks agent start E2ETester --auto-accept
```

## Integration with MCP

Agents integrate seamlessly with MCP servers:

```bash
# Start MCP server with agent tools
pks mcp start --enable-agents --port 3000

# Agents become available as MCP tools for AI clients
```

## Best Practices

### Naming Conventions
- Use descriptive names: `APITester`, `DocsWriter`, `DeployBot`
- Include specialization: `ReactExpert`, `KubernetesOps`
- Avoid generic names: `Agent1`, `Bot`, `Helper`

### Agent Specialization
- Create focused agents rather than generalists
- Specialize agents for specific technologies or frameworks
- Use type-appropriate specializations

### Team Coordination
- Assign a coordinator agent for complex projects
- Use clear communication channels between agents
- Monitor agent workloads and performance

### Resource Management
- Limit concurrent agents based on system resources
- Stop idle agents to conserve memory
- Use background mode for long-running tasks

## Troubleshooting

### Agent Won't Start
```bash
# Check agent configuration
pks agent status MyAgent --detailed

# View logs
pks agent logs MyAgent

# Restart agent
pks agent stop MyAgent --force
pks agent start MyAgent
```

### Performance Issues
```bash
# Limit concurrent tasks
pks agent configure MyAgent --max-tasks 2

# Monitor resource usage
pks agent stats --all

# Stop idle agents
pks agent stop --idle
```

## Next Steps

- **[Working with Agents Tutorial](../tutorials/working-with-agents.md)** - Learn agent workflows
- **[MCP Integration](../advanced/mcp-integration.md)** - Connect agents to AI tools
- **[Architecture Guide](../architecture/agent-framework.md)** - Understand agent internals

Ready to create your first AI development team? 🤖

```bash
pks agent create --name DevAssistant --type developer
pks agent start DevAssistant
```