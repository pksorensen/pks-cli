using FluentAssertions;
using Microsoft.Extensions.FileSystemGlobbing;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using Xunit;

namespace PKS.CLI.Tests.Services.Storage;

/// <summary>
/// Pruning the remote walk must never hide a file the file-level matcher would have transferred,
/// and must skip the trees the globs plainly rule out — that is where a snapshot's time goes.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class SyncTreePrunerTests
{
    // The shape of the share that prompted this: config trees worth fetching, beside derived
    // slot and booking trees that dwarf them.
    private static readonly string[] Share =
    [
        "meeting-types/intro/meeting-type.json",
        "meeting-types/intro/slots/2026-10/a.json",
        "meeting-types/intro/slots/2026-10/b/c.json",
        "meeting-types/intro/webhooks/hook.json",
        "meeting-types/intro/subscription.json",
        "meeting-types/Review/settings.JSON",
        "sites/main/site.json",
        "sites/main/deep/er/still.json",
        "brands/acme/brand.json",
        "staff/p1/profile.json",
        "bookings/2026/10/08/b1.json",
        "calendars/c1/events/e1.json",
        "root.json",
        "slots/top-level.json",
    ];

    private static readonly string[][] IncludeSets =
    [
        [],
        ["meeting-types/**", "sites/**", "brands/**", "staff/**"],
        ["**/*.json"],
        ["sites"],
        ["sites/*"],
        ["sites/*/site.json"],
        ["meeting-*/**"],
        ["*/intro/**"],
        ["MEETING-TYPES/**"],
        ["root.json"],
        ["**/slots/**"],
        ["meeting-types/{intro,Review}/**"],
    ];

    private static readonly string[][] ExcludeSets =
    [
        [],
        ["**/slots/**", "**/webhooks/**", "**/subscription.json"],
        ["slots/**"],
        ["meeting-types/*/slots/*"],
        ["**/SLOTS/**"],
        ["bookings/**", "calendars/**"],
        ["**/*.json"],
        ["**"],
        ["meeting-types/[a-z]*/**"],
    ];

    public static IEnumerable<object[]> GlobCombinations() =>
        from inc in IncludeSets
        from exc in ExcludeSets
        select new object[] { inc, exc };

    [Theory]
    [MemberData(nameof(GlobCombinations))]
    public void Never_prunes_a_directory_holding_a_file_the_matcher_would_transfer(string[] include, string[] exclude)
    {
        var pruner = new SyncTreePruner(include, exclude);
        var matcher = BuildMatcher(include, exclude);

        foreach (var file in Share.Where(f => matcher is null || matcher.Match(f).HasMatches))
            foreach (var dir in Ancestors(file))
                pruner.ShouldDescend(dir).Should().BeTrue(
                    $"'{file}' is transferred with include [{string.Join(", ", include)}] " +
                    $"exclude [{string.Join(", ", exclude)}], so '{dir}' must be walked");
    }

    [Fact]
    public void Prunes_the_trees_the_reported_snapshot_walked_for_nothing()
    {
        var pruner = new SyncTreePruner(
            ["meeting-types/**", "sites/**", "brands/**", "staff/**"],
            ["**/slots/**", "**/webhooks/**", "**/subscription.json"]);

        pruner.ShouldDescend("bookings").Should().BeFalse();
        pruner.ShouldDescend("calendars").Should().BeFalse();
        pruner.ShouldDescend("meeting-types/intro/slots").Should().BeFalse();
        pruner.ShouldDescend("meeting-types/intro/webhooks").Should().BeFalse();

        pruner.ShouldDescend("meeting-types").Should().BeTrue();
        pruner.ShouldDescend("meeting-types/intro").Should().BeTrue();
        pruner.ShouldDescend("sites/main/deep").Should().BeTrue();
    }

    [Fact]
    public void A_literal_include_only_walks_the_path_leading_to_it()
    {
        var pruner = new SyncTreePruner(["sites/*/site.json"], []);

        pruner.ShouldDescend("sites").Should().BeTrue();
        pruner.ShouldDescend("sites/main").Should().BeTrue();
        pruner.ShouldDescend("sites/main/deep").Should().BeFalse();
        pruner.ShouldDescend("brands").Should().BeFalse();
    }

    [Fact]
    public void A_file_level_exclude_prunes_nothing()
    {
        var pruner = new SyncTreePruner([], ["**/subscription.json", "slots/*"]);

        pruner.IsEmpty.Should().BeTrue();
        pruner.ShouldDescend("slots").Should().BeTrue();
    }

    private static Matcher? BuildMatcher(string[] include, string[] exclude)
    {
        if (include.Length == 0 && exclude.Length == 0)
            return null;

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        if (include.Length > 0)
            foreach (var p in include) matcher.AddInclude(p);
        else
            matcher.AddInclude("**");
        foreach (var p in exclude) matcher.AddExclude(p);
        return matcher;
    }

    private static IEnumerable<string> Ancestors(string file)
    {
        var parts = file.Split('/');
        for (var i = 1; i < parts.Length; i++)
            yield return string.Join('/', parts[..i]);
    }
}
