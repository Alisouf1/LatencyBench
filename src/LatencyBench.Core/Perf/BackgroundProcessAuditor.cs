using System;
using System.Collections.Generic;
using System.Linq;

namespace LatencyBench.Core.Perf;

public static class BackgroundProcessAuditor
{
    private const double MinimumCpuPercent = 1.0;

    private const int MaxEntries = 8;

    private static readonly Dictionary<string, string> KnownCulprits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SearchIndexer"] = "Windows Search file indexing — runs in bursts after new/changed files.",
        ["MsMpEng"] = "Windows Defender real-time scanning.",
        ["OneDrive"] = "OneDrive background sync.",
        ["Dropbox"] = "Dropbox background sync.",
        ["GoogleDriveFS"] = "Google Drive background sync.",
        ["SIHClient"] = "Windows Update client health check.",
        ["UsoClient"] = "Windows Update orchestrator.",
        ["backgroundTaskHost"] = "A background task belonging to a Store/UWP app.",
        ["RuntimeBroker"] = "A permission broker belonging to a Store/UWP app."
    };

    public static IReadOnlyList<FlaggedProcess> Audit(IReadOnlyList<ProcessCpuSample> samples)
    {
        return (from s in (from s in samples
                           where s.CpuPercent >= 1.0
                           orderby s.CpuPercent descending
                           select s).Take(8)
                select new FlaggedProcess(s.ProcessName, s.CpuPercent, KnownCulprits.GetValueOrDefault(s.ProcessName))).ToList();
    }
}
