using System;
using System.IO;
using LatencyBench.Core.MouseTesting;
using LatencyBench.Core.MouseTesting.Models;

namespace LatencyBench.Tests;

/// <summary>
/// Regression coverage for the atomic-write fix: Save() used to be a bare File.WriteAllText with no
/// try/catch, which (a) could truncate the file and lose every saved run if interrupted mid-write, and
/// (b) crashed the whole app on any IOException/UnauthorizedAccessException, since nothing above it —
/// not SaveResult() in the ViewModel, not (before this fix) App.OnDispatcherUnhandledException either —
/// caught it.
/// </summary>
public sealed class MouseTestHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LatencyBenchTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }

    private static MouseTestSession Session(string name = "Test Mouse") => new()
    {
        DeviceFriendlyName = name,
        SampleCount = 100,
        DurationMs = 1000,
        EffectiveReportRateHz = 1000,
        TotalPathCounts = 500,
        NetDisplacementCounts = 400,
        PeakSpeedCountsPerMs = 10,
        AngleSnapScore = 0.5,
    };

    [Fact]
    public void AddPersistsAcrossANewStoreInstance()
    {
        string path = Path.Combine(_directory, "history.json");
        var store = new MouseTestHistoryStore(path);

        store.Add(Session("First Mouse"));

        var reloaded = new MouseTestHistoryStore(path);
        Assert.Single(reloaded.Results);
        Assert.Equal("First Mouse", reloaded.Results[0].DeviceFriendlyName);
    }

    [Fact]
    public void SaveNeverLeavesATemporaryFileBehind()
    {
        string path = Path.Combine(_directory, "history.json");
        var store = new MouseTestHistoryStore(path);

        store.Add(Session());
        store.Add(Session());

        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void AFailedSaveThrowsAClearErrorInsteadOfARawIOException()
    {
        // Forces the write to fail deterministically the same way AtomicFileWriterTests does: the
        // "file" path is actually a pre-existing directory, so the final swap fails no matter what
        // exact I/O condition would trigger it on a real machine (disk full, OneDrive lock, permissions).
        string path = Path.Combine(_directory, "history.json");
        Directory.CreateDirectory(path);
        var store = new MouseTestHistoryStore(path);

        var ex = Assert.Throws<InvalidOperationException>(() => store.Add(Session()));

        Assert.Contains("mouse test history", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void AFailedSaveDoesNotCorruptAnyPreviouslySavedHistory()
    {
        // The whole point of the atomic write: a save that fails must never touch the file that was
        // already on disk from the last successful save.
        string path = Path.Combine(_directory, "history.json");
        var store = new MouseTestHistoryStore(path);
        store.Add(Session("Kept Mouse"));

        string before = File.ReadAllText(path);

        // Forces the very first step of the next save — writing the ".tmp" companion file — to fail,
        // before the real file at `path` is touched at all: turning that companion path into a
        // directory means File.WriteAllText can't write into it, deterministically, without relying
        // on any particular disk/permission condition.
        string temporaryPath = path + ".tmp";
        Directory.CreateDirectory(temporaryPath);
        try
        {
            Assert.ThrowsAny<Exception>(() => store.Add(Session("Should Not Persist")));
        }
        finally
        {
            Directory.Delete(temporaryPath);
        }

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void AFailedAddRemovesTheEntryFromInMemoryResultsToo()
    {
        // The bug this guards against: Add() used to add to Results, then call Save() with no
        // try/catch — if Save() threw, Results still showed the new run as recorded even though
        // nothing was ever written to disk.
        string path = Path.Combine(_directory, "history.json");
        var store = new MouseTestHistoryStore(path);
        store.Add(Session("Kept Mouse"));

        string temporaryPath = path + ".tmp";
        Directory.CreateDirectory(temporaryPath);
        try
        {
            Assert.ThrowsAny<Exception>(() => store.Add(Session("Should Not Persist")));
        }
        finally
        {
            Directory.Delete(temporaryPath);
        }

        Assert.Single(store.Results);
        Assert.Equal("Kept Mouse", store.Results[0].DeviceFriendlyName);
    }

    [Fact]
    public void AFailedClearRestoresTheInMemoryResults()
    {
        string path = Path.Combine(_directory, "history.json");
        var store = new MouseTestHistoryStore(path);
        store.Add(Session("Kept Mouse A"));
        store.Add(Session("Kept Mouse B"));

        string temporaryPath = path + ".tmp";
        Directory.CreateDirectory(temporaryPath);
        try
        {
            Assert.ThrowsAny<Exception>(() => store.Clear());
        }
        finally
        {
            Directory.Delete(temporaryPath);
        }

        Assert.Equal(2, store.Results.Count);
        Assert.Equal("Kept Mouse A", store.Results[0].DeviceFriendlyName);
        Assert.Equal("Kept Mouse B", store.Results[1].DeviceFriendlyName);
    }
}
