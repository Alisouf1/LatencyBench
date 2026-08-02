using System;
using System.Text.RegularExpressions;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Storage;

public sealed class TrimEnabledTweak : ITweak
{
	private const string BackupKey = "fsutil.trim.disable-delete-notify";

	private readonly TweakBackupStore _backupStore;

	private readonly Func<string, string[], (int ExitCode, string StdOut, string StdErr)> _runFsutil;

	public TweakDefinition Definition { get; } = new TweakDefinition
	{
		Id = "ssd.trim-enabled",
		Category = TweakCategory.Ssd,
		Name = "TRIM enabled",
		Description = "Confirms TRIM is enabled system-wide. Windows enables this by default on real SSDs — this just verifies/restores it.",
		Risk = TweakRisk.Safe
	};

	public TrimEnabledTweak(TweakBackupStore backupStore)
		: this(backupStore, ConsoleToolRunner.Run)
	{
	}

	/// <summary>
	/// Seam for tests: substitutes a fake fsutil invocation so Apply/Revert can be exercised without
	/// touching this machine's actual system-wide TRIM setting. Every real caller uses the
	/// constructor above, which always runs the real fsutil.exe.
	/// </summary>
	public TrimEnabledTweak(TweakBackupStore backupStore, Func<string, string[], (int ExitCode, string StdOut, string StdErr)> runFsutil)
	{
		_backupStore = backupStore;
		_runFsutil = runFsutil;
	}

	public TweakState GetState()
	{
		int? num = ReadDisableDeleteNotify();
		if (!num.HasValue)
		{
			return TweakState.Unknown;
		}
		return (num != 0) ? TweakState.NotApplied : TweakState.Applied;
	}

	public void Apply()
	{
		int? previous = ReadDisableDeleteNotify();
		if (!previous.HasValue)
		{
			// Proceeding here without knowing the prior state would repeat the exact bug this class was
			// already fixed for once: if TRIM was actually off, that fact would be silently lost and
			// Revert could never restore it. Failing loudly is safer than guessing.
			throw new InvalidOperationException(
				"Could not determine whether TRIM is currently enabled, so applying this tweak would risk " +
				"being unable to revert it later. fsutil's query did not return a recognizable value.");
		}

		if (previous.Value != 0)
		{
			// TRIM was off before LatencyBench touched it. Record that so Revert can turn it back off
			// instead of leaving it force-enabled forever — the bug this backup exists to prevent.
			_backupStore.Save(BackupKey, previous.Value.ToString());
		}

		WriteDisableDeleteNotify(0, "enable");
	}

	public void Revert()
	{
		string? previous = _backupStore.TryGet(BackupKey);
		if (previous is null)
		{
			// TRIM was already enabled — or its prior state could not be determined — before
			// LatencyBench touched anything, so there is no known prior "disabled" state to restore.
			return;
		}

		if (!int.TryParse(previous, out int value))
		{
			throw new InvalidOperationException($"The saved backup for '{Definition.Name}' is invalid.");
		}

		WriteDisableDeleteNotify(value, "restore");
		_backupStore.Remove(BackupKey);
	}

	private void WriteDisableDeleteNotify(int value, string action)
	{
		var (num, _, text) = Run("behavior", "set", "DisableDeleteNotify", value.ToString());
		if (num != 0)
		{
			throw new InvalidOperationException($"Could not {action} TRIM: {text}");
		}

		int? current = ReadDisableDeleteNotify();
		if (current != value)
		{
			throw new InvalidOperationException($"fsutil reported success but DisableDeleteNotify is not {value}.");
		}
	}

	/// <summary>
	/// Matches fsutil's NTFS line specifically, e.g. "NTFS DisableDeleteNotify = 0". "NTFS" and
	/// "DisableDeleteNotify" are technical identifiers fsutil does not translate, unlike the
	/// descriptive words around them (fsutil's output is confirmed to be localized on non-English
	/// Windows) — anchoring on those two tokens plus the digit avoids depending on any translatable
	/// text. This also targets the NTFS value specifically: the query's output has a separate ReFS
	/// line too (present on any Windows version new enough to report it), which can legitimately
	/// disagree with the NTFS value — a bare "contains '= 0'/'= 1' anywhere in the text" check can't
	/// tell the two apart and would silently match whichever one happens to appear first.
	/// </summary>
	private static readonly Regex NtfsDisableDeleteNotifyPattern =
		new(@"NTFS\s+DisableDeleteNotify\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private int? ReadDisableDeleteNotify()
	{
		var (num, text, _) = Run("behavior", "query", "DisableDeleteNotify");
		if (num != 0)
		{
			return null;
		}

		Match match = NtfsDisableDeleteNotifyPattern.Match(text);
		return match.Success ? int.Parse(match.Groups[1].Value) : null;
	}

	private (int ExitCode, string StdOut, string StdErr) Run(params string[] arguments)
	{
		return _runFsutil("fsutil.exe", arguments);
	}
}
