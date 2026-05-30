using Spectre.Console.Cli;

namespace PKS.Commands.Persona;

/// <summary>
/// Branch-level settings for <c>pks persona ...</c>. Leaf commands declare
/// their own options locally — same convention as
/// <see cref="PKS.Commands.Writing.WritingSettings"/>.
/// </summary>
public class PersonaSettings : CommandSettings
{
}
