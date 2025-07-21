# Model Context Protocol (MCP) Integration Guide

## Overview

PKS CLI implements the Model Context Protocol (MCP) to provide seamless integration with AI assistants like Claude. Each PKS project can have its own MCP server that exposes project-specific tools and resources, enabling AI assistants to directly interact with PKS CLI functionality.

## MCP Architecture

### Core Components

#### 1. Project-Specific MCP Servers
Each PKS project gets its own MCP server:
- **Unique Server ID**: Based on project identity
- **Project Context**: Server has access to project configuration and state
- **Isolated Resources**: Each server only exposes resources for its project
- **Secure Communication**: Encrypted communication channels

#### 2. Transport Modes
PKS CLI supports multiple MCP transport modes:

##### Standard I/O (stdio)
- **Use Case**: Local AI assistant integration
- **Communication**: Process stdin/stdout
- **Security**: Local process isolation
- **Performance**: Low latency, high throughput

##### Server-Sent Events (SSE)
- **Use Case**: Remote AI assistant integration
- **Communication**: HTTP-based SSE protocol
- **Security**: HTTPS with authentication
- **Performance**: Network-dependent, good for remote scenarios

#### 3. Tool Exposure
PKS CLI commands are exposed as MCP tools:
- **Project Management**: Init, status, configuration
- **Agent Management**: Create, start, stop, monitor agents
- **GitHub Integration**: Repository management, issue creation
- **Hooks Management**: Execute hooks, configure automation
- **PRD Tools**: Requirements generation and management

#### 4. Resource Exposure
Project resources are available through MCP:
- **Project Information**: Identity, configuration, status
- **GitHub Resources**: Repository data, issues, workflows
- **Agent Resources**: Agent status, logs, configurations
- **Configuration Resources**: Project and global settings

## MCP Server Management

### 1. Starting MCP Server
```bash
# Start MCP server for current project
pks mcp --start

# Start with specific project ID
pks mcp --start --project-id pks-myproject-123abc

# Start with stdio transport (default)
pks mcp --start --transport stdio

# Start with SSE transport
pks mcp --start --transport sse --port 8080

# Start with custom configuration
pks mcp --start --config /path/to/mcp-config.json
```

### 2. Server Status and Monitoring
```bash
# Check server status
pks mcp --status

# View server logs
pks mcp --logs

# Monitor server in real-time
pks mcp --logs --follow

# Get server diagnostics
pks mcp --diagnostics

# Export server metrics
pks mcp --metrics --export metrics.json
```

### 3. Server Lifecycle
```bash
# Restart server
pks mcp --restart

# Stop server
pks mcp --stop

# Graceful shutdown
pks mcp --shutdown --timeout 30s

# Force kill server
pks mcp --kill
```

## MCP Tools Reference

### Project Management Tools

#### `pks_init`
Initialize new projects with PKS CLI.
```json
{
  "name": "pks_init",
  "description": "Initialize a new PKS CLI project",
  "inputSchema": {
    "type": "object",
    "properties": {
      "projectName": {
        "type": "string",
        "description": "Name of the project to create"
      },
      "template": {
        "type": "string",
        "enum": ["console", "api", "web", "agent", "library"],
        "description": "Project template to use"
      },
      "enableAgentic": {
        "type": "boolean",
        "description": "Enable agentic features"
      },
      "enableMcp": {
        "type": "boolean",
        "description": "Enable MCP integration"
      },
      "enableGithub": {
        "type": "boolean",
        "description": "Enable GitHub integration"
      }
    },
    "required": ["projectName"]
  }
}
```

#### `pks_status`
Get project status and health information.
```json
{
  "name": "pks_status",
  "description": "Get current project status",
  "inputSchema": {
    "type": "object",
    "properties": {
      "detailed": {
        "type": "boolean",
        "description": "Include detailed status information"
      },
      "includeMetrics": {
        "type": "boolean",
        "description": "Include performance metrics"
      }
    }
  }
}
```

