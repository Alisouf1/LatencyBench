using System;
using System.Diagnostics;
using System.Threading.Tasks;
using LatencyBench.Core.Tweaks;

namespace LatencyBench.Tests;

/// <summary>
/// The timeout must bound every wait in the run, including draining the redirected pipes.
///
/// <para>
/// The regression: after WaitForExit succeeded, the code called Task.WaitAll(tasks, timeout) and
/// discarded its return value, then read Task.Result. Result blocks with no timeout on a task that
/// has not completed, so a stream reader that never finished hung the call permanently - an unbounded
/// wait on exactly the path the timeout exists to bound. A child exiting does not close the pipe if a
/// grandchild inherited the handle, which is the real-world shape of this: cmd.exe spawning a
/// detached process, or a tool that starts a background helper.
/// </para>
/// </summary>
public sealed class ConsoleToolRunnerTimeoutTests
{
    [Fact]
    public void AToolThatExitsPromptlyReturnsItsOutput()
    {
        var (exitCode, stdOut, _) = ConsoleToolRunner.Run(
            "cmd.exe", TimeSpan.FromSeconds(20), "/c", "echo", "latencybench-probe");

        Assert.Equal(0, exitCode);
        Assert.Contains("latencybench-probe", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonZeroExitCodeIsReturnedRatherThanThrown()
    {
        // Callers decide what a non-zero exit means; the runner's job is to report it.
        var (exitCode, _, _) = ConsoleToolRunner.Run("cmd.exe", TimeSpan.FromSeconds(20), "/c", "exit", "3");

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public void AToolThatOutlivesTheTimeoutIsKilledAndReported()
    {
        // ping rather than "cmd /c pause": with CreateNoWindow there is no console attached and stdin
        // is not redirected, so pause reads EOF and returns immediately instead of blocking. ping
        // genuinely runs for ~30 seconds regardless of console state.
        var stopwatch = Stopwatch.StartNew();

        Assert.Throws<TimeoutException>(() => ConsoleToolRunner.Run(
            "ping.exe", TimeSpan.FromSeconds(2), "-n", "30", "127.0.0.1"));

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"expected to give up near the 2s timeout, took {stopwatch.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public void AGrandchildHoldingThePipeOpenTimesOutInsteadOfHangingForever()
    {
        // The exact regression. cmd.exe exits immediately, but the detached child it starts inherits
        // the stdout handle and keeps it open for well beyond the timeout, so ReadToEndAsync never
        // completes. Before the fix this reached Task.Result on an incomplete task and blocked with
        // no bound; now the discarded WaitAll result is acted on.
        var stopwatch = Stopwatch.StartNew();

        Assert.Throws<TimeoutException>(() => ConsoleToolRunner.Run(
            "cmd.exe",
            TimeSpan.FromSeconds(3),
            "/c", "start", "/b", "cmd.exe", "/c", "ping -n 30 127.0.0.1"));

        stopwatch.Stop();

        // The bound that matters is that it returns at all. Generous ceiling because the process
        // teardown itself takes a moment; the pre-fix behaviour was to never return.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(25),
            $"the call did not honour its timeout - took {stopwatch.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public void AMissingExecutableFailsImmediatelyRatherThanWaitingOutTheTimeout()
    {
        var stopwatch = Stopwatch.StartNew();

        Assert.ThrowsAny<Exception>(() => ConsoleToolRunner.Run(
            "latencybench-no-such-tool.exe", TimeSpan.FromSeconds(20), "/c"));

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"a missing executable should fail fast, took {stopwatch.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public async Task ConcurrentRunsDoNotInterfereWithEachOther()
    {
        // Apply runs tweaks in sequence, but nothing prevents two subsystems shelling out at once.
        string[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            var (_, stdOut, _) = ConsoleToolRunner.Run(
                "cmd.exe", TimeSpan.FromSeconds(20), "/c", "echo", $"probe-{i}");
            return stdOut.Trim();
        })));

        for (int i = 0; i < 8; i++)
        {
            Assert.Contains(results, r => r.Contains($"probe-{i}", StringComparison.Ordinal));
        }
    }
}
