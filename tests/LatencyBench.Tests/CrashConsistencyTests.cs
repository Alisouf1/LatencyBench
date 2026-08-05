using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LatencyBench.Core.Persistence;
using LatencyBench.Core.Tweaks;

namespace LatencyBench.Tests;

/// <summary>
/// Covers what survives an abrupt termination: the tweak backup store is the only record of what to
/// restore when a tweak is undone, so losing or corrupting it turns a reversible change into a
/// permanent one.
/// </summary>
public sealed class CrashConsistencyTests : IDisposable
{
    private readonly string _directory;

    public CrashConsistencyTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "LatencyBenchCrash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string Path_(string name) => Path.Combine(_directory, name);

    // --- AtomicFileWriter ------------------------------------------------------------------------

    [Fact]
    public void ExistingContentSurvivesWhenTheNewWriteFails()
    {
        // The property that matters: a failed save must never destroy what was already stored.
        string target = Path_("store.json");
        File.WriteAllText(target, "{\"original\":true}");

        // A directory occupying the temp path makes the write fail without touching the target.
        Directory.CreateDirectory(target + ".tmp");

        Assert.ThrowsAny<Exception>(() => AtomicFileWriter.Write(target, "{\"replacement\":true}"));
        Assert.Equal("{\"original\":true}", File.ReadAllText(target));
    }

    [Fact]
    public void NoTemporaryFileIsLeftBehindOnSuccess()
    {
        string target = Path_("store.json");
        AtomicFileWriter.Write(target, "{\"a\":1}");

        Assert.True(File.Exists(target));
        Assert.False(File.Exists(target + ".tmp"), "the temp file must be consumed by the replace");
    }

    [Fact]
    public void WritingCreatesTheFileWhenNoneExisted()
    {
        string target = Path_("new.json");
        AtomicFileWriter.Write(target, "{\"a\":1}");

        Assert.Equal("{\"a\":1}", File.ReadAllText(target));
    }

    [Fact]
    public void RepeatedWritesLeaveExactlyTheLastContent()
    {
        string target = Path_("store.json");
        for (int i = 0; i < 50; i++)
        {
            AtomicFileWriter.Write(target, $"{{\"n\":{i}}}");
        }

        Assert.Equal("{\"n\":49}", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public void ContentIsWrittenAsUtf8WithoutABom()
    {
        // A BOM would be read back as part of the JSON by strict parsers.
        string target = Path_("utf8.json");
        AtomicFileWriter.Write(target, "{\"name\":\"café\"}");

        byte[] bytes = File.ReadAllBytes(target);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "a UTF-8 BOM was written");
        Assert.Equal("{\"name\":\"café\"}", File.ReadAllText(target, Encoding.UTF8));
    }

    [Fact]
    public void LargeContentIsWrittenIntact()
    {
        string target = Path_("large.json");
        string big = "{\"pad\":\"" + new string('x', 4 * 1024 * 1024) + "\"}";

        AtomicFileWriter.Write(target, big);

        Assert.Equal(big.Length, File.ReadAllText(target).Length);
    }

    // --- TweakBackupStore ------------------------------------------------------------------------

    [Fact]
    public void ABackupSurvivesBeingReloadedFromDisk()
    {
        string path = Path_("tweak-backups.json");
        var store = new TweakBackupStore(path);
        store.Save("registry:HKLM\\Foo\\Bar", "1");

        // A new instance over the same file is what happens after a restart.
        var reloaded = new TweakBackupStore(path);

        Assert.Equal("1", reloaded.TryGet("registry:HKLM\\Foo\\Bar"));
    }

    [Fact]
    public void ATruncatedStoreFileDoesNotPreventStartup()
    {
        // Simulates the pre-fix failure mode: metadata committed, data never flushed.
        string path = Path_("tweak-backups.json");
        File.WriteAllText(path, "{\"registry:HKLM\\\\Foo\":");

        Exception? failure = Record.Exception(() => new TweakBackupStore(path));

        Assert.Null(failure);
    }

    [Fact]
    public void AnEmptyStoreFileDoesNotPreventStartup()
    {
        string path = Path_("tweak-backups.json");
        File.WriteAllText(path, string.Empty);

        Exception? failure = Record.Exception(() => new TweakBackupStore(path));

        Assert.Null(failure);
    }

    [Fact]
    public void GarbageInTheStoreFileDoesNotPreventStartup()
    {
        string path = Path_("tweak-backups.json");
        File.WriteAllBytes(path, new byte[] { 0x00, 0xFF, 0x42, 0x13, 0x37 });

        Exception? failure = Record.Exception(() => new TweakBackupStore(path));

        Assert.Null(failure);
    }

    [Fact]
    public void ConcurrentSavesAllPersistAndTheFileStaysParseable()
    {
        // Apply writes a backup per step; a torn write here would lose the ability to revert.
        string path = Path_("tweak-backups.json");
        var store = new TweakBackupStore(path);

        Parallel.For(0, 200, i => store.Save($"key-{i}", i.ToString()));

        var reloaded = new TweakBackupStore(path);
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(i.ToString(), reloaded.TryGet($"key-{i}"));
        }

        using JsonDocument parsed = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(200, parsed.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void RemovingAKeyPersistsAcrossReload()
    {
        string path = Path_("tweak-backups.json");
        var store = new TweakBackupStore(path);
        store.Save("a", "1");
        store.Save("b", "2");
        store.Remove("a");

        var reloaded = new TweakBackupStore(path);

        Assert.Null(reloaded.TryGet("a"));
        Assert.Equal("2", reloaded.TryGet("b"));
    }

    [Fact]
    public void SavingTheSameKeyTwiceKeepsTheLatestValue()
    {
        // Last write wins, and that is correct here rather than merely tolerated. Protecting the
        // original value is the caller's job, and every tweak does it two ways: Apply only saves when
        // the current value differs from the applied one, so re-applying an already-applied tweak
        // records nothing; and Revert calls Remove, so the next Apply legitimately needs to record a
        // fresh baseline. A first-write-wins store would break that second case by pinning a stale
        // value from before the previous revert. Verified against every tweak: each Save is paired
        // with a Remove on revert.
        string path = Path_("tweak-backups.json");
        var store = new TweakBackupStore(path);

        store.Save("k", "original");
        store.Save("k", "modified");

        Assert.Equal("modified", new TweakBackupStore(path).TryGet("k"));
    }

    [Fact]
    public void RemoveThenSaveRecordsTheNewBaseline()
    {
        // The revert-then-reapply cycle the comment above depends on.
        string path = Path_("tweak-backups.json");
        var store = new TweakBackupStore(path);

        store.Save("k", "first-baseline");
        store.Remove("k");
        store.Save("k", "second-baseline");

        Assert.Equal("second-baseline", new TweakBackupStore(path).TryGet("k"));
    }
}
