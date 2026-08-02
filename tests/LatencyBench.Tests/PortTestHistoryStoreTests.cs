using System;
using System.IO;
using LatencyBench.Core.Models;
using LatencyBench.Core.PortTesting;

namespace LatencyBench.Tests;

/// <summary>Mirrors MouseTestHistoryStoreTests — PortTestHistoryStore had the exact same unguarded,
/// non-atomic Save() and got the exact same fix.</summary>
public sealed class PortTestHistoryStoreTests : IDisposable
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

    private static PortRankResult Result(string label = "Port 1") => new()
    {
        PortLabel = label,
        PortLocation = "Controller 1, port 1",
    };

    [Fact]
    public void AddPersistsAcrossANewStoreInstance()
    {
        string path = Path.Combine(_directory, "history.json");
        var store = new PortTestHistoryStore(path);

        store.Add(Result("First Port"));

        var reloaded = new PortTestHistoryStore(path);
        Assert.Single(reloaded.Results);
        Assert.Equal("First Port", reloaded.Results[0].PortLabel);
    }

    [Fact]
    public void SaveNeverLeavesATemporaryFileBehind()
    {
        string path = Path.Combine(_directory, "history.json");
        var store = new PortTestHistoryStore(path);

        store.Add(Result());
        store.Add(Result());

        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void AFailedSaveThrowsAClearErrorInsteadOfARawIOException()
    {
        string path = Path.Combine(_directory, "history.json");
        Directory.CreateDirectory(path);
        var store = new PortTestHistoryStore(path);

        var ex = Assert.Throws<InvalidOperationException>(() => store.Add(Result()));

        Assert.Contains("port test history", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void AFailedSaveDoesNotCorruptAnyPreviouslySavedHistory()
    {
        string path = Path.Combine(_directory, "history.json");
        var store = new PortTestHistoryStore(path);
        store.Add(Result("Kept Port"));

        string before = File.ReadAllText(path);

        string temporaryPath = path + ".tmp";
        Directory.CreateDirectory(temporaryPath);
        try
        {
            Assert.ThrowsAny<Exception>(() => store.Add(Result("Should Not Persist")));
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
        var store = new PortTestHistoryStore(path);
        store.Add(Result("Kept Port"));

        string temporaryPath = path + ".tmp";
        Directory.CreateDirectory(temporaryPath);
        try
        {
            Assert.ThrowsAny<Exception>(() => store.Add(Result("Should Not Persist")));
        }
        finally
        {
            Directory.Delete(temporaryPath);
        }

        Assert.Single(store.Results);
        Assert.Equal("Kept Port", store.Results[0].PortLabel);
    }

    [Fact]
    public void AFailedClearRestoresTheInMemoryResults()
    {
        string path = Path.Combine(_directory, "history.json");
        var store = new PortTestHistoryStore(path);
        store.Add(Result("Kept Port A"));
        store.Add(Result("Kept Port B"));

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
        Assert.Equal("Kept Port A", store.Results[0].PortLabel);
        Assert.Equal("Kept Port B", store.Results[1].PortLabel);
    }
}
