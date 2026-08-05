using LatencyBench.Core.Diagnostics;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Advisor;
using LatencyBench.Core.Models;
using LatencyBench.Core.Perf;
using LatencyBench.Core.PortTesting;
using LatencyBench.Core.UsbTree;

namespace LatencyBench.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly PortTestHistoryStore _historyStore;
    private readonly UsbTreeEnumerator _treeEnumerator;
    private readonly ProcessCpuMonitor _processCpuMonitor = new();

    public AffinityViewModel Affinity { get; }

    public DpcIsrViewModel DpcIsr { get; }

    public MsiModeViewModel MsiMode { get; }

    /// <summary>Only the best saved run per physical port — repeated tests of the same port are combined, not listed separately (see Port test for the full history).</summary>
    public ObservableCollection<PortRankResult> Ports { get; } = [];

    [ObservableProperty]
    private string _advisorMessage;

    /// <summary>One ranked action plan synthesized from every tab's data — see UnifiedDiagnosisGenerator. Recomputed whenever any tab's underlying data changes.</summary>
    public ObservableCollection<DiagnosisFinding> DiagnosisFindings { get; } = [];

    /// <summary>Top CPU-consuming processes right now, flagged where a known cause is recognized — see BackgroundProcessAuditor. Informational only; nothing here can be closed from this app.</summary>
    public ObservableCollection<FlaggedProcess> BackgroundProcesses { get; } = [];

    [ObservableProperty]
    private bool _isSamplingBackgroundLoad;

    [ObservableProperty]
    private bool _hasSampledBackgroundLoad;

    public DashboardViewModel(
        AffinityViewModel affinity,
        DpcIsrViewModel dpcIsr,
        MsiModeViewModel msiMode,
        PortTestHistoryStore historyStore,
        UsbTreeEnumerator treeEnumerator)
    {
        _historyStore = historyStore;
        _treeEnumerator = treeEnumerator;
        Affinity = affinity;
        DpcIsr = dpcIsr;
        MsiMode = msiMode;
        _advisorMessage = AdvisorMessageGenerator.Generate([]);

        historyStore.Results.CollectionChanged += (_, _) =>
        {
            RefreshPorts();
            RefreshDiagnosis();
        };
        DpcIsr.SavedTraces.CollectionChanged += (_, _) => RefreshDiagnosis();
        Affinity.Controllers.CollectionChanged += (_, _) => RefreshDiagnosis();
        MsiMode.Devices.CollectionChanged += (_, _) => RefreshDiagnosis();

        RefreshPorts();
        RefreshDiagnosis();
        RefreshBackgroundLoad();
    }

    private void RefreshPorts()
    {
        var groups = PortHistoryGrouping.GroupByPort(_historyStore.Results);

        Ports.Clear();
        foreach (var group in groups)
        {
            Ports.Add(group.Best);
        }

        AdvisorMessage = AdvisorMessageGenerator.Generate(Ports);
    }

    /// <summary>Recomputes the unified action plan. Safe to call anytime — called whenever any tab's data changes, and on navigating to the Dashboard, so a change made elsewhere shows up here without extra effort from the user.</summary>
    public void RefreshDiagnosis() => _ = RefreshDiagnosisAsync();

    private async Task RefreshDiagnosisAsync()
    {
        // The action plan is a convenience layer over data the individual tabs already show, and this
        // runs fire-and-forget from several CollectionChanged handlers with nothing awaiting it — so
        // the whole recompute is wrapped in one try/catch. A failure anywhere in it (a stale
        // enumeration, an unexpected data shape) just means the action plan doesn't update this time
        // rather than becoming an unobserved exception or breaking anything else on the Dashboard; the
        // next change that fires this again gets another chance.
        try
        {
            var tree = await Task.Run(() => _treeEnumerator.EnumerateHostControllers());

            var controllerSignals = Affinity.Controllers.Select(c =>
            {
                var pinned = c.Cores.Where(x => x.IsSelected).Select(x => x.Index).ToList();
                var policy = pinned.Count > 0 ? InterruptAffinityPolicy.SpecifiedProcessors : InterruptAffinityPolicy.MachineDefault;
                var node = tree.FirstOrDefault(n => string.Equals(n.InstanceId, c.InstanceId, StringComparison.OrdinalIgnoreCase));
                var contentionDetail = node is null ? null : ControllerContentionAdvisor.Generate([node], [c.ControllerNumber]);
                var hasLatencySensitiveDevice = node is not null && ControllerContentionAdvisor.HasLatencySensitiveDevice(node);

                var matchingCoreLoads = DpcIsr.CoreLoads.Where(cl => pinned.Contains(cl.CoreIndex)).ToList();
                double? pinnedShare = matchingCoreLoads.Count == 0 ? null : matchingCoreLoads.Max(cl => cl.SharePercent);

                return new ControllerSignal(c.AttachedDevicesSummary, c.ControllerNumber, c.InstanceId, contentionDetail, policy, pinned, pinnedShare, hasLatencySensitiveDevice);
            }).ToList();

            // Every MSI-mode category, not just GPU — a mouse/keyboard/USB microphone doesn't have its
            // own MSI/priority settings, the USB controller carrying it does, so that controller has to
            // be a candidate here too for the Action Plan to ever recommend tuning it.
            var interruptDeviceSignals = MsiMode.Devices
                .Select(d => new InterruptDeviceSignal(d.DeviceLabel, d.CategoryLabel, d.ServiceName, d.IsMsiEnabled, d.SelectedPriority))
                .ToList();

            var coreLoads = DpcIsr.CoreLoads.Select(c => new CoreLoad(c.CoreIndex, c.SharePercent)).ToList();
            var wirelessInterferenceWarning = WirelessInterferenceAdvisor.Generate(tree);

            var findings = UnifiedDiagnosisGenerator.Generate(_historyStore.Results.ToList(), DpcIsr.SavedTraces.ToList(), controllerSignals, coreLoads, interruptDeviceSignals, wirelessInterferenceWarning);

            DiagnosisFindings.Clear();
            foreach (var finding in findings)
            {
                DiagnosisFindings.Add(finding);
            }
        }
        catch (Exception ex)
        {
            // Still swallowed, for the reason above: this is fire-and-forget from CollectionChanged
            // handlers, so rethrowing would surface as an unobserved task exception rather than
            // anything the user could act on. But swallowing it silently meant a persistent failure
            // here showed up only as an Action Plan that never updated, with nothing to diagnose it
            // from. Logging costs nothing and turns that into an answerable question.
            DiagnosticLog.Warn("Dashboard", $"Action plan refresh failed and was skipped: {ex}");
        }
    }

    [RelayCommand]
    private void ClearResults() => _historyStore.Clear();

    /// <summary>Fire-and-forget entry point — safe to call from the constructor or a navigation hook without awaiting.</summary>
    public void RefreshBackgroundLoad() => _ = RefreshBackgroundLoadAsync();

    [RelayCommand]
    private async Task RefreshBackgroundLoadAsync()
    {
        if (IsSamplingBackgroundLoad)
        {
            return;
        }

        IsSamplingBackgroundLoad = true;
        try
        {
            // Half a second is enough to get a stable delta without making the Dashboard feel stuck
            // on every visit — this only needs to point at what's using meaningful CPU right now, not
            // produce a precise long-run average.
            var samples = await _processCpuMonitor.SampleAsync(TimeSpan.FromMilliseconds(500));
            var flagged = BackgroundProcessAuditor.Audit(samples);

            BackgroundProcesses.Clear();
            foreach (var process in flagged)
            {
                BackgroundProcesses.Add(process);
            }

            HasSampledBackgroundLoad = true;
        }
        finally
        {
            IsSamplingBackgroundLoad = false;
        }
    }
}
