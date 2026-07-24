using System;
using System.Diagnostics;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Storage;

public sealed class ScheduledDefragTweak : ITweak
{
	private const string TaskName = "\\Microsoft\\Windows\\Defrag\\ScheduledDefrag";

	public TweakDefinition Definition { get; } = new TweakDefinition
	{
		Id = "ssd.scheduled-defrag",
		Category = TweakCategory.Ssd,
		Name = "Disable scheduled defrag",
		Description = "Disables Windows' scheduled defrag task. Modern Windows already skips defrag on SSDs automatically, but this removes the task entirely.",
		Risk = TweakRisk.Safe
	};

	public TweakState GetState()
	{
		var (num, text, _) = Run("/Query /TN \"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\" /FO LIST");
		if (num != 0)
		{
			return TweakState.Unknown;
		}
		if (text.Contains("Disabled", StringComparison.OrdinalIgnoreCase))
		{
			return TweakState.Applied;
		}
		return TweakState.NotApplied;
	}

	public void Apply()
	{
		var (num, _, text) = Run("/Change /TN \"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\" /Disable");
		if (num != 0 || GetState() != TweakState.Applied)
		{
			throw new InvalidOperationException("Could not disable the scheduled defrag task: " + text);
		}
	}

	public void Revert()
	{
		var (num, _, text) = Run("/Change /TN \"\\Microsoft\\Windows\\Defrag\\ScheduledDefrag\" /Enable");
		if (num != 0 || GetState() != TweakState.NotApplied)
		{
			throw new InvalidOperationException("Could not re-enable the scheduled defrag task: " + text);
		}
	}

	private static (int ExitCode, string StdOut, string StdErr) Run(string arguments)
	{
		ProcessStartInfo startInfo = new ProcessStartInfo("schtasks.exe", arguments)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start schtasks.exe.");
		string item = process.StandardOutput.ReadToEnd();
		string item2 = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return (ExitCode: process.ExitCode, StdOut: item, StdErr: item2);
	}
}
