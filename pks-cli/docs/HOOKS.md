# Hooks System Guide

## Overview

The PKS CLI Hooks System provides an event-driven automation framework that allows developers to execute custom scripts and actions at specific points in the development lifecycle. The system features a smart dispatcher that intelligently routes hook execution based on context and conditions.

## Hook Architecture

### Core Components

#### 1. Hook Service
The central service that manages hook execution:
- Hook discovery and registration
- Context-aware execution routing
- Smart dispatcher with conditional logic
- Error handling and recovery mechanisms

#### 2. Hook Types
PKS CLI supports several built-in hook types:

##### Build Hooks
- **pre-build**: Execute before build operations
- **post-build**: Execute after successful builds
- **build-failed**: Execute when builds fail

##### Deployment Hooks
- **pre-deploy**: Execute before deployment
- **post-deploy**: Execute after successful deployment
- **deploy-failed**: Execute when deployment fails

##### Git Hooks
- **pre-commit**: Execute before git commits
- **post-commit**: Execute after git commits
- **pre-push**: Execute before git push
- **post-merge**: Execute after git merge

##### Custom Hooks
- **custom-[name]**: User-defined hooks for specific scenarios
- **agent-[event]**: Agent-triggered hooks
- **mcp-[event]**: MCP server event hooks

#### 3. Smart Dispatcher
The intelligent routing system that:
- Analyzes execution context
- Applies conditional logic
- Selects appropriate hook implementations
- Handles parallel and sequential execution

#### 4. Hook Context
Rich context information available to hooks:
- Project information and configuration
- Git repository state
- Build and deployment status
- Agent activity and status
- Environment variables and settings

## Hook Configuration

### 1. Hook Registration
```bash
# List available hooks
pks hooks --list

# Register a new hook
pks hooks --register pre-build --script "scripts/pre-build.sh"

# Register with conditions
pks hooks --register post-build --script "scripts/notify.sh" --condition "environment=production"

# Register for specific file patterns
pks hooks --register pre-commit --script "scripts/lint.sh" --pattern "*.cs"
```

### 2. Hook Configuration File
Create a `.pks/hooks.json` configuration file:
```json
{
  "hooks": {
    "pre-build": {
      "enabled": true,
      "scripts": [
        {
          "name": "code-analysis",
          "script": "scripts/analyze.sh",
          "conditions": {
            "fileTypes": ["*.cs", "*.ts"],
            "environment": ["development", "staging"]
          },
          "timeout": 60,
          "failureAction": "warn"
        }
      ]
    },
    "post-build": {
      "enabled": true,
      "scripts": [
        {
          "name": "run-tests",
          "script": "dotnet test",
          "conditions": {
            "buildSuccess": true
          },
          "timeout": 300,
          "failureAction": "fail"
        },
        {
          "name": "notify-team",
          "script": "scripts/notify.sh",
          "conditions": {
            "environment": "production",
            "testsPassed": true
          },
          "timeout": 30,
          "failureAction": "ignore"
        }
      ]
    },
    "pre-deploy": {
      "enabled": true,
      "scripts": [
        {
          "name": "security-scan",
          "script": "scripts/security-scan.sh",
          "conditions": {
            "environment": ["staging", "production"]
          },
          "timeout": 120,
          "failureAction": "fail"
        }
      ]
    }
  },
  "dispatcher": {
    "parallelExecution": true,
    "maxConcurrency": 3,
    "defaultTimeout": 60,
    "retryAttempts": 2,
    "retryDelay": 5
  }
}
```

### 3. Environment-Specific Configuration
```json
{
  "environments": {
    "development": {
      "hooks": {
        "pre-commit": {
          "scripts": ["lint", "format", "quick-test"]
        }
      }
    },
    "staging": {
      "hooks": {
        "pre-deploy": {
          "scripts": ["full-test-suite", "security-scan", "performance-test"]
        }
      }
    },
    "production": {
      "hooks": {
        "pre-deploy": {
          "scripts": ["full-test-suite", "security-scan", "performance-test", "manual-approval"]
        },
        "post-deploy": {
          "scripts": ["health-check", "monitoring-setup", "team-notification"]
        }
      }
    }
  }
}
```

## Hook Execution

### 1. Manual Hook Execution
```bash
# Execute specific hook
pks hooks --execute pre-build

# Execute with custom context
pks hooks --execute pre-build --context "environment=staging,branch=main"

# Execute with dry-run mode
pks hooks --execute pre-build --dry-run

# Execute with verbose output
pks hooks --execute pre-build --verbose

# Execute specific script within hook
pks hooks --execute pre-build --script "code-analysis"
```

### 2. Automatic Hook Execution
Hooks are automatically triggered by:
- Build system integration (MSBuild, dotnet CLI)
- Git operations (when git hooks are configured)
- PKS CLI commands (deploy, status, etc.)
- Agent activities and events
- MCP server events

