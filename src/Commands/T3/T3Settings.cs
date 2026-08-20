using Spectre.Console.Cli;

namespace PKS.Commands.T3;

/// <summary>
/// Branch marker for <c>pks t3</c>, deliberately empty — same shape as <c>VmSettings</c>.
///
/// Options do not go here. Spectre binds a branch's settings only when the flag appears *before* the
/// subcommand (<c>pks t3 --domain x init</c>); written the way anyone actually types it —
/// <c>pks t3 init --domain x</c> — the flag parses without error and arrives unset, and the command
/// then prompts for a value it was already given. Leaf options belong on the leaf.
/// </summary>
public class T3Settings : CommandSettings { }
