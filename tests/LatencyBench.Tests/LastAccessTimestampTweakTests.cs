using System;
using System.Collections.Generic;
using System.IO;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.Tweaks.Models;
using LatencyBench.Core.Tweaks.Storage;

namespace LatencyBench.Tests;

/// <summary>
/// Exercises the tweak against a scripted fsutil rather than the real one, so the machine's actual
/// NTFS behaviour is never touched. The real fsutil output shape this is scripted from was confirmed
/// against a live machine: <c>fsutil behavior query disablelastaccess</c> is a single global line
/// with no separate NTFS/ReFS split, unlike DisableDeleteNotify.
/// </summary>
public sealed class LastAccessTimestampTweakTests : IDisposable
{
	private readonly string _backupPath;

	public LastAccessTimestampTweakTests()
	{
		_backupPath = Path.Combine(Path.GetTempPath(), "LatencyBenchTests", Guid.NewGuid().ToString("N") + ".json");
	}

	public void Dispose()
	{
		try
		{
			File.Delete(_backupPath);
		}
		catch (IOException)
		{
		}
	}

	private sealed class ScriptedFsutil
	{
		public int CurrentValue { get; set; } = 2;

		public bool FailNextSet { get; set; }

		public int SetCallCount { get; private set; }

		public (int ExitCode, string StdOut, string StdErr) Run(string exe, string[] arguments)
		{
			// arguments are ["behavior", "query"|"set", "disablelastaccess", value?] — matching exactly
			// what LastAccessTimestampTweak.Run passes, so arguments[1] is the sub-command.
			if (arguments[1] == "query")
			{
				return (0, $"DisableLastAccess = {CurrentValue}  (System Managed, Last Access Time Updates ENABLED)", string.Empty);
			}

			SetCallCount++;
			if (FailNextSet)
			{
				return (1, string.Empty, "Access is denied.");
			}

			CurrentValue = int.Parse(arguments[3]);
			return (0, string.Empty, string.Empty);
		}
	}

	private (LastAccessTimestampTweak Tweak, ScriptedFsutil Fsutil) Build(int initialValue = 2)
	{
		var fsutil = new ScriptedFsutil { CurrentValue = initialValue };
		var tweak = new LastAccessTimestampTweak(new TweakBackupStore(_backupPath), fsutil.Run);
		return (tweak, fsutil);
	}

	[Theory]
	[InlineData(0, TweakState.NotApplied)]
	[InlineData(1, TweakState.Applied)]
	[InlineData(2, TweakState.NotApplied)]
	public void StateReflectsTheThreeRealFsutilValues(int currentValue, TweakState expected)
	{
		var (tweak, _) = Build(currentValue);

		Assert.Equal(expected, tweak.GetState());
	}

	[Fact]
	public void ApplySetsTheValueToOne()
	{
		var (tweak, fsutil) = Build(initialValue: 2);

		tweak.Apply();

		Assert.Equal(1, fsutil.CurrentValue);
		Assert.Equal(TweakState.Applied, tweak.GetState());
	}

	[Fact]
	public void RevertRestoresTheExactPriorValueNotJustEnabled()
	{
		// The prior value can legitimately be 0, 1, or 2 — reverting must restore whichever one it
		// actually was, not assume "enabled" the way a simple two-state tweak could.
		var (tweak, fsutil) = Build(initialValue: 0);

		tweak.Apply();
		Assert.Equal(1, fsutil.CurrentValue);

		tweak.Revert();

		Assert.Equal(0, fsutil.CurrentValue);
	}

	[Fact]
	public void RevertFromSystemManagedRestoresSystemManagedNotEnabled()
	{
		var (tweak, fsutil) = Build(initialValue: 2);

		tweak.Apply();
		tweak.Revert();

		Assert.Equal(2, fsutil.CurrentValue);
	}

	[Fact]
	public void RevertIsANoOpWhenNothingWasEverApplied()
	{
		var (tweak, fsutil) = Build(initialValue: 1);

		tweak.Revert();

		Assert.Equal(1, fsutil.CurrentValue);
	}

	[Fact]
	public void ApplyingWhenAlreadyDisabledDoesNotRecordANeedlessBackup()
	{
		var (tweak, fsutil) = Build(initialValue: 1);

		tweak.Apply();
		fsutil.CurrentValue = 0; // simulate something else changing it afterwards
		tweak.Revert();

		// No backup was recorded (it was already 1 before Apply), so Revert must be a no-op and must
		// not stomp the value 0 that is there now.
		Assert.Equal(0, fsutil.CurrentValue);
	}

	[Fact]
	public void AFailedWriteThrowsAndDoesNotSilentlyReportSuccess()
	{
		var (tweak, fsutil) = Build(initialValue: 2);
		fsutil.FailNextSet = true;

		Assert.Throws<InvalidOperationException>(() => tweak.Apply());
	}

	[Fact]
	public void UnparsableQueryOutputReportsUnknownRatherThanGuessing()
	{
		var tweak = new LastAccessTimestampTweak(
			new TweakBackupStore(_backupPath),
			(_, _) => (0, "some unexpected localized text with no recognizable value", string.Empty));

		Assert.Equal(TweakState.Unknown, tweak.GetState());
	}

	[Fact]
	public void ApplyRefusesWhenTheCurrentStateCannotBeDetermined()
	{
		// Proceeding blind here would risk losing the ability to revert — the same failure mode the
		// TRIM tweak was already fixed for once.
		var tweak = new LastAccessTimestampTweak(
			new TweakBackupStore(_backupPath),
			(_, _) => (0, "unrecognizable", string.Empty));

		Assert.Throws<InvalidOperationException>(() => tweak.Apply());
	}

	[Fact]
	public void DefinitionIdMatchesTheCatalogEntry()
	{
		var (tweak, _) = Build();

		Assert.Equal("storage.disable-last-access-timestamps", tweak.Definition.Id);
	}
}
