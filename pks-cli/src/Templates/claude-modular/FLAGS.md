# Flags and Options Reference

## PKS CLI Global Flags

### Common Flags
- `--help, -h` - Show help information
- `--version, -v` - Show version information
- `--verbose` - Enable verbose output
- `--quiet, -q` - Suppress non-essential output
- `--dry-run` - Show what would be done without executing

### Init Command Flags
- `--template, -t` - Specify project template (console, api, web, agent, library)
- `--agentic, -a` - Enable agentic features and AI automation
- `--mcp, -m` - Enable Model Context Protocol integration
- `--hooks, -h` - Enable hooks system
- `--prd` - Enable PRD (Product Requirements Document) tools
- `--github, -g` - Enable GitHub integration
- `--create-repo, -cr` - Create new GitHub repository
- `--private-repo, -pr` - Create private repository
- `--force, -f` - Force overwrite existing directory
- `--description, -d` - Project description

### Agent Command Flags
- `--list, -l` - List all agents
- `--create, -c` - Create new agent
- `--type, -t` - Agent type (automation, monitoring, deployment, custom)
- `--start, -s` - Start an agent
- `--stop` - Stop an agent
- `--remove, -r` - Remove an agent
- `--status` - Show agent status
- `--config` - Show agent configuration

### MCP Command Flags
- `--start` - Start MCP server
- `--stop` - Stop MCP server
- `--restart` - Restart MCP server
- `--status` - Show MCP server status
- `--logs` - Show MCP server logs
- `--project-id, -p` - Specify project ID
- `--transport, -tr` - Transport mode (stdio, sse)
- `--port` - Port for SSE transport

### Deploy Command Flags
- `--environment, -e` - Target environment (dev, staging, production)
- `--image, -i` - Container image
- `--replicas, -r` - Number of replicas
- `--namespace, -n` - Kubernetes namespace
- `--config, -c` - Configuration file

### GitHub Command Flags
- `--create-repo` - Create new repository
- `--repo-name, -rn` - Repository name
- `--private` - Create private repository
- `--check-access` - Check repository access
- `--create-issue` - Create new issue
- `--token, -t` - GitHub personal access token

### Hooks Command Flags
- `--list, -l` - List available hooks
- `--execute, -e` - Execute specific hook
- `--enable` - Enable hook
- `--disable` - Disable hook
- `--configure` - Configure hook settings

### PRD Command Flags
- `--create, -c` - Create new PRD
- `--status, -s` - Show PRD status
- `--generate-requirements, -gr` - Generate requirements document
- `--generate-user-stories, -gs` - Generate user stories
- `--validate, -v` - Validate PRD content
- `--template, -t` - Use specific template

## Environment Variables

### PKS CLI Configuration
- `PKS_CONFIG_PATH` - Custom configuration directory
- `PKS_LOG_LEVEL` - Logging level (debug, info, warn, error)
- `PKS_GITHUB_TOKEN` - Default GitHub personal access token
- `PKS_MCP_TRANSPORT` - Default MCP transport mode

### Development Environment
- `ASPNETCORE_ENVIRONMENT` - ASP.NET Core environment
- `DOTNET_ENVIRONMENT` - .NET environment
- `CONNECTION_STRING` - Database connection string
- `API_KEY` - External API key (if applicable)