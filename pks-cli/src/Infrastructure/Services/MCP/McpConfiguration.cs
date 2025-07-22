namespace PKS.CLI.Infrastructure.Services.MCP;

/// <summary>
/// Configuration options for MCP hosting service
/// </summary>
public class McpConfiguration
{
    /// <summary>
    /// Configuration section name in appsettings
    /// </summary>
    public const string SectionName = "Mcp";

    /// <summary>
    /// Enable the new SDK-based MCP hosting service instead of the legacy implementation
    /// </summary>
    public bool UseSdkHosting { get; set; } = true;

    /// <summary>
    /// Default transport mode for MCP server
    /// </summary>
    public string DefaultTransport { get; set; } = "stdio";

    /// <summary>
    /// Default port for HTTP/SSE transports
    /// </summary>
    public int DefaultPort { get; set; } = 3000;

    /// <summary>
    /// Enable debug logging for MCP operations
    /// </summary>
    public bool EnableDebugLogging { get; set; } = false;

    /// <summary>
    /// Maximum number of concurrent MCP connections
    /// </summary>
    public int MaxConnections { get; set; } = 10;

    /// <summary>
    /// Timeout for MCP operations in milliseconds
    /// </summary>
    public int OperationTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Enable tool execution metrics collection
    /// </summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// Enable automatic tool discovery from loaded assemblies
    /// </summary>
    public bool EnableAutoToolDiscovery { get; set; } = true;

    /// <summary>
    /// Enable automatic resource discovery from loaded assemblies
    /// </summary>
    public bool EnableAutoResourceDiscovery { get; set; } = true;

    /// <summary>
    /// List of tool categories to enable (empty means all categories)
    /// </summary>
    public string[] EnabledToolCategories { get; set; } = Array.Empty<string>();

    /// <summary>
    /// List of tool names to disable explicitly
    /// </summary>
    public string[] DisabledTools { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Security settings for MCP operations
    /// </summary>
    public McpSecurityConfiguration Security { get; set; } = new();
}

/// <summary>
/// Security configuration for MCP operations
/// </summary>
public class McpSecurityConfiguration
{
    /// <summary>
    /// Enable authentication for HTTP/SSE transports
    /// </summary>
    public bool EnableAuthentication { get; set; } = false;

    /// <summary>
    /// Enable authorization checks for tool execution
    /// </summary>
    public bool EnableAuthorization { get; set; } = false;

    /// <summary>
    /// Enable rate limiting for MCP operations
    /// </summary>
    public bool EnableRateLimiting { get; set; } = false;

    /// <summary>
    /// Maximum number of requests per minute per client
    /// </summary>
    public int RateLimitPerMinute { get; set; } = 60;

    /// <summary>
    /// Enable input validation for tool parameters
    /// </summary>
    public bool EnableInputValidation { get; set; } = true;

    /// <summary>
    /// Enable output sanitization for tool results
    /// </summary>
    public bool EnableOutputSanitization { get; set; } = false;

    /// <summary>
    /// List of allowed client origins for CORS (HTTP/SSE only)
    /// </summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
}