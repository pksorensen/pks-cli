# MCP SDK Migration Plan

This document outlines the migration plan from the custom MCP server implementation to the SDK-based hosting service.

## Overview

We are migrating from a 819-line custom `McpServerService` to a modern SDK-based approach using the official `ModelContextProtocol` NuGet packages. This provides better maintainability, standards compliance, and extensibility.

## Architecture Changes

### Before (Legacy)
- **Custom Implementation**: 819-line `McpServerService.cs` with hardcoded transport handling
- **Hardcoded Tools**: 11 PKS tools implemented directly in the service
- **Manual Resource Management**: Resources handled through string generation
- **Transport Coupling**: Tight coupling between transport and business logic

### After (SDK-based)
- **SDK Hosting**: Clean separation using `ModelContextProtocol` SDK
- **Attribute-based Tools**: Tools marked with `[McpServerTool]` attributes
- **Reflection-based Execution**: Automatic discovery and parameter binding
- **Transport Abstraction**: SDK handles stdio, HTTP, and SSE transports

## New Services

### 1. IMcpHostingService & McpHostingService
**File**: `/Infrastructure/Services/MCP/McpHostingService.cs`
- **Purpose**: Main hosting service using SDK transport abstractions
- **Features**: 
  - Lifecycle management (start/stop/restart)
  - Multi-transport support (stdio, HTTP, SSE)
  - Service registration
  - Error handling and logging

### 2. McpToolService
**File**: `/Infrastructure/Services/MCP/McpToolService.cs`
- **Purpose**: Attribute-based tool registration and execution
- **Features**:
  - `[McpServerTool]` attribute discovery
  - Reflection-based method invocation
  - Parameter validation and conversion
  - JSON schema generation
  - Tool lifecycle management

