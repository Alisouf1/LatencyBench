using System;
using System.Diagnostics;
using System.Threading;
using LatencyBench.Core.Tweaks;

namespace LatencyBench.Tests;

/// <summary>
/// The regression this suite exists for: CreateRestorePoint's WMI call had no timeout at all. Called
/// from OptimizeViewModel.ApplyAsync — which sets IsBusy for the whole operation, and every control on
/// the Optimize page is bound to IsBusy via IsEnabled — a hung WMI call meant the entire page went
/// permanently unresponsive with no error and no way to recover short of restarting the app.
/// </summary>
public sealed class RestorePointServiceTests
{
    private sealed class HangingRestorePointService : RestorePointService
    {
        protected override TimeSpan Timeout => TimeSpan.FromMilliseconds(50);

        protected override (bool Success, string Message) CreateRestorePointCore(string description)
        {
            // Far longer than Timeout — simulates a stuck VSS writer / hung WMI call without needing
            // a real one to reproduce it against.
            Thread.Sleep(TimeSpan.FromSeconds(5));
            return (true, "should never be observed");
        }
    }

    private sealed class ImmediateRestorePointService : RestorePointService
    {
        /// <summary>
        /// Deliberately generous, unlike <see cref="HangingRestorePointService"/>'s 50ms. These two
        /// cases are about the result being passed through faithfully, not about timing, and the work
        /// is queued to the thread pool — so under a fully parallel test run, scheduling latency alone
        /// could exceed a 50ms budget and time out a call that returns instantly, failing the test for
        /// a reason that has nothing to do with the code under test. The core here returns immediately,
        /// so a large timeout costs the suite nothing while removing that flakiness entirely.
        /// </summary>
        protected override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public bool Succeed { get; set; } = true;

        protected override (bool Success, string Message) CreateRestorePointCore(string description) =>
            Succeed ? (true, "Restore point created.") : (false, "System Restore returned error code 5.");
    }

    [Fact]
    public void AHungCallGivesUpAfterTheTimeoutInsteadOfBlockingForever()
    {
        var service = new HangingRestorePointService();
        var stopwatch = Stopwatch.StartNew();

        (bool success, string message) = service.CreateRestorePoint("test");

        stopwatch.Stop();
        Assert.False(success);
        Assert.Contains("did not respond", message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Expected CreateRestorePoint to give up around its 50ms timeout, took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void ACallThatFinishesWellWithinTheTimeoutReturnsItsRealResult()
    {
        var service = new ImmediateRestorePointService { Succeed = true };

        (bool success, string message) = service.CreateRestorePoint("test");

        Assert.True(success);
        Assert.Equal("Restore point created.", message);
    }

    [Fact]
    public void ACallThatFinishesWithAFailureStillReturnsThatFailureRatherThanATimeoutMessage()
    {
        // The timeout wrapper must not mask a real, fast failure (e.g. System Restore disabled by
        // policy) as if it had hung.
        var service = new ImmediateRestorePointService { Succeed = false };

        (bool success, string message) = service.CreateRestorePoint("test");

        Assert.False(success);
        Assert.Contains("error code", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("did not respond", message, StringComparison.OrdinalIgnoreCase);
    }
}
