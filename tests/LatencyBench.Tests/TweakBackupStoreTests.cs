using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LatencyBench.Core.Tweaks;

namespace LatencyBench.Tests;

/// <summary>
/// The backup file is the only record of what the machine looked like before LatencyBench touched
/// it, so these tests are about not losing it rather than about happy-path storage.
/// </summary>
public sealed class TweakBackupStoreTests : IDisposable
{
	private readonly string _directory;
	private readonly string _filePath;

	public TweakBackupStoreTests()
	{
		_directory = Path.Combine(Path.GetTempPath(), "LatencyBenchTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_directory);
		_filePath = Path.Combine(_directory, "tweak-backups.json");
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_directory, recursive: true);
		}
		catch (IOException)
		{
			// A leftover temp directory is not worth failing a test run over.
		}
	}

	[Fact]
	public void RoundTripsAValue()
	{
		var store = new TweakBackupStore(_filePath);

		store.Save("registry:HKLM\\Foo\\Bar", "42");

		Assert.Equal("42", store.TryGet("registry:HKLM\\Foo\\Bar"));
	}

	[Fact]
	public void ReturnsNullForAnUnknownKey()
	{
		var store = new TweakBackupStore(_filePath);

		Assert.Null(store.TryGet("never-saved"));
	}

	[Fact]
	public void PersistsAcrossInstances()
	{
		new TweakBackupStore(_filePath).Save("key", "value");

		Assert.Equal("value", new TweakBackupStore(_filePath).TryGet("key"));
	}

	[Fact]
	public void RemoveDeletesTheEntry()
	{
		var store = new TweakBackupStore(_filePath);
		store.Save("key", "value");

		store.Remove("key");

		Assert.Null(store.TryGet("key"));
		Assert.Null(new TweakBackupStore(_filePath).TryGet("key"));
	}

	[Fact]
	public void CreatesTheContainingDirectory()
	{
		string nested = Path.Combine(_directory, "a", "b", "backups.json");

		var store = new TweakBackupStore(nested);
		store.Save("key", "value");

		Assert.True(File.Exists(nested));
	}

	[Fact]
	public void LeavesNoTemporaryFileBehindAfterASave()
	{
		var store = new TweakBackupStore(_filePath);

		store.Save("a", "1");
		store.Save("b", "2");

		Assert.False(File.Exists(_filePath + ".tmp"));
	}

	[Fact]
	public void CorruptFileIsQuarantinedRatherThanSilentlyTreatedAsEmpty()
	{
		// Silently starting over would make every subsequent Revert a no-op that reports success —
		// the user would believe their machine had been restored when it had not.
		File.WriteAllText(_filePath, "{ this is not json");

		var store = new TweakBackupStore(_filePath);

		Assert.Throws<InvalidOperationException>(() => store.TryGet("anything"));

		string[] quarantined = Directory.GetFiles(_directory, "*.corrupt-*");
		Assert.Single(quarantined);
		Assert.Contains("this is not json", File.ReadAllText(quarantined[0]));
	}

	[Fact]
	public void GetAllReturnsEverySavedEntry()
	{
		var store = new TweakBackupStore(_filePath);
		store.Save("one", "1");
		store.Save("two", "2");

		var all = store.GetAll();

		Assert.Equal(2, all.Count);
		Assert.Equal("1", all["one"]);
		Assert.Equal("2", all["two"]);
	}

	[Fact]
	public void GetAllReturnsASnapshotThatLaterWritesDoNotMutate()
	{
		var store = new TweakBackupStore(_filePath);
		store.Save("one", "1");

		var snapshot = store.GetAll();
		store.Save("two", "2");

		Assert.Single(snapshot);
	}

	[Fact]
	public async Task ConcurrentSavesDoNotLoseEntries()
	{
		// The pre-fix implementation did read-modify-write with no lock, so two tweaks applied at the
		// same time could each persist a dictionary that lacked the other's entry.
		var store = new TweakBackupStore(_filePath);
		const int writers = 32;

		await Task.WhenAll(Enumerable.Range(0, writers)
			.Select(i => Task.Run(() => store.Save($"key-{i}", i.ToString()))));

		var reloaded = new TweakBackupStore(_filePath).GetAll();

		Assert.Equal(writers, reloaded.Count);
		for (int i = 0; i < writers; i++)
		{
			Assert.Equal(i.ToString(), reloaded[$"key-{i}"]);
		}
	}

	[Fact]
	public void WritesValidJsonThatAnIndependentReaderCanParse()
	{
		var store = new TweakBackupStore(_filePath);
		store.Save("powercfg:scheme\\sub\\setting", "100");

		var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath));

		Assert.NotNull(parsed);
		Assert.Equal("100", parsed!["powercfg:scheme\\sub\\setting"]);
	}

	[Fact]
	public void OverwritingAKeyKeepsTheMostRecentValue()
	{
		var store = new TweakBackupStore(_filePath);

		store.Save("key", "first");
		store.Save("key", "second");

		Assert.Equal("second", new TweakBackupStore(_filePath).TryGet("key"));
	}

	[Fact]
	public void RemovingAnAbsentKeyIsANoOp()
	{
		var store = new TweakBackupStore(_filePath);
		store.Save("present", "1");

		store.Remove("absent");

		Assert.Equal("1", store.TryGet("present"));
	}
}
