using System.Windows;
using System.Windows.Interop;
using LatencyBench.App.ViewModels;

namespace LatencyBench.App;

public partial class MainWindow : Window
{
    private const int WM_INPUT = 0x00FF;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        hwndSource.AddHook(WndProc);
        ((MainViewModel)DataContext).SetWindowHandle(hwndSource.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            ((MainViewModel)DataContext).OnRawInputMessage(msg, lParam);
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as MainViewModel)?.Shutdown();
        base.OnClosed(e);
    }
}
