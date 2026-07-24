using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LatencyBench.Core.Advisor;
using LatencyBench.Core.Affinity;
using LatencyBench.Core.DpcIsr;
using LatencyBench.Core.Elevation;
using LatencyBench.Core.Msi;
using LatencyBench.Core.MouseTesting;
using LatencyBench.Core.PortTesting;
using LatencyBench.Core.Tweaks;
using LatencyBench.Core.UsbTree;

namespace LatencyBench.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public string ElevationStatus { get; } = ElevationHelper.IsRunningAsAdministrator()
        ? "Running as administrator"
        : "Not running as administrator — affinity changes will fail";

    public DashboardViewModel Dashboard { get; }

    public PortTestViewModel PortTest { get; }

    public DpcIsrViewModel DpcIsr { get; }

    public MsiModeViewModel MsiMode { get; }

    public AffinityViewModel Affinity { get; }

    public TweaksViewModel Tweaks { get; }

    public MouseTestViewModel MouseTest { get; }

    [ObservableProperty]
    private NavSection _currentSection = NavSection.Dashboard;

    [ObservableProperty]
    private object? _currentViewModel;

    [ObservableProperty]
    private string? _rescanStatus;

    [ObservableProperty]
    private bool _isRunningFullDiagnostic;

    public string FullDiagnosticButtonLabel => IsRunningFullDiagnostic ? "Running…" : "Run full diagnostic";

    partial void OnIsRunningFullDiagnosticChanged(bool value) => OnPropertyChanged(nameof(FullDiagnosticButtonLabel));

    public MainViewModel()
    {
        var treeEnumerator = new UsbTreeEnumerator();
        var historyStore = new PortTestHistoryStore();
        var restartService = new DeviceRestartService();

        MsiMode = new MsiModeViewModel(new InterruptDeviceService(), restartService, new InterruptDeviceEnumerator(), treeEnumerator);
        Affinity = new AffinityViewModel(new InterruptAffinityService(treeEnumerator), restartService, treeEnumerator, new InterruptDeviceEnumerator());
        Tweaks = new TweaksViewModel(new TweakCatalog(), new RestorePointService());
        PortTest = new PortTestViewModel(historyStore, treeEnumerator);
        DpcIsr = new DpcIsrViewModel(new DpcIsrHistoryStore(), new InterruptDeviceEnumerator());
        MouseTest = new MouseTestViewModel(new MouseTestHistoryStore(), treeEnumerator);
        Dashboard = new DashboardViewModel(Affinity, DpcIsr, MsiMode, historyStore, treeEnumerator);
        CurrentViewModel = Dashboard;

        Affinity.LoadIfNeeded();
    }

    /// <summary>Forward WM_INPUT from the window's message hook here — safe to call regardless of which tab is active, since each underlying capture engine only records while its own test is actually running (and self-filters by device handle), so forwarding to both unconditionally never cross-contaminates a run.</summary>
    public void OnRawInputMessage(int msg, IntPtr lParam)
    {
        PortTest.OnRawInputMessage(msg, lParam);
        MouseTest.OnRawInputMessage(msg, lParam);
    }

    /// <summary>Release OS-level resources on window close. Critical for DPC/ISR: its ETW session is
    /// the single system-wide NT Kernel Logger, which would otherwise dangle until reboot (and block
    /// LatencyMon and other tools) if the app is closed mid-trace.</summary>
    public void Shutdown()
    {
        Affinity.SetActive(false);
        DpcIsr.Dispose();
    }

    public void SetWindowHandle(IntPtr handle)
    {
        PortTest.SetWindowHandle(handle);
        MouseTest.SetWindowHandle(handle);
    }

    [RelayCommand]
    private void Navigate(NavSection section) => CurrentSection = section;

    [RelayCommand]
    private async Task RescanAsync()
    {
        RescanStatus = "Rescanning…";
        try
        {
            await MsiMode.RefreshCommand.ExecuteAsync(null);
            await Affinity.RefreshCommand.ExecuteAsync(null);
            RescanStatus = MsiMode.ErrorMessage ?? Affinity.ErrorMessage ?? "Rescanned";
        }
        catch (Exception ex)
        {
            RescanStatus = $"Rescan failed: {ex.Message}";
        }
    }

    /// <summary>
    /// One click: rescan the state-based tabs, then run the DPC/ISR trace and the port test capture
    /// together (they don't conflict — one traces system-wide interrupts via ETW, the other reads HID
    /// reports), save both automatically, and land on the Dashboard's Action plan with fresh data.
    ///
    /// One thing this cannot do: the port test still needs a human moving the mouse or typing during
    /// its window — jitter is measured from real input events, so there is no way to synthesize that
    /// which would still mean anything. This starts that capture immediately; you still have to use
    /// the device while it runs.
    /// </summary>
    [RelayCommand]
    private async Task RunFullDiagnosticAsync()
    {
        if (IsRunningFullDiagnostic)
        {
            return;
        }

        IsRunningFullDiagnostic = true;
        try
        {
            RescanStatus = "Rescanning affinity and MSI state…";
            await MsiMode.RefreshCommand.ExecuteAsync(null);
            await Affinity.RefreshCommand.ExecuteAsync(null);

            CurrentSection = NavSection.PortTest;
            PortTest.RefreshDevicesCommand.Execute(null);

            if (PortTest.SelectedDevice is null)
            {
                RescanStatus = "No testable HID device found — plug in a mouse or keyboard and try again.";
                return;
            }

            // Named up front, while the test is still running, not just after — so testing the wrong
            // physical port (e.g. a device that was just moved and hasn't finished re-enumerating) is
            // obvious immediately instead of only being discovered once the result is already saved.
            RescanStatus = $"Testing {PortTest.SelectedDevice.DisplayLabel} now — move it or type on it for {PortTestViewModel.TestDurationSeconds}s (tracing interrupts at the same time)…";

            // Same length as the port test: a longer trace kept running silently in the background
            // after the port test's own progress UI already showed "complete", which read as though
            // only the port test had happened at all.
            var dpcIsrTask = DpcIsr.RunOnceAsync(PortTestViewModel.TestDurationSeconds);
            var portTestTask = PortTest.StartTestCommand.ExecuteAsync(null);
            await Task.WhenAll(dpcIsrTask, portTestTask);

            if (PortTest.LastResult is null)
            {
                // Not enough input was captured. Stay on Port test — its own status message explains
                // why — rather than silently continuing to the Dashboard as if a result was saved.
                RescanStatus = "Port test didn't capture enough input — try again and keep moving the mouse or typing the whole time.";
                return;
            }

            PortTest.SaveResultCommand.Execute(null);
            DpcIsr.SaveTraceCommand.Execute(null);

            // Only steer back to Dashboard if the user is still where this flow put them — if they
            // already navigated elsewhere themselves during the test, respect that instead of
            // yanking them back.
            if (CurrentSection == NavSection.PortTest)
            {
                CurrentSection = NavSection.Dashboard;
            }

            Dashboard.RefreshDiagnosis();
            RescanStatus = "Full diagnostic complete — see the Action plan.";
        }
        catch (Exception ex)
        {
            RescanStatus = $"Full diagnostic failed: {ex.Message}";
        }
        finally
        {
            IsRunningFullDiagnostic = false;
        }
    }

    partial void OnCurrentSectionChanged(NavSection value)
    {
        CurrentViewModel = value switch
        {
            NavSection.Dashboard => Dashboard,
            NavSection.PortTest => PortTest,
            NavSection.DpcIsr => DpcIsr,
            NavSection.MsiMode => MsiMode,
            NavSection.Affinity => Affinity,
            NavSection.Tweaks => Tweaks,
            NavSection.MouseTest => MouseTest,
            _ => Dashboard,
        };

        if (value == NavSection.Dashboard)
        {
            // Picks up anything changed on another tab since the Dashboard was last shown — the
            // per-tab CollectionChanged hooks in DashboardViewModel already catch most of this, but
            // core-affinity pinning changes don't raise one (Cores is mutated in place), so this is
            // the catch-all that guarantees the action plan is current every time it's viewed.
            MsiMode.LoadIfNeeded();
            Dashboard.RefreshDiagnosis();
            Dashboard.RefreshBackgroundLoad();
        }
        else if (value == NavSection.MsiMode)
        {
            MsiMode.LoadIfNeeded();
        }
        else if (value == NavSection.Affinity)
        {
            // Hand over the interrupt distribution averaged across every SAVED trace, not just the
            // newest one, so the core advice is based on where interrupts actually land rather than on
            // CPU usage (which can't see them) — and so it stops changing after each individual trace.
            Affinity.SetInterruptLoads(CoreLoadHistoryAggregator.Aggregate(DpcIsr.SavedTraces, Environment.ProcessorCount));
            Affinity.LoadIfNeeded();
        }
        else if (value == NavSection.Tweaks)
        {
            Tweaks.LoadIfNeeded();
        }
        else if (value == NavSection.PortTest)
        {
            PortTest.LoadIfNeeded();
        }
        else if (value == NavSection.MouseTest)
        {
            MouseTest.LoadIfNeeded();
        }

        Affinity.SetActive(value == NavSection.Affinity);
    }
}
