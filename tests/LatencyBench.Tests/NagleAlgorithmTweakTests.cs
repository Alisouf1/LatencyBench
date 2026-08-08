using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;
using LatencyBench.Core.Tweaks.Network;

namespace LatencyBench.Tests;

/// <summary>
/// Covers the Nagle tweak through its seams, so nothing here touches the real TCP/IP interface keys.
///
/// This tweak writes to every network adapter rather than one device, which makes two things matter
/// more than usual: an adapter that had no value before must end up with no value after reverting,
/// not a fabricated default, and every adapter must be handled independently so one machine's mix of
/// configured and unconfigured interfaces cannot leave the others half-done.
/// </summary>
public sealed class NagleAlgorithmTweakTests : IDisposable
{
    private const string Eth = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{ETH}";
    private const string WiFi = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{WIFI}";

    private readonly string _directory;
    private readonly TweakBackupStore _backupStore;

    public NagleAlgorithmTweakTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "LatencyBenchNagle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _backupStore = new TweakBackupStore(Path.Combine(_directory, "tweak-backups.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>Models the per-adapter interface keys in memory.</summary>
    private sealed class FakeNagle : NagleAlgorithmTweak
    {
        private readonly string[] _adapters;
        private readonly Dictionary<string, int> _values = new(StringComparer.OrdinalIgnoreCase);

        public FakeNagle(TweakBackupStore backupStore, params string[] adapters)
            : base(backupStore) => _adapters = adapters;

        public List<string> Operations { get; } = new();

        private static string Key(string path, string name) => $"{path}|{name}";

        public void Seed(string path, string name, int value) => _values[Key(path, name)] = value;

        public int? Peek(string path, string name) =>
            _values.TryGetValue(Key(path, name), out int v) ? v : null;

        protected override IEnumerable<string> GetAdapterKeyPaths() => _adapters;

        protected override int? ReadValue(string keyPath, string valueName) => Peek(keyPath, valueName);

        protected override void WriteValue(string keyPath, string valueName, int value)
        {
            Operations.Add($"write {valueName}={value}");
            _values[Key(keyPath, valueName)] = value;
        }

        protected override void DeleteValue(string keyPath, string valueName)
        {
            Operations.Add($"delete {valueName}");
            _values.Remove(Key(keyPath, valueName));
        }
    }

    // --- State -----------------------------------------------------------------------------------

    [Fact]
    public void StateIsUnknownWhenThereAreNoAdaptersToInspect()
    {
        // No adapters is not "not applied" - there is nothing to have applied it to.
        Assert.Equal(TweakState.Unknown, new FakeNagle(_backupStore).GetState());
    }

    [Fact]
    public void StateIsAppliedOnlyWhenEveryAdapterHasBothValues()
    {
        var tweak = new FakeNagle(_backupStore, Eth, WiFi);
        foreach (string adapter in new[] { Eth, WiFi })
        {
            tweak.Seed(adapter, "TcpAckFrequency", 1);
            tweak.Seed(adapter, "TCPNoDelay", 1);
        }

        Assert.Equal(TweakState.Applied, tweak.GetState());
    }

    [Fact]
    public void OneUnconfiguredAdapterMakesTheWholeTweakNotApplied()
    {
        // Reporting Applied while one adapter still batches packets would claim a machine-wide
        // change that did not happen.
        var tweak = new FakeNagle(_backupStore, Eth, WiFi);
        tweak.Seed(Eth, "TcpAckFrequency", 1);
        tweak.Seed(Eth, "TCPNoDelay", 1);

        Assert.Equal(TweakState.NotApplied, tweak.GetState());
    }

    [Fact]
    public void HalfConfiguringOneAdapterIsNotApplied()
    {
        // Both values are needed; one alone does not disable Nagle.
        var tweak = new FakeNagle(_backupStore, Eth);
        tweak.Seed(Eth, "TcpAckFrequency", 1);

        Assert.Equal(TweakState.NotApplied, tweak.GetState());
    }

    [Fact]
    public void AValueOtherThanOneIsNotApplied()
    {
        var tweak = new FakeNagle(_backupStore, Eth);
        tweak.Seed(Eth, "TcpAckFrequency", 2);
        tweak.Seed(Eth, "TCPNoDelay", 1);

        Assert.Equal(TweakState.NotApplied, tweak.GetState());
    }

    // --- Apply -----------------------------------------------------------------------------------

    [Fact]
    public void ApplySetsBothValuesOnEveryAdapter()
    {
        var tweak = new FakeNagle(_backupStore, Eth, WiFi);

        tweak.Apply();

        foreach (string adapter in new[] { Eth, WiFi })
        {
            Assert.Equal(1, tweak.Peek(adapter, "TcpAckFrequency"));
            Assert.Equal(1, tweak.Peek(adapter, "TCPNoDelay"));
        }

        Assert.Equal(TweakState.Applied, tweak.GetState());
    }

    [Fact]
    public void ApplyRecordsTheCurrentValueBeforeOverwritingIt()
    {
        // The stash must happen before the write, or the backup records the value this tweak just
        // set and reverting restores nothing.
        var tweak = new FakeNagle(_backupStore, Eth);
        tweak.Seed(Eth, "TcpAckFrequency", 13);
        tweak.Seed(Eth, "TCPNoDelay", 7);

        tweak.Apply();
        tweak.Revert();

        Assert.Equal(13, tweak.Peek(Eth, "TcpAckFrequency"));
        Assert.Equal(7, tweak.Peek(Eth, "TCPNoDelay"));
    }

