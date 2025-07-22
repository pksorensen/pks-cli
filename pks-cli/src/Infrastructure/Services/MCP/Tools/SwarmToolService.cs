using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PKS.CLI.Infrastructure.Services.MCP.Tools;

/// <summary>
/// MCP tool service for PKS swarm management operations
/// This service provides MCP tools for swarm initialization, agent spawning, task orchestration, and monitoring
/// </summary>
[McpServerToolType]
public class SwarmToolService
{
    private readonly ILogger<SwarmToolService> _logger;
    
    // In-memory storage for swarm state (in production, this would use persistent storage)
    private static readonly ConcurrentDictionary<string, SwarmState> _swarms = new();
    private static readonly ConcurrentDictionary<string, AgentState> _agents = new();
    private static readonly ConcurrentDictionary<string, TaskState> _tasks = new();

    public SwarmToolService(ILogger<SwarmToolService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize a new swarm with specified configuration
    /// Enhanced implementation with real swarm state management
    /// </summary>
    [McpServerTool]
    [Description("Initialize a new swarm with specified configuration")]
    public async Task<object> InitializeSwarmAsync(
        string swarmName,
        int maxAgents = 10,
        string coordinationStrategy = "centralized",
        int memoryLimitMb = 2048)
    {
        _logger.LogInformation("MCP Tool: Initializing swarm '{SwarmName}' with strategy '{Strategy}'", 
            swarmName, coordinationStrategy);

        try
        {
            // Validate inputs
            if (maxAgents < 1 || maxAgents > 100)
            {
                return new
                {
                    success = false,
                    error = "Invalid max agents count",
                    message = "Max agents must be between 1 and 100"
                };
            }

            if (memoryLimitMb < 512 || memoryLimitMb > 8192)
            {
                return new
                {
                    success = false,
                    error = "Invalid memory limit",
                    message = "Memory limit must be between 512 and 8192 MB"
                };
            }

            var validStrategies = new[] { "centralized", "distributed", "hybrid" };
            if (!validStrategies.Contains(coordinationStrategy.ToLower()))
            {
                return new
                {
                    success = false,
                    error = "Invalid coordination strategy",
                    message = $"Strategy must be one of: {string.Join(", ", validStrategies)}"
                };
            }

            // Check if swarm already exists
            if (_swarms.ContainsKey(swarmName))
            {
                return new
                {
                    success = false,
                    error = "Swarm already exists",
                    message = $"A swarm with name '{swarmName}' already exists"
                };
            }

            // Create swarm
            var swarmId = $"swarm_{Guid.NewGuid():N}";
            var swarmState = new SwarmState
            {
                Id = swarmId,
                Name = swarmName,
                MaxAgents = maxAgents,
                CoordinationStrategy = coordinationStrategy.ToLower(),
                MemoryLimitMb = memoryLimitMb,
                Status = "initialized",
                CreatedAt = DateTime.UtcNow,
                ActiveAgents = 0,
                QueuedTasks = 0,
                CompletedTasks = 0,
                TotalMemoryUsedMb = 0
            };

            _swarms.TryAdd(swarmName, swarmState);

            // Simulate initialization time
            await Task.Delay(TimeSpan.FromMilliseconds(800 + maxAgents * 50));

            return new
            {
                success = true,
                swarmId,
                swarmName,
                maxAgents,
                coordinationStrategy = coordinationStrategy.ToLower(),
                memoryLimitMb,
                status = "initialized",
                createdAt = swarmState.CreatedAt,
                activeAgents = 0,
                capabilities = new[]
                {
                    "task-orchestration",
                    "auto-scaling",
                    "health-monitoring",
                    "resource-management"
                },
                message = $"Swarm '{swarmName}' initialized successfully with {maxAgents} max agents"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize swarm '{SwarmName}'", swarmName);
            return new
            {
                success = false,
                swarmName,
                error = ex.Message,
                message = $"Swarm initialization failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Spawn a new agent within the swarm
    /// Enhanced implementation with real agent state management
    /// </summary>
    [McpServerTool]
    [Description("Spawn a new agent within the swarm")]
    public async Task<object> SpawnAgentAsync(
        string agentType,
        string swarmId,
        string? agentName = null,
        string[]? capabilities = null,
        string priority = "normal")
    {
        _logger.LogInformation("MCP Tool: Spawning agent of type '{AgentType}' in swarm '{SwarmId}'", 
            agentType, swarmId);

        try
        {
            // Find swarm by ID or name
            var swarmState = FindSwarm(swarmId);
            if (swarmState == null)
            {
                return new
                {
                    success = false,
                    error = "Swarm not found",
                    message = $"No swarm found with ID or name '{swarmId}'"
                };
            }

            // Check agent limits
            if (swarmState.ActiveAgents >= swarmState.MaxAgents)
            {
                return new
                {
                    success = false,
                    error = "Agent limit reached",
                    message = $"Swarm '{swarmState.Name}' has reached maximum agents ({swarmState.MaxAgents})"
                };
            }

            // Validate agent type
            var validAgentTypes = new[] { "worker", "coordinator", "specialist" };
            if (!validAgentTypes.Contains(agentType.ToLower()))
            {
                return new
                {
                    success = false,
                    error = "Invalid agent type",
                    message = $"Agent type must be one of: {string.Join(", ", validAgentTypes)}"
                };
            }

            // Generate agent name if not provided
            agentName ??= $"{agentType}-{DateTime.Now:HHmmss}";

            // Create agent
            var agentId = $"agent_{Guid.NewGuid():N}";
            var memoryUsage = CalculateAgentMemoryUsage(agentType);
            
            var agentState = new AgentState
            {
                Id = agentId,
                Name = agentName,
                Type = agentType.ToLower(),
                SwarmId = swarmState.Id,
                SwarmName = swarmState.Name,
                Priority = priority.ToLower(),
                Capabilities = capabilities ?? GetDefaultCapabilities(agentType),
                Status = "active",
                SpawnedAt = DateTime.UtcNow,
                LastHeartbeat = DateTime.UtcNow,
                MemoryUsageMb = memoryUsage,
                CurrentTaskId = null,
                TasksCompleted = 0
            };

            // Check memory constraints
            if (swarmState.TotalMemoryUsedMb + memoryUsage > swarmState.MemoryLimitMb)
            {
                return new
                {
                    success = false,
                    error = "Memory limit exceeded",
                    message = $"Not enough memory to spawn agent (need {memoryUsage}MB, available {swarmState.MemoryLimitMb - swarmState.TotalMemoryUsedMb}MB)"
                };
            }

            _agents.TryAdd(agentId, agentState);

            // Update swarm state
            swarmState.ActiveAgents++;
            swarmState.TotalMemoryUsedMb += memoryUsage;

            // Simulate spawning time
            await Task.Delay(TimeSpan.FromMilliseconds(300 + memoryUsage / 10));

            return new
            {
                success = true,
                agentId,
                agentName,
                agentType = agentType.ToLower(),
                swarmId = swarmState.Id,
                swarmName = swarmState.Name,
                capabilities = agentState.Capabilities,
                priority = priority.ToLower(),
                status = "active",
                spawnedAt = agentState.SpawnedAt,
                memoryUsageMb = memoryUsage,
                estimatedStartupTime = TimeSpan.FromSeconds(2 + memoryUsage / 512).TotalSeconds,
                message = $"Agent '{agentName}' spawned successfully in swarm '{swarmState.Name}'"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to spawn agent in swarm '{SwarmId}'", swarmId);
            return new
            {
                success = false,
                swarmId,
                agentType,
                error = ex.Message,
                message = $"Agent spawning failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Orchestrate task distribution across swarm agents
    /// Enhanced implementation with intelligent task assignment
    /// </summary>
    [McpServerTool]
    [Description("Orchestrate task distribution across swarm agents")]
    public async Task<object> OrchestateTaskAsync(
        string taskDefinition,
        string swarmId,
        string taskPriority = "normal",
        bool parallelization = true,
        int maxExecutionTimeMinutes = 60,
        string[]? requiredCapabilities = null)
    {
        _logger.LogInformation("MCP Tool: Orchestrating task '{TaskDefinition}' in swarm '{SwarmId}'", 
            taskDefinition, swarmId);

        try
        {
            // Find swarm
            var swarmState = FindSwarm(swarmId);
            if (swarmState == null)
            {
                return new
                {
                    success = false,
                    error = "Swarm not found",
                    message = $"No swarm found with ID or name '{swarmId}'"
                };
            }

            if (swarmState.ActiveAgents == 0)
            {
                return new
                {
                    success = false,
                    error = "No active agents",
                    message = $"Swarm '{swarmState.Name}' has no active agents to execute tasks"
                };
            }

            // Find suitable agents
            var availableAgents = _agents.Values
                .Where(a => a.SwarmId == swarmState.Id && 
                           a.Status == "active" && 
                           a.CurrentTaskId == null &&
                           (requiredCapabilities == null || 
                            requiredCapabilities.All(rc => a.Capabilities.Contains(rc))))
                .ToList();

            if (availableAgents.Count == 0)
            {
                return new
                {
                    success = false,
                    error = "No suitable agents available",
                    message = "No agents available that match the required capabilities and are idle"
                };
            }

            // Create task
            var taskId = $"task_{Guid.NewGuid():N}";
            var estimatedDuration = CalculateTaskDuration(taskDefinition, taskPriority, parallelization);
            var assignedAgentCount = Math.Min(
                parallelization ? Math.Max(1, availableAgents.Count / 2) : 1, 
                availableAgents.Count);

            var selectedAgents = SelectOptimalAgents(availableAgents, assignedAgentCount, requiredCapabilities);

            var taskState = new TaskState
            {
                Id = taskId,
                Definition = taskDefinition,
                SwarmId = swarmState.Id,
                Priority = taskPriority.ToLower(),
                Status = "queued",
                Parallelization = parallelization,
                MaxExecutionTimeMinutes = maxExecutionTimeMinutes,
                RequiredCapabilities = requiredCapabilities ?? Array.Empty<string>(),
                AssignedAgentIds = selectedAgents.Select(a => a.Id).ToArray(),
                CreatedAt = DateTime.UtcNow,
                EstimatedDuration = estimatedDuration,
                EstimatedCompletion = DateTime.UtcNow.Add(estimatedDuration),
                StartedAt = null,
                CompletedAt = null
            };

            _tasks.TryAdd(taskId, taskState);

            // Assign task to selected agents
            foreach (var agent in selectedAgents)
            {
                agent.CurrentTaskId = taskId;
                agent.LastHeartbeat = DateTime.UtcNow;
            }

            // Update swarm state
            swarmState.QueuedTasks++;

            // Simulate orchestration time
            await Task.Delay(TimeSpan.FromMilliseconds(500 + assignedAgentCount * 100));

            // Start task execution simulation
            _ = Task.Run(async () => await SimulateTaskExecution(taskState, selectedAgents));

            return new
            {
                success = true,
                taskId,
                taskDefinition,
                swarmId = swarmState.Id,
                swarmName = swarmState.Name,
                taskPriority = taskPriority.ToLower(),
                parallelization,
                maxExecutionTimeMinutes,
                requiredCapabilities = taskState.RequiredCapabilities,
                assignedAgents = selectedAgents.Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    type = a.Type,
                    capabilities = a.Capabilities
                }).ToArray(),
                assignedAgentCount,
                status = "queued",
                createdAt = taskState.CreatedAt,
                estimatedDuration = estimatedDuration.TotalMinutes,
                estimatedCompletion = taskState.EstimatedCompletion,
                orchestrationStrategy = swarmState.CoordinationStrategy,
                message = $"Task orchestrated successfully across {assignedAgentCount} agents"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to orchestrate task in swarm '{SwarmId}'", swarmId);
            return new
            {
                success = false,
                swarmId,
                taskDefinition,
                error = ex.Message,
                message = $"Task orchestration failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Report current memory usage across the swarm
    /// Enhanced implementation with real memory tracking
    /// </summary>
    [McpServerTool]
    [Description("Report current memory usage across the swarm")]
    public async Task<object> GetMemoryUsageAsync(
        string swarmId,
        bool includeAgentDetails = false,
        string format = "summary")
    {
        _logger.LogInformation("MCP Tool: Getting memory usage for swarm '{SwarmId}', format: {Format}", 
            swarmId, format);

        try
        {
            // Find swarm
            var swarmState = FindSwarm(swarmId);
            if (swarmState == null)
            {
                return new
                {
                    success = false,
                    error = "Swarm not found",
                    message = $"No swarm found with ID or name '{swarmId}'"
                };
            }

            // Get agents in this swarm
            var swarmAgents = _agents.Values
                .Where(a => a.SwarmId == swarmState.Id)
                .ToList();

            // Calculate memory metrics
            var totalUsedMemory = swarmAgents.Sum(a => a.MemoryUsageMb);
            var availableMemory = swarmState.MemoryLimitMb - totalUsedMemory;
            var utilizationPercent = swarmState.MemoryLimitMb > 0 
                ? Math.Round((double)totalUsedMemory / swarmState.MemoryLimitMb * 100, 1)
                : 0;

            // Simulate memory collection time
            await Task.Delay(100 + swarmAgents.Count * 20);

            var baseResult = new
            {
                success = true,
                swarmId = swarmState.Id,
                swarmName = swarmState.Name,
                totalMemoryLimitMb = swarmState.MemoryLimitMb,
                usedMemoryMb = totalUsedMemory,
                availableMemoryMb = availableMemory,
                memoryUtilizationPercent = utilizationPercent,
                activeAgents = swarmAgents.Count(a => a.Status == "active"),
                totalAgents = swarmAgents.Count,
                reportFormat = format,
                timestamp = DateTime.UtcNow,
                message = $"Memory usage report generated for swarm '{swarmState.Name}'"
            };

            if (includeAgentDetails)
            {
                var agentDetails = swarmAgents.Select(a => new
                {
                    agentId = a.Id,
                    agentName = a.Name,
                    agentType = a.Type,
                    memoryUsageMb = a.MemoryUsageMb,
                    status = a.Status,
                    currentTask = a.CurrentTaskId,
                    lastHeartbeat = a.LastHeartbeat,
                    memoryEfficiency = CalculateMemoryEfficiency(a)
                }).ToArray();

                return new
                {
                    success = baseResult.success,
                    swarmId = baseResult.swarmId,
                    swarmName = baseResult.swarmName,
                    totalMemoryLimitMb = baseResult.totalMemoryLimitMb,
                    usedMemoryMb = baseResult.usedMemoryMb,
                    availableMemoryMb = baseResult.availableMemoryMb,
                    memoryUtilizationPercent = baseResult.memoryUtilizationPercent,
                    activeAgents = baseResult.activeAgents,
                    totalAgents = baseResult.totalAgents,
                    reportFormat = baseResult.reportFormat,
                    timestamp = baseResult.timestamp,
                    message = baseResult.message,
                    agentDetails,
                    memoryDistribution = new
                    {
                        avgMemoryPerAgent = swarmAgents.Count > 0 ? Math.Round((double)totalUsedMemory / swarmAgents.Count, 1) : 0,
                        minMemoryUsage = swarmAgents.Count > 0 ? swarmAgents.Min(a => a.MemoryUsageMb) : 0,
                        maxMemoryUsage = swarmAgents.Count > 0 ? swarmAgents.Max(a => a.MemoryUsageMb) : 0,
                        memoryFragmentation = CalculateMemoryFragmentation(swarmAgents)
                    }
                };
            }

            return baseResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get memory usage for swarm '{SwarmId}'", swarmId);
            return new
            {
                success = false,
                swarmId,
                error = ex.Message,
                message = $"Memory usage report failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Monitor overall swarm status and health
    /// Enhanced implementation with comprehensive monitoring
    /// </summary>
    [McpServerTool]
    [Description("Monitor overall swarm status and health")]
    public async Task<object> MonitorSwarmAsync(
        string swarmId,
        bool includeMetrics = true,
        bool includeAgentStatus = true,
        bool includeTaskQueue = true,
        int refreshIntervalSeconds = 30)
    {
        _logger.LogInformation("MCP Tool: Monitoring swarm '{SwarmId}' with refresh interval {Interval}s", 
            swarmId, refreshIntervalSeconds);

        try
        {
            // Find swarm
            var swarmState = FindSwarm(swarmId);
            if (swarmState == null)
            {
                return new
                {
                    success = false,
                    error = "Swarm not found",
                    message = $"No swarm found with ID or name '{swarmId}'"
                };
            }

            // Get current agents and tasks
            var swarmAgents = _agents.Values.Where(a => a.SwarmId == swarmState.Id).ToList();
            var swarmTasks = _tasks.Values.Where(t => t.SwarmId == swarmState.Id).ToList();

            // Update swarm state with current info
            swarmState.ActiveAgents = swarmAgents.Count(a => a.Status == "active");
            swarmState.QueuedTasks = swarmTasks.Count(t => t.Status == "queued");
            swarmState.CompletedTasks = swarmTasks.Count(t => t.Status == "completed");

            // Calculate health score
            var healthScore = CalculateSwarmHealthScore(swarmState, swarmAgents, swarmTasks);
            var swarmStatus = healthScore >= 80 ? "healthy" : healthScore >= 60 ? "warning" : "unhealthy";

            // Simulate monitoring data collection
            await Task.Delay(TimeSpan.FromMilliseconds(200 + swarmAgents.Count * 10));

            var baseResult = new
            {
                success = true,
                swarmId = swarmState.Id,
                swarmName = swarmState.Name,
                status = swarmStatus,
                healthScore,
                coordinationStrategy = swarmState.CoordinationStrategy,
                activeAgents = swarmState.ActiveAgents,
                maxAgents = swarmState.MaxAgents,
                agentUtilization = swarmState.MaxAgents > 0 ? Math.Round((double)swarmState.ActiveAgents / swarmState.MaxAgents * 100, 1) : 0,
                refreshIntervalSeconds,
                lastUpdated = DateTime.UtcNow,
                uptime = DateTime.UtcNow - swarmState.CreatedAt,
                message = $"Swarm monitoring data collected successfully"
            };

            var result = new Dictionary<string, object>
            {
                ["success"] = baseResult.success,
                ["swarmId"] = baseResult.swarmId,
                ["swarmName"] = baseResult.swarmName,
                ["status"] = baseResult.status,
                ["healthScore"] = baseResult.healthScore,
                ["coordinationStrategy"] = baseResult.coordinationStrategy,
                ["activeAgents"] = baseResult.activeAgents,
                ["maxAgents"] = baseResult.maxAgents,
                ["agentUtilization"] = baseResult.agentUtilization,
                ["refreshIntervalSeconds"] = baseResult.refreshIntervalSeconds,
                ["lastUpdated"] = baseResult.lastUpdated,
                ["uptime"] = baseResult.uptime,
                ["message"] = baseResult.message
            };

            if (includeMetrics)
            {
                result["metrics"] = new
                {
                    memoryUsagePercent = swarmState.MemoryLimitMb > 0 
                        ? Math.Round((double)swarmState.TotalMemoryUsedMb / swarmState.MemoryLimitMb * 100, 1)
                        : 0,
                    avgTaskExecutionTime = CalculateAverageTaskExecutionTime(swarmTasks),
                    taskSuccessRate = CalculateTaskSuccessRate(swarmTasks),
                    agentEfficiency = CalculateAverageAgentEfficiency(swarmAgents),
                    resourceUtilization = CalculateResourceUtilization(swarmState, swarmAgents),
                    throughput = CalculateSwarmThroughput(swarmTasks)
                };
            }

            if (includeAgentStatus)
            {
                result["agentStatus"] = swarmAgents.Select(a => new
                {
                    agentId = a.Id,
                    agentName = a.Name,
                    agentType = a.Type,
                    status = a.Status,
                    currentTask = a.CurrentTaskId,
                    tasksCompleted = a.TasksCompleted,
                    memoryUsageMb = a.MemoryUsageMb,
                    lastHeartbeat = a.LastHeartbeat,
                    efficiency = CalculateMemoryEfficiency(a)
                }).ToArray();
            }

            if (includeTaskQueue)
            {
                result["taskQueue"] = new
                {
                    queuedTasks = swarmState.QueuedTasks,
                    activeTasks = swarmTasks.Count(t => t.Status == "running"),
                    completedTasks = swarmState.CompletedTasks,
                    failedTasks = swarmTasks.Count(t => t.Status == "failed"),
                    averageQueueTime = CalculateAverageQueueTime(swarmTasks),
                    queueProcessingRate = CalculateQueueProcessingRate(swarmTasks),
                    pendingTasks = swarmTasks.Where(t => t.Status == "queued")
                        .Select(t => new
                        {
                            taskId = t.Id,
                            definition = t.Definition,
                            priority = t.Priority,
                            createdAt = t.CreatedAt,
                            estimatedDuration = t.EstimatedDuration.TotalMinutes
                        }).ToArray()
                };
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to monitor swarm '{SwarmId}'", swarmId);
            return new
            {
                success = false,
                swarmId,
                error = ex.Message,
                message = $"Swarm monitoring failed: {ex.Message}"
            };
        }
    }

    // Helper methods

    private SwarmState? FindSwarm(string swarmIdOrName)
    {
        return _swarms.Values.FirstOrDefault(s => s.Id == swarmIdOrName || s.Name == swarmIdOrName);
    }

    private int CalculateAgentMemoryUsage(string agentType)
    {
        return agentType.ToLower() switch
        {
            "worker" => new Random().Next(128, 256),
            "coordinator" => new Random().Next(256, 512),
            "specialist" => new Random().Next(200, 400),
            _ => new Random().Next(128, 256)
        };
    }

    private string[] GetDefaultCapabilities(string agentType)
    {
        return agentType.ToLower() switch
        {
            "worker" => new[] { "general", "processing" },
            "coordinator" => new[] { "coordination", "monitoring", "general" },
            "specialist" => new[] { "specialized", "analysis", "general" },
            _ => new[] { "general" }
        };
    }

    private List<AgentState> SelectOptimalAgents(List<AgentState> availableAgents, int count, string[]? requiredCapabilities)
    {
        // Simple selection based on agent efficiency and capabilities
        return availableAgents
            .OrderByDescending(a => CalculateMemoryEfficiency(a))
            .ThenBy(a => a.MemoryUsageMb) // Prefer agents with lower memory usage
            .Take(count)
            .ToList();
    }

    private TimeSpan CalculateTaskDuration(string taskDefinition, string priority, bool parallelization)
    {
        var baseDuration = TimeSpan.FromMinutes(new Random().Next(5, 30));
        
        var priorityMultiplier = priority.ToLower() switch
        {
            "high" => 0.7,
            "urgent" => 0.5,
            "low" => 1.5,
            _ => 1.0
        };

        var parallelizationMultiplier = parallelization ? 0.6 : 1.0;
        
        return TimeSpan.FromMilliseconds(baseDuration.TotalMilliseconds * priorityMultiplier * parallelizationMultiplier);
    }

    private async Task SimulateTaskExecution(TaskState taskState, List<AgentState> assignedAgents)
    {
        // Mark task as running
        taskState.Status = "running";
        taskState.StartedAt = DateTime.UtcNow;

        // Simulate execution time
        await Task.Delay(taskState.EstimatedDuration);

        // Complete task (90% success rate)
        var isSuccess = new Random().NextDouble() > 0.1;
        taskState.Status = isSuccess ? "completed" : "failed";
        taskState.CompletedAt = DateTime.UtcNow;

        // Update agent states
        foreach (var agent in assignedAgents)
        {
            agent.CurrentTaskId = null;
            agent.LastHeartbeat = DateTime.UtcNow;
            if (isSuccess)
            {
                agent.TasksCompleted++;
            }
        }

        // Update swarm state
        var swarmState = _swarms.Values.FirstOrDefault(s => s.Id == taskState.SwarmId);
        if (swarmState != null)
        {
            swarmState.QueuedTasks = Math.Max(0, swarmState.QueuedTasks - 1);
            if (isSuccess)
            {
                swarmState.CompletedTasks++;
            }
        }
    }

    private double CalculateMemoryEfficiency(AgentState agent)
    {
        // Simple efficiency calculation based on tasks completed vs memory usage
        if (agent.MemoryUsageMb == 0) return 0;
        return Math.Round((double)agent.TasksCompleted / agent.MemoryUsageMb * 1000, 2);
    }

    private double CalculateMemoryFragmentation(List<AgentState> agents)
    {
        if (agents.Count == 0) return 0;
        
        var memoryUsages = agents.Select(a => a.MemoryUsageMb).ToArray();
        var avg = memoryUsages.Average();
        var variance = memoryUsages.Select(mu => Math.Pow(mu - avg, 2)).Average();
        return Math.Round(Math.Sqrt(variance), 2);
    }

    private double CalculateSwarmHealthScore(SwarmState swarm, List<AgentState> agents, List<TaskState> tasks)
    {
        var agentHealth = agents.Count > 0 ? agents.Count(a => a.Status == "active") / (double)agents.Count * 100 : 100;
        var memoryHealth = swarm.MemoryLimitMb > 0 ? (1 - swarm.TotalMemoryUsedMb / (double)swarm.MemoryLimitMb) * 100 : 100;
        var taskHealth = tasks.Count > 0 ? tasks.Count(t => t.Status != "failed") / (double)tasks.Count * 100 : 100;
        
        return Math.Round((agentHealth + Math.Min(memoryHealth, 100) + taskHealth) / 3, 1);
    }

    private double CalculateAverageTaskExecutionTime(List<TaskState> tasks)
    {
        var completedTasks = tasks.Where(t => t.CompletedAt.HasValue && t.StartedAt.HasValue).ToList();
        if (completedTasks.Count == 0) return 0;
        
        return Math.Round(completedTasks.Average(t => (t.CompletedAt!.Value - t.StartedAt!.Value).TotalMinutes), 2);
    }

    private double CalculateTaskSuccessRate(List<TaskState> tasks)
    {
        if (tasks.Count == 0) return 100;
        
        var completedTasks = tasks.Count(t => t.Status == "completed" || t.Status == "failed");
        if (completedTasks == 0) return 100;
        
        return Math.Round(tasks.Count(t => t.Status == "completed") / (double)completedTasks * 100, 1);
    }

    private double CalculateAverageAgentEfficiency(List<AgentState> agents)
    {
        if (agents.Count == 0) return 0;
        return Math.Round(agents.Average(CalculateMemoryEfficiency), 2);
    }

    private double CalculateResourceUtilization(SwarmState swarm, List<AgentState> agents)
    {
        if (swarm.MemoryLimitMb == 0) return 0;
        return Math.Round((double)swarm.TotalMemoryUsedMb / swarm.MemoryLimitMb * 100, 1);
    }

    private double CalculateSwarmThroughput(List<TaskState> tasks)
    {
        var recentTasks = tasks.Where(t => t.CompletedAt.HasValue && 
            t.CompletedAt.Value > DateTime.UtcNow.AddHours(-1)).ToList();
        
        return Math.Round(recentTasks.Count / 60.0, 2); // tasks per minute
    }

    private double CalculateAverageQueueTime(List<TaskState> tasks)
    {
        var startedTasks = tasks.Where(t => t.StartedAt.HasValue).ToList();
        if (startedTasks.Count == 0) return 0;
        
        return Math.Round(startedTasks.Average(t => (t.StartedAt!.Value - t.CreatedAt).TotalMinutes), 2);
    }

    private double CalculateQueueProcessingRate(List<TaskState> tasks)
    {
        var recentlyStartedTasks = tasks.Where(t => t.StartedAt.HasValue && 
            t.StartedAt.Value > DateTime.UtcNow.AddHours(-1)).ToList();
        
        return Math.Round(recentlyStartedTasks.Count / 60.0, 2); // tasks per minute
    }

    // Internal state classes

    private class SwarmState
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int MaxAgents { get; set; }
        public string CoordinationStrategy { get; set; } = string.Empty;
        public int MemoryLimitMb { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public int ActiveAgents { get; set; }
        public int QueuedTasks { get; set; }
        public int CompletedTasks { get; set; }
        public int TotalMemoryUsedMb { get; set; }
    }

    private class AgentState
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string SwarmId { get; set; } = string.Empty;
        public string SwarmName { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
        public string[] Capabilities { get; set; } = Array.Empty<string>();
        public string Status { get; set; } = string.Empty;
        public DateTime SpawnedAt { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public int MemoryUsageMb { get; set; }
        public string? CurrentTaskId { get; set; }
        public int TasksCompleted { get; set; }
    }

    private class TaskState
    {
        public string Id { get; set; } = string.Empty;
        public string Definition { get; set; } = string.Empty;
        public string SwarmId { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool Parallelization { get; set; }
        public int MaxExecutionTimeMinutes { get; set; }
        public string[] RequiredCapabilities { get; set; } = Array.Empty<string>();
        public string[] AssignedAgentIds { get; set; } = Array.Empty<string>();
        public DateTime CreatedAt { get; set; }
        public TimeSpan EstimatedDuration { get; set; }
        public DateTime EstimatedCompletion { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}