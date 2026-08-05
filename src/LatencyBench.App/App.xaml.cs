using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LatencyBench.App;

public partial class App : Application
{
    /// <summary>
    /// Must match installer/LatencyBench.iss's AppMutex exactly — that directive only stops the
    /// installer running while the app is open if the app itself holds a system mutex by this
    /// name. Also doubles as this process's own single-instance guard: two copies running at once
    /// would both open InterruptAffinityService, TweakBackupStore and the saved-history files
    /// concurrently, and nothing in Core serialises writes across process boundaries — only within
    /// one process's own locks — so a second instance is refused outright rather than left to race
    /// the first.
    /// </summary>
    private const string SingleInstanceMutexName = "LatencyBenchSingleInstanceMutex";

    private Mutex? _singleInstanceMutex;

    /// <summary>
    /// Whether this process actually owns <see cref="_singleInstanceMutex"/>, as opposed to merely
    /// holding a handle to one another process owns. The two are not the same thing, and
    /// ReleaseMutex() on a mutex you do not own throws ApplicationException rather than being a
    /// no-op — which is exactly what happened to the second instance: it opened the existing mutex,
    /// failed to acquire it, showed the "already running" notice, and then crashed on the way out
    /// instead of exiting cleanly, because OnExit released unconditionally.
    /// </summary>
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!AcquireSingleInstanceLock())
        {
            MessageBox.Show(
                "LatencyBench is already running. Check the taskbar or system tray.",
                "LatencyBench",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // Shutdown() rather than Environment.Exit(): this runs before the main window is ever
            // created, so there is nothing to tear down, but Shutdown() still lets WPF unwind its
            // own startup sequence normally instead of hard-killing the process mid-init.
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Covers what DispatcherUnhandledException cannot: an exception on a thread pool thread
        // (Task.Run) that nothing awaits, or one thrown after the awaiting Task itself was garbage
        // collected before anyone observed its fault. Neither crashes the process on .NET 8 the way
        // it would have on .NET Framework, but both used to fail completely silently — the operation
        // just never completed, with no error shown and nothing in crash.log to explain why. These
        // two handlers exist purely so a silent failure becomes a logged one.
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Hardware-accelerated rendering can leave stale pixels un-redrawn over Remote Desktop /
        // virtualized GPUs, showing up as leftover text fragments from a previous view. Forcing
        // software rendering avoids that class of compositor bug at a small performance cost.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Ownership is per-thread, and OnExit is not contractually guaranteed to run on the
                // same thread that acquired it. Failing to release is harmless here - Dispose below
                // closes the handle, and the OS releases ownership when the process ends either way
                // - so this must never be allowed to turn a normal shutdown into a crash.
            }
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// True if this process now holds the mutex — either because it created it, or because it
    /// exists but nothing currently owns it (the previous holder exited without a clean
    /// OnExit — e.g. Task Manager "End task" — which abandons rather than releases the mutex; that
    /// is reported as an exception, not a false "already running").
    /// </summary>
    private bool AcquireSingleInstanceLock()
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (createdNew)
        {
            // initiallyOwned only actually grants ownership when this call created the mutex; when
            // one already existed the flag is ignored, which is why ownership is tracked explicitly
            // rather than assumed from the constructor argument.
            _ownsSingleInstanceMutex = true;
            return true;
        }

        try
        {
            _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(TimeSpan.Zero);
            return _ownsSingleInstanceMutex;
        }
        catch (AbandonedMutexException)
        {
            // The previous instance did not release it cleanly, but the mutex is not actually held
            // by anything now — this instance owns it as of the exception being thrown.
            _ownsSingleInstanceMutex = true;
            return true;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);

        // Every RelayCommand/timer callback that doesn't catch its own exceptions used to take the
        // whole app down here, since e.Handled defaulted to false and WPF terminates the process once
        // this handler returns. Marking it handled turns that into "this one action failed, the app
        // is still up" — the same recoverable-per-action failure mode every guarded command already
        // shows via its own StatusMessage, just applied as a last-resort net for the ones that aren't
        // guarded yet. The exception is still fully logged above first, so nothing about diagnosing it
        // is lost by not crashing.
        MessageBox.Show(
            $"Something went wrong and the last action didn't complete:\n\n{e.Exception.Message}\n\n" +
            "LatencyBench is still running — you can keep using it. Details were written to " +
            "crash.log in case this keeps happening.",
            "LatencyBench",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    /// <summary>
    /// AppDomain.UnhandledException fires for an exception .NET could not route anywhere else —
    /// meaning by the time this runs, the process is already on its way down and e.IsTerminating is
    /// effectively always true in practice. Nothing here can save the process; the only thing worth
    /// doing is making sure the failure is not silent.
    /// </summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogCrash(exception);
        }
    }

    /// <summary>
    /// A faulted Task that nothing ever awaited. This does not crash the process on .NET 8 (unlike
    /// .NET Framework's default), so e.SetObserved() below is what stops the exception being
    /// rethrown a second time during finalization — the process was never at risk, this is purely
    /// about making sure the failure ends up in the log instead of vanishing.
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        e.SetObserved();
    }

    private static void LogCrash(Exception exception)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LatencyBench");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "crash.log"), $"{DateTime.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort logging only — never let the logger itself mask the original crash.
        }
    }
}
