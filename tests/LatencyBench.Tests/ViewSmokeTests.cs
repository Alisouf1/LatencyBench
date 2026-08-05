using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LatencyBench.App.ViewModels;
using LatencyBench.App.Views;

namespace LatencyBench.Tests;

/// <summary>
/// Instantiates the real views against real view-models.
/// <para>
/// WPF resolves XAML at runtime, so a StaticResource that does not exist, a converter key that was
/// mistyped, or a DataTemplate bound to the wrong type all compile cleanly and throw the first time
/// the tab is opened. Nothing else in this suite would catch that. These tests are deliberately
/// shallow — they prove the view can be constructed and bound, not that it looks right.
/// </para>
/// </summary>
public class ViewSmokeTests
{
    /// <summary>
    /// WPF requires a single-threaded apartment, which xUnit's worker threads are not. Each case runs
    /// on its own STA thread, and the Application object is created once because a process may only
    /// ever have one.
    /// </summary>
    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication();
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The STA test thread did not finish in time.");

        if (failure is not null)
        {
            throw new InvalidOperationException($"View construction failed: {failure.Message}", failure);
        }
    }

    // Delegates to the process-wide gate in WpfTestHost. Keeping a second gate here would not be
    // enough: xUnit runs test classes in parallel, so two classes with independent locks can still
    // both decide the Application does not exist yet and race to create it.
    private static void EnsureApplication() => WpfTestHost.EnsureApplication();

    [Fact]
    public void OptimizeViewLoadsAndBindsToItsViewModel()
    {
        RunOnStaThread(() =>
        {
            var view = new OptimizeView();
            view.DataContext = BuildOptimizeViewModel();

            // Measure forces the template tree to be built, which is when a bad StaticResource or a
            // missing DataTemplate actually throws. Constructing the control alone would not.
            view.Measure(new Size(1200, 2400));
            view.Arrange(new Rect(0, 0, 1200, 2400));

            Assert.NotNull(view.Content);
        });
    }

    [Fact]
    public void OptimizeViewRendersTheBenchmarkComparisonPanel()
    {
        // The before/after panel is hidden until BenchmarkResult is set, so the smoke tests above never
        // touch its template — a real 16-second apply-with-measurement round trip is not something a
        // fast test suite should run. Building the view-model result directly and assigning it exercises
        // the same binding and DataTemplate without paying for the real capture.
        RunOnStaThread(() =>
        {
            var comparisonResult = LatencyBench.Core.Benchmarking.BenchmarkComparer.Compare(
                new LatencyBench.Core.Benchmarking.Models.BenchmarkSnapshot(
                    DateTimeOffset.UtcNow, TimeSpan.FromSeconds(8), 16, 8.0, 15.0, 8.0, 15.0, 30.0, 40.0),
                new LatencyBench.Core.Benchmarking.Models.BenchmarkSnapshot(
                    DateTimeOffset.UtcNow, TimeSpan.FromSeconds(8), 16, 2.0, 4.0, 2.0, 4.0, 25.0, 60.0));

            var viewModel = new OptimizeViewModel(
                new LatencyBench.Core.Recommendations.RecommendationService(
                    new LatencyBench.Core.SystemInfo.SystemProfiler()),
                new LatencyBench.Core.DpcIsr.DpcIsrHistoryStore(),
                new LatencyBench.Core.Affinity.InterruptAffinityService(),
                new LatencyBench.Core.Msi.InterruptDeviceService())
            {
                BenchmarkResult = new BenchmarkComparisonViewModel(comparisonResult)
            };

            var view = new OptimizeView { DataContext = viewModel };
            view.Measure(new Size(1200, 4000));
            view.Arrange(new Rect(0, 0, 1200, 4000));

            Assert.NotNull(view.Content);
            Assert.NotEmpty(viewModel.BenchmarkResult!.MetricLines);
        });
    }

    [Fact]
    public void OptimizeViewRendersAPopulatedPlan()
    {
        // The previous test only proves the page frame renders. Every item template — the plan steps
        // with their evidence and conflict lists, the skipped items, the non-findings — is inside an
        // ItemsControl, and an ItemsControl with no items never instantiates its template at all. On
        // an already-tuned development machine the live analysis returns nothing, so a real bug in
        // those templates would go unnoticed. This forces a machine that needs work.
        RunOnStaThread(() =>
        {
            var viewModel = new OptimizeViewModel(
                new StubRecommendationService(),
                new LatencyBench.Core.DpcIsr.DpcIsrHistoryStore(),
                new LatencyBench.Core.Affinity.InterruptAffinityService(),
                new LatencyBench.Core.Msi.InterruptDeviceService());

            viewModel.LoadIfNeeded();
            PumpUntil(() => viewModel.Steps.Count > 0);

            Assert.NotEmpty(viewModel.Steps);
            Assert.NotEmpty(viewModel.NonFindings);

            var view = new OptimizeView { DataContext = viewModel };
            view.Measure(new Size(1200, 6000));
            view.Arrange(new Rect(0, 0, 1200, 6000));

            // Advanced mode swaps in the per-item checkboxes, which is a separate visual path.
            viewModel.AdvancedMode = true;
            view.Measure(new Size(1200, 6000));
            view.Arrange(new Rect(0, 0, 1200, 6000));

            Assert.NotNull(view.Content);
        });
    }

    /// <summary>
    /// The view-model's commands complete on the dispatcher, so the STA thread has to run its message
    /// loop for the results to arrive.
    /// </summary>
    private static void PumpUntil(Func<bool> condition, int timeoutMilliseconds = 30_000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;

        while (!condition() && Environment.TickCount64 < deadline)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        Assert.True(condition(), "The view-model did not reach the expected state in time.");
    }

    /// <summary>Returns a report describing a machine with work to do, regardless of the real one.</summary>
    private sealed class StubRecommendationService : LatencyBench.Core.Recommendations.RecommendationService
    {
        public StubRecommendationService()
            : base(new LatencyBench.Core.SystemInfo.SystemProfiler())
        {
        }

        public override Task<LatencyBench.Core.Recommendations.RecommendationReport> AnalyzeAsync(
            System.Collections.Generic.IReadOnlyList<LatencyBench.Core.DpcIsr.DpcIsrTestResult>? traces = null,
            CancellationToken cancellationToken = default)
        {
            var context = new LatencyBench.Core.Recommendations.RecommendationContext
            {
                Profile = TestProfiles.Profile(
                    windows: TestProfiles.Windows(
                        memoryIntegrity: LatencyBench.Core.SystemInfo.Models.FeatureState.Enabled)),
                TweakStates = TestProfiles.AllNotApplied(),
                IsElevated = true
            };

            return Task.FromResult(new LatencyBench.Core.Recommendations.RecommendationEngine().Analyze(context));
        }
    }

    [Fact]
    public void OptimizeViewModelStartsWithABalancedProfileSelected()
    {
        RunOnStaThread(() =>
        {
            OptimizeViewModel viewModel = BuildOptimizeViewModel();

            Assert.NotNull(viewModel.SelectedProfile);
            Assert.Equal(
                LatencyBench.Core.Profiles.Models.ProfileKind.Balanced,
                viewModel.SelectedProfile!.Kind);
            Assert.Equal(
                new LatencyBench.Core.Profiles.ProfileCatalog().BuildAll().Count,
                viewModel.Profiles.Count);
        });
    }

    [Fact]
    public void MainWindowResourcesResolveForEveryTab()
    {
        // Each tab is reached through a DataTemplate keyed on its view-model type. A tab whose
        // template is missing renders as the type name instead of the view, silently.
        RunOnStaThread(() =>
        {
            var window = new LatencyBench.App.MainWindow();

            foreach (Type viewModelType in new[]
            {
                typeof(OptimizeViewModel),
                typeof(ProcessTuningViewModel),
                typeof(MonitorViewModel),
                typeof(DashboardViewModel),
                typeof(TweaksViewModel),
                typeof(AffinityViewModel),
                typeof(MsiModeViewModel),
                typeof(PortTestViewModel),
                typeof(DpcIsrViewModel),
                typeof(MouseTestViewModel)
            })
            {
                object? template = window.TryFindResource(new DataTemplateKey(viewModelType));
                Assert.True(template is DataTemplate, $"No DataTemplate is registered for {viewModelType.Name}.");
            }
        });
    }

    [Fact]
    public void EveryNavSectionHasATabTarget()
    {
        RunOnStaThread(() =>
        {
            var window = new LatencyBench.App.MainWindow();
            var main = new MainViewModel();

            foreach (NavSection section in Enum.GetValues<NavSection>())
            {
                main.CurrentSection = section;

                Assert.NotNull(main.CurrentViewModel);
                object? template = window.TryFindResource(new DataTemplateKey(main.CurrentViewModel!.GetType()));
                Assert.True(
                    template is DataTemplate,
                    $"NavSection.{section} resolves to {main.CurrentViewModel.GetType().Name}, which has no DataTemplate.");
            }

            main.Shutdown();
        });
    }

    [Fact]
    public void ProcessTuningViewRendersAPopulatedList()
    {
        RunOnStaThread(() =>
        {
            var viewModel = new ProcessTuningViewModel(new LatencyBench.Core.Processes.ProcessTuner());
            viewModel.LoadIfNeeded();
            PumpUntil(() => !viewModel.IsLoading);

            // The test host itself always has a window on some CI configurations and not on others,
            // so this asserts the list loaded rather than asserting a specific count.
            Assert.False(viewModel.IsLoading);

            var view = new ProcessTuningView { DataContext = viewModel };
            view.Measure(new Size(1200, 4000));
            view.Arrange(new Rect(0, 0, 1200, 4000));

            Assert.NotNull(view.Content);
        });
    }

    [Fact]
    public void MonitorViewRendersWithAPopulatedWarningLog()
    {
        // The warning log's item template is only exercised with at least one entry, so this drives a
        // scripted counter source through a sustained high-DPC scenario to force a real warning rather
        // than asserting against an always-empty list.
        RunOnStaThread(() =>
        {
            var monitor = new LatencyBench.Core.Monitoring.LatencyMonitor(
                new ScriptedHighDpcCounterSource(),
                interval: TimeSpan.FromMilliseconds(5));

            var viewModel = new MonitorViewModel(monitor);
            viewModel.ToggleMonitoringCommand.Execute(null);

            PumpUntil(() => viewModel.WarningLog.Count > 0, timeoutMilliseconds: 15_000);

            var view = new MonitorView { DataContext = viewModel };
            view.Measure(new Size(1200, 3000));
            view.Arrange(new Rect(0, 0, 1200, 3000));

            Assert.NotNull(view.Content);

            viewModel.Shutdown();
        });
    }

    /// <summary>Reports 20% DPC time forever, well past the 5% sustained threshold, so the view test
    /// above reaches a real warning quickly and deterministically.</summary>
    private sealed class ScriptedHighDpcCounterSource : LatencyBench.Core.Monitoring.ICounterSource
    {
        public void Prime()
        {
        }

        public (double InterruptTimePercent, double DpcTimePercent, double ProcessorTimePercent) SampleCpu()
            => (1.0, 20.0, 30.0);

        public double SampleAvailableMemoryPercent() => 80.0;

        public void Dispose()
        {
        }
    }

    private static OptimizeViewModel BuildOptimizeViewModel() => new(
        new LatencyBench.Core.Recommendations.RecommendationService(
            new LatencyBench.Core.SystemInfo.SystemProfiler()),
        new LatencyBench.Core.DpcIsr.DpcIsrHistoryStore(),
        new LatencyBench.Core.Affinity.InterruptAffinityService(),
        new LatencyBench.Core.Msi.InterruptDeviceService());
}
