using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LatencyBench.Core.Tweaks;

public sealed class PowerCfgRunner
{
	public const string SchemeHighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

	public const string SubgroupProcessor = "54533251-82be-4824-96c1-47b60b740d00";

	public const string SettingProcessorMinState = "893dee8e-2bef-41e0-89c6-b55d0929964c";

	public const string SettingProcessorBoostMode = "be337238-0d82-4146-a960-4f3749d470c7";

	public const string SettingCoreParkingMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";

	public const string SubgroupUsb = "2a737441-1930-4402-8d77-b2bebba308a3";

	public const string SettingUsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

	public (int ExitCode, string StdOut, string StdErr) Run(string arguments)
	{
		ProcessStartInfo startInfo = new ProcessStartInfo("powercfg.exe", arguments)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start powercfg.exe.");
		string item = process.StandardOutput.ReadToEnd();
		string item2 = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return (ExitCode: process.ExitCode, StdOut: item, StdErr: item2);
	}

	public string GetActiveSchemeGuid()
	{
		string item = Run("/getactivescheme").StdOut;
		Match match = Regex.Match(item, "GUID:\\s*([0-9a-fA-F-]{36})");
		if (!match.Success)
		{
			throw new InvalidOperationException("Could not parse active power scheme from: " + item);
		}
		return match.Groups[1].Value;
	}

	public void SetActiveScheme(string schemeGuid)
	{
		Run("/setactive " + schemeGuid);
	}

	public void SetAcValueIndex(string schemeGuid, string subgroupGuid, string settingGuid, uint value)
	{
		Run($"/setacvalueindex {schemeGuid} {subgroupGuid} {settingGuid} {value}");
	}

	public uint? QueryAcValueIndex(string schemeGuid, string subgroupGuid, string settingGuid)
	{
		string item = Run($"/query {schemeGuid} {subgroupGuid} {settingGuid}").StdOut;
		Match match = Regex.Match(item, "Current AC Power Setting Index:\\s*0x([0-9a-fA-F]+)");
		return match.Success ? new uint?(Convert.ToUInt32(match.Groups[1].Value, 16)) : ((uint?)null);
	}
}
