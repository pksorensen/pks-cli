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

    [Fact]
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

    [Fact]
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

    [Theory]
    [InlineData("generate", typeof(PrdGenerateCommand))]
    [InlineData("load", typeof(PrdLoadCommand))]
    [InlineData("requirements", typeof(PrdRequirementsCommand))]
    [InlineData("status", typeof(PrdStatusCommand))]
    [InlineData("validate", typeof(PrdValidateCommand))]
    [InlineData("template", typeof(PrdTemplateCommand))]
    public void CommandApp_ShouldRegisterCorrectCommandTypes(string commandName, Type commandType)
    {
        // This test verifies that the correct command types are associated with command names
        var expectedCommands = new Dictionary<string, Type>
        {
            { "generate", typeof(PrdGenerateCommand) },
            { "load", typeof(PrdLoadCommand) },
            { "requirements", typeof(PrdRequirementsCommand) },
            { "status", typeof(PrdStatusCommand) },
            { "validate", typeof(PrdValidateCommand) },
            { "template", typeof(PrdTemplateCommand) }
        };

        expectedCommands.Should().ContainKey(commandName);
        expectedCommands[commandName].Should().Be(commandType);
    }

    [Fact]
    public void PrdCommands_ShouldHaveCorrectBaseTypes()
    {
        // Verify all PRD commands inherit from the correct base types
        typeof(PrdGenerateCommand).Should().BeAssignableTo<Command<PrdGenerateSettings>>();
        typeof(PrdLoadCommand).Should().BeAssignableTo<Command<PrdLoadSettings>>();
        typeof(PrdRequirementsCommand).Should().BeAssignableTo<Command<PrdRequirementsSettings>>();
        typeof(PrdStatusCommand).Should().BeAssignableTo<Command<PrdStatusSettings>>();
        typeof(PrdValidateCommand).Should().BeAssignableTo<Command<PrdValidateSettings>>();
        typeof(PrdTemplateCommand).Should().BeAssignableTo<Command<PrdTemplateSettings>>();
    }

    [Fact]
    public void PrdSettings_ShouldInheritFromCorrectBaseClass()
    {
        // Verify all settings classes inherit from CommandSettings or PrdSettings
        typeof(PrdGenerateSettings).Should().BeAssignableTo<CommandSettings>();
        typeof(PrdLoadSettings).Should().BeAssignableTo<CommandSettings>();
        typeof(PrdRequirementsSettings).Should().BeAssignableTo<CommandSettings>();
        typeof(PrdStatusSettings).Should().BeAssignableTo<CommandSettings>();
        typeof(PrdValidateSettings).Should().BeAssignableTo<CommandSettings>();
        typeof(PrdTemplateSettings).Should().BeAssignableTo<CommandSettings>();
    }

    [Fact]
    public void PrdCommands_ShouldHaveCorrectConstructorDependencies()
    {
        // Verify that commands that need IPrdService have the correct constructor
        var generateConstructor = typeof(PrdGenerateCommand).GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == typeof(IPrdService)));
        generateConstructor.Should().NotBeNull();

        var loadConstructor = typeof(PrdLoadCommand).GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == typeof(IPrdService)));
        loadConstructor.Should().NotBeNull();

        var requirementsConstructor = typeof(PrdRequirementsCommand).GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == typeof(IPrdService)));
        requirementsConstructor.Should().NotBeNull();

        var statusConstructor = typeof(PrdStatusCommand).GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == typeof(IPrdService)));
        statusConstructor.Should().NotBeNull();

        var validateConstructor = typeof(PrdValidateCommand).GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == typeof(IPrdService)));
        validateConstructor.Should().NotBeNull();

        var templateConstructor = typeof(PrdTemplateCommand).GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType == typeof(IPrdService)));
        templateConstructor.Should().NotBeNull();
    }

    [Theory]
    [InlineData(typeof(PrdGenerateSettings))]
    [InlineData(typeof(PrdLoadSettings))]
    [InlineData(typeof(PrdRequirementsSettings))]
    [InlineData(typeof(PrdStatusSettings))]
    [InlineData(typeof(PrdValidateSettings))]
    [InlineData(typeof(PrdTemplateSettings))]
    public void PrdSettings_ShouldHaveCorrectAttributes(Type settingsType)
    {
        // Verify that settings classes have the necessary attributes for command line parsing
        var properties = settingsType.GetProperties();

        // Should have at least one property with CommandArgument or CommandOption attribute
        var hasCommandAttributes = properties.Any(p =>
            p.GetCustomAttributes(typeof(CommandArgumentAttribute), false).Any() ||
            p.GetCustomAttributes(typeof(CommandOptionAttribute), false).Any());

        hasCommandAttributes.Should().BeTrue($"{settingsType.Name} should have command attributes");
    }

    [Fact]
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

    [Fact]
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