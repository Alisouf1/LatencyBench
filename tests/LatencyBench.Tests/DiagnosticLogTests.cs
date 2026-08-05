using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LatencyBench.Core.Diagnostics;

namespace LatencyBench.Tests;

/// <summary>
/// Serialised against the rest of the suite. The log's path override is process-wide static state,
/// and now that application code logs during its own tests, any class running in parallel with this
/// one would write into the file under assertion here — or reset the override mid-test and send these
/// writes to the real user profile. That is a genuine hazard of the override, not a quirk of the
/// assertions, so it is fixed by not running these concurrently with anything else.
/// </summary>
[CollectionDefinition("DiagnosticLog", DisableParallelization = true)]
public sealed class DiagnosticLogCollection
{
}

/// <summary>
/// The log exists to diagnose failures that cannot be reproduced on a developer machine, so it has
/// to be dependable in exactly the conditions where things are already going wrong: concurrent
/// writers, an unwritable path, and a file that would otherwise grow forever.
/// </summary>
[Collection("DiagnosticLog")]
public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public DiagnosticLogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "LatencyBenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "activity.log");
        DiagnosticLog.OverridePathForTesting(_path);
    }

    public void Dispose()
    {
        DiagnosticLog.OverridePathForTesting(null);

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void WritesTheLevelCategoryAndMessage()
    {
        DiagnosticLog.Info("Optimize", "plan built");

        string contents = File.ReadAllText(_path);

        Assert.Contains("INFO", contents, StringComparison.Ordinal);
        Assert.Contains("[Optimize]", contents, StringComparison.Ordinal);
        Assert.Contains("plan built", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordsTheExceptionDetailForErrors()
    {
        DiagnosticLog.Error("Optimize", "apply threw", new InvalidOperationException("boom"));

        string contents = File.ReadAllText(_path);

        Assert.Contains("ERROR", contents, StringComparison.Ordinal);
        Assert.Contains("boom", contents, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), contents, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatesTheDirectoryWhenItDoesNotExistYet()
    {
        string nested = Path.Combine(_directory, "does", "not", "exist", "activity.log");
        DiagnosticLog.OverridePathForTesting(nested);

        DiagnosticLog.Info("App", "startup");

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void AnUnwritablePathIsSwallowedRatherThanThrowingIntoTheCaller()
    {
        // A logger that throws would take down the very operation it is meant to be observing. On
        // Windows a path containing an invalid character can never be created.
        DiagnosticLog.OverridePathForTesting(Path.Combine(_directory, "in|valid", "activity.log"));

        Exception? failure = Record.Exception(() => DiagnosticLog.Info("App", "should not throw"));

        Assert.Null(failure);
    }

    [Fact]
    public async Task ConcurrentWritersDoNotLoseOrInterleaveLines()
    {
        const int writers = 8;
        const int perWriter = 40;

        // Tagged uniquely so the assertion counts only this test's own writes. Even serialised, the
        // app code under test in the same process may log, and counting raw line totals would make
        // this fail for a reason unrelated to what it is checking.
        string token = Guid.NewGuid().ToString("N");

        await Task.WhenAll(Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            for (int i = 0; i < perWriter; i++)
            {
                DiagnosticLog.Info("Concurrency", $"{token}-writer{writer}-line{i}");
            }
        })));

        string[] mine = File.ReadAllLines(_path)
            .Where(line => line.Contains(token, StringComparison.Ordinal))
            .ToArray();

        // Nothing lost: every write from every thread landed as its own line.
        Assert.Equal(writers * perWriter, mine.Length);

        // Nothing torn: each line is a complete record, not two interleaved half-writes.
        Assert.All(mine, line => Assert.Contains("[Concurrency]", line, StringComparison.Ordinal));

        // And every individual message survived intact.
        for (int writer = 0; writer < writers; writer++)
        {
            for (int i = 0; i < perWriter; i++)
            {
                string expected = $"{token}-writer{writer}-line{i}";
                Assert.Contains(mine, line => line.EndsWith(expected, StringComparison.Ordinal));
            }
        }
    }
}
