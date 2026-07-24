using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace LatencyBench.Core.Perf;

public sealed class ProcessCpuMonitor
{
	public async Task<IReadOnlyList<ProcessCpuSample>> SampleAsync(TimeSpan interval)
	{
		Dictionary<string, TimeSpan> before = Snapshot();
		await Task.Delay(interval).ConfigureAwait(continueOnCapturedContext: false);
		Dictionary<string, TimeSpan> after = Snapshot();
		List<ProcessCpuSample> results = new List<ProcessCpuSample>();
		foreach (KeyValuePair<string, TimeSpan> item in after)
		{
			item.Deconstruct(out var key, out var value);
			string name = key;
			TimeSpan afterCpu = value;
			if (before.TryGetValue(name, out var beforeCpu))
			{
				value = afterCpu - beforeCpu;
				double deltaMs = value.TotalMilliseconds;
				if (!(deltaMs <= 0.0))
				{
					double percent = 100.0 * deltaMs / (interval.TotalMilliseconds * (double)Environment.ProcessorCount);
					results.Add(new ProcessCpuSample(name, percent));
					beforeCpu = default(TimeSpan);
				}
			}
		}
		return results;
	}

	private static Dictionary<string, TimeSpan> Snapshot()
	{
		Dictionary<string, TimeSpan> dictionary = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
		Process[] processes = Process.GetProcesses();
		foreach (Process process in processes)
		{
			using (process)
			{
				try
				{
					TimeSpan totalProcessorTime = process.TotalProcessorTime;
					dictionary[process.ProcessName] = (dictionary.TryGetValue(process.ProcessName, out var value) ? (value + totalProcessorTime) : totalProcessorTime);
				}
				catch
				{
				}
			}
		}
		return dictionary;
	}
}
