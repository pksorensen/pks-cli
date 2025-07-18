using Spectre.Console;
using Spectre.Console.Testing;
using System.Text;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Helper class for testing console interactions and capturing output
/// </summary>
public class ConsoleTestHelper : IDisposable
{
    private readonly TestConsole _testConsole;
    private readonly StringBuilder _inputBuffer;
    private readonly Queue<string> _inputQueue;

    public ConsoleTestHelper()
    {
        _testConsole = new TestConsole();
        _inputBuffer = new StringBuilder();
        _inputQueue = new Queue<string>();
        
        // Configure test console
        _testConsole.Profile.Width = 80;
        _testConsole.Profile.Height = 24;
        _testConsole.Profile.Capabilities.Unicode = true;
        _testConsole.Profile.Capabilities.Ansi = true;
        _testConsole.Profile.Capabilities.ColorSystem = ColorSystem.TrueColor;
    }

    public IAnsiConsole Console => _testConsole;

    public string Output => _testConsole.Output;

    public void QueueInput(string input)
    {
        _inputQueue.Enqueue(input);
        _testConsole.Input.PushText(input + Environment.NewLine);
    }

    public void QueueInputSequence(params string[] inputs)
    {
        foreach (var input in inputs)
        {
            QueueInput(input);
        }
    }

    public void ClearOutput()
    {
        _testConsole.Clear();
    }

    public bool OutputContains(string text)
    {
        return _testConsole.Output.Contains(text);
    }

    public bool OutputContainsAll(params string[] texts)
    {
        return texts.All(text => _testConsole.Output.Contains(text));
    }

    public string[] GetOutputLines()
    {
        return _testConsole.Output
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line))
            .ToArray();
    }

    public void AssertOutputContains(string expectedText, string? customMessage = null)
    {
        var message = customMessage ?? $"Expected output to contain: {expectedText}";
        Assert.True(OutputContains(expectedText), $"{message}\nActual output:\n{_testConsole.Output}");
    }

    public void AssertOutputDoesNotContain(string unexpectedText, string? customMessage = null)
    {
        var message = customMessage ?? $"Expected output to NOT contain: {unexpectedText}";
        Assert.False(OutputContains(unexpectedText), $"{message}\nActual output:\n{_testConsole.Output}");
    }

    public void Dispose()
    {
        _testConsole?.Dispose();
        GC.SuppressFinalize(this);
    }
}