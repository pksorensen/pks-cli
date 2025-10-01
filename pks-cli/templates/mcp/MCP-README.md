# MCP Configuration for {{ProjectName}}

This directory contains Model Context Protocol (MCP) configuration files for integrating {{ProjectName}} with AI development tools.

## Overview

The Model Context Protocol (MCP) is an open protocol that standardizes how AI applications connect to and interact with external data sources and tools. This configuration enables AI tools like Claude Code to use {{ProjectName}} CLI as an intelligent development assistant.

## Configuration Files

### `.mcp.json` - Local Stdio Configuration
- **Purpose**: Local development with direct stdio communication
- **Transport**: stdio (direct process communication)
- **Use Case**: Local development, testing, and debugging
- **Command**: Uses `dnx {{project_name}}-cli mcp` for one-shot execution

### `.mcp.sse.json` - Remote SSE Configuration  
- **Purpose**: Remote server integration with Server-Sent Events
- **Transport**: SSE (HTTP-based streaming)
- **Use Case**: Cloud deployments, team collaboration, remote AI assistance
- **Authentication**: OAuth 2.0 with configurable scopes

## Setup Instructions

### Prerequisites
1. Install .NET 8.0 or later
2. Install {{ProjectName}} CLI as a global tool:
   ```bash
   dotnet tool install -g {{project_name}}-cli
   ```

### Environment Variables
Configure the following environment variables for secure operation:

#### Required for Local (Stdio)
```bash
export {{MCP.EnvPrefix}}_LOG_LEVEL=Information
export {{MCP.EnvPrefix}}_CONFIG_PATH=./config
export {{MCP.EnvPrefix}}_WORK_DIR=./
export DOTNET_ENVIRONMENT=Development
```

#### Required for Remote (SSE)
```bash
export {{MCP.EnvPrefix}}_SERVER_URL={{MCP.ServerUrl}}
export {{MCP.EnvPrefix}}_API_KEY=your_api_key_here
export {{MCP.EnvPrefix}}_CLIENT_ID=your_oauth_client_id
export {{MCP.EnvPrefix}}_CLIENT_SECRET=your_oauth_client_secret
export {{MCP.EnvPrefix}}_MCP_LOG_LEVEL=info
```

### AI Tool Integration

#### Claude Code
1. Copy `.mcp.json` to your project's `.mcp.json` file
2. Configure environment variables
3. Restart Claude Code to load the configuration

#### Cursor
1. Copy configuration to `~/.cursor/mcp.json` (global) or `.cursor/mcp.json` (project)
2. Set environment variables in your shell profile
3. Restart Cursor

#### Other MCP-Compatible Tools
- Follow the tool's specific MCP configuration instructions
- Use the provided JSON schema for validation
- Ensure proper environment variable setup

## Available Tools

### `init` - Project Initialization
Initialize new projects with intelligent templates and configurations.

**Parameters:**
- `template`: Project template (console, api, web, library, agentic)
- `name`: Project name
- `description`: Project description
- `force`: Force overwrite existing files

**Example:**
```json
{
  "tool": "init",
  "parameters": {
    "template": "agentic",
    "name": "MyAIProject",
    "description": "AI-powered development tool"
  }
}
```

### `agent` - AI Agent Management
Manage AI agents for development automation.

**Parameters:**
- `action`: Agent action (create, list, status, remove, execute)
- `type`: Agent type (automation, monitoring, optimization, testing, deployment)
- `name`: Agent identifier
- `capabilities`: Agent capabilities to enable

**Example:**
```json
{
  "tool": "agent",
  "parameters": {
    "action": "create",
    "type": "automation",
    "name": "code-generator",
    "capabilities": ["code-generation", "testing", "documentation"]
  }
}
```

### `deploy` - Intelligent Deployment
Deploy applications with Kubernetes orchestration and intelligent scaling.

**Parameters:**
- `environment`: Target environment (development, staging, production)
- `strategy`: Deployment strategy (rolling, blue-green, canary)
- `replicas`: Number of replicas
- `auto-scale`: Enable automatic scaling

**Example:**
```json
{
  "tool": "deploy",
  "parameters": {
    "environment": "staging",
    "strategy": "rolling",
    "replicas": 3,
    "auto-scale": true
  }
}
```

### `status` - System Monitoring
Monitor system status with real-time insights and intelligent analysis.

**Parameters:**
- `component`: Component to check (all, agents, deployments, services, resources)
- `detailed`: Show detailed information
- `watch`: Enable real-time updates

**Example:**
```json
{
  "tool": "status",
  "parameters": {
    "component": "all",
    "detailed": true,
    "watch": false
  }
}
```

## Resources

The MCP configuration provides access to several project resources:

- **project-config**: Current project configuration and settings
- **agent-definitions**: Available agent definitions and capabilities  
- **deployment-manifests**: Kubernetes deployment manifests
- **remote-templates**: Available remote project templates (SSE only)
- **ai-models**: Available AI models and capabilities (SSE only)

## Security Configuration

### Local Development
- File access: Enabled (for local project files)
- Network access: Disabled (stdio transport only)
- Remote servers: Disabled

### Remote/Cloud
- SSL validation: Enabled
- OAuth 2.0 authentication: Required
- Scoped access: Read, write, deploy, ai-assist
- Connection limits: Configurable

## Troubleshooting

### Common Issues

1. **Tool not found**: Ensure {{ProjectName}} CLI is installed globally
2. **Environment variables**: Verify all required variables are set
3. **Permissions**: Check file and directory permissions
4. **Network**: Verify connectivity for remote configurations

### Debug Logging
Enable detailed logging by setting:
```bash
export {{MCP.EnvPrefix}}_MCP_LOG_LEVEL=debug
export {{MCP.EnvPrefix}}_LOG_LEVEL=Debug
```

### Testing Configuration
Test the MCP configuration:
```bash
# Test local stdio configuration
dnx {{project_name}}-cli mcp --test

# Validate JSON schema
mcp-validate .mcp.json
```

## Development

### Extending Tools
Add new tools by modifying the `tools` array in the MCP configuration and implementing the corresponding commands in {{ProjectName}} CLI.

### Custom Resources
Add project-specific resources by extending the `resources` array with appropriate URIs and MIME types.

### Authentication
For custom authentication, modify the `authentication` section in the SSE configuration.

## Support

For issues and questions:
- GitHub Issues: [{{ProjectName}} Repository](https://github.com/{{project_name}}/{{project_name}}-cli/issues)
- Documentation: [MCP Protocol Specification](https://modelcontextprotocol.io)
- Community: [MCP GitHub Discussions](https://github.com/modelcontextprotocol/modelcontextprotocol/discussions)

---

Generated by {{ProjectName}} MCP Configuration Initializer on {{Date}}