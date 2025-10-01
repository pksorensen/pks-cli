using Spectre.Console;
using Spectre.Console.Testing;

var testConsole = new TestConsole();

// Test direct usage
testConsole.WriteLine("Direct TestConsole write");
Console.WriteLine($"TestConsole output after direct write: '{testConsole.Output}'");

// Test IAnsiConsole interface
IAnsiConsole ansiConsole = testConsole;
ansiConsole.WriteLine("IAnsiConsole interface write");
Console.WriteLine($"TestConsole output after interface write: '{testConsole.Output}'");

// Test markup
ansiConsole.MarkupLine("[red]✗ Error message[/]");
Console.WriteLine($"TestConsole output after markup: '{testConsole.Output}'");

// Clear and test again
testConsole.Clear();
Console.WriteLine($"TestConsole output after clear: '{testConsole.Output}'");

ansiConsole.MarkupLine("[green]✓ Success message[/]");
Console.WriteLine($"TestConsole output final: '{testConsole.Output}'");