    // --- Revert ----------------------------------------------------------------------------------

    /// <summary>
    /// The behaviour that matters most here. Windows leaves these values absent on a default install,
    /// so reverting has to REMOVE what this tweak wrote rather than write some assumed default - an
    /// interface with an explicit TcpAckFrequency=2 is not the same as one with none.
    /// </summary>
    [Fact]
    public void RevertRemovesValuesThatDidNotExistBeforehand()
    {
        var tweak = new FakeNagle(_backupStore, Eth);

        tweak.Apply();
        Assert.Equal(1, tweak.Peek(Eth, "TcpAckFrequency"));

        tweak.Revert();

        Assert.Null(tweak.Peek(Eth, "TcpAckFrequency"));
        Assert.Null(tweak.Peek(Eth, "TCPNoDelay"));
    }

    [Fact]
    public void RevertHandlesAMixOfPreviouslySetAndPreviouslyAbsentAdapters()
    {
        // A real machine has both: an adapter someone configured by hand, and one Windows left alone.
        var tweak = new FakeNagle(_backupStore, Eth, WiFi);
        tweak.Seed(Eth, "TcpAckFrequency", 5);
        tweak.Seed(Eth, "TCPNoDelay", 5);

        tweak.Apply();
        tweak.Revert();

        Assert.Equal(5, tweak.Peek(Eth, "TcpAckFrequency"));
        Assert.Equal(5, tweak.Peek(Eth, "TCPNoDelay"));
        Assert.Null(tweak.Peek(WiFi, "TcpAckFrequency"));
        Assert.Null(tweak.Peek(WiFi, "TCPNoDelay"));
    }

    [Fact]
    public void RevertWithNoBackupAtAllRemovesTheValues()
    {
        // No recorded backup gives nothing to put back, and removing what the tweak writes returns
        // the interface to Windows' own default behaviour.
        var tweak = new FakeNagle(_backupStore, Eth);
        tweak.Seed(Eth, "TcpAckFrequency", 1);
        tweak.Seed(Eth, "TCPNoDelay", 1);

        tweak.Revert();

        Assert.Null(tweak.Peek(Eth, "TcpAckFrequency"));
        Assert.Null(tweak.Peek(Eth, "TCPNoDelay"));
    }

    [Fact]
    public void AnUnparseableBackupLeavesTheValueAloneRatherThanGuessing()
    {
        var tweak = new FakeNagle(_backupStore, Eth);
        tweak.Apply();

        _backupStore.Save($@"nagle:{Eth}\TcpAckFrequency", "corrupted");
        tweak.Operations.Clear();

        tweak.Revert();

        Assert.DoesNotContain(tweak.Operations, op => op.StartsWith("write TcpAckFrequency", StringComparison.Ordinal));
    }

    [Fact]
    public void RevertClearsTheBackupSoASecondRevertIsANoOp()
    {
        var tweak = new FakeNagle(_backupStore, Eth);
        tweak.Seed(Eth, "TcpAckFrequency", 9);
        tweak.Seed(Eth, "TCPNoDelay", 9);

        tweak.Apply();
        tweak.Revert();
        Assert.Equal(9, tweak.Peek(Eth, "TcpAckFrequency"));

        tweak.Revert();

        // The second revert has no backup, so it removes rather than restoring - and must not
        // resurrect the value it already put back.
        Assert.Null(tweak.Peek(Eth, "TcpAckFrequency"));
    }

    [Fact]
    public void ApplyRevertApplyRecordsTheBaselineFreshEachTime()
    {
        var tweak = new FakeNagle(_backupStore, Eth);
        tweak.Seed(Eth, "TcpAckFrequency", 4);
        tweak.Seed(Eth, "TCPNoDelay", 4);

        tweak.Apply();
        tweak.Revert();
        Assert.Equal(4, tweak.Peek(Eth, "TcpAckFrequency"));

        tweak.Seed(Eth, "TcpAckFrequency", 6);
        tweak.Seed(Eth, "TCPNoDelay", 6);
        tweak.Apply();
        tweak.Revert();

        Assert.Equal(6, tweak.Peek(Eth, "TcpAckFrequency"));
    }

    [Fact]
    public void EachAdapterIsBackedUpUnderItsOwnKey()
    {
        // A shared backup key would make the second adapter overwrite the first's recorded value and
        // restore the wrong number to both.
        var tweak = new FakeNagle(_backupStore, Eth, WiFi);
        tweak.Seed(Eth, "TcpAckFrequency", 11);
        tweak.Seed(WiFi, "TcpAckFrequency", 22);
        tweak.Seed(Eth, "TCPNoDelay", 11);
        tweak.Seed(WiFi, "TCPNoDelay", 22);

        tweak.Apply();
        tweak.Revert();

        Assert.Equal(11, tweak.Peek(Eth, "TcpAckFrequency"));
        Assert.Equal(22, tweak.Peek(WiFi, "TcpAckFrequency"));
    }
}
