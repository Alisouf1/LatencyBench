using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using LatencyBench.Core.Models;
using LatencyBench.Core.MouseTesting;
using LatencyBench.Core.MouseTesting.Models;
using LatencyBench.Core.PortTesting;
using LatencyBench.Core.Tweaks;

namespace LatencyBench.Tests;

/// <summary>
/// LatencyBench ships English-only with no localized resources, but it still has to run correctly
/// on a non-English Windows — which is a different question. On a de-DE machine the decimal
/// separator is a comma, so any number written with the ambient culture instead of an explicit
/// invariant one produces "12,5" where "12.5" was meant. In a CSV that is an extra column; in a
/// file that gets read back it is either a parse failure or, worse, a silently wrong value.
///
/// These tests pin the two places where a number crosses a machine-readable boundary: the saved
/// history/backup JSON, and the CSV export. Each one forces de-DE as the ambient culture for the
/// duration of the test, so a regression to ambient-culture formatting fails here rather than only
/// on a German user's machine where nobody would be able to reproduce it.
/// </summary>
public sealed class InvariantCultureTests : IDisposable
{
    private readonly string _directory;
    private readonly CultureInfo _originalCulture;
    private readonly CultureInfo _originalUiCulture;

    public InvariantCultureTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "LatencyBenchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _originalCulture = Thread.CurrentThread.CurrentCulture;
        _originalUiCulture = Thread.CurrentThread.CurrentUICulture;

        // de-DE: comma decimal separator, period as the thousands separator — the exact inversion
        // of the invariant culture, so anything culture-sensitive shows up immediately.
        var german = new CultureInfo("de-DE");
        Thread.CurrentThread.CurrentCulture = german;
        Thread.CurrentThread.CurrentUICulture = german;
    }

    public void Dispose()
    {
        Thread.CurrentThread.CurrentCulture = _originalCulture;
        Thread.CurrentThread.CurrentUICulture = _originalUiCulture;

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    [Fact]
    public void CultureFixtureActuallyAppliesCommaDecimalSeparator()
    {
        // Guards the other tests in this file: if the runtime ever ignored the culture assignment
        // (globalization-invariant mode, for instance), every assertion below would pass for the
        // wrong reason and quietly stop testing anything.
        Assert.Equal("12,5", 12.5.ToString("0.0"));
    }

    [Fact]
    public void PortHistoryRoundTripsFractionalValuesUnderACommaDecimalCulture()
    {
        string path = Path.Combine(_directory, "port-history.json");
        var store = new PortTestHistoryStore(path);

        store.Add(new PortRankResult
        {
            PortLabel = "Port 1",
            PortLocation = "Controller 1, port 1",
            AverageJitterMs = 12.5,
            AverageReportIntervalMs = 0.875,
        });

        var reloaded = new PortTestHistoryStore(path);

        Assert.Single(reloaded.Results);
        Assert.Equal(12.5, reloaded.Results[0].AverageJitterMs);
        Assert.Equal(0.875, reloaded.Results[0].AverageReportIntervalMs);

        // The value must be on disk in invariant form, not as "12,5" — a file written on a German
        // machine has to stay readable on an English one and vice versa.
        string json = File.ReadAllText(path);
        Assert.Contains("12.5", json);
        Assert.DoesNotContain("12,5", json);
    }

    [Fact]
    public void TweakBackupRoundTripsUnderACommaDecimalCulture()
    {
        string path = Path.Combine(_directory, "tweak-backups.json");
        var store = new TweakBackupStore(path);

        store.Save("registry:HKLM\\Foo\\Bar", "42");

        var reloaded = new TweakBackupStore(path);

        // This is the only record of the pre-tweak machine state, so a culture-dependent failure
        // here would mean an unrevertable tweak, not just a display glitch.
        Assert.Equal("42", reloaded.TryGet("registry:HKLM\\Foo\\Bar"));
    }

    [Fact]
    public void CsvExportUsesAPeriodDecimalSeparatorUnderACommaDecimalCulture()
    {
        var samples = new List<MouseSample>
        {
            new(TimestampTicks: 0, Dx: 1, Dy: 2, ButtonFlags: 0),
            new(TimestampTicks: 15_000, Dx: -3, Dy: 4, ButtonFlags: 1),
        };

        using var writer = new StringWriter();
        MouseCsvExporter.Write(writer, samples, ticksPerMillisecond: 10_000d);
        string csv = writer.ToString();

        // 15000 ticks / 10000 ticks-per-ms = 1.5 ms. Written as "1,5" it would split the row into
        // an extra column and corrupt every downstream parse of this file.
        Assert.Contains("1.5,", csv);
        Assert.DoesNotContain("1,5,", csv);
    }
}
