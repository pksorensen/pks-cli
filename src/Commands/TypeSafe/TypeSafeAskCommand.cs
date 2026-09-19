using System.ComponentModel;
using PKS.Infrastructure.Services.TypeSafe;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.TypeSafe;

/// <summary>
/// Send one System One request to TypeSafe with the stored key and print the answer as JSON.
/// Usage: pks typesafe ask --file request.json | cat request.json | pks typesafe ask
///
/// Exists so a question set can be tried from a terminal with the exact body a station tool
/// would send, without exporting the key into the shell. Output is the API's JSON, untouched, so
/// it pipes into jq. Errors go to stderr and the exit code is non-zero.
/// </summary>
public class TypeSafeAskCommand : AsyncCommand<TypeSafeAskCommand.Settings>
{
    private readonly ITypeSafeCredentialService _typeSafe;
    private readonly IAnsiConsole _console;

    public TypeSafeAskCommand(ITypeSafeCredentialService typeSafe, IAnsiConsole console)
    {
        _typeSafe = typeSafe ?? throw new ArgumentNullException(nameof(typeSafe));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public class Settings : TypeSafeSettings
    {
        [CommandOption("-f|--file <PATH>")]
        [Description("JSON request body ({ state, questions[, model] }); stdin when omitted")]
        public string? File { get; set; }

        [CommandOption("--model <MODEL>")]
        [Description("Override the model for this request")]
        public string? Model { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        string body;
        if (!string.IsNullOrWhiteSpace(settings.File))
        {
            if (!System.IO.File.Exists(settings.File))
            {
                await System.Console.Error.WriteLineAsync($"pks typesafe ask: file not found: {settings.File}");
                return 1;
            }
            body = await System.IO.File.ReadAllTextAsync(settings.File);
        }
        else
        {
            body = await System.Console.In.ReadToEndAsync();
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            await System.Console.Error.WriteLineAsync("pks typesafe ask: empty request — pass --file or pipe JSON on stdin");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(body) as System.Text.Json.Nodes.JsonObject;
                if (node == null) throw new System.Text.Json.JsonException("body is not a JSON object");
                node["model"] = settings.Model.Trim();
                body = node.ToJsonString();
            }
            catch (System.Text.Json.JsonException ex)
            {
                await System.Console.Error.WriteLineAsync($"pks typesafe ask: {ex.Message}");
                return 1;
            }
        }

        var result = await _typeSafe.EvaluateAsync(body);
        if (result.StatusCode is >= 200 and < 300)
        {
            System.Console.Out.WriteLine(result.Body);
            return 0;
        }

        await System.Console.Error.WriteLineAsync($"pks typesafe ask: HTTP {result.StatusCode}");
        await System.Console.Error.WriteLineAsync(result.Body);
        return 1;
    }
}
