using System;
using System.Collections.Generic;
using System.Threading;

namespace LatencyBench.Core.Perf;

public sealed class CpuLoadGenerator : IDisposable
{
	private const double CoreUtilizationShare = 0.75;

	private CancellationTokenSource? _cts;

	private List<Thread>? _threads;

	public bool IsRunning => _cts != null;

	public int ThreadCount => Math.Max(1, (int)((double)Environment.ProcessorCount * 0.75));

	public void Start()
	{
		if (_cts != null)
		{
			return;
		}
		_cts = new CancellationTokenSource();
		CancellationToken token = _cts.Token;
		_threads = new List<Thread>();
		for (int i = 0; i < ThreadCount; i++)
		{
			Thread thread = new Thread((ThreadStart)delegate
			{
				Spin(token);
			})
			{
				IsBackground = true,
				Priority = ThreadPriority.BelowNormal
			};
			thread.Start();
			_threads.Add(thread);
		}
	}

	public void Stop()
	{
		if (_cts == null)
		{
			return;
		}
		_cts.Cancel();
		foreach (Thread item in _threads ?? new List<Thread>())
		{
			item.Join(TimeSpan.FromSeconds(1.0));
		}
		_cts.Dispose();
		_cts = null;
		_threads = null;
	}

	private static void Spin(CancellationToken token)
	{
		double num = 1.0;
		while (!token.IsCancellationRequested)
		{
			for (int i = 0; i < 10000; i++)
			{
				num = Math.Sqrt(num + 1.0) * 1.0000001;
			}
			if (double.IsInfinity(num) || double.IsNaN(num))
			{
				num = 1.0;
			}
		}
	}

	public void Dispose()
	{
		Stop();
	}
}
