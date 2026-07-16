using System.Diagnostics;
using System.Text;

namespace PKS.Commands.Claude;

/// <summary>
/// Drives a detached tmux session running `claude --dangerously-skip-permissions`, sends
/// `/usage`, and returns the rendered pane text once the Usage panel appears. Never uses a
/// tmux library — shells out to the `tmux` binary directly (same convention as `stty` calls
/// elsewhere in this codebase). The tmux session (and its scratch cwd) are always cleaned up,
/// even on timeout/error.
/// </summary>
public static class UsageTmuxDriver
{
    private const string SessionPrefix = "pks-usage-";
    private const int Width = 200;
    // Tall viewport so the whole /usage panel renders without a fold. `capture-pane` only
    // returns the visible viewport, so a short height silently drops any `Current week
    // (<Model>)` blocks pushed below the fold (the panel overflows 50 rows — the fixture's
    // trailing scroll indicator proves it).
    private const int Height = 200;
    private const int BootPollMs = 500;
    private const int RenderPollMs = 500;
    private const int SlashSettleMs = 1500;
    // Once the session block has painted, wait at most this long for the (slower / occasionally
    // throttled) weekly block to join it, then return best-effort with whatever parsed. This is
    // a GRACE window, never a hard requirement: blocking indefinitely on the weekly block is
    // what made `pks claude limits` hang at "Capturing…". The session block always paints fast;
    // the weekly block usually lands within a second or two of it.
    private const int WeekGraceMs = 6000;

    /// <summary>
    /// Spawn claude in a fresh detached tmux session, drive it through `/usage`, and return the
    /// raw captured pane text once the panel is parseable. Returns as soon as BOTH the session
    /// and weekly blocks parse; if only the session block has painted, waits a short grace
    /// window for the weekly block and then returns best-effort — it never blocks indefinitely
    /// on the weekly rows. Always kills the tmux session (and best-effort deletes the scratch
    /// cwd) before returning or throwing.
    /// </summary>
    public static async Task<string> CaptureUsagePanelAsync(CancellationToken ct, Action<string>? debugLog = null)
    {
        var name = SessionPrefix + Guid.NewGuid().ToString("N")[..8];
        var scratch = Directory.CreateTempSubdirectory("pks-usage-").FullName;

        try
        {
            debugLog?.Invoke($"tmux session {name} in {scratch}");

            await RunAsync("tmux",
                $"new-session -d -s {name} -x {Width} -y {Height} -c \"{scratch}\" \"claude --dangerously-skip-permissions\"",
                ct);

            // 1. Wait for boot — the input prompt box present.
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var pane = await Capture(name, ct);
                if (IsBooted(pane))
                {
                    break;
                }

                await Task.Delay(BootPollMs, ct);
            }

            // 2. Type "/usage" literally, let autocomplete settle, then press Enter.
            await SubmitUsageAsync(name, ct);

            // 3. Wait for the panel to render. Return the moment BOTH session + week parse.
            //    If only the session block has painted, hold it and give the weekly block a
            //    bounded grace window, then return best-effort — never hang on the week rows.
            string? sessionOnlyPane = null;
            var graceDeadline = Stopwatch.StartNew();

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var pane = await Capture(name, ct);
                var blocks = UsagePanelParser.TryParse(pane, DateTimeOffset.UtcNow);
                var hasSession = blocks.Any(b => b.Kind == LimitKind.Session);
                var hasWeek = blocks.Any(b => b.Kind == LimitKind.Week && b.Model == null);

                if (hasSession && hasWeek)
                {
                    return pane;
                }

                if (hasSession)
                {
                    if (sessionOnlyPane == null)
                    {
                        // First frame with the session block — start the weekly grace clock.
                        sessionOnlyPane = pane;
                        graceDeadline.Restart();
                    }
                    else
                    {
                        sessionOnlyPane = pane;
                        if (graceDeadline.ElapsedMilliseconds >= WeekGraceMs)
                        {
                            debugLog?.Invoke("weekly block did not paint within grace — returning session-only");
                            return pane;
                        }
                    }
                }

                await Task.Delay(RenderPollMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Timed out capturing /usage from Claude Code inside tmux.");
        }
        finally
        {
            await RunBestEffort("tmux", $"kill-session -t {name}");
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch
            {
                // best-effort cleanup only
            }
        }
    }

    /// <summary>
    /// Boot is complete when the interactive bottom status/hint line has rendered. Matched
    /// case-insensitively against the markers Claude Code actually prints once the input box
    /// is live: the "bypass permissions on" / "for shortcuts" hint, or "shift+tab to cycle".
    /// (The earlier "│ >" / "Bypassing Permissions" sentinels never matched this build.)
    /// </summary>
    internal static bool IsBooted(string pane) =>
        pane.Trim().Length > 40 &&
        (pane.Contains("bypass permissions", StringComparison.OrdinalIgnoreCase) ||
         pane.Contains("for shortcuts", StringComparison.OrdinalIgnoreCase) ||
         pane.Contains("shift+tab to cycle", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Type "/usage" literally, let the slash-command autocomplete settle, then press Enter.
    /// </summary>
    private static async Task SubmitUsageAsync(string name, CancellationToken ct)
    {
        await RunAsync("tmux", $"send-keys -t {name} -l \"/usage\"", ct);
        await Task.Delay(SlashSettleMs, ct);
        await RunAsync("tmux", $"send-keys -t {name} Enter", ct);
    }

    private static async Task<string> Capture(string name, CancellationToken ct)
    {
        var (stdout, _) = await RunAsync("tmux", $"capture-pane -p -t {name}", ct);
        return stdout;
    }

    private static async Task<(string Stdout, string Stderr)> RunAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        return (stdout.ToString(), stderr.ToString());
    }

    private static async Task RunBestEffort(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // ignore — fire-and-forget cleanup
                }
            }
        }
        catch
        {
            // ignore — best-effort cleanup only, never let this mask the real error/result
        }
    }
}
