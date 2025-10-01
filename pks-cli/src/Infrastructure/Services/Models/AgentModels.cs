using System.ComponentModel.DataAnnotations;

namespace PKS.CLI.Infrastructure.Services.Models;

/// <summary>
/// Configuration for creating a new agent
/// </summary>
public class AgentConfiguration
{
    /// <summary>
    /// The name of the agent
    /// </summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The type/role of the agent (e.g., automation, monitoring, deployment)
    /// </summary>
    public string Type { get; set; } = "automation";

    /// <summary>
    /// Additional settings for the agent
    /// </summary>
    public Dictionary<string, object> Settings { get; set; } = new();

    /// <summary>
    /// Agent description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Agent knowledge content (persona.md content)
    /// </summary>
    public string Knowledge { get; set; } = string.Empty;

    /// <summary>
    /// Agent persona content (knowledge.md content)
    /// </summary>
    public string Persona { get; set; } = string.Empty;
}

/// <summary>
/// Information about an existing agent
/// </summary>
public class AgentInfo
{
    /// <summary>
    /// Unique identifier for the agent
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the agent
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Type/role of the agent
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the agent
    /// </summary>
    public string Status { get; set; } = "Inactive";

    /// <summary>
    /// Agent description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// When the agent was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last activity timestamp
    /// </summary>
    public DateTime? LastActivity { get; set; }

    /// <summary>
    /// Path to the agent's directory
    /// </summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// Detailed status information for an agent
/// </summary>
public class AgentStatus
{
    /// <summary>
    /// Agent identifier
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Current status (Active, Inactive, Error, etc.)
    /// </summary>
    public string Status { get; set; } = "Inactive";

    /// <summary>
    /// Last activity timestamp
    /// </summary>
    public DateTime? LastActivity { get; set; }

    /// <summary>
    /// Current tasks the agent is working on
    /// </summary>
    public List<string> CurrentTasks { get; set; } = new();

    /// <summary>
    /// Number of messages in the agent's queue
    /// </summary>
    public int MessageQueueCount { get; set; }

    /// <summary>
    /// Agent performance metrics
    /// </summary>
    public Dictionary<string, object> Metrics { get; set; } = new();

    /// <summary>
    /// Additional status details
    /// </summary>
    public string Details { get; set; } = string.Empty;
}

/// <summary>
/// Result of an agent operation
/// </summary>
public class AgentResult
{
    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Result message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Agent ID (for create operations)
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// Additional result data
    /// </summary>
    public Dictionary<string, object> Data { get; set; } = new();

    /// <summary>
    /// Error details if operation failed
    /// </summary>
    public string? ErrorDetails { get; set; }
}

/// <summary>
/// Runtime context for an agent
/// </summary>
public class AgentContext
{
    /// <summary>
    /// Agent information
    /// </summary>
    public AgentInfo Agent { get; set; } = new();

    /// <summary>
    /// Working directory for the agent
    /// </summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Environment variables available to the agent
    /// </summary>
    public Dictionary<string, string> Environment { get; set; } = new();

    /// <summary>
    /// Configuration settings for this execution
    /// </summary>
    public Dictionary<string, object> Configuration { get; set; } = new();

    /// <summary>
    /// Messages in the agent's queue
    /// </summary>
    public List<AgentMessage> MessageQueue { get; set; } = new();
}

/// <summary>
/// Message for inter-agent communication
/// </summary>
public class AgentMessage
{
    /// <summary>
    /// Unique message identifier
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Source agent that sent the message
    /// </summary>
    public string FromAgent { get; set; } = string.Empty;

    /// <summary>
    /// Target agent to receive the message
    /// </summary>
    public string ToAgent { get; set; } = string.Empty;

    /// <summary>
    /// Message type (task, response, notification, etc.)
    /// </summary>
    public string Type { get; set; } = "task";

    /// <summary>
    /// Message content
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Message payload data
    /// </summary>
    public Dictionary<string, object> Data { get; set; } = new();

    /// <summary>
    /// When the message was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Priority level of the message
    /// </summary>
    public int Priority { get; set; } = 5;

    /// <summary>
    /// Whether the message has been processed
    /// </summary>
    public bool Processed { get; set; }
}

/// <summary>
/// Exception thrown when an agent is not found
/// </summary>
public class AgentNotFoundException : Exception
{
    public AgentNotFoundException(string message) : base(message) { }
    public AgentNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown when agent operations fail
/// </summary>
public class AgentOperationException : Exception
{
    public AgentOperationException(string message) : base(message) { }
    public AgentOperationException(string message, Exception innerException) : base(message, innerException) { }
}