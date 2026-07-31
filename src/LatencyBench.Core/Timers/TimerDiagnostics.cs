using System;
using System.Text.RegularExpressions;
using LatencyBench.Core.Interop;
using LatencyBench.Core.Tweaks;
using Microsoft.Win32;

namespace LatencyBench.Core.Timers;

public enum BcdFlagState
{
	/// <summary>The value could not be read — normally because the process is not elevated.</summary>
	Unknown,

	/// <summary>Not present in the boot configuration, which is the Windows default.</summary>
	NotSet,

	On,

	Off
}

/// <summary>
/// The state of the machine's timers, and of the boot options people most often change in pursuit
/// of latency.
/// </summary>
public sealed class TimerState
{
	/// <summary>Coarsest supported interval, in milliseconds. Normally 15.625.</summary>
	public required double CoarsestMs { get; init; }

	/// <summary>Finest supported interval, in milliseconds. Normally 0.5.</summary>
	public required double FinestMs { get; init; }

	/// <summary>What the system clock is running at right now, in milliseconds.</summary>
	public required double CurrentMs { get; init; }

	/// <summary>
	/// True when a process has raised the global timer resolution above the idle default. Something
	/// on the machine — a browser, a game, a media player — is holding it there.
	/// </summary>
	public bool IsRaised => CurrentMs < CoarsestMs - 0.001;

	/// <summary>
	/// True on builds where timer resolution requests became per-process. From Windows 10 2004
	/// onwards a process asking for 0.5 ms only gets it for itself; other processes keep seeing the
	/// default. Every "set your timer to 0.5 ms globally" utility predates this change.
	/// </summary>
	public required bool UsesPerProcessTimerResolution { get; init; }

	/// <summary>
	/// Whether the machine has been told to restore the old global behaviour via
	/// GlobalTimerResolutionRequests.
	/// </summary>
	public required bool GlobalTimerResolutionRequested { get; init; }

	/// <summary>
	/// bcdedit useplatformclock. Forces the platform timer (HPET on most machines) to be the system
	/// clock source instead of letting Windows choose, which on modern hardware means giving up the
	/// invariant TSC.
	/// </summary>
	public required BcdFlagState UsePlatformClock { get; init; }

	/// <summary>bcdedit useplatformtick.</summary>
	public required BcdFlagState UsePlatformTick { get; init; }

	/// <summary>bcdedit disabledynamictick. Stops Windows coalescing timer ticks when idle.</summary>
	public required BcdFlagState DisableDynamicTick { get; init; }

	/// <summary>Populated when the boot configuration could not be read.</summary>
	public string? BootConfigurationError { get; init; }
}

/// <summary>
/// Reads timer resolution and the timer-related boot options.
/// </summary>
public sealed class TimerDiagnostics
{
	private const string KernelKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";

	/// <summary>
	/// Windows 10 2004. The build where a timer resolution request stopped applying machine-wide and
	/// became scoped to the requesting process.
	/// </summary>
	public const int PerProcessTimerResolutionBuild = 19041;

	/// <summary>100-nanosecond units per millisecond.</summary>
	private const double UnitsPerMillisecond = 10_000.0;

	public TimerState Read(int windowsBuildNumber)
	{
		TimerApi.NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);

		var (usePlatformClock, usePlatformTick, disableDynamicTick, error) = ReadBootConfiguration();

		return new TimerState
		{
			CoarsestMs = coarsest / UnitsPerMillisecond,
			FinestMs = finest / UnitsPerMillisecond,
			CurrentMs = current / UnitsPerMillisecond,
			UsesPerProcessTimerResolution = windowsBuildNumber >= PerProcessTimerResolutionBuild,
			GlobalTimerResolutionRequested = ReadGlobalTimerResolutionRequests(),
			UsePlatformClock = usePlatformClock,
			UsePlatformTick = usePlatformTick,
			DisableDynamicTick = disableDynamicTick,
			BootConfigurationError = error
		};
	}

	private static bool ReadGlobalTimerResolutionRequests()
	{
		using RegistryKey? key = Registry.LocalMachine.OpenSubKey(KernelKey);
		return key?.GetValue("GlobalTimerResolutionRequests") is int value && value == 1;
	}

	/// <summary>
	/// Reads the boot options through bcdedit.
	/// <para>
	/// bcdedit localises its column headers but never the element names themselves, so matching on
	/// "useplatformclock" is safe on a non-English Windows in a way that matching on a translated
	/// heading would not be. Reading the store requires elevation; without it bcdedit exits non-zero
	/// and the flags are reported as Unknown rather than guessed at.
	/// </para>
	/// </summary>
	private static (BcdFlagState Clock, BcdFlagState Tick, BcdFlagState DynamicTick, string? Error) ReadBootConfiguration()
	{
		try
		{
			var (exitCode, stdOut, stdErr) = ConsoleToolRunner.Run("bcdedit.exe", "/enum", "{current}");
			if (exitCode != 0)
			{
				return (BcdFlagState.Unknown, BcdFlagState.Unknown, BcdFlagState.Unknown,
					string.IsNullOrWhiteSpace(stdErr)
						? "bcdedit could not read the boot configuration. Administrator rights are required."
						: stdErr.Trim());
			}

			return (
				ParseBootFlag(stdOut, "useplatformclock"),
				ParseBootFlag(stdOut, "useplatformtick"),
				ParseBootFlag(stdOut, "disabledynamictick"),
				null);
		}
		catch (Exception ex)
		{
			return (BcdFlagState.Unknown, BcdFlagState.Unknown, BcdFlagState.Unknown, ex.Message);
		}
	}

	/// <summary>
	/// Extracts one boolean element from bcdedit's output. Public so it can be tested against real
	/// output shapes — including a non-English one — without needing an elevated process and a
	/// machine whose boot configuration has been deliberately modified.
	/// </summary>
	public static BcdFlagState ParseBootFlag(string output, string elementName)
	{
		// An absent element is the Windows default, which is a different fact from "present and off".
		Match match = Regex.Match(
			output,
			$@"^\s*{Regex.Escape(elementName)}\s+(\S+)\s*$",
			RegexOptions.IgnoreCase | RegexOptions.Multiline);

		if (!match.Success)
		{
			return BcdFlagState.NotSet;
		}

		string value = match.Groups[1].Value;

		// bcdedit prints Yes/No for boolean elements. Those two words ARE localised, unlike the
		// element names, so the check also accepts the raw forms bcdedit /set takes.
		if (value.Equals("Yes", StringComparison.OrdinalIgnoreCase)
			|| value.Equals("true", StringComparison.OrdinalIgnoreCase)
			|| value.Equals("on", StringComparison.OrdinalIgnoreCase)
			|| value.Equals("1", StringComparison.Ordinal))
		{
			return BcdFlagState.On;
		}

		if (value.Equals("No", StringComparison.OrdinalIgnoreCase)
			|| value.Equals("false", StringComparison.OrdinalIgnoreCase)
			|| value.Equals("off", StringComparison.OrdinalIgnoreCase)
			|| value.Equals("0", StringComparison.Ordinal))
		{
			return BcdFlagState.Off;
		}

		// Present but in a language this cannot classify. Saying so beats guessing.
		return BcdFlagState.Unknown;
	}
}
