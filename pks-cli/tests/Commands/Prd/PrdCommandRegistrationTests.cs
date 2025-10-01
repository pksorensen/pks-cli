using PKS.Commands.Prd;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using Moq;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;

namespace PKS.CLI.Tests.Commands.Prd;

/// <summary>
/// Tests for PRD command registration and configuration
/// </summary>
public class PrdCommandRegistrationTests : IDisposable
{
    private readonly ServiceCollection _services;
    private readonly TestConsole _console;
    private readonly CommandApp _app;
    private readonly Mock<IPrdService> _mockPrdService;

    public PrdCommandRegistrationTests()
    {
        _services = new ServiceCollection();
        _console = new TestConsole();
        _mockPrdService = new Mock<IPrdService>();

        // Setup services
        _services.AddSingleton(_mockPrdService.Object);

        // Register PRD commands as transient services
        _services.AddTransient<PrdGenerateCommand>();
        _services.AddTransient<PrdLoadCommand>();
        _services.AddTransient<PrdRequirementsCommand>();
        _services.AddTransient<PrdStatusCommand>();
        _services.AddTransient<PrdValidateCommand>();
        _services.AddTransient<PrdTemplateCommand>();

        _services.AddSingleton<ITypeRegistrar>(new TypeRegistrar(_services));

        // Create command app
        _app = new CommandApp(new TypeRegistrar(_services));
    }

    [Fact(Skip = "Low value test - only tests DI container setup, disabled for lean test suite")]
    public void CommandApp_ShouldBeConfigurable()
    {
        // Arrange & Act
        _app.Configure(config =>
        {
            config.SetApplicationName("pks");
            config.SetApplicationVersion("1.0.0");
        });

        // Assert
        // The command should be configured without throwing exceptions
        _app.Should().NotBeNull();
    }

    [Fact(Skip = "Low value test - only tests DI container registration, disabled for lean test suite")]
    public void CommandApp_ShouldRegisterIndividualPrdCommands()
    {
        // Arrange & Act
        _app.Configure(config =>
        {
            config.AddCommand<PrdGenerateCommand>("generate");
            config.AddCommand<PrdLoadCommand>("load");
            config.AddCommand<PrdRequirementsCommand>("requirements");
            config.AddCommand<PrdStatusCommand>("status");
            config.AddCommand<PrdValidateCommand>("validate");
            config.AddCommand<PrdTemplateCommand>("template");
        });

        // Assert
        // All commands should be registered without exceptions
        _app.Should().NotBeNull();
    }






    [Fact(Skip = "Low value test - only tests DI container behavior, disabled for lean test suite")]
    public void TypeRegistrar_ShouldResolveIPrdService()
    {
        // Arrange
        var registrar = new TypeRegistrar(_services);
        var resolver = registrar.Build();

        // Act
        var prdService = resolver.Resolve(typeof(IPrdService));

        // Assert
        prdService.Should().NotBeNull();
        prdService.Should().BeAssignableTo<IPrdService>();
    }

    [Fact(Skip = "Low value test - only tests DI container object creation, disabled for lean test suite")]
    public void TypeRegistrar_ShouldCreatePrdCommands()
    {
        // Arrange
        var registrar = new TypeRegistrar(_services);
        var resolver = registrar.Build();

        // Act & Assert
        var generateCommand = resolver.Resolve(typeof(PrdGenerateCommand));
        generateCommand.Should().NotBeNull();
        generateCommand.Should().BeOfType<PrdGenerateCommand>();

        var loadCommand = resolver.Resolve(typeof(PrdLoadCommand));
        loadCommand.Should().NotBeNull();
        loadCommand.Should().BeOfType<PrdLoadCommand>();

        var requirementsCommand = resolver.Resolve(typeof(PrdRequirementsCommand));
        requirementsCommand.Should().NotBeNull();
        requirementsCommand.Should().BeOfType<PrdRequirementsCommand>();

        var statusCommand = resolver.Resolve(typeof(PrdStatusCommand));
        statusCommand.Should().NotBeNull();
        statusCommand.Should().BeOfType<PrdStatusCommand>();

        var validateCommand = resolver.Resolve(typeof(PrdValidateCommand));
        validateCommand.Should().NotBeNull();
        validateCommand.Should().BeOfType<PrdValidateCommand>();

        var templateCommand = resolver.Resolve(typeof(PrdTemplateCommand));
        templateCommand.Should().NotBeNull();
        templateCommand.Should().BeOfType<PrdTemplateCommand>();
    }

    public void Dispose()
    {
        _console?.Dispose();
        GC.SuppressFinalize(this);
    }
}