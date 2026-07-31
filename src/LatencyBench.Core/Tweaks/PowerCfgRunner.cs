using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace LatencyBench.Core.Tweaks;

/// <summary>
/// Power-scheme access. Writes go through powercfg.exe (it is the only supported way to edit a
/// scheme and have Windows notice), but every read goes to the registry instead.
/// <para>
/// Reads used to shell out to <c>powercfg /query</c> and regex the output for
/// "Current AC Power Setting Index". That was wrong twice over: powercfg localises its output, so
/// the pattern never matched on a non-English Windows and every affected tweak reported
/// <see cref="Models.TweakState.Unknown"/>; and it cost a process launch per read, which made a
/// single refresh of the tweak list spawn two processes per powercfg-backed tweak.
/// </para>
/// </summary>
public sealed class PowerCfgRunner
{
	public const string SchemeHighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

	public const string SchemeBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

	public const string SubgroupProcessor = "54533251-82be-4824-96c1-47b60b740d00";

	public const string SettingProcessorMinState = "893dee8e-2bef-41e0-89c6-b55d0929964c";

	public const string SettingProcessorBoostMode = "be337238-0d82-4146-a960-4f3749d470c7";

	public const string SettingCoreParkingMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";

	public const string SubgroupUsb = "2a737441-1930-4402-8d77-b2bebba308a3";

	public const string SettingUsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

	private const string PowerSchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

	private const string PowerSettingsKey = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings";

	/// <summary>
	/// A hung powercfg.exe must not hang the caller. Deliberately generous: powercfg on a machine
	/// with many schemes can legitimately take a second or two.
	/// </summary>
	private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(20);

	public (int ExitCode, string StdOut, string StdErr) Run(params string[] arguments)
	{
		ProcessStartInfo startInfo = new ProcessStartInfo("powercfg.exe")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		// ArgumentList passes each token straight to CreateProcess as its own argument — unlike the
		// single-string ProcessStartInfo(fileName, arguments) overload, there's no shell-style
		// re-parsing step for a value to break out of, so this is safe even if a future caller ever
		// passes something less trusted than today's hardcoded GUIDs and OS-reported scheme IDs.
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start powercfg.exe.");

		// Both pipes are drained concurrently. Reading one to completion before touching the other
		// deadlocks whenever the child fills the pipe buffer it is NOT being drained from: the child
		// blocks writing, we block reading the other handle, and neither side ever moves.
		Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync();
		Task<string> stdErrTask = process.StandardError.ReadToEndAsync();

		if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
		{
			TryKill(process);
			throw new TimeoutException($"powercfg.exe did not exit within {ProcessTimeout.TotalSeconds:0} seconds.");
		}

		// WaitForExit(int) does not wait for the async readers to drain the redirected streams, so
		// join them explicitly before reading their results.
		Task.WaitAll(new Task[] { stdOutTask, stdErrTask }, ProcessTimeout);

		return (ExitCode: process.ExitCode, StdOut: stdOutTask.Result, StdErr: stdErrTask.Result);
	}

	private static void TryKill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (Exception)
		{
			// The process may have exited between the timeout and the kill; nothing to clean up.
		}
	}

	/// <summary>The active scheme GUID, read from the registry value Windows itself keeps up to
	/// date. No process launch and no localised text to parse.</summary>
	public string GetActiveSchemeGuid()
	{
		using RegistryKey? schemes = Registry.LocalMachine.OpenSubKey(PowerSchemesKey);
		if (schemes?.GetValue("ActivePowerScheme") is string active && Guid.TryParse(active, out _))
		{
			return active;
		}

		throw new InvalidOperationException(
			@"Could not read the active power scheme from HKLM\" + PowerSchemesKey + @"\ActivePowerScheme.");
	}

	/// <summary>Windows 11 hides the High performance plan on many OEM and modern-standby systems.
	/// Checking before activating turns a silent no-op into an explicit, explainable failure.</summary>
	public bool SchemeExists(string schemeGuid)
	{
		using RegistryKey? scheme = Registry.LocalMachine.OpenSubKey($@"{PowerSchemesKey}\{schemeGuid}");
		return scheme is not null;
	}

	public void SetActiveScheme(string schemeGuid)
	{
		var (exitCode, _, stdErr) = Run("/setactive", schemeGuid);
		if (exitCode != 0)
		{
			throw new InvalidOperationException(
				$"Windows refused to activate power scheme {schemeGuid}. powercfg reported: {stdErr.Trim()}");
		}
	}

	public void SetAcValueIndex(string schemeGuid, string subgroupGuid, string settingGuid, uint value)
	{
		var (exitCode, _, stdErr) = Run("/setacvalueindex", schemeGuid, subgroupGuid, settingGuid, value.ToString());
		if (exitCode != 0)
		{
			throw new InvalidOperationException(
				$"powercfg could not set {settingGuid} on scheme {schemeGuid}: {stdErr.Trim()}");
		}

		// /setacvalueindex only edits the scheme's stored setting. Windows does not push the change
		// into the running power policy until the scheme is (re-)activated, so a tweak applied to the
		// CURRENTLY ACTIVE scheme reads back as changed while having no effect on the machine.
		// Re-activating the scheme it just edited is what makes the setting take effect now.
		if (string.Equals(schemeGuid, GetActiveSchemeGuid(), StringComparison.OrdinalIgnoreCase))
		{
			SetActiveScheme(schemeGuid);
		}
	}

	/// <summary>
	/// The effective AC setting index for a scheme. Prefers the scheme's own override; falls back to
	/// the per-scheme default Windows ships for that setting, which is what powercfg reports when a
	/// scheme has never had the value changed. Returns null when the setting does not exist on this
	/// machine at all (for example a processor setting on a system that does not expose it).
	/// </summary>
	public uint? QueryAcValueIndex(string schemeGuid, string subgroupGuid, string settingGuid)
	{
		using (RegistryKey? overrideKey = Registry.LocalMachine.OpenSubKey(
			$@"{PowerSchemesKey}\{schemeGuid}\{subgroupGuid}\{settingGuid}"))
		{
			if (overrideKey?.GetValue("ACSettingIndex") is int stored)
			{
				return unchecked((uint)stored);
			}
		}

		using RegistryKey? defaultKey = Registry.LocalMachine.OpenSubKey(
			$@"{PowerSettingsKey}\{subgroupGuid}\{settingGuid}\DefaultPowerSchemeValues\{schemeGuid}");
		if (defaultKey?.GetValue("ACSettingIndex") is int fallback)
		{
			return unchecked((uint)fallback);
		}

		return null;
	}
}
