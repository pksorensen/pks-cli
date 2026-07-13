using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Codex;

/// <summary>
/// Shared settings for the <c>pks codex</c> branch. Keep this free of arguments so native Codex
/// subcommands such as <c>resume &lt;session&gt;</c> can bind their arguments on the selected command.
/// </summary>
public class CodexBranchSettings : CommandSettings
{
    [CommandOption("-m|--model")]
    [Description("Foundry deployment name (e.g. gpt-5.6-sol). Defaults to the configured model or gpt-5-codex.")]
    public string? Model { get; set; }

    [CommandOption("-e|--reasoning-effort")]
    [Description("Reasoning effort: none|low|medium|high|xhigh (default: configured or medium)")]
    public string? ReasoningEffort { get; set; }

    [CommandOption("-p|--port")]
    [Description("Loopback port for the passthrough (default: configured or 8788)")]
    public int? Port { get; set; }

    [CommandOption("--print-env")]
    [Description("Run the passthrough in the foreground and print the launch command instead of starting codex (launch only)")]
    public bool PrintEnv { get; set; }

    [CommandOption("--safe")]
    [Description("Keep codex's approval prompts + sandbox enabled. By default pks codex bypasses them (--dangerously-bypass-approvals-and-sandbox), assuming you run in an already-isolated environment.")]
    public bool Safe { get; set; }
}

/// <summary>Settings for launch/pass-through commands that can carry native Codex arguments.</summary>
public sealed class CodexSettings : CodexBranchSettings
{
    [CommandArgument(0, "[ARGS]")]
    [Description("Arguments passed through to the underlying codex command.")]
    public string[] Args { get; set; } = Array.Empty<string>();
}
