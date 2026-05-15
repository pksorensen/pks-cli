using Spectre.Console.Cli;

namespace PKS.Commands.Brain;

/// Empty branch-level settings for `pks brain ...`. Leaf commands declare their
/// own --dry-run / --verbose locally — Spectre.Console.Cli treats branch-level
/// options as separate from subcommand-level options, so declaring them here
/// would force the user to write `pks brain --dry-run extract` instead of
/// `pks brain extract --dry-run`.
public class BrainSettings : CommandSettings
{
}
