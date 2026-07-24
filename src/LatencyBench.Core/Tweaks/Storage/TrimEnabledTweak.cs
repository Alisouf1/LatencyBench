using System;
using System.Diagnostics;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Storage;

public sealed class TrimEnabledTweak : ITweak
{
	public TweakDefinition Definition { get; } = new TweakDefinition
	{
		Id = "ssd.trim-enabled",
		Category = TweakCategory.Ssd,
		Name = "TRIM enabled",
		Description = "Confirms TRIM is enabled system-wide. Windows enables this by default on real SSDs — this just verifies/restores it.",
		Risk = TweakRisk.Safe
	};

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
		var (num, _, text) = Run("behavior set DisableDeleteNotify 0");
		if (num != 0 || GetState() != TweakState.Applied)
		{
			throw new InvalidOperationException("Could not enable TRIM: " + text);
		}
	}

	public void Revert()
	{
		Apply();
	}

	private int? ReadDisableDeleteNotify()
	{
		var (num, text, _) = Run("behavior query DisableDeleteNotify");
		if (num != 0)
		{
			return null;
		}
		return text.Contains("= 0", StringComparison.Ordinal) ? new int?(0) : (text.Contains("= 1", StringComparison.Ordinal) ? new int?(1) : ((int?)null));
	}

	private static (int ExitCode, string StdOut, string StdErr) Run(string arguments)
	{
		ProcessStartInfo startInfo = new ProcessStartInfo("fsutil.exe", arguments)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start fsutil.exe.");
		string item = process.StandardOutput.ReadToEnd();
		string item2 = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return (ExitCode: process.ExitCode, StdOut: item, StdErr: item2);
	}
}
