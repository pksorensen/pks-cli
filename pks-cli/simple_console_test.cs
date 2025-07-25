using Spectre.Console;
using Spectre.Console.Testing;

var testConsole = new TestConsole();

// Test basic write
testConsole.WriteLine("Hello World");
Console.WriteLine($"TestConsole Output: '{testConsole.Output}'");

// Test markup
testConsole.MarkupLine("[red]Error message[/]");
Console.WriteLine($"TestConsole Output After Markup: '{testConsole.Output}'");

// Test the DevcontainerInitCommand directly with TestConsole
var mockDevcontainerService = new Mock<PKS.Infrastructure.Services.IDevcontainerService>();
var mockFeatureRegistry = new Mock<PKS.Infrastructure.Services.IDevcontainerFeatureRegistry>();
var mockTemplateService = new Mock<PKS.Infrastructure.Services.IDevcontainerTemplateService>();

// Setup return values
mockDevcontainerService.Setup(x => x.ValidateOutputPathAsync(It.IsAny<string>()))
    .ReturnsAsync(new PKS.Infrastructure.Services.Models.PathValidationResult { IsValid = true, CanWrite = true });

var command = new PKS.Commands.Devcontainer.DevcontainerInitCommand(
    mockDevcontainerService.Object,
    mockFeatureRegistry.Object,
    mockTemplateService.Object,
    testConsole
);

// Clear console before test
testConsole.Clear();
Console.WriteLine($"TestConsole Output Before Command: '{testConsole.Output}'");

// Create minimal settings that will trigger error
var settings = new PKS.Commands.Devcontainer.DevcontainerInitSettings
{
    Name = null, // This should trigger validation error
    OutputPath = "/tmp/test"
};

// Execute command
try
{
    var context = new Spectre.Console.Cli.CommandContext(Mock.Of<Spectre.Console.Cli.IRemainingArguments>(), "devcontainer", null);
    var result = await command.ExecuteAsync(context, settings);
    Console.WriteLine($"Command Result: {result}");
}
catch (Exception ex)
{
    Console.WriteLine($"Command Exception: {ex.Message}");
}

Console.WriteLine($"TestConsole Output After Command: '{testConsole.Output}'");