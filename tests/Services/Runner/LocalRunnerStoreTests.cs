using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class LocalRunnerStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pks-local-runner-tests", Guid.NewGuid().ToString("N"));

    private LocalRunnerStore MakeStore() => new(Path.Combine(_dir, "agentics-local-runners.json"));

    private static LocalRunnerRecord Record(string owner, string project, int pid = 4242) => new()
    {
        Owner = owner,
        Project = project,
        Server = "https://agentics.dk",
        Mode = LocalRunnerMode.Process,
        Pid = pid,
        WorkDir = "/work",
        StartedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Missing_file_is_an_empty_store_not_an_error()
    {
        (await MakeStore().ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_round_trips_through_disk()
    {
        var store = MakeStore();
        await store.UpsertAsync(Record("pksorensen", "museliving"));

        var reloaded = await MakeStore().FindAsync("pksorensen", "museliving");

        reloaded.Should().NotBeNull();
        reloaded!.Mode.Should().Be(LocalRunnerMode.Process);
        reloaded.Pid.Should().Be(4242);
    }

    [Fact]
    public async Task One_record_per_project_so_two_runners_cannot_claim_the_same_jobs()
    {
        var store = MakeStore();
        await store.UpsertAsync(Record("pksorensen", "museliving", pid: 1));
        await store.UpsertAsync(Record("pksorensen", "museliving", pid: 2));

        var all = await store.ListAsync();

        all.Should().ContainSingle();
        all[0].Pid.Should().Be(2);
    }

    [Fact]
    public async Task Project_lookup_is_case_insensitive()
    {
        var store = MakeStore();
        await store.UpsertAsync(Record("PKSorensen", "MuseLiving"));

        (await store.FindAsync("pksorensen", "museliving")).Should().NotBeNull();
    }

    [Fact]
    public async Task Remove_only_takes_the_named_project()
    {
        var store = MakeStore();
        await store.UpsertAsync(Record("pksorensen", "museliving"));
        await store.UpsertAsync(Record("pksorensen", "arvo-works-quickforms"));

        await store.RemoveAsync("pksorensen", "museliving");

        var all = await store.ListAsync();
        all.Should().ContainSingle().Which.Project.Should().Be("arvo-works-quickforms");
    }

    [Fact]
    public async Task A_corrupt_state_file_degrades_to_empty_rather_than_breaking_every_command()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "agentics-local-runners.json");
        await File.WriteAllTextAsync(path, "{ this is not json");

        (await new LocalRunnerStore(path).ListAsync()).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
