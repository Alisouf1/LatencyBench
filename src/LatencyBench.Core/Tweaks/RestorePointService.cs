using System;
using System.Management;
using System.Threading.Tasks;

namespace LatencyBench.Core.Tweaks;

/// <summary>
/// Virtual and non-sealed so a test can run the optimisation flow without asking Windows to take a
/// real System Restore snapshot, which is slow and rate-limited.
/// </summary>
public class RestorePointService
{
    /// <summary>
    /// How long to wait for the WMI call before giving up on it. System Restore is documented above
    /// as "slow and rate-limited," but nothing here used to act on that: <c>InvokeMethod</c> has no
    /// timeout or cancellation of its own, so if it ever actually hung — a stuck VSS writer, a
    /// throttled or disabled System Restore service — the wait was unbounded. Called from
    /// OptimizeViewModel.ApplyAsync, which sets IsBusy for the whole operation and only clears it in a
    /// finally once this returns, an unbounded hang here meant the Optimize page's IsEnabled bindings
    /// (every control on it is gated by IsBusy) never went back to true — the page looked permanently
    /// broken, with no error, and no way to recover short of restarting the app.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Overridable so a test can shrink the wait rather than actually waiting out
    /// <see cref="DefaultTimeout"/> to prove a simulated hang is bounded.</summary>
    protected virtual TimeSpan Timeout => DefaultTimeout;

    public virtual (bool Success, string Message) CreateRestorePoint(string description)
    {
        // Run on its own thread and bounded-wait for it rather than calling InvokeMethod directly:
        // that gives up waiting after Timeout instead of blocking forever. The WMI call itself has no
        // cancellation token, so a genuinely hung call keeps running on that background thread even
        // after this method returns — a leaked thread in the rare hang case, touching only its own
        // local COM objects, which is a far better trade than the whole Apply flow (and the page
        // showing it) hanging indefinitely with no way to recover.
        Task<(bool Success, string Message)> task = Task.Run(() => CreateRestorePointCore(description));

        if (task.Wait(Timeout))
        {
            return task.Result;
        }

        return (Success: false, Message:
            $"System Restore did not respond within {Timeout.TotalSeconds:0}s, so it was skipped. " +
            "It may be disabled by policy, throttled (Windows normally allows a new restore point about " +
            "once a day), or the service is stuck.");
    }

    /// <summary>The real WMI call, isolated behind a protected virtual seam so a test can simulate it
    /// hanging (to prove CreateRestorePoint's timeout actually bounds the wait) without needing a real
    /// stuck VSS writer to reproduce that against.</summary>
    protected virtual (bool Success, string Message) CreateRestorePointCore(string description)
    {
        try
        {
            ManagementClass managementClass = new ManagementClass("root\\default", "SystemRestore", null);
            try
            {
                ManagementBaseObject methodParameters = managementClass.GetMethodParameters("CreateRestorePoint");
                try
                {
                    methodParameters["Description"] = description;
                    methodParameters["RestorePointType"] = 12;
                    methodParameters["EventType"] = 100;
                    ManagementBaseObject managementBaseObject = managementClass.InvokeMethod("CreateRestorePoint", methodParameters, null);
                    try
                    {
                        uint num = ((managementBaseObject?["ReturnValue"] is uint num2) ? num2 : uint.MaxValue);
                        return (num == 0) ? (Success: true, Message: "Restore point created.") : (Success: false, Message: $"System Restore returned error code {num} — it may be disabled by policy, edition, or on this drive.");
                    }
                    finally
                    {
                        ((IDisposable)managementBaseObject)?.Dispose();
                    }
                }
                finally
                {
                    ((IDisposable)methodParameters)?.Dispose();
                }
            }
            finally
            {
                ((IDisposable)managementClass)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            return (Success: false, Message: "Could not create a restore point: " + ex.Message);
        }
    }
}
