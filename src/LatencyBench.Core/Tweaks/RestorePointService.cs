using System;
using System.Management;

namespace LatencyBench.Core.Tweaks;

/// <summary>
/// Virtual and non-sealed so a test can run the optimisation flow without asking Windows to take a
/// real System Restore snapshot, which is slow and rate-limited.
/// </summary>
public class RestorePointService
{
    public virtual (bool Success, string Message) CreateRestorePoint(string description)
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
