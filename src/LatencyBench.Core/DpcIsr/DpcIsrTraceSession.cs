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

    /// <summary>
    /// Loaded module ranges, kept sorted by base address so <see cref="ResolveDriverName"/> can binary
    /// search. This lookup runs once per ISR and once per DPC — on a busy machine that is tens of
    /// thousands of calls per second — and used to be a linear scan across every loaded module, so its
    /// cost grew with the size of the very list it was searching.
    /// </summary>
    private readonly List<(ulong Base, ulong End, string Name)> _modules = new List<(ulong, ulong, string)>();

    /// <summary>Set whenever a module is added out of order, so the sort is paid once on the next
    /// lookup rather than on every insert during the image rundown at session start.</summary>
    private bool _modulesNeedSort;

    /// <summary>
    /// Consecutive events overwhelmingly come from the same handful of drivers, so the previous
    /// resolution is checked before searching at all.
    /// </summary>
    private ulong _lastResolvedBase;

    private ulong _lastResolvedEnd;

    private string _lastResolvedName = "unknown";

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
            _modulesNeedSort = false;
            _lastResolvedBase = 0UL;
            _lastResolvedEnd = 0UL;
            _lastResolvedName = "unknown";
            session.Source.Kernel.ImageDCStart += delegate (ImageLoadTraceData data)
            {
                RecordModule(data.ImageBase, data.ImageSize, data.FileName);
            };
            session.Source.Kernel.ImageLoad += delegate (ImageLoadTraceData data)
            {
                RecordModule(data.ImageBase, data.ImageSize, data.FileName);
            };
            session.Source.Kernel.PerfInfoISR += delegate (ISRTraceData data)
            {
                Raise(data.TimeStamp, DpcIsrKind.Isr, data.ElapsedTimeMSec, data.Routine, data.ProcessorNumber);
            };
            session.Source.Kernel.PerfInfoDPC += delegate (DPCTraceData data)
            {
                Raise(data.TimeStamp, DpcIsrKind.Dpc, data.ElapsedTimeMSec, data.Routine, data.ProcessorNumber);
            };
            session.Source.Kernel.PerfInfoThreadedDPC += delegate (DPCTraceData data)
            {
                Raise(data.TimeStamp, DpcIsrKind.Dpc, data.ElapsedTimeMSec, data.Routine, data.ProcessorNumber);
            };
            session.Source.Kernel.PerfInfoTimerDPC += delegate (DPCTraceData data)
            {
                Raise(data.TimeStamp, DpcIsrKind.Dpc, data.ElapsedTimeMSec, data.Routine, data.ProcessorNumber);
            };
            _processingTask = Task.Run(() => session.Source.Process());
        }
    }

    private void RecordModule(ulong imageBase, int imageSize, string fileName)
    {
        if (imageSize <= 0 || string.IsNullOrEmpty(fileName))
        {
            return;
        }

        if (_modules.Count > 0 && imageBase < _modules[^1].Base)
        {
            _modulesNeedSort = true;
        }

        _modules.Add((imageBase, imageBase + (ulong)imageSize, Path.GetFileName(fileName)));

        // A newly loaded driver can occupy the address range a previously unloaded one held, so the
        // one-entry cache has to be dropped whenever the map changes.
        _lastResolvedEnd = 0UL;
    }

    private string ResolveDriverName(ulong routine)
    {
        if (routine >= _lastResolvedBase && routine < _lastResolvedEnd)
        {
            return _lastResolvedName;
        }

        if (_modulesNeedSort)
        {
            _modules.Sort(static (left, right) => left.Base.CompareTo(right.Base));
            _modulesNeedSort = false;
        }

        // Largest base address <= routine, then a single containment check. Ranges do not overlap, so
        // if that candidate does not contain the address, no other module can either.
        int low = 0;
        int high = _modules.Count - 1;
        int candidate = -1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            if (_modules[middle].Base <= routine)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (candidate < 0)
        {
            return "unknown";
        }

        var (moduleBase, moduleEnd, name) = _modules[candidate];
        if (routine >= moduleEnd)
        {
            return "unknown";
        }

        _lastResolvedBase = moduleBase;
        _lastResolvedEnd = moduleEnd;
        _lastResolvedName = name;
        return name;
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
        TraceEventSession? session = _session;
        Task? processingTask = _processingTask;
        if (session == null)
        {
            return;
        }

        // Stop flushes ETW and lets Source.Process exit.  Wait for that exit before
        // disposing the session or allowing a same-name kernel session to start.
        session.Stop();
        try
        {
            processingTask?.GetAwaiter().GetResult();
        }
        finally
        {
            session.Dispose();
        }

        _session = null;
        _processingTask = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