### 3. Hook Context
Hooks receive rich context information:
```json
{
  "project": {
    "id": "pks-myproject-123abc",
    "name": "MyProject",
    "path": "/path/to/project",
    "template": "api"
  },
  "git": {
    "branch": "feature/new-api",
    "commit": "abc123...",
    "author": "developer@example.com",
    "changedFiles": ["src/Controllers/ApiController.cs", "tests/ApiTests.cs"]
  },
  "build": {
    "configuration": "Release",
    "target": "net8.0",
    "success": true,
    "duration": 45.2
  },
  "environment": {
    "name": "staging",
    "variables": {
      "ASPNETCORE_ENVIRONMENT": "Staging",
      "CONNECTION_STRING": "***"
    }
  },
  "agents": {
    "active": ["dev-agent", "test-agent"],
    "lastActivity": "2024-01-15T15:30:00Z"
  }
}
```

## Smart Dispatcher

### 1. Conditional Execution
The smart dispatcher evaluates conditions before executing hooks:

#### File-based Conditions
```json
{
  "conditions": {
    "fileTypes": ["*.cs", "*.ts"],
    "filePatterns": ["src/**/*", "tests/**/*"],
    "changedFiles": ["Controllers/*", "Models/*"],
    "excludePatterns": ["bin/**/*", "obj/**/*"]
  }
}
```

#### Environment-based Conditions
```json
{
  "conditions": {
    "environment": ["staging", "production"],
    "branch": ["main", "develop"],
    "buildConfiguration": "Release",
    "testsPassed": true
  }
}
```

#### Time-based Conditions
```json
{
  "conditions": {
    "timeWindow": {
      "start": "09:00",
      "end": "17:00",
      "timezone": "UTC",
      "weekdays": ["monday", "tuesday", "wednesday", "thursday", "friday"]
    },
    "cooldown": "5m",
    "debounce": "30s"
  }
}
```

### 2. Execution Strategies

#### Sequential Execution
```json
{
  "execution": {
    "strategy": "sequential",
    "continueOnFailure": false,
    "timeout": 300
  }
}
```

#### Parallel Execution
```json
{
  "execution": {
    "strategy": "parallel",
    "maxConcurrency": 3,
    "timeout": 300,
    "aggregateResults": true
  }
}
```

#### Conditional Execution
```json
{
  "execution": {
    "strategy": "conditional",
    "conditions": [
      {
        "if": "environment=production",
        "then": "sequential",
        "else": "parallel"
      }
    ]
  }
}
```

### 3. Error Handling
```json
{
  "errorHandling": {
    "strategy": "fail-fast",
    "retryAttempts": 3,
    "retryDelay": "5s",
    "fallbackAction": "warn",
    "notificationChannels": ["email", "slack"]
  }
}
```

## Built-in Hooks

### 1. Code Quality Hooks
```bash
# Pre-commit code formatting
#!/bin/bash
# scripts/pre-commit-format.sh
dotnet format --verify-no-changes
if [ $? -ne 0 ]; then
    echo "Code formatting required. Run 'dotnet format' to fix."
    exit 1
fi
```

### 2. Testing Hooks
```bash
# Post-build testing
#!/bin/bash
# scripts/post-build-test.sh
dotnet test --configuration Release --logger "console;verbosity=minimal"
if [ $? -ne 0 ]; then
    echo "Tests failed. Deployment blocked."
    exit 1
fi
```

### 3. Security Hooks
```bash
# Pre-deploy security scan
#!/bin/bash
# scripts/pre-deploy-security.sh
dotnet list package --vulnerable
if [ $? -ne 0 ]; then
    echo "Vulnerable packages detected. Please update."
    exit 1
fi
```

### 4. Deployment Hooks
```bash
# Post-deploy health check
#!/bin/bash
# scripts/post-deploy-health.sh
curl -f http://localhost:5000/health
if [ $? -ne 0 ]; then
    echo "Health check failed. Rolling back deployment."
    exit 1
fi
```

## Integration with PKS CLI Components

### 1. Agent Integration
Hooks can interact with agents:
```bash
# Trigger agent from hook
pks agent --start test-agent --task "run-integration-tests"

# Check agent status in hook
AGENT_STATUS=$(pks agent --status dev-agent --format json)
if [[ $AGENT_STATUS == *"error"* ]]; then
    echo "Agent in error state, skipping deployment"
    exit 1
fi
```

### 2. GitHub Integration
Hooks can interact with GitHub:
```bash
# Create issue from hook failure
if [ $TEST_RESULT -ne 0 ]; then
    pks github --create-issue "Test Failure in $BRANCH" "Tests failed in commit $COMMIT"
fi

# Update PR status
pks github --update-pr-status $PR_NUMBER "failure" "Tests failed"
```

### 3. MCP Integration
Hooks are exposed through MCP:
```json
{
  "name": "pks_hooks_execute",
  "description": "Execute a project hook",
  "inputSchema": {
    "type": "object",
    "properties": {
      "hookName": {
        "type": "string",
        "description": "Name of the hook to execute"
      },
      "context": {
        "type": "object",
        "description": "Additional context for hook execution"
      }
    }
  }
}
```

