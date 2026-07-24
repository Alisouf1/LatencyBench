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
    }
}
