using System;
using System.Threading;
using System.Windows;

namespace LatencyBench.Tests;

/// <summary>
/// Shared harness for tests that construct real WPF controls.
///
/// A process may only ever hold one <see cref="Application"/> instance, and xUnit runs test classes
/// in parallel by default — so two classes each doing their own "create it if it doesn't exist yet"
/// check will race and the loser throws "Cannot create more than one System.Windows.Application
/// instance". Every WPF test class therefore goes through this one gate rather than keeping its own.
/// </summary>
internal static class WpfTestHost
{
    private static readonly object ApplicationGate = new();

    /// <summary>
    /// Creates the Application exactly once per process. Constructing App loads App.xaml, which is
    /// where every converter and brush the views reference by StaticResource is registered.
    /// </summary>
    internal static void EnsureApplication()
    {
        lock (ApplicationGate)
        {
            if (Application.Current is not null)
            {
                return;
            }

            var application = new LatencyBench.App.App();
            application.InitializeComponent();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a dedicated STA thread, which WPF requires and xUnit's
    /// worker threads are not. Exceptions are marshalled back to the calling test.
    /// </summary>
    internal static void RunOnStaThread(Action action, string failureContext, int timeoutSeconds = 90)
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

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(timeoutSeconds)),
            "The STA test thread did not finish in time.");

        if (failure is not null)
        {
            throw new InvalidOperationException($"{failureContext}: {failure.Message}", failure);
        }
    }
}
