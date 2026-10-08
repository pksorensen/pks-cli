using FluentAssertions;
using PKS.Infrastructure;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// The argv shapes that make Spectre fail with a bare "Invalid long option name", and the ones
/// that must pass without a hint.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class ArgvHintsTests
{
    [Theory]
    [InlineData("storage", "ls", "users/", "--account insurancebookingprdvu5gc")]
    [InlineData("storage", "sync", "--dry-run --include **/slots/**", "./x")]
    [InlineData("storage", "ls", "users/", "--dirs‑only")]
    [InlineData("storage", "ls", "—json")]
    public void Flags_spectre_rejects_get_a_hint(params string[] args)
    {
        ArgvHints.Find(args).Should().ContainSingle();
    }

    [Theory]
    [InlineData("storage", "ls", "users/", "--account", "insurancebookingprdvu5gc", "--json")]
    [InlineData("storage", "sync", "--include=**/a b", "./x")]
    [InlineData("storage", "sync", "--include:a b", "./x")]
    [InlineData("storage", "ls", "users/ with space")]
    [InlineData("agent", "run", "--", "--model sonnet")]
    public void Well_formed_args_get_no_hint(params string[] args)
    {
        ArgvHints.Find(args).Should().BeEmpty();
    }
}
