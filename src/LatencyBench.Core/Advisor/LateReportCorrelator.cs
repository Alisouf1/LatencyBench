namespace LatencyBench.Core.Advisor;

/// <summary>One report-to-report window, and whether that report arrived late.</summary>
public readonly record struct ReportWindow(DateTime Start, DateTime End, bool IsLate);

/// <summary>A DPC/ISR event long enough to plausibly delay a HID report.</summary>
public readonly record struct SpikeEvent(DateTime Timestamp, double DurationMicroseconds, string DriverName);

public readonly record struct CorrelationResult(
    int LateCount,
    int LateWithSpike,
    int OnTimeCount,
    int OnTimeWithSpike,
    string? TopDriver,
    string Message);

/// <summary>
/// Works out whether late HID reports actually line up with interrupt spikes, and which driver is
/// responsible.
///
/// The naive version of this — "a DPC happened during a late report, blame it" — is worthless: a
/// busy system fires tens of thousands of DPC/ISR events per second, so *every* window contains
/// several and everything appears guilty. Instead this contrasts how often a spike lands in a late
/// window against how often one lands in an on-time window. Only a clear excess is reported as a
/// cause; otherwise it says so plainly rather than blaming whatever happened to be running.
/// </summary>
public static class LateReportCorrelator
{
    /// <summary>A spike must land in late windows at least this many times as often as in on-time windows to be called a cause.</summary>
    private const double MinimumLift = 2.0;

    /// <summary>...and must be present in at least this share of late windows, so we don't explain 4% of the problem and call it solved.</summary>
    private const double MinimumLateShare = 0.20;

    public static CorrelationResult Correlate(IReadOnlyList<ReportWindow> windows, IReadOnlyList<SpikeEvent> spikes)
    {
        var late = windows.Where(w => w.IsLate).ToList();
        var onTime = windows.Where(w => !w.IsLate).ToList();

        if (late.Count == 0)
        {
            return new CorrelationResult(0, 0, onTime.Count, 0, null,
                "No late reports in this run — there's nothing to explain.");
        }

        var ordered = windows.OrderBy(w => w.Start).ToList();

        // Which distinct late windows each driver appears in — deliberately a set of window indices
        // rather than an event tally. Counting raw events reintroduces, at the blame step, exactly the
        // bias the lift calculation below exists to remove: a driver firing 500 times inside a single
        // late window would outrank one present in every late window, and the burst is the weaker
        // explanation of the two. Measured before this changed: a 500-event burst confined to one
        // window beat a driver present in three.
        var driverLateWindows = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var lateHit = new HashSet<int>();
        var onTimeHit = new HashSet<int>();

        foreach (var spike in spikes)
        {
            var index = FindWindow(ordered, spike.Timestamp);
            if (index < 0)
            {
                continue;
            }

            if (ordered[index].IsLate)
            {
                lateHit.Add(index);

                if (!driverLateWindows.TryGetValue(spike.DriverName, out var windowsForDriver))
                {
                    windowsForDriver = new HashSet<int>();
                    driverLateWindows[spike.DriverName] = windowsForDriver;
                }

                windowsForDriver.Add(index);
            }
            else
            {
                onTimeHit.Add(index);
            }
        }

        var lateShare = (double)lateHit.Count / late.Count;
        var onTimeShare = onTime.Count > 0 ? (double)onTimeHit.Count / onTime.Count : 0;

        // Name-ordered as the tiebreak so two drivers covering the same number of windows always
        // produce the same answer, rather than one that depends on dictionary iteration order.
        var topDriver = driverLateWindows
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key)
            .FirstOrDefault();

        if (lateShare < MinimumLateShare || topDriver is null)
        {
            return new CorrelationResult(late.Count, lateHit.Count, onTime.Count, onTimeHit.Count, null,
                $"{late.Count} late report(s), but only {lateShare:P0} of them coincided with an interrupt spike — no driver is clearly responsible. " +
                "The cause is more likely the device, cable, or hub itself than the system's interrupt handling.");
        }

        // Lift: how much more often a spike appears in a late window than a normal one. Without this,
        // a driver that spikes constantly would be blamed for late reports it had nothing to do with.
        var lift = onTimeShare > 0 ? lateShare / onTimeShare : double.PositiveInfinity;
        if (lift < MinimumLift)
        {
            return new CorrelationResult(late.Count, lateHit.Count, onTime.Count, onTimeHit.Count, null,
                $"Interrupt spikes occur about as often during on-time reports ({onTimeShare:P0}) as during late ones ({lateShare:P0}), " +
                "so they aren't what's making reports late. The cause is more likely the device, cable, or hub itself.");
        }

        var liftText = double.IsInfinity(lift) ? "never during on-time reports" : $"{lift:0.#}x more often than during on-time reports";
        return new CorrelationResult(late.Count, lateHit.Count, onTime.Count, onTimeHit.Count, topDriver,
            $"{lateShare:P0} of your {late.Count} late report(s) coincided with an interrupt spike — {liftText}. " +
            $"The most frequent source was {topDriver}. That's the driver to address first.");
    }

    /// <summary>Binary search for the window containing a timestamp; -1 when it falls in none (e.g. during an idle pause).</summary>
    private static int FindWindow(List<ReportWindow> ordered, DateTime timestamp)
    {
        var low = 0;
        var high = ordered.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (timestamp < ordered[mid].Start)
            {
                high = mid - 1;
            }
            else if (timestamp > ordered[mid].End)
            {
                low = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }
}