### Agent Management Tools

#### `pks_agent_create`
Create new development agents.
```json
{
  "name": "pks_agent_create",
  "description": "Create a new development agent",
  "inputSchema": {
    "type": "object",
    "properties": {
      "agentName": {
        "type": "string",
        "description": "Name for the new agent"
      },
      "agentType": {
        "type": "string",
        "enum": ["development", "testing", "devops", "documentation", "security"],
        "description": "Type of agent to create"
      },
      "configuration": {
        "type": "object",
        "description": "Agent-specific configuration"
      }
    },
    "required": ["agentName", "agentType"]
  }
}
```

#### `pks_agent_list`
List all registered agents.
```json
{
  "name": "pks_agent_list",
  "description": "List all registered agents",
  "inputSchema": {
    "type": "object",
    "properties": {
      "status": {
        "type": "string",
        "enum": ["all", "active", "inactive"],
        "description": "Filter agents by status"
      },
      "type": {
        "type": "string",
        "description": "Filter agents by type"
      }
    }
  }
}
```

### GitHub Integration Tools

#### `pks_github_create_repo`
Create GitHub repositories.
```json
{
  "name": "pks_github_create_repo",
  "description": "Create a new GitHub repository",
  "inputSchema": {
    "type": "object",
    "properties": {
      "repositoryName": {
        "type": "string",
        "description": "Name of the repository"
      },
      "description": {
        "type": "string",
        "description": "Repository description"
      },
      "isPrivate": {
        "type": "boolean",
        "description": "Create as private repository"
      },
      "autoInit": {
        "type": "boolean",
        "description": "Initialize with README"
      }
    },
    "required": ["repositoryName"]
  }
}
```

#### `pks_github_create_issue`
Create GitHub issues.
```json
{
  "name": "pks_github_create_issue",
  "description": "Create a new GitHub issue",
  "inputSchema": {
    "type": "object",
    "properties": {
      "title": {
        "type": "string",
        "description": "Issue title"
      },
      "body": {
        "type": "string",
        "description": "Issue description"
      },
      "labels": {
        "type": "array",
        "items": {"type": "string"},
        "description": "Issue labels"
      },
      "assignees": {
        "type": "array",
        "items": {"type": "string"},
        "description": "Issue assignees"
      }
    },
    "required": ["title", "body"]
  }
}
```

### Hooks Management Tools

#### `pks_hooks_execute`
Execute project hooks.
```json
{
  "name": "pks_hooks_execute",
  "description": "Execute a specific project hook",
  "inputSchema": {
    "type": "object",
    "properties": {
      "hookName": {
        "type": "string",
        "enum": ["pre-build", "post-build", "pre-deploy", "post-deploy"],
        "description": "Name of the hook to execute"
      },
      "context": {
        "type": "object",
        "description": "Hook execution context"
      }
    },
    "required": ["hookName"]
  }
}
```

### PRD Tools

#### `pks_prd_generate_requirements`
Generate requirements documentation.
```json
{
  "name": "pks_prd_generate_requirements",
  "description": "Generate project requirements documentation",
  "inputSchema": {
    "type": "object",
    "properties": {
      "template": {
        "type": "string",
        "description": "Requirements template to use"
      },
      "includeUserStories": {
        "type": "boolean",
        "description": "Include user stories in requirements"
      },
      "outputFormat": {
        "type": "string",
        "enum": ["markdown", "json", "html"],
        "description": "Output format for requirements"
      }
    }
  }
}
```

## MCP Resources Reference

### Project Resources

#### `project://identity`
Project identity and configuration information.
```json
{
  "uri": "project://identity",
  "name": "Project Identity",
  "description": "Core project identity and configuration",
  "mimeType": "application/json",
  "content": {
    "projectId": "pks-myproject-123abc",
    "name": "MyProject",
    "description": "My awesome project",
    "createdAt": "2024-01-15T10:30:00Z",
    "gitHubRepository": "https://github.com/user/myproject",
    "mcpServerId": "pks-myproject-123abc",
    "agents": ["dev-agent", "test-agent"],
    "hooksEnabled": true
  }
}
```

