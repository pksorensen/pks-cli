using FluentAssertions;
using PKS.Commands.Brain;
using PKS.Infrastructure.Services.Brain;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Brain;

/// A push offers hundreds of chunks and thousands of blobs. What it must never do
/// is emit a line per artifact into a terminal — that is scrollback, not progress.
public class BrainPushProgressTests
{
    private static (TestConsole console, IDisposable scope) Capture()
    {
        var console = new TestConsole();
        var previous = global::Spectre.Console.AnsiConsole.Console;
        global::Spectre.Console.AnsiConsole.Console = console;

        return (console, new Restore(() => global::Spectre.Console.AnsiConsole.Console = previous));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    [Fact]
    public void Line_progress_stays_quiet_about_individual_artifacts()
    {
        var (console, scope) = Capture();
        using (scope)
        {
            var progress = new LinePushProgress(verbose: false);
            progress.Planned(chunks: 2, blobs: 3, bytes: 1024);
            progress.SyncOpened("sy_abc", 2, 3);

            for (var i = 0; i < 5; i++)
                progress.Uploaded("chunk", $"{i}0123456789abcdef", duplicate: false);

            progress.Committed(new CommitResult { Queued = 2, QueuedEvents = 40 });
        }

        console.Output.Should().Contain("Offering");
        console.Output.Should().Contain("sy_abc");
        console.Output.Should().Contain("commit");
        console.Output.Should().NotContain("0123456789ab", "per-artifact lines are the noise this replaced");
    }

    [Fact]
    public void Verbose_restores_the_per_artifact_trace()
    {
        var (console, scope) = Capture();
        using (scope)
        {
            new LinePushProgress(verbose: true).Uploaded("chunk", "00123456789abcdef", duplicate: false);
        }

        console.Output.Should().Contain("00123456789a", "verbose prints the hash prefix per artifact");
    }

    [Fact]
    public async Task Bar_renders_counts_and_never_exceeds_the_total()
    {
        var console = new TestConsole().Interactive();
        console.Profile.Width = 100;

        await console.Progress()
            .AutoClear(false)
            .Columns(new ProgressColumn[] { new TaskDescriptionColumn(), new ProgressBarColumn(), new CountColumn(), new PercentageColumn() })
            .StartAsync(ctx =>
            {
                var progress = new BarPushProgress(ctx);
                progress.Planned(chunks: 400, blobs: 200, bytes: 0);
                progress.Advanced(new PushProgressSnapshot(120, 400, 30, 200, 0));
                // A retried batch re-walks: the same number must not push it past 100%.
                progress.Advanced(new PushProgressSnapshot(700, 400, 30, 200, 0));
                progress.Projecting(new ProjectionStatus { LogLines = 400, Projected = 400, Done = true });

                return Task.CompletedTask;
            });

        console.Output.Should().Contain("400/400");
        console.Output.Should().Contain("Folding");
        console.Output.Should().NotContain("700");
    }
}
