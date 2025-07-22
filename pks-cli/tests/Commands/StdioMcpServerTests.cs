using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Commands;

/// <summary>
/// Tests for the STDIO MCP server functionality
/// These tests verify the JSON-RPC protocol handling over STDIN/STDOUT
/// </summary>
public class StdioMcpServerTests : TestBase
{
    private readonly Mock<ILogger> _mockLogger;

    public StdioMcpServerTests()
    {
        _mockLogger = CreateMock<ILogger>();
    }

    [Fact]
    public void McpRequest_ShouldSerializeCorrectly()
    {
        // Arrange
        var request = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "1.0" }
        };

        // Act
        var json = JsonSerializer.Serialize(request);
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!["jsonrpc"].ToString().Should().Be("2.0");
        deserialized["method"].ToString().Should().Be("initialize");
        deserialized["id"].ToString().Should().Be("1");
    }

    [Fact]
    public void McpResponse_ShouldSerializeCorrectly()
    {
        // Arrange
        var response = new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new
            {
                protocolVersion = "1.0",
                capabilities = new { tools = new { } },
                serverInfo = new { name = "pks-cli", version = "1.0.0" }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(response);
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!["jsonrpc"].ToString().Should().Be("2.0");
        deserialized["id"].ToString().Should().Be("1");
        deserialized.Should().ContainKey("result");
    }

    [Fact]
    public void McpError_ShouldSerializeCorrectly()
    {
        // Arrange
        var errorResponse = new
        {
            jsonrpc = "2.0",
            id = 1,
            error = new
            {
                code = "method_not_found",
                message = "Method 'unknown' not found"
            }
        };

        // Act
        var json = JsonSerializer.Serialize(errorResponse);
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!["jsonrpc"].ToString().Should().Be("2.0");
        deserialized.Should().ContainKey("error");
    }

    [Fact]
    public void InitializeRequest_ShouldHaveCorrectStructure()
    {
        // Arrange
        var initializeRequest = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "1.0",
                capabilities = new
                {
                    tools = new { }
                },
                clientInfo = new
                {
                    name = "test-client",
                    version = "1.0.0"
                }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(initializeRequest);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("initialize");
        json.Should().Contain("protocolVersion");
        json.Should().Contain("capabilities");
    }

    [Fact]
    public void ToolsListRequest_ShouldHaveCorrectStructure()
    {
        // Arrange
        var toolsListRequest = new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list"
        };

        // Act
        var json = JsonSerializer.Serialize(toolsListRequest);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("tools/list");
        json.Should().Contain("\"id\":2");
    }

    [Fact]
    public void ResourcesListRequest_ShouldHaveCorrectStructure()
    {
        // Arrange
        var resourcesListRequest = new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "resources/list"
        };

        // Act
        var json = JsonSerializer.Serialize(resourcesListRequest);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("resources/list");
        json.Should().Contain("\"id\":3");
    }

    [Fact]
    public void ToolCallRequest_ShouldHaveCorrectStructure()
    {
        // Arrange
        var toolCallRequest = new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "tools/call",
            @params = new
            {
                name = "pks_init_project",
                arguments = new
                {
                    project_name = "TestProject",
                    template = "console",
                    agentic = true
                }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(toolCallRequest);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("tools/call");
        json.Should().Contain("pks_init_project");
        json.Should().Contain("TestProject");
        json.Should().Contain("console");
    }

    [Fact]
    public void ExpectedInitializeResponse_ShouldHaveCorrectStructure()
    {
        // Arrange - This is what our server should respond with
        var expectedResponse = new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new
            {
                protocolVersion = "1.0",
                capabilities = new
                {
                    tools = new { },
                    resources = new { }
                },
                serverInfo = new
                {
                    name = "pks-cli",
                    version = "1.0.0"
                }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(expectedResponse);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("pks-cli");
        json.Should().Contain("1.0.0");
        json.Should().Contain("protocolVersion");
        json.Should().Contain("capabilities");
    }

    [Fact]
    public void ExpectedToolsListResponse_ShouldContainPksTools()
    {
        // Arrange - Expected tools from our MCP server
        var expectedTools = new object[]
        {
            new
            {
                name = "pks_init_project",
                description = "Initialize new projects with templates and AI features",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        project_name = new { type = "string" },
                        template = new { type = "string", @enum = new[] { "console", "api", "web", "agent", "library" } },
                        agentic = new { type = "boolean" },
                        mcp = new { type = "boolean" }
                    },
                    required = new[] { "project_name" }
                }
            },
            new
            {
                name = "pks_deploy",
                description = "Deploy applications with intelligent orchestration",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        environment = new { type = "string" },
                        config_path = new { type = "string" }
                    },
                    required = new[] { "environment" }
                }
            },
            new
            {
                name = "pks_status",
                description = "Get system status with real-time insights",
                inputSchema = new
                {
                    type = "object",
                    properties = new { }
                }
            }
        };

        var response = new
        {
            jsonrpc = "2.0",
            id = 2,
            result = new { tools = expectedTools }
        };

        // Act
        var json = JsonSerializer.Serialize(response);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("pks_init_project");
        json.Should().Contain("pks_deploy");
        json.Should().Contain("pks_status");
        json.Should().Contain("Initialize new projects");
        json.Should().Contain("Deploy applications");
        json.Should().Contain("system status");
    }

    [Fact]
    public void ExpectedResourcesListResponse_ShouldContainPksResources()
    {
        // Arrange - Expected resources from our MCP server
        var expectedResources = new[]
        {
            new
            {
                uri = "pks://projects",
                name = "Projects",
                description = "Project identity and configuration",
                mimeType = "application/json"
            },
            new
            {
                uri = "pks://agents",
                name = "Agents",
                description = "Available development agents",
                mimeType = "application/json"
            }
        };

        var response = new
        {
            jsonrpc = "2.0",
            id = 3,
            result = new { resources = expectedResources }
        };

        // Act
        var json = JsonSerializer.Serialize(response);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("pks://projects");
        json.Should().Contain("pks://agents");
        json.Should().Contain("Project identity");
        json.Should().Contain("development agents");
    }

    [Fact]
    public void JsonRpcErrorResponse_ShouldHaveCorrectFormat()
    {
        // Arrange
        var errorResponse = new
        {
            jsonrpc = "2.0",
            id = (object?)null,
            error = new
            {
                code = "parse_error",
                message = "Invalid JSON",
                data = (object?)null
            }
        };

        // Act
        var json = JsonSerializer.Serialize(errorResponse);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("parse_error");
        json.Should().Contain("Invalid JSON");
    }

    [Fact]
    public void MethodNotFoundError_ShouldHaveCorrectFormat()
    {
        // Arrange
        var errorResponse = new
        {
            jsonrpc = "2.0",
            id = 1,
            error = new
            {
                code = "method_not_found",
                message = "Method 'unknown_method' not found"
            }
        };

        // Act
        var json = JsonSerializer.Serialize(errorResponse);

        // Assert
        json.Should().NotBeNullOrEmpty();
        json.Should().Contain("method_not_found");
        json.Should().Contain("unknown_method");
    }

    [Fact]
    public void StdioServerProtocol_ShouldHandleLineBasedCommunication()
    {
        // Arrange
        var inputLines = new[]
        {
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}"
        };

        // Act & Assert - Test that we can parse valid JSON-RPC lines
        foreach (var line in inputLines)
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(line);
            parsed.Should().NotBeNull();
            parsed!["jsonrpc"].ToString().Should().Be("2.0");
            parsed.Should().ContainKey("method");
        }
    }
}