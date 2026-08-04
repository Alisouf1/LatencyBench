using System;
using System.Text.RegularExpressions;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Storage;

/// <summary>
/// NTFS updates a file's last-access timestamp on every read unless told not to — a metadata write
/// for every read, on every file, including the ones an application reads constantly and never
/// otherwise touches.
/// <para>
/// Verified against fsutil directly rather than assumed: <c>fsutil behavior query disablelastaccess</c>
/// reports three states, not two — 0 (updates always enabled), 1 (updates always disabled), and
/// 2 (system-managed, which is the modern Windows default and currently resolves to enabled). Because
/// the original value can legitimately be any of the three, Revert restores the exact value that was
/// there before rather than assuming "enabled" — the same reasoning <see cref="RegistryDwordTweak"/>
/// uses, applied here because this setting is not a registry value at all but an NTFS volume
/// parameter fsutil is the only supported way to read or write.
/// </para>
/// <para>
/// Unlike <see cref="TrimEnabledTweak"/>'s DisableDeleteNotify query, this one's output was confirmed
/// (against a real machine) to be a single global line with no separate NTFS/ReFS lines to
/// disambiguate — <c>DisableLastAccess = 2 (...)</c>, nothing else — so a single pattern match is
/// correct here and is not the same trap that query has to guard against.
/// </para>
/// </summary>
public sealed class LastAccessTimestampTweak : ITweak
{
	private const string BackupKey = "fsutil:disablelastaccess";

	private static readonly Regex ValuePattern = new(@"DisableLastAccess\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private readonly TweakBackupStore _backupStore;

	private readonly Func<string, string[], (int ExitCode, string StdOut, string StdErr)> _runFsutil;

	public TweakDefinition Definition { get; } = new TweakDefinition
	{
		Id = "storage.disable-last-access-timestamps",
		Category = TweakCategory.Ssd,
		Name = "Disable NTFS last-access timestamps",
		Description = "Stops NTFS updating a file's last-accessed time on every read, removing a metadata write from every file read on every fixed drive.",
		Risk = TweakRisk.Safe
	};

	public LastAccessTimestampTweak(TweakBackupStore backupStore)
		: this(backupStore, ConsoleToolRunner.Run)
	{
	}

	/// <summary>Seam for tests: substitutes a fake fsutil invocation so Apply/Revert can be exercised
	/// without touching this machine's actual NTFS timestamp behaviour. Every real caller uses the
	/// constructor above.</summary>
	public LastAccessTimestampTweak(
		TweakBackupStore backupStore,
		Func<string, string[], (int ExitCode, string StdOut, string StdErr)> runFsutil)
	{
		_backupStore = backupStore;
		_runFsutil = runFsutil;
	}

	public TweakState GetState()
	{
		int? current = ReadCurrentValue();
		if (current is null)
		{
			return TweakState.Unknown;
		}

		// Only an explicit 1 (always disabled) counts as applied. 2 (system-managed) currently behaves
		// as enabled on a default Windows install, but "currently behaves as" is not the same
		// guarantee as "explicitly turned off", so it is treated as not-yet-applied rather than
		// silently accepted as equivalent.
		return current == 1 ? TweakState.Applied : TweakState.NotApplied;
	}

	public void Apply()
	{
		int? previous = ReadCurrentValue();
		if (!previous.HasValue)
		{
			throw new InvalidOperationException(
				"Could not determine the current last-access timestamp behaviour, so applying this tweak " +
				"would risk being unable to revert it later. fsutil's query did not return a recognizable value.");
		}

		if (previous.Value != 1)
		{
			_backupStore.Save(BackupKey, previous.Value.ToString());
		}

		WriteValue(1, "disable");
	}

	public void Revert()
	{
		string? saved = _backupStore.TryGet(BackupKey);
		if (saved is null)
		{
			// Already explicitly disabled before this app touched it — no known prior value to
			// restore safely, so nothing is changed.
			return;
		}

		if (!int.TryParse(saved, out int value))
		{
			throw new InvalidOperationException($"The saved backup for '{Definition.Name}' is invalid.");
		}

		WriteValue(value, "restore");
		_backupStore.Remove(BackupKey);
	}

	private void WriteValue(int value, string action)
	{
		var (exitCode, _, stdErr) = Run("behavior", "set", "disablelastaccess", value.ToString());
		if (exitCode != 0)
		{
			throw new InvalidOperationException($"Could not {action} last-access timestamp behaviour: {stdErr.Trim()}");
		}

		int? current = ReadCurrentValue();
		if (current != value)
		{
			throw new InvalidOperationException($"fsutil reported success but DisableLastAccess is not {value}.");
		}
	}

	private int? ReadCurrentValue()
	{
		var (exitCode, stdOut, _) = Run("behavior", "query", "disablelastaccess");
		if (exitCode != 0)
		{
			return null;
		}

		Match match = ValuePattern.Match(stdOut);
		return match.Success && int.TryParse(match.Groups[1].Value, out int value) ? value : null;
	}

	private (int ExitCode, string StdOut, string StdErr) Run(params string[] arguments)
	{
		return _runFsutil("fsutil.exe", arguments);
	}
}