#### `project://status`
Current project status and health.
```json
{
  "uri": "project://status",
  "name": "Project Status",
  "description": "Current project status and health metrics",
  "mimeType": "application/json",
  "content": {
    "status": "healthy",
    "lastUpdated": "2024-01-15T15:45:00Z",
    "activeAgents": 3,
    "mcpServerRunning": true,
    "githubConnected": true,
    "hooksConfigured": true,
    "buildStatus": "passing",
    "testCoverage": 85.2
  }
}
```

### GitHub Resources

#### `github://repository`
Repository information and metadata.
```json
{
  "uri": "github://repository",
  "name": "GitHub Repository",
  "description": "Repository information and metadata",
  "mimeType": "application/json",
  "content": {
    "fullName": "user/myproject",
    "description": "My awesome project",
    "isPrivate": false,
    "defaultBranch": "main",
    "language": "C#",
    "stars": 42,
    "forks": 8,
    "issues": 3,
    "pullRequests": 1
  }
}
```

#### `github://issues`
Repository issues and their status.
```json
{
  "uri": "github://issues",
  "name": "GitHub Issues",
  "description": "Repository issues and their current status",
  "mimeType": "application/json",
  "content": {
    "openIssues": [
      {
        "number": 15,
        "title": "Add user authentication",
        "state": "open",
        "assignee": "developer1",
        "labels": ["enhancement", "backend"]
      }
    ],
    "totalOpen": 3,
    "totalClosed": 12
  }
}
```

### Agent Resources

#### `agents://list`
List of all registered agents.
```json
{
  "uri": "agents://list",
  "name": "Agent List",
  "description": "List of all registered project agents",
  "mimeType": "application/json",
  "content": {
    "agents": [
      {
        "agentId": "dev-agent",
        "agentType": "development",
        "status": "running",
        "lastActivity": "2024-01-15T15:30:00Z"
      },
      {
        "agentId": "test-agent",
        "agentType": "testing",
        "status": "idle",
        "lastActivity": "2024-01-15T14:15:00Z"
      }
    ]
  }
}
```

#### `agents://logs`
Agent execution logs.
```json
{
  "uri": "agents://logs",
  "name": "Agent Logs",
  "description": "Agent execution logs and activity history",
  "mimeType": "text/plain",
  "content": "2024-01-15 15:30:15 [INFO] dev-agent: Started code analysis\n2024-01-15 15:30:45 [INFO] dev-agent: Found 3 optimization opportunities\n2024-01-15 15:31:00 [INFO] dev-agent: Generated refactoring suggestions"
}
```

## Client Configuration

### Claude Desktop Configuration
Add PKS CLI MCP server to Claude Desktop:
```json
{
  "mcpServers": {
    "pks-myproject": {
      "command": "pks",
      "args": ["mcp", "--start", "--project-id", "pks-myproject-123abc", "--transport", "stdio"],
      "env": {
        "PROJECT_PATH": "/path/to/myproject",
        "PKS_PROJECT_ID": "pks-myproject-123abc",
        "PKS_LOG_LEVEL": "info"
      }
    }
  }
}
```

### VS Code Extension Configuration
Configure PKS CLI MCP integration in VS Code:
```json
{
  "pks.mcp.enabled": true,
  "pks.mcp.transport": "stdio",
  "pks.mcp.autoStart": true,
  "pks.mcp.projectDetection": "auto"
}
```

### Environment Variables
Required environment variables for MCP integration:
```bash
# Project identification
export PKS_PROJECT_ID=pks-myproject-123abc
export PROJECT_PATH=/path/to/myproject

# GitHub integration (optional)
export GITHUB_TOKEN=your_github_token

# Logging configuration
export PKS_LOG_LEVEL=info
export PKS_MCP_LOG_ENABLED=true

# MCP server configuration
export PKS_MCP_TRANSPORT=stdio
export PKS_MCP_HOST=localhost
export PKS_MCP_PORT=8080
```

