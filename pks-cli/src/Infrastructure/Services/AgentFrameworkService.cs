using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services.Models;

namespace PKS.CLI.Infrastructure.Services;

/// <summary>
/// Implementation of the agent framework service
/// </summary>
public class AgentFrameworkService : IAgentFrameworkService
{
    private readonly ILogger<AgentFrameworkService> _logger;
    private readonly Dictionary<string, AgentInfo> _agents = new();
    private readonly Dictionary<string, AgentStatus> _agentStatuses = new();
    private readonly Dictionary<string, List<AgentMessage>> _messageQueues = new();
    private static readonly object _lock = new();

    public AgentFrameworkService(ILogger<AgentFrameworkService> logger)
    {
        _logger = logger;
    }

    public async Task<AgentResult> CreateAgentAsync(AgentConfiguration configuration)
    {
        try
        {
            _logger.LogInformation("Creating agent: {AgentName}", configuration.Name);

            // Validate configuration
            if (string.IsNullOrWhiteSpace(configuration.Name))
            {
                return new AgentResult
                {
                    Success = false,
                    Message = "Agent name is required"
                };
            }

            // Generate unique agent ID
            var agentId = GenerateAgentId(configuration.Name);

            // Check if agent already exists
            if (_agents.ContainsKey(agentId))
            {
                return new AgentResult
                {
                    Success = false,
                    Message = $"Agent with name '{configuration.Name}' already exists"
                };
            }

            // Create agent directory structure
            var agentsDir = GetAgentsDirectory();
            var agentDir = Path.Combine(agentsDir, configuration.Name);
            
            await CreateAgentDirectoryAsync(agentDir, configuration);

            // Create agent info
            var agentInfo = new AgentInfo
            {
                Id = agentId,
                Name = configuration.Name,
                Type = configuration.Type,
                Description = configuration.Description,
                Status = "Inactive",
                CreatedAt = DateTime.UtcNow,
                Path = agentDir
            };

            // Create agent status
            var agentStatus = new AgentStatus
            {
                Id = agentId,
                Status = "Inactive",
                MessageQueueCount = 0
            };

            lock (_lock)
            {
                _agents[agentId] = agentInfo;
                _agentStatuses[agentId] = agentStatus;
                _messageQueues[agentId] = new List<AgentMessage>();
            }

            _logger.LogInformation("Agent created successfully: {AgentId}", agentId);

            return new AgentResult
            {
                Success = true,
                Message = "Agent created successfully",
                AgentId = agentId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create agent: {AgentName}", configuration.Name);
            return new AgentResult
            {
                Success = false,
                Message = "Failed to create agent: " + ex.Message,
                ErrorDetails = ex.ToString()
            };
        }
    }

    public async Task<List<AgentInfo>> ListAgentsAsync()
    {
        await Task.Delay(10); // Simulate async operation

        // Load agents from file system if empty
        if (!_agents.Any())
        {
            await LoadAgentsFromFileSystemAsync();
        }

        lock (_lock)
        {
            return _agents.Values.OrderBy(a => a.Name).ToList();
        }
    }

    public async Task<AgentStatus> GetAgentStatusAsync(string agentId)
    {
        await Task.Delay(10); // Simulate async operation

        lock (_lock)
        {
            if (!_agentStatuses.TryGetValue(agentId, out var status))
            {
                throw new AgentNotFoundException($"Agent '{agentId}' not found");
            }

            // Update message queue count
            if (_messageQueues.TryGetValue(agentId, out var messages))
            {
                status.MessageQueueCount = messages.Count(m => !m.Processed);
            }

            return status;
        }
    }

    public async Task<AgentResult> StartAgentAsync(string agentId)
    {
        await Task.Delay(100); // Simulate startup time

        lock (_lock)
        {
            if (!_agents.TryGetValue(agentId, out var agent))
            {
                return new AgentResult
                {
                    Success = false,
                    Message = $"Agent '{agentId}' not found"
                };
            }

            if (_agentStatuses.TryGetValue(agentId, out var status))
            {
                if (status.Status == "Active")
                {
                    return new AgentResult
                    {
                        Success = false,
                        Message = "Agent is already running"
                    };
                }

                status.Status = "Active";
                status.LastActivity = DateTime.UtcNow;
                agent.Status = "Active";
                agent.LastActivity = DateTime.UtcNow;
            }
        }

        _logger.LogInformation("Agent started: {AgentId}", agentId);

        return new AgentResult
        {
            Success = true,
            Message = "Agent started successfully",
            AgentId = agentId
        };
    }

    public async Task<AgentResult> StopAgentAsync(string agentId)
    {
        await Task.Delay(50); // Simulate stop time

        lock (_lock)
        {
            if (!_agents.TryGetValue(agentId, out var agent))
            {
                return new AgentResult
                {
                    Success = false,
                    Message = $"Agent '{agentId}' not found"
                };
            }

            if (_agentStatuses.TryGetValue(agentId, out var status))
            {
                status.Status = "Inactive";
                status.LastActivity = DateTime.UtcNow;
                agent.Status = "Inactive";
                agent.LastActivity = DateTime.UtcNow;
            }
        }

        _logger.LogInformation("Agent stopped: {AgentId}", agentId);

        return new AgentResult
        {
            Success = true,
            Message = "Agent stopped successfully",
            AgentId = agentId
        };
    }

    public async Task<bool> RemoveAgentAsync(string agentId)
    {
        try
        {
            lock (_lock)
            {
                if (!_agents.TryGetValue(agentId, out var agent))
                {
                    return false;
                }

                // Remove agent directory if it exists
                if (Directory.Exists(agent.Path))
                {
                    Directory.Delete(agent.Path, true);
                }

                // Remove from memory
                _agents.Remove(agentId);
                _agentStatuses.Remove(agentId);
                _messageQueues.Remove(agentId);
            }

            _logger.LogInformation("Agent removed: {AgentId}", agentId);
            await Task.Delay(10); // Simulate async operation
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove agent: {AgentId}", agentId);
            return false;
        }
    }

    public async Task<bool> SendMessageAsync(AgentMessage message)
    {
        await Task.Delay(10); // Simulate async operation

        lock (_lock)
        {
            if (!_messageQueues.TryGetValue(message.ToAgent, out var queue))
            {
                _messageQueues[message.ToAgent] = queue = new List<AgentMessage>();
            }

            queue.Add(message);
            return true;
        }
    }

    public async Task<List<AgentMessage>> GetMessageQueueAsync(string agentId)
    {
        await Task.Delay(10); // Simulate async operation

        lock (_lock)
        {
            if (_messageQueues.TryGetValue(agentId, out var queue))
            {
                return queue.ToList();
            }
            return new List<AgentMessage>();
        }
    }

    public async Task<AgentConfiguration> LoadConfigurationAsync(string configFilePath)
    {
        if (!File.Exists(configFilePath))
        {
            throw new FileNotFoundException($"Configuration file not found: {configFilePath}");
        }

        var jsonContent = await File.ReadAllTextAsync(configFilePath);
        var config = JsonSerializer.Deserialize<AgentConfiguration>(jsonContent, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return config ?? new AgentConfiguration();
    }

    public string GetAgentsDirectory()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var pksDir = Path.Combine(currentDir, ".pks");
        return Path.Combine(pksDir, "agents");
    }

    public async Task<bool> InitializeAgentsInfrastructureAsync(string projectPath)
    {
        try
        {
            var pksDir = Path.Combine(projectPath, ".pks");
            var agentsDir = Path.Combine(pksDir, "agents");

            if (!Directory.Exists(pksDir))
            {
                Directory.CreateDirectory(pksDir);
            }

            if (!Directory.Exists(agentsDir))
            {
                Directory.CreateDirectory(agentsDir);
            }

            // Create a readme for the agents directory
            var readmePath = Path.Combine(agentsDir, "README.md");
            if (!File.Exists(readmePath))
            {
                var readmeContent = @"# Agents Directory

This directory contains AI agents for your project. Each agent has its own subdirectory with:

- `knowledge.md` - Agent-specific knowledge and documentation
- `persona.md` - Agent personality and behavior configuration

## Agent Structure

```
.pks/agents/
├── agent-name/
│   ├── knowledge.md
│   └── persona.md
```

## Managing Agents

Use the PKS CLI to manage agents:

```bash
# Create a new agent
pks agent create my-agent --type automation

# List all agents
pks agent list

# Get agent status
pks agent status my-agent

# Start/stop agents
pks agent start my-agent
pks agent stop my-agent

# Remove an agent
pks agent remove my-agent
```
";
                await File.WriteAllTextAsync(readmePath, readmeContent);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize agents infrastructure");
            return false;
        }
    }

    private string GenerateAgentId(string agentName)
    {
        // Create a simple ID based on the agent name and timestamp
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hash = agentName.GetHashCode().ToString("X");
        return $"{agentName.ToLower().Replace(" ", "-")}-{hash}-{timestamp}";
    }

    private async Task CreateAgentDirectoryAsync(string agentDir, AgentConfiguration configuration)
    {
        Directory.CreateDirectory(agentDir);

        // Create knowledge.md
        var knowledgePath = Path.Combine(agentDir, "knowledge.md");
        var knowledgeContent = !string.IsNullOrWhiteSpace(configuration.Knowledge) 
            ? configuration.Knowledge 
            : GenerateDefaultKnowledge(configuration);
        
        await File.WriteAllTextAsync(knowledgePath, knowledgeContent);

        // Create persona.md
        var personaPath = Path.Combine(agentDir, "persona.md");
        var personaContent = !string.IsNullOrWhiteSpace(configuration.Persona) 
            ? configuration.Persona 
            : GenerateDefaultPersona(configuration);
        
        await File.WriteAllTextAsync(personaPath, personaContent);
    }

    private string GenerateDefaultKnowledge(AgentConfiguration configuration)
    {
        return $@"# {configuration.Name} Agent Knowledge

## Agent Information

- **Name**: {configuration.Name}
- **Type**: {configuration.Type}
- **Description**: {configuration.Description}
- **Created**: {DateTime.UtcNow:yyyy-MM-dd}

## Knowledge Base

This file contains the knowledge base for the {configuration.Name} agent.

### Capabilities

Add information about what this agent can do:
- Task automation
- Monitoring and alerting
- Data processing
- Integration with external systems

### Tools and Resources

List the tools and resources this agent has access to:
- CLI tools
- APIs
- File system access
- Environment variables

### Configuration

Document any configuration options or settings:
- Environment-specific settings
- Performance tuning
- Security considerations

## Usage Examples

Provide examples of how to interact with this agent:

```bash
# Example commands or tasks
pks agent start {configuration.Name}
```

## Troubleshooting

Common issues and solutions:
- Connection problems
- Performance issues
- Error recovery
";
    }

    private string GenerateDefaultPersona(AgentConfiguration configuration)
    {
        return $@"# {configuration.Name} Agent Persona

## Personality

Define the agent's personality and behavior patterns:

- **Communication Style**: Professional, helpful, and informative
- **Approach**: Methodical and thorough
- **Error Handling**: Graceful degradation with clear error messages
- **Interaction Pattern**: Responsive and proactive

## Role Definition

As a {configuration.Type} agent, this agent should:

1. **Primary Responsibilities**:
   - Execute {configuration.Type} tasks efficiently
   - Monitor system health and performance
   - Provide status updates and alerts
   - Maintain logs and documentation

2. **Secondary Responsibilities**:
   - Assist other agents when needed
   - Learn from interactions and improve performance
   - Provide recommendations for optimization

## Behavioral Guidelines

- Always validate inputs before processing
- Provide clear, actionable feedback
- Handle errors gracefully and informatively
- Maintain security and best practices
- Work collaboratively with other agents

## Communication Patterns

### Inter-Agent Communication

- Use structured message formats
- Prioritize messages appropriately
- Acknowledge receipt of important messages
- Escalate issues when necessary

### Human Interaction

- Provide clear status updates
- Ask for clarification when needed
- Offer suggestions and alternatives
- Document decisions and actions

## Learning and Adaptation

This agent should continuously improve by:
- Analyzing successful task completions
- Learning from errors and failures
- Adapting to changing requirements
- Incorporating user feedback

## Context Awareness

The agent should be aware of:
- Current project state
- Available resources
- Other active agents
- System constraints and limitations
";
    }

    private async Task LoadAgentsFromFileSystemAsync()
    {
        try
        {
            var agentsDir = GetAgentsDirectory();
            if (!Directory.Exists(agentsDir))
            {
                return;
            }

            var agentDirs = Directory.GetDirectories(agentsDir);
            foreach (var agentDir in agentDirs)
            {
                var agentName = Path.GetFileName(agentDir);
                var knowledgePath = Path.Combine(agentDir, "knowledge.md");
                var personaPath = Path.Combine(agentDir, "persona.md");

                if (File.Exists(knowledgePath) && File.Exists(personaPath))
                {
                    var agentId = GenerateAgentId(agentName);
                    var agentInfo = new AgentInfo
                    {
                        Id = agentId,
                        Name = agentName,
                        Type = "automation", // Default type
                        Status = "Inactive",
                        CreatedAt = Directory.GetCreationTime(agentDir),
                        Path = agentDir
                    };

                    var agentStatus = new AgentStatus
                    {
                        Id = agentId,
                        Status = "Inactive",
                        MessageQueueCount = 0
                    };

                    lock (_lock)
                    {
                        _agents[agentId] = agentInfo;
                        _agentStatuses[agentId] = agentStatus;
                        _messageQueues[agentId] = new List<AgentMessage>();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load agents from file system");
        }
    }
}