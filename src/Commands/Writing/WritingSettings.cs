using Spectre.Console.Cli;

namespace PKS.Commands.Writing;

/// Empty branch-level settings for `pks writing ...`. Leaf commands declare
/// their own options locally — same convention as [[BrainSettings]].
public class WritingSettings : CommandSettings
{
}
