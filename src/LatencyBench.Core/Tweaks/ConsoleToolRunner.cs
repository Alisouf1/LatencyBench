using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace LatencyBench.Core.Tweaks;

/// <summary>
/// Runs a Windows console tool and collects its output without deadlocking or hanging.
/// <para>
/// Every tweak that shelled out to powercfg, schtasks or fsutil had written the same unsafe
/// pattern: redirect both pipes, then <c>StandardOutput.ReadToEnd()</c> followed by
/// <c>StandardError.ReadToEnd()</c>, then <c>WaitForExit()</c> with no timeout. That deadlocks
/// whenever the child fills the pipe buffer it is not currently being drained from — the child
/// blocks writing to stderr, the parent blocks reading stdout, and neither ever moves. With no
/// timeout the UI thread waiting on the tweak then hangs for good.
/// </para>
/// </summary>
public static class ConsoleToolRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public static (int ExitCode, string StdOut, string StdErr) Run(string fileName, params string[] arguments)
    {
        return Run(fileName, DefaultTimeout, arguments);
    }

    public static (int ExitCode, string StdOut, string StdErr) Run(string fileName, TimeSpan timeout, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // ArgumentList hands each token to CreateProcess separately, so there is no shell-style
        // re-parsing step for an embedded quote or space to break out of.
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");

        Task<string> stdOut = process.StandardOutput.ReadToEndAsync();
        Task<string> stdErr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // It may have exited between the timeout expiring and the kill.
            }

            throw new TimeoutException($"{fileName} did not exit within {timeout.TotalSeconds:0} seconds.");
        }

        // WaitForExit(int) does not guarantee the redirected streams have been drained.
        Task.WaitAll(new Task[] { stdOut, stdErr }, timeout);

        return (process.ExitCode, stdOut.Result, stdErr.Result);
    }
}