## Security Considerations

### 1. Authentication
- **Token-based**: Use secure tokens for authentication
- **Scoped Access**: Limit access to specific resources and operations
- **Encryption**: Encrypt all communication channels
- **Audit Logging**: Log all MCP operations for security auditing

### 2. Authorization
- **Role-based**: Implement role-based access control
- **Resource-level**: Control access at the resource level
- **Operation-level**: Control access to specific operations
- **Project-scoped**: Limit access to project-specific resources

### 3. Network Security
- **HTTPS Only**: Use HTTPS for all network communication
- **Certificate Validation**: Validate SSL certificates
- **Rate Limiting**: Implement rate limiting for API calls
- **IP Whitelisting**: Allow connections only from trusted IPs

## Performance Optimization

### 1. Caching
- **Resource Caching**: Cache frequently accessed resources
- **Tool Result Caching**: Cache tool execution results
- **Configuration Caching**: Cache configuration data
- **Intelligent Invalidation**: Invalidate cache based on changes

### 2. Connection Management
- **Connection Pooling**: Reuse connections where possible
- **Keep-alive**: Use keep-alive for persistent connections
- **Timeout Management**: Implement appropriate timeouts
- **Graceful Degradation**: Handle connection failures gracefully

### 3. Resource Management
- **Memory Management**: Optimize memory usage
- **CPU Optimization**: Minimize CPU-intensive operations
- **I/O Optimization**: Optimize file and network I/O
- **Garbage Collection**: Minimize GC pressure

## Troubleshooting

### 1. Common Issues

#### MCP Server Won't Start
```bash
# Check project configuration
pks mcp --validate-config

# Check port availability (for SSE)
netstat -an | grep 8080

# Check logs for errors
pks mcp --logs --level debug
```

#### Client Connection Failures
```bash
# Test connection
pks mcp --test-connection

# Verify authentication
pks config --get github.token

# Check network connectivity
curl -I https://api.github.com
```

#### Tool Execution Errors
```bash
# Enable debug mode
pks mcp --debug --logs

# Validate tool parameters
pks mcp --validate-tools

# Check agent status
pks agent --health-check
```

### 2. Debugging
```bash
# Enable verbose logging
export PKS_LOG_LEVEL=debug
export PKS_MCP_DEBUG=true

# Capture network traffic
pks mcp --capture-traffic

# Export diagnostics
pks mcp --export-diagnostics diagnostics.zip
```

### 3. Performance Tuning
```bash
# Monitor server performance
pks mcp --metrics

# Optimize cache settings
pks config mcp.cache.enabled true
pks config mcp.cache.ttl 300

# Adjust connection settings
pks config mcp.connection.timeout 30
pks config mcp.connection.retries 3
```

## Best Practices

### 1. MCP Server Management
- **One Server Per Project**: Each project should have its own MCP server
- **Proper Lifecycle**: Start/stop servers appropriately
- **Health Monitoring**: Monitor server health regularly
- **Resource Cleanup**: Clean up resources on shutdown

### 2. Tool Design
- **Clear Documentation**: Document all tools clearly
- **Input Validation**: Validate all tool inputs
- **Error Handling**: Provide clear error messages
- **Idempotency**: Make tools idempotent where possible

### 3. Resource Management
- **Efficient Updates**: Update resources efficiently
- **Appropriate Caching**: Cache resources appropriately
- **Access Control**: Implement proper access control
- **Version Management**: Handle resource versioning

### 4. Security
- **Secure Defaults**: Use secure defaults for all configurations
- **Regular Updates**: Keep dependencies updated
- **Audit Logging**: Log all security-relevant operations
- **Incident Response**: Have incident response procedures

The PKS CLI MCP integration provides a powerful bridge between AI assistants and development tools, enabling unprecedented levels of AI-assisted development productivity and collaboration.