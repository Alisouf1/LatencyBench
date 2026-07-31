using System;
using LatencyBench.Core.Monitoring;
using LatencyBench.Core.Monitoring.Models;

namespace LatencyBench.Tests;

public class MetricWatcherTests
{
	private static MetricWatcher Build(
		double raise = 10.0,
		double clear = 6.0,
		int toRaise = 3,
		int toClear = 3) => new(
			WarningMetric.DpcTime,
			raiseThreshold: raise,
			clearThreshold: clear,
			consecutiveToRaise: toRaise,
			consecutiveToClear: toClear,
			severity: WarningSeverity.Warning,
			describeRaise: v => $"raised at {v}",
			describeClear: v => $"cleared at {v}");

	private static DateTimeOffset T(int seconds) => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

	[Fact]
	public void DoesNotRaiseOnASingleSpike()
	{
		var watcher = Build(toRaise: 3);

		Assert.Null(watcher.Observe(50, T(0)));
		Assert.Null(watcher.Observe(1, T(1)));
		Assert.Null(watcher.Observe(1, T(2)));

		Assert.False(watcher.IsAlarmed);
	}

	[Fact]
	public void RaisesOnlyAfterEnoughConsecutiveSamplesOverThreshold()
	{
		var watcher = Build(raise: 10, toRaise: 3);

		Assert.Null(watcher.Observe(11, T(0)));
		Assert.Null(watcher.Observe(12, T(1)));
		var warning = watcher.Observe(13, T(2));

		Assert.NotNull(warning);
		Assert.False(warning!.IsRecovery);
		Assert.True(watcher.IsAlarmed);
	}

	[Fact]
	public void ADipBelowThresholdResetsTheConsecutiveCount()
	{
		var watcher = Build(raise: 10, toRaise: 3);

		Assert.Null(watcher.Observe(11, T(0)));
		Assert.Null(watcher.Observe(12, T(1)));
		Assert.Null(watcher.Observe(9, T(2))); // dips back under — count resets
		Assert.Null(watcher.Observe(11, T(3)));
		Assert.Null(watcher.Observe(12, T(4)));

		Assert.False(watcher.IsAlarmed);
	}

	[Fact]
	public void OnlyOneWarningIsRaisedWhileStayingOverThreshold()
	{
		var watcher = Build(raise: 10, toRaise: 2);

		watcher.Observe(11, T(0));
		var first = watcher.Observe(11, T(1));
		Assert.NotNull(first);

		// Ten more samples over threshold: the panel must not repeat the same warning every tick.
		for (int i = 2; i < 12; i++)
		{
			Assert.Null(watcher.Observe(15, T(i)));
		}
	}

	[Fact]
	public void ClearsOnlyAfterEnoughConsecutiveSamplesUnderTheClearThreshold()
	{
		var watcher = Build(raise: 10, clear: 5, toRaise: 1, toClear: 3);

		watcher.Observe(11, T(0)); // raised
		Assert.True(watcher.IsAlarmed);

		Assert.Null(watcher.Observe(4, T(1)));
		Assert.Null(watcher.Observe(4, T(2)));
		var recovery = watcher.Observe(4, T(3));

		Assert.NotNull(recovery);
		Assert.True(recovery!.IsRecovery);
		Assert.False(watcher.IsAlarmed);
	}

	[Fact]
	public void HysteresisGapPreventsFlappingAtTheBoundary()
	{
		// Sitting exactly between the raise and clear thresholds must not toggle every sample once
		// alarmed — that is the entire purpose of having two different thresholds.
		var watcher = Build(raise: 10, clear: 5, toRaise: 1, toClear: 1);

		watcher.Observe(10, T(0)); // raised
		Assert.True(watcher.IsAlarmed);

		for (int i = 1; i < 20; i++)
		{
			var result = watcher.Observe(7, T(i)); // between clear (5) and raise (10)
			Assert.Null(result);
			Assert.True(watcher.IsAlarmed);
		}
	}

	[Fact]
	public void RecoveryMessageUsesTheClearDescription()
	{
		var watcher = Build(raise: 10, clear: 5, toRaise: 1, toClear: 1);

		watcher.Observe(11, T(0));
		var recovery = watcher.Observe(2, T(1));

		Assert.Equal("cleared at 2", recovery!.Message);
		Assert.Equal(WarningSeverity.Info, recovery.Severity);
	}

	[Fact]
	public void RaiseMessageUsesTheConfiguredSeverity()
	{
		var watcher = new MetricWatcher(
			WarningMetric.MemoryPressure,
			raiseThreshold: 90, clearThreshold: 85,
			consecutiveToRaise: 1, consecutiveToClear: 1,
			severity: WarningSeverity.Critical,
			describeRaise: v => "bad",
			describeClear: v => "fine");

		var warning = watcher.Observe(95, T(0));

		Assert.Equal(WarningSeverity.Critical, warning!.Severity);
		Assert.Equal(WarningMetric.MemoryPressure, warning.Metric);
	}

	[Fact]
	public void ValueExactlyAtTheRaiseThresholdCounts()
	{
		var watcher = Build(raise: 10, toRaise: 1);

		Assert.NotNull(watcher.Observe(10, T(0)));
	}

	[Fact]
	public void ValueExactlyAtTheClearThresholdCounts()
	{
		var watcher = Build(raise: 10, clear: 5, toRaise: 1, toClear: 1);

		watcher.Observe(11, T(0));
		Assert.NotNull(watcher.Observe(5, T(1)));
	}
}
