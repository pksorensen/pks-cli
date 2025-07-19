using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Infrastructure.Mocks;
using PKS.CLI.Tests.Infrastructure.Fixtures;
using PKS.Commands.Hooks;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console.Cli;
using Xunit;

namespace PKS.CLI.Tests.Commands.Hooks;

/// <summary>
/// Tests for the hooks command functionality
/// These tests define the expected behavior for hooks management
/// </summary>
public class HooksCommandTests : TestBase
{
    private readonly Mock<PKS.Infrastructure.Services.IHooksService> _mockHooksService;
    private readonly HooksCommand _command;

    public HooksCommandTests()
    {
        _mockHooksService = new Mock<PKS.Infrastructure.Services.IHooksService>();
        _command = new HooksCommand(_mockHooksService.Object);
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton(_mockHooksService.Object);
        services.AddSingleton<HooksCommand>();
    }

    [Fact]
    public async Task List_ShouldDisplayAvailableHooks_WhenHooksExist()
    {
        // Arrange
        var expectedHooks = new List<PKS.Infrastructure.Services.Models.HookDefinition>
        {
            new PKS.Infrastructure.Services.Models.HookDefinition { Name = "test-hook-1", Description = "Test hook 1", Parameters = new List<string> { "param1", "param2" } },
            new PKS.Infrastructure.Services.Models.HookDefinition { Name = "test-hook-2", Description = "Test hook 2", Parameters = new List<string> { "param1", "param2" } },
            new PKS.Infrastructure.Services.Models.HookDefinition { Name = "test-hook-3", Description = "Test hook 3", Parameters = new List<string> { "param1", "param2" } }
        };
        _mockHooksService.Setup(x => x.GetAvailableHooksAsync())
            .ReturnsAsync(expectedHooks);

        var settings = new HooksSettings { Action = HookAction.List };

        // Act
        // This will fail until HooksCommand is implemented
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Available Hooks");
        expectedHooks.ForEach(hook => AssertConsoleOutput(hook.Name));
        _mockHooksService.Verify(x => x.GetAvailableHooksAsync(), Times.Once);
    }

    [Fact]
    public async Task Execute_ShouldRunSpecifiedHook_WhenValidHookProvided()
    {
        // Arrange
        var hookName = "test-hook-1";
        var expectedResult = new PKS.Infrastructure.Services.Models.HookResult 
        { 
            Success = true, 
            Message = "Hook executed successfully",
            Output = new Dictionary<string, object> { ["result"] = "success" }
        };

        _mockHooksService.Setup(x => x.ExecuteHookAsync(hookName, It.IsAny<PKS.Infrastructure.Services.Models.HookContext>()))
            .ReturnsAsync(expectedResult);

        var settings = new HooksSettings 
        { 
            Action = HookAction.Execute,
            HookName = hookName,
            Parameters = new[] { "param1=value1", "param2=value2" }
        };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Executing hook");
        AssertConsoleOutput(hookName);
        AssertConsoleOutput("Hook executed successfully");
        _mockHooksService.Verify(x => x.ExecuteHookAsync(hookName, It.IsAny<PKS.Infrastructure.Services.Models.HookContext>()), Times.Once);
    }

    [Fact]
    public async Task Execute_ShouldReturnError_WhenHookExecutionFails()
    {
        // Arrange
        var hookName = "failing-hook";
        var expectedResult = new PKS.Infrastructure.Services.Models.HookResult 
        { 
            Success = false, 
            Message = "Hook execution failed"
        };

        _mockHooksService.Setup(x => x.ExecuteHookAsync(hookName, It.IsAny<PKS.Infrastructure.Services.Models.HookContext>()))
            .ReturnsAsync(expectedResult);

        var settings = new HooksSettings 
        { 
            Action = HookAction.Execute,
            HookName = hookName
        };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(1);
        AssertConsoleOutput("Hook execution failed");
    }

    [Fact]
    public async Task Execute_ShouldThrowException_WhenHookNameIsEmpty()
    {
        // Arrange
        var settings = new HooksSettings 
        { 
            Action = HookAction.Execute,
            HookName = string.Empty
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _command.ExecuteAsync(null!, settings));
    }

    [Fact]
    public async Task Install_ShouldInstallHookFromSource_WhenValidSourceProvided()
    {
        // Arrange
        var hookSource = "https://github.com/example/hook.git";
        _mockHooksService.Setup(x => x.InstallHookAsync(hookSource))
            .ReturnsAsync(new PKS.Infrastructure.Services.Models.HookInstallResult { Success = true, HookName = "new-hook" });

        var settings = new HooksSettings 
        { 
            Action = HookAction.Install,
            Source = hookSource
        };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Installing hook");
        AssertConsoleOutput("Hook installed successfully");
    }

    [Fact]
    public async Task Remove_ShouldUninstallHook_WhenValidHookNameProvided()
    {
        // Arrange
        var hookName = "hook-to-remove";
        _mockHooksService.Setup(x => x.RemoveHookAsync(hookName))
            .ReturnsAsync(true);

        var settings = new HooksSettings 
        { 
            Action = HookAction.Remove,
            HookName = hookName
        };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Removing hook");
        AssertConsoleOutput("Hook removed successfully");
    }
}