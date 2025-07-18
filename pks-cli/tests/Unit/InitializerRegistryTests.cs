using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Initializers;
using PKS.Infrastructure.Initializers.Context;
using PKS.Infrastructure.Initializers.Registry;
using Xunit;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Unit;

/// <summary>
/// Unit tests for the InitializerRegistry class
/// </summary>
public class InitializerRegistryTests : TestBase
{
    private readonly MockInitializerFactory _mockFactory;

    public InitializerRegistryTests(ITestOutputHelper output) : base(output)
    {
        _mockFactory = new MockInitializerFactory(FileSystem);
    }

    [Fact]
    public void InitializerRegistry_Constructor_RequiresServiceProvider()
    {
        // Arrange & Act
        Action act = () => new InitializerRegistry(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void InitializerRegistry_Register_AddsInitializerInstance()
    {
        // Arrange
        var registry = CreateRegistry();
        var initializer = _mockFactory.CreateTestInitializer();

        // Act
        registry.Register(initializer);

        // Assert
        var allInitializers = registry.GetAllAsync().Result;
        allInitializers.Should().Contain(initializer);
    }

    [Fact]
    public void InitializerRegistry_RegisterGeneric_AddsInitializerType()
    {
        // Arrange
        var serviceProvider = CreateServiceProviderWithInitializer<TestInitializer>();
        var registry = new InitializerRegistry(serviceProvider);

        // Act
        registry.Register<TestInitializer>();

        // Assert
        var allInitializers = registry.GetAllAsync().Result;
        allInitializers.Should().ContainSingle(i => i is TestInitializer);
    }

    [Fact]
    public async Task InitializerRegistry_GetAllAsync_ReturnsInstancesAndTypes()
    {
        // Arrange
        var serviceProvider = CreateServiceProviderWithInitializer<TestInitializer>();
        var registry = new InitializerRegistry(serviceProvider);
        var instanceInitializer = _mockFactory.CreateTestInitializer("instance-init", "Instance Initializer");
        
        registry.Register(instanceInitializer);
        registry.Register<TestInitializer>();

        // Act
        var allInitializers = await registry.GetAllAsync();

        // Assert
        allInitializers.Should().HaveCount(2);
        allInitializers.Should().Contain(instanceInitializer);
        allInitializers.Should().ContainSingle(i => i is TestInitializer);
    }

    [Fact]
    public async Task InitializerRegistry_GetAllAsync_OrdersByOrderThenName()
    {
        // Arrange
        var registry = CreateRegistry();
        var init1 = _mockFactory.CreateTestInitializer("init1", "Z Initializer", order: 200);
        var init2 = _mockFactory.CreateTestInitializer("init2", "A Initializer", order: 100);
        var init3 = _mockFactory.CreateTestInitializer("init3", "B Initializer", order: 100);
        
        registry.Register(init1);
        registry.Register(init2);
        registry.Register(init3);

        // Act
        var allInitializers = (await registry.GetAllAsync()).ToList();

        // Assert
        allInitializers.Should().HaveCount(3);
        allInitializers[0].Should().Be(init2); // Order 100, name "A Initializer"
        allInitializers[1].Should().Be(init3); // Order 100, name "B Initializer"
        allInitializers[2].Should().Be(init1); // Order 200, name "Z Initializer"
    }

    [Fact]
    public async Task InitializerRegistry_GetApplicableAsync_ReturnsOnlyApplicableInitializers()
    {
        // Arrange
        var registry = CreateRegistry();
        var applicableInit = _mockFactory.CreateTestInitializer("applicable", shouldRun: true);
        var nonApplicableInit = _mockFactory.CreateTestInitializer("non-applicable", shouldRun: false);
        
        registry.Register(applicableInit);
        registry.Register(nonApplicableInit);
        
        var context = CreateTestContext("TestProject");

        // Act
        var applicableInitializers = await registry.GetApplicableAsync(context);

        // Assert
        applicableInitializers.Should().ContainSingle();
        applicableInitializers.Should().Contain(applicableInit);
        applicableInitializers.Should().NotContain(nonApplicableInit);
    }

    [Fact]
    public async Task InitializerRegistry_GetApplicableAsync_HandlesExceptionsGracefully()
    {
        // Arrange
        var registry = CreateRegistry();
        var mockInitializer = new Mock<IInitializer>();
        mockInitializer.Setup(x => x.Id).Returns("failing-init");
        mockInitializer.Setup(x => x.Name).Returns("Failing Initializer");
        mockInitializer.Setup(x => x.Order).Returns(100);
        mockInitializer.Setup(x => x.ShouldRunAsync(It.IsAny<InitializationContext>()))
                       .ThrowsAsync(new InvalidOperationException("Test exception"));
        
        var workingInit = _mockFactory.CreateTestInitializer("working", shouldRun: true);
        
        registry.Register(mockInitializer.Object);
        registry.Register(workingInit);
        
        var context = CreateTestContext("TestProject");

        // Act
        var applicableInitializers = await registry.GetApplicableAsync(context);

        // Assert
        applicableInitializers.Should().ContainSingle();
        applicableInitializers.Should().Contain(workingInit);
    }

    [Fact]
    public async Task InitializerRegistry_GetByIdAsync_ReturnsCorrectInitializer()
    {
        // Arrange
        var registry = CreateRegistry();
        var targetInit = _mockFactory.CreateTestInitializer("target-id", "Target Initializer");
        var otherInit = _mockFactory.CreateTestInitializer("other-id", "Other Initializer");
        
        registry.Register(targetInit);
        registry.Register(otherInit);

        // Act
        var foundInitializer = await registry.GetByIdAsync("target-id");

        // Assert
        foundInitializer.Should().Be(targetInit);
    }

    [Fact]
    public async Task InitializerRegistry_GetByIdAsync_IsCaseInsensitive()
    {
        // Arrange
        var registry = CreateRegistry();
        var targetInit = _mockFactory.CreateTestInitializer("Target-ID", "Target Initializer");
        registry.Register(targetInit);

        // Act
        var foundInitializer = await registry.GetByIdAsync("target-id");

        // Assert
        foundInitializer.Should().Be(targetInit);
    }

    [Fact]
    public async Task InitializerRegistry_GetByIdAsync_ReturnsNullForNotFound()
    {
        // Arrange
        var registry = CreateRegistry();
        var otherInit = _mockFactory.CreateTestInitializer("other-id", "Other Initializer");
        registry.Register(otherInit);

        // Act
        var foundInitializer = await registry.GetByIdAsync("non-existent");

        // Assert
        foundInitializer.Should().BeNull();
    }

    [Fact]
    public void InitializerRegistry_GetAllOptions_ReturnsDistinctOptions()
    {
        // Arrange
        var registry = CreateRegistry();
        var init1 = _mockFactory.CreateTestInitializer("init1");
        var init2 = _mockFactory.CreateTestInitializer("init2");
        
        registry.Register(init1);
        registry.Register(init2);

        // Act
        var options = registry.GetAllOptions().ToList();

        // Assert
        options.Should().NotBeEmpty();
        options.Should().ContainSingle(opt => opt.Name == "test-option");
        // Since both test initializers return the same option, it should be deduplicated
    }

    [Fact]
    public async Task InitializerRegistry_ExecuteAllAsync_RunsApplicableInitializers()
    {
        // Arrange
        var registry = CreateRegistry();
        var init1 = _mockFactory.CreateTestInitializer("init1", shouldRun: true, shouldSucceed: true);
        var init2 = _mockFactory.CreateTestInitializer("init2", shouldRun: true, shouldSucceed: true);
        var init3 = _mockFactory.CreateTestInitializer("init3", shouldRun: false); // Should not run
        
        registry.Register(init1);
        registry.Register(init2);
        registry.Register(init3);
        
        var context = CreateTestContext("TestProject");

        // Act
        var results = (await registry.ExecuteAllAsync(context)).ToList();

        // Assert
        results.Should().HaveCount(2, "Only applicable initializers should be executed");
        results.Should().AllSatisfy(r => r.Success.Should().BeTrue());
    }

    [Fact]
    public async Task InitializerRegistry_ExecuteAllAsync_StopsOnCriticalInitializerFailure()
    {
        // Arrange
        var registry = CreateRegistry();
        var criticalInit = _mockFactory.CreateTestInitializer("critical", shouldRun: true, shouldSucceed: false, order: 10); // Critical (order < 50)
        var nonCriticalInit = _mockFactory.CreateTestInitializer("non-critical", shouldRun: true, shouldSucceed: true, order: 100);
        
        registry.Register(criticalInit);
        registry.Register(nonCriticalInit);
        
        var context = CreateTestContext("TestProject");

        // Act
        var results = (await registry.ExecuteAllAsync(context)).ToList();

        // Assert
        results.Should().HaveCount(1, "Execution should stop after critical initializer fails");
        results[0].Success.Should().BeFalse("Critical initializer should have failed");
    }

    [Fact]
    public async Task InitializerRegistry_ExecuteAllAsync_ContinuesOnNonCriticalInitializerFailure()
    {
        // Arrange
        var registry = CreateRegistry();
        var failingInit = _mockFactory.CreateTestInitializer("failing", shouldRun: true, shouldSucceed: false, order: 100); // Non-critical
        var successInit = _mockFactory.CreateTestInitializer("success", shouldRun: true, shouldSucceed: true, order: 200);
        
        registry.Register(failingInit);
        registry.Register(successInit);
        
        var context = CreateTestContext("TestProject");

        // Act
        var results = (await registry.ExecuteAllAsync(context)).ToList();

        // Assert
        results.Should().HaveCount(2, "Execution should continue after non-critical initializer fails");
        results[0].Success.Should().BeFalse("First initializer should have failed");
        results[1].Success.Should().BeTrue("Second initializer should have succeeded");
    }

    [Fact]
    public async Task InitializerRegistry_ExecuteAllAsync_HandlesExceptionInInitializer()
    {
        // Arrange
        var registry = CreateRegistry();
        var mockInitializer = new Mock<IInitializer>();
        mockInitializer.Setup(x => x.Id).Returns("throwing-init");
        mockInitializer.Setup(x => x.Name).Returns("Throwing Initializer");
        mockInitializer.Setup(x => x.Order).Returns(100);
        mockInitializer.Setup(x => x.ShouldRunAsync(It.IsAny<InitializationContext>())).ReturnsAsync(true);
        mockInitializer.Setup(x => x.ExecuteAsync(It.IsAny<InitializationContext>()))
                       .ThrowsAsync(new InvalidOperationException("Test exception"));
        
        var workingInit = _mockFactory.CreateTestInitializer("working", shouldRun: true, shouldSucceed: true);
        
        registry.Register(mockInitializer.Object);
        registry.Register(workingInit);
        
        var context = CreateTestContext("TestProject");

        // Act
        var results = (await registry.ExecuteAllAsync(context)).ToList();

        // Assert
        results.Should().HaveCount(2);
        results[0].Success.Should().BeFalse("Exception should result in failure");
        results[0].Message.Should().Contain("Exception in Throwing Initializer");
        results[1].Success.Should().BeTrue("Second initializer should still execute");
    }

    private InitializerRegistry CreateRegistry()
    {
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        return new InitializerRegistry(serviceProvider);
    }

    private IServiceProvider CreateServiceProviderWithInitializer<T>() where T : class, IInitializer
    {
        var services = new ServiceCollection();
        services.AddTransient<T>(_ => (T)_mockFactory.CreateTestInitializer());
        return services.BuildServiceProvider();
    }

    private InitializationContext CreateTestContext(
        string projectName,
        string template = "console",
        string targetDirectory = "/test/output")
    {
        CreateTestDirectory(targetDirectory);
        
        return new InitializationContext
        {
            ProjectName = projectName,
            Template = template,
            TargetDirectory = targetDirectory,
            WorkingDirectory = TestDirectory,
            Force = false,
            Interactive = false
        };
    }

    /// <summary>
    /// Test implementation of IInitializer for dependency injection testing
    /// </summary>
    public class TestInitializer : IInitializer
    {
        public string Id => "test-di-initializer";
        public string Name => "Test DI Initializer";
        public string Description => "Test initializer for DI testing";
        public int Order => 100;

        public Task<bool> ShouldRunAsync(InitializationContext context) => Task.FromResult(true);

        public Task<InitializationResult> ExecuteAsync(InitializationContext context)
        {
            return Task.FromResult(InitializationResult.CreateSuccess("DI initializer executed"));
        }

        public IEnumerable<InitializerOption> GetOptions()
        {
            return new[]
            {
                new InitializerOption
                {
                    Name = "di-option",
                    Description = "DI test option",
                    DefaultValue = "di-default",
                    Required = false
                }
            };
        }
    }
}