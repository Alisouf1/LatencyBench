using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LatencyBench.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Hardware-accelerated rendering can leave stale pixels un-redrawn over Remote Desktop /
        // virtualized GPUs, showing up as leftover text fragments from a previous view. Forcing
        // software rendering avoids that class of compositor bug at a small performance cost.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LatencyBench");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "crash.log"), $"{DateTime.Now:O}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort logging only — never let the logger itself mask the original crash.
        }

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
}
