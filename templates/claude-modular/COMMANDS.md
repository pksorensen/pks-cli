# Commands Reference

## Development Commands

### Build Commands
```bash
# Standard build
dotnet build

# Release build
dotnet build --configuration Release

# Publish for deployment
dotnet publish --configuration Release --output ./publish
```

### Test Commands
```bash
# Run all tests
dotnet test

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run tests in watch mode
dotnet watch test

# Run specific test project
dotnet test tests/{{ProjectName}}.Tests
```

### PKS CLI Commands
```bash
# Project management
pks init [project-name] --template [template] --agentic --mcp
pks status
pks deploy --environment [env]

# Agent management
pks agent --list
pks agent --create [name] --type [type]
pks agent --start [agent-id]
pks agent --stop [agent-id]
pks agent --remove [agent-id]

# MCP server management
pks mcp --start --project-id [id]
pks mcp --stop
pks mcp --status
pks mcp --restart
pks mcp --logs

# Hooks system
pks hooks --list
pks hooks --execute pre-build
pks hooks --execute post-build
pks hooks --execute pre-deploy
pks hooks --execute post-deploy

# PRD tools
pks prd --create
pks prd --status
pks prd --generate-requirements
pks prd --generate-user-stories
pks prd --validate

# GitHub integration
pks github --create-repo [name]
pks github --check-access
pks github --create-issue "[title]" "[body]"

# Configuration management
pks config --list
pks config --set [key] [value]
pks config --get [key]
pks config --delete [key]
```

## Container Commands (if enabled)
```bash
# Docker commands
docker build -t {{ProjectName}} .
docker run -p 8080:8080 {{ProjectName}}
docker-compose up -d

# Kubernetes commands
kubectl apply -f k8s/
kubectl get pods
kubectl logs -l app={{ProjectName}}
```