using PKS.Infrastructure.Initializers.Base;
using PKS.Infrastructure.Initializers.Context;

namespace PKS.Infrastructure.Initializers.Implementations;

/// <summary>
/// Template-based initializer for MCP (Model Context Protocol) configuration
/// </summary>
public class McpConfigurationInitializer : TemplateInitializer
{
    public override string Id => "mcp-configuration";
    public override string Name => "MCP Configuration";
    public override string Description => "Creates MCP (Model Context Protocol) configuration for AI tool integration";
    public override int Order => 75; // Run after basic project structure but before deployment
    
    protected override string TemplateDirectory => "mcp";

    public override IEnumerable<InitializerOption> GetOptions()
    {
        return new[]
        {
            InitializerOption.Flag("enable-stdio", "Enable stdio transport for local MCP server", "s"),
            InitializerOption.Flag("enable-sse", "Enable SSE transport for remote MCP server", "e"),
            InitializerOption.String("server-url", "Base URL for remote MCP server", "u", "https://localhost:8080"),
            InitializerOption.String("mcp-tools", "Comma-separated list of tools to expose", "t", "init,agent,deploy,status"),
            InitializerOption.Flag("enable-auth", "Enable OAuth 2.0 authentication", "a"),
            InitializerOption.String("env-prefix", "Environment variable prefix", "p", "PKS")
        };
    }

    public override async Task<bool> ShouldRunAsync(InitializationContext context)
    {
        // Run if MCP features are explicitly enabled or if using agentic template
        return context.GetOption("mcp", false) || 
               context.GetOption("enable-mcp", false) ||
               (context.Template?.Equals("agentic", StringComparison.OrdinalIgnoreCase) == true);
    }

    protected override async Task<string> ProcessTemplateContentAsync(string content, string templateFile, string targetFile, InitializationContext context)
    {
        var enableStdio = context.GetOption("enable-stdio", true);
        var enableSse = context.GetOption("enable-sse", false);
        var serverUrl = context.GetOption("server-url", "https://localhost:8080");
        var mcpTools = context.GetOption("mcp-tools", "init,agent,deploy,status");
        var enableAuth = context.GetOption("enable-auth", false);
        var envPrefix = context.GetOption("env-prefix", "PKS");

        var customPlaceholders = new Dictionary<string, string>
        {
            { "{{MCP.EnableStdio}}", enableStdio.ToString().ToLower() },
            { "{{MCP.EnableSSE}}", enableSse.ToString().ToLower() },
            { "{{MCP.ServerUrl}}", serverUrl ?? "https://localhost:8080" },
            { "{{MCP.Tools}}", mcpTools ?? "init,agent,deploy,status" },
            { "{{MCP.EnableAuth}}", enableAuth.ToString().ToLower() },
            { "{{MCP.EnvPrefix}}", envPrefix ?? "PKS" },
            { "{{MCP.ToolCount}}", (mcpTools ?? "init,agent,deploy,status").Split(',').Length.ToString() },
            { "{{MCP.ProjectTool}}", context.ProjectName.ToLowerInvariant() + "-cli" }
        };

        return ReplacePlaceholdersWithCustom(content, context, customPlaceholders);
    }

    protected override async Task PostProcessTemplateAsync(InitializationContext context, InitializationResult result)
    {
        var enableStdio = context.GetOption("enable-stdio", true);
        var enableSse = context.GetOption("enable-sse", false);
        var enableAuth = context.GetOption("enable-auth", false);

        if (enableStdio)
        {
            result.Data["mcp_stdio"] = "enabled";
        }

        if (enableSse)
        {
            result.Data["mcp_sse"] = "enabled";
        }

        if (enableAuth)
        {
            result.Data["mcp_auth"] = "enabled";
            result.Warnings.Add("Remember to configure OAuth 2.0 credentials for authentication");
        }

        result.Warnings.Add("Configure environment variables for secure API keys and endpoints");
        result.Warnings.Add("Add the .mcp.json to your AI tool's configuration directory");
        
        await base.PostProcessTemplateAsync(context, result);
    }
}