### 4. PRD Integration
Hooks can validate against requirements:
```bash
# Validate requirements compliance
pks prd --validate-compliance --exit-code
if [ $? -ne 0 ]; then
    echo "Requirements compliance check failed"
    exit 1
fi
```

## Advanced Hook Features

### 1. Hook Templating
Create reusable hook templates:
```yaml
# templates/test-hook.yaml
name: "test-hook"
description: "Standard testing hook template"
parameters:
  - name: "testType"
    type: "string"
    required: true
  - name: "coverage"
    type: "number"
    default: 80
script: |
  dotnet test --configuration Release
  dotnet test --collect:"XPlat Code Coverage"
  coverage=$(get_coverage)
  if [ $coverage -lt {{coverage}} ]; then
    echo "Coverage $coverage below threshold {{coverage}}"
    exit 1
  fi
```

### 2. Hook Composition
Compose complex hooks from simpler ones:
```json
{
  "compositeHooks": {
    "full-pre-deploy": {
      "hooks": [
        "pre-deploy-test",
        "pre-deploy-security",
        "pre-deploy-performance",
        "pre-deploy-approval"
      ],
      "execution": "sequential",
      "stopOnFailure": true
    }
  }
}
```

### 3. Dynamic Hook Generation
Generate hooks based on project analysis:
```bash
# Auto-generate hooks based on project structure
pks hooks --generate --analyze-project

# Generate hooks for specific technologies
pks hooks --generate --technology react,nodejs

# Generate hooks from CI/CD templates
pks hooks --generate --template github-actions
```

## Monitoring and Metrics

### 1. Hook Execution Monitoring
```bash
# Monitor hook execution
pks hooks --monitor

# View hook execution history
pks hooks --history --limit 50

# Get hook performance metrics
pks hooks --metrics --hook pre-build

# Export hook analytics
pks hooks --analytics --export hooks-analytics.json
```

### 2. Performance Metrics
Track hook performance:
- Execution time and duration
- Success and failure rates
- Resource usage (CPU, memory)
- Dependency analysis
- Bottleneck identification

### 3. Alerting and Notifications
Configure alerts for hook failures:
```json
{
  "alerting": {
    "channels": {
      "email": {
        "enabled": true,
        "recipients": ["team@example.com"],
        "threshold": "error"
      },
      "slack": {
        "enabled": true,
        "webhook": "https://hooks.slack.com/...",
        "threshold": "warning"
      }
    },
    "rules": [
      {
        "hook": "pre-deploy",
        "condition": "failure",
        "action": "immediate"
      },
      {
        "hook": "*",
        "condition": "repeated_failure",
        "threshold": 3,
        "action": "escalate"
      }
    ]
  }
}
```

## Best Practices

### 1. Hook Design
- **Single Responsibility**: Each hook should have a clear, single purpose
- **Idempotency**: Hooks should be safe to run multiple times
- **Fast Execution**: Keep hooks lightweight and fast
- **Error Handling**: Implement proper error handling and recovery

### 2. Testing
- **Test Hooks**: Write tests for your hook scripts
- **Dry Run**: Use dry-run mode for testing
- **Staging Environment**: Test hooks in staging before production
- **Rollback Plans**: Have rollback procedures for hook failures

### 3. Security
- **Secure Scripts**: Ensure hook scripts are secure and validated
- **Access Control**: Limit access to hook configuration
- **Secrets Management**: Handle secrets securely in hooks
- **Audit Logging**: Log all hook executions for audit purposes

### 4. Performance
- **Parallel Execution**: Use parallel execution where appropriate
- **Caching**: Implement caching for expensive operations
- **Resource Limits**: Set appropriate resource limits
- **Monitoring**: Monitor hook performance continuously

## Troubleshooting

### 1. Common Issues

#### Hook Not Executing
```bash
# Check hook configuration
pks hooks --validate-config

# Check hook conditions
pks hooks --debug --execute pre-build --dry-run

# Verify file permissions
ls -la scripts/pre-build.sh
```

#### Hook Execution Failures
```bash
# View detailed logs
pks hooks --logs --hook pre-build --verbose

# Check script syntax
bash -n scripts/pre-build.sh

# Test script manually
./scripts/pre-build.sh
```

#### Performance Issues
```bash
# Profile hook execution
pks hooks --profile --execute pre-build

# Monitor resource usage
pks hooks --monitor --real-time

# Optimize hook configuration
pks hooks --optimize --analyze
```

### 2. Debugging
```bash
# Enable debug mode
export PKS_HOOKS_DEBUG=true

# Capture execution traces
pks hooks --trace --execute pre-build

# Export diagnostics
pks hooks --diagnostics --export hooks-debug.zip
```

The PKS CLI Hooks System provides a powerful, flexible framework for automating development workflows and ensuring quality gates throughout the software development lifecycle. By leveraging the smart dispatcher and rich context system, teams can create sophisticated automation workflows that adapt to their specific needs and environments.