### 3. McpResourceService
**File**: `/Infrastructure/Services/MCP/McpResourceService.cs`
- **Purpose**: Attribute-based resource providers
- **Features**:
  - `[McpServerResource]` attribute discovery
  - Built-in PKS resources (pks://projects, pks://agents, pks://tasks)
  - Content generation and metadata
  - URI-based resource access

### 4. McpConfiguration
**File**: `/Infrastructure/Services/MCP/McpConfiguration.cs`
- **Purpose**: Configuration options with feature flagging
- **Features**:
  - Enable/disable SDK hosting
  - Transport configuration
  - Security settings
  - Tool filtering options

## Migration Strategy

### Phase 1: Foundation ✅ COMPLETED
1. ✅ Create new SDK-based services
2. ✅ Register services in DI container
3. ✅ Add configuration support
4. ✅ Create example tool service

### Phase 2: Tool Migration (Planned)
Migrate the 11 existing PKS tools to attribute-based system:

#### Tools to Migrate:
1. **pks-init** - Project initialization
   - Current: Hardcoded in `McpServerService`
   - Target: `[McpServerTool]` in `ProjectToolService`
   
2. **pks-agent** - Agent management
   - Current: Hardcoded tool implementation
   - Target: `[McpServerTool]` in `AgentToolService`
   
3. **pks-deploy** - Deployment operations
   - Current: Manual implementation
   - Target: `[McpServerTool]` in `DeploymentToolService`
   
4. **pks-status** - Status monitoring
   - Current: Status checking code
   - Target: `[McpServerTool]` in `MonitoringToolService`
   
5. **pks-ascii** - ASCII art generation
   - Current: ASCII generation logic
   - Target: `[McpServerTool]` in `UtilityToolService`
   
6. **pks-devcontainer** - Dev container management
   - Current: Devcontainer operations
   - Target: `[McpServerTool]` in `DevcontainerToolService`
   
7. **pks-hooks** - Git hooks management
   - Current: Hook management code
   - Target: `[McpServerTool]` in `HooksToolService`
   
8. **pks-prd** - PRD document operations
   - Current: PRD handling code
   - Target: `[McpServerTool]` in `PrdToolService`
   
9. **pks-mcp** - MCP server management
   - Current: Self-management code
   - Target: `[McpServerTool]` in `McpManagementToolService`
   
10. **pks-github** - GitHub integration
    - Current: GitHub API operations
    - Target: `[McpServerTool]` in `GitHubToolService`
    
11. **pks-template** - Template operations
    - Current: Template handling code
    - Target: `[McpServerTool]` in `TemplateToolService`

### Phase 3: Resource Migration (Planned)
Migrate the 3 PKS resources:

1. **pks://projects** - Project listing and details
2. **pks://agents** - Agent status and management
3. **pks://tasks** - Task tracking and history

### Phase 4: Integration & Testing (Planned)
1. Feature flag configuration
2. Backward compatibility testing
3. Performance validation
4. Integration testing

### Phase 5: Cleanup (Planned)
1. Remove legacy `McpServerService` after migration
2. Update documentation
3. Clean up unused code

## Tool Service Example

Here's how a PKS tool would be migrated:

### Before (Legacy)
```csharp
// Inside McpServerService.ExecuteToolAsync
case "pks-init":
    // 50+ lines of hardcoded implementation
    var result = InitializeProject(args);
    return result;
```

### After (SDK-based)
```csharp
[McpServerTool("pks-init", "Initialize a new PKS project", "project")]
public async Task<object> InitializeProjectAsync(
    [McpToolParameter("Project name", required: true)] string projectName,
    [McpToolParameter("Template type", defaultValue: "console")] string template = "console",
    [McpToolParameter("Enable agentic features")] bool agentic = false)
{
    // Clean implementation
    return await _projectService.InitializeAsync(projectName, template, agentic);
}
```

## Benefits of Migration

### 1. Standards Compliance
- Uses official ModelContextProtocol SDK
- Follows MCP specifications exactly
- Better compatibility with MCP clients

### 2. Maintainability
- Attribute-based configuration
- Clear separation of concerns
- Reduced code complexity

### 3. Extensibility
- Easy to add new tools
- Plugin-style architecture
- Reflection-based discovery

### 4. Testing
- Individual tool services can be unit tested
- Mock-friendly architecture
- Better error isolation

### 5. Performance
- Efficient reflection caching
- Better resource management
- Optimized transport handling

## Configuration Options

The new system supports extensive configuration:

```csharp
services.Configure<McpConfiguration>(config =>
{
    config.UseSdkHosting = true;              // Feature flag
    config.DefaultTransport = "stdio";        // Default transport
    config.EnableAutoToolDiscovery = true;    // Automatic tool discovery
    config.EnabledToolCategories = new[] { "project", "deployment" }; // Filter tools
    config.DisabledTools = new[] { "pks-experimental" }; // Disable specific tools
});
```

## Backward Compatibility

During migration, both implementations will coexist:
- Legacy `IMcpServerService` remains available
- New `IMcpHostingService` is available
- Feature flag controls which implementation is used
- Gradual migration of individual tools

## Next Steps

1. **Review and approve** this architecture
2. **Begin Phase 2**: Start migrating individual tools
3. **Create tool services** for each PKS functionality area
4. **Add integration tests** for the new system
5. **Performance testing** to ensure parity with legacy system

## Files Created

- ✅ `/Infrastructure/Services/MCP/IMcpHostingService.cs`
- ✅ `/Infrastructure/Services/MCP/McpHostingService.cs`
- ✅ `/Infrastructure/Services/MCP/McpToolService.cs`
- ✅ `/Infrastructure/Services/MCP/McpResourceService.cs`
- ✅ `/Infrastructure/Services/MCP/McpConfiguration.cs`
- ✅ `/Infrastructure/Services/MCP/Examples/PksToolsService.cs`
- ✅ `/Infrastructure/Services/MCP/MIGRATION-PLAN.md`

## Implementation Status

✅ **Phase 1 Complete**: Foundation SDK-based services created and registered
🔄 **Phase 2 Ready**: Tool migration can begin
⏳ **Phase 3 Planned**: Resource migration
⏳ **Phase 4 Planned**: Integration & testing
⏳ **Phase 5 Planned**: Legacy cleanup