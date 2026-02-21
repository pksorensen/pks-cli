# Model Context Protocol (MCP) Integration

## MCP Server Configuration

### Project-Specific MCP Server
This project has an MCP server configured with the following details:
- **Project ID**: `{{ProjectId}}`
- **Server ID**: `{{McpServerId}}`
- **Transport**: {{McpTransport}}
- **Status**: {{McpStatus}}

### MCP Tools Exposed
The following PKS CLI commands are exposed as MCP tools:

#### Project Management Tools
- `pks_init` - Initialize new projects
- `pks_status` - Get project status and health
- `pks_deploy` - Deploy project to environments

#### Agent Management Tools
- `pks_agent_list` - List all agents
- `pks_agent_create` - Create new agents
- `pks_agent_start` - Start agents
- `pks_agent_stop` - Stop agents
- `pks_agent_status` - Get agent status

#### GitHub Integration Tools
- `pks_github_create_repo` - Create GitHub repositories
- `pks_github_create_issue` - Create GitHub issues
- `pks_github_check_access` - Check repository access

#### Hooks Management Tools
- `pks_hooks_list` - List available hooks
- `pks_hooks_execute` - Execute specific hooks
- `pks_hooks_configure` - Configure hook settings

#### PRD Tools
- `pks_prd_create` - Create PRD documents
- `pks_prd_generate_requirements` - Generate requirements
- `pks_prd_generate_user_stories` - Generate user stories

### MCP Resources Exposed
The MCP server exposes the following resources:

#### Project Information
- `project://identity` - Project identity and configuration
- `project://status` - Current project status
- `project://metrics` - Project metrics and statistics

#### GitHub Integration
- `github://repository` - Repository information
- `github://issues` - Repository issues
- `github://workflows` - GitHub Actions workflows

#### Agents
- `agents://list` - List of active agents
- `agents://logs` - Agent execution logs
- `agents://configuration` - Agent configurations

#### Configuration
- `config://project` - Project-specific configuration
- `config://global` - Global PKS CLI configuration
- `config://secrets` - Encrypted configuration values

## MCP Server Management

### Starting the MCP Server
```bash
# Start MCP server for this project
pks mcp --start --project-id {{ProjectId}}

# Start with specific transport
pks mcp --start --transport stdio --project-id {{ProjectId}}
pks mcp --start --transport sse --port 8080 --project-id {{ProjectId}}
```

### Monitoring the MCP Server
```bash
# Check server status
pks mcp --status

# View server logs
pks mcp --logs

# Restart server
pks mcp --restart
```

### MCP Configuration Files
- `.pks/mcp-config.json` - MCP server configuration
- `.pks/mcp-tools.json` - Available tools definition
- `.pks/mcp-resources.json` - Exposed resources definition

## Client Configuration

### Claude Desktop Configuration
Add this to your Claude Desktop configuration:

```json
{
  "mcpServers": {
    "{{ProjectName}}-pks": {
      "command": "pks",
      "args": ["mcp", "--start", "--project-id", "{{ProjectId}}", "--transport", "stdio"],
      "env": {
        "PROJECT_PATH": "{{ProjectPath}}",
        "PKS_PROJECT_ID": "{{ProjectId}}"
      }
    }
  }
}
```

### Environment Variables
```bash
# Required for MCP server
export PKS_PROJECT_ID={{ProjectId}}
export PROJECT_PATH={{ProjectPath}}

# Optional GitHub integration
export GITHUB_TOKEN=your_github_token

# Optional configuration
export PKS_LOG_LEVEL=info
export PKS_MCP_TRANSPORT=stdio
```

## MCP Tool Development

### Adding New Tools
To add new MCP tools to PKS CLI:

1. **Implement the tool** in PKS CLI commands
2. **Add tool definition** to MCP tools registry
3. **Update MCP server** configuration
4. **Test tool exposure** through MCP protocol

### Tool Schema Example
```json
{
  "name": "pks_custom_tool",
  "description": "Custom PKS CLI tool",
  "inputSchema": {
    "type": "object",
    "properties": {
      "parameter": {
        "type": "string",
        "description": "Tool parameter"
      }
    },
    "required": ["parameter"]
  }
}
```

### Resource Schema Example
```json
{
  "uri": "custom://resource",
  "name": "Custom Resource",
  "description": "Custom project resource",
  "mimeType": "application/json"
}
```

## Integration with AI Assistants

### Claude Code Integration
This MCP server enables Claude Code to:
- **Manage project lifecycle** through PKS CLI tools
- **Access project information** through exposed resources
- **Execute development tasks** through automation agents
- **Monitor project health** through status resources

### Best Practices
- **Use project-specific servers** for better isolation
- **Monitor server performance** and resource usage
- **Keep tool schemas updated** with command changes
- **Test MCP integration** regularly
- **Document tool usage** for AI assistants

### Troubleshooting
```bash
# Debug MCP server issues
pks mcp --logs --verbose

# Test tool availability
pks mcp --list-tools

# Test resource access
pks mcp --list-resources

# Validate configuration
pks mcp --validate-config
```