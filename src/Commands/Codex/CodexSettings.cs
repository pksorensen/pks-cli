using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Codex;

/// <summary>
/// Shared settings for the <c>pks codex</c> branch. The branch is typed with these so the default
/// launch command's options bind at the branch level (<c>pks codex -m … --print-env</c>); the
/// <c>init</c> subcommand reuses the same type (it ignores the launch-only flags).
/// </summary>
public sealed class CodexSettings : CommandSettings
{
    [CommandOption("-m|--model")]
    [Description("Foundry deployment name (e.g. gpt-5-codex). Defaults to the configured model or gpt-5-codex.")]
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
