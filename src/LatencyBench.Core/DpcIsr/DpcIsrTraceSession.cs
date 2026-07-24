using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace LatencyBench.Core.DpcIsr;

public sealed class DpcIsrTraceSession : IDisposable
{
	private TraceEventSession? _session;

	private Task? _processingTask;

	private readonly List<(ulong Base, ulong End, string Name)> _modules = new List<(ulong, ulong, string)>();

	public bool IsRunning => _session != null;

	public event Action<DpcIsrSample>? SampleReceived;

	public void Start()
	{
		if (_session == null)
		{
			TraceEventSession session = new TraceEventSession(KernelTraceEventParser.KernelSessionName);
			try
			{
				session.EnableKernelProvider(KernelTraceEventParser.Keywords.ImageLoad | KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.Interrupt);
			}
			catch
			{
				session.Dispose();
				throw;
			}
			_session = session;
			_modules.Clear();
			session.Source.Kernel.ImageDCStart += delegate(ImageLoadTraceData data)
			{
				RecordModule(data.ImageBase, data.ImageSize, data.FileName);
			};
			session.Source.Kernel.ImageLoad += delegate(ImageLoadTraceData data)
			{
				RecordModule(data.ImageBase, data.ImageSize, data.FileName);
			};
			session.Source.Kernel.PerfInfoISR += delegate(ISRTraceData data)
			{
				Raise(data.TimeStamp, DpcIsrKind.Isr, data.ElapsedTimeMSec, data.Routine, data.ProcessorNumber);
			};
			session.Source.Kernel.PerfInfoDPC += delegate(DPCTraceData data)
			{
				Raise(data.TimeStamp, DpcIsrKind.Dpc, data.ElapsedTimeMSec, data.Routine, data.ProcessorNumber);
			};
			session.Source.Kernel.PerfInfoThreadedDPC += delegate(DPCTraceData data)
			{
				Raise(data.TimeStamp, DpcIsrKind.Dpc, data.ElapsedTimeMSec, data.Routine, data.ProcessorNumber);
			};
			session.Source.Kernel.PerfInfoTimerDPC += delegate(DPCTraceData data)
			{
				Raise(data.TimeStamp, DpcIsrKind.Dpc, data.ElapsedTimeMSec, data.Routine, data.ProcessorNumber);
			};
			_processingTask = Task.Run(() => session.Source.Process());
		}
	}

	private void RecordModule(ulong imageBase, int imageSize, string fileName)
	{
		if (imageSize > 0 && !string.IsNullOrEmpty(fileName))
		{
			_modules.Add((imageBase, imageBase + (ulong)imageSize, Path.GetFileName(fileName)));
		}
	}

	private string ResolveDriverName(ulong routine)
	{
		foreach (var (num, num2, result) in _modules)
		{
			if (routine >= num && routine < num2)
			{
				return result;
			}
		}
		return "unknown";
	}

	private void Raise(DateTime timestamp, DpcIsrKind kind, double elapsedMSec, ulong routine, int processorNumber)
	{
		this.SampleReceived?.Invoke(new DpcIsrSample
		{
			Timestamp = timestamp,
			Kind = kind,
			DurationMicroseconds = elapsedMSec * 1000.0,
			DriverName = ResolveDriverName(routine),
			ProcessorNumber = processorNumber
		});
	}

	public void Stop()
	{
		_session?.Stop();
		_session?.Dispose();
		_session = null;
		_processingTask = null;
	}

	public void Dispose()
	{
		Stop();
	}
}
