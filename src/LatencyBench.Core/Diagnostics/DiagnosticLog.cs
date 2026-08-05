using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace LatencyBench.Core.Diagnostics;

/// <summary>
/// A small, always-on activity log written next to the app's saved data.
///
/// This exists because the failures that matter most here are the ones that cannot be reproduced on
/// the developer's machine: whether the analyser reached a verdict, which profile was selected, what
/// a plan actually contained, and whether each optimisation verified after being written. All of that
/// was previously invisible once the app was running on someone else's PC — the only artefact was
/// crash.log, which by definition only appears when something throws, and the interesting failures
/// here do not throw.
///
/// Deliberately not a logging framework: one file, no dependency, no configuration, and it can never
/// throw into the caller. A diagnostic aid that can itself break the operation it is observing is
/// worse than none.
/// </summary>
public static class DiagnosticLog
{
    /// <summary>
    /// Rotated at this size rather than growing without bound. Generous enough to hold many sessions
    /// of normal use, small enough that a user can attach it to a bug report.
    /// </summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    private static readonly object Gate = new();

    private static string? _pathOverride;

    /// <summary>
    /// Redirects the log, for tests that must not write to the real user profile. Passing null
    /// restores the default location.
    /// </summary>
    public static void OverridePathForTesting(string? path)
    {
        lock (Gate)
        {
            _pathOverride = path;
        }
    }

    public static string FilePath => _pathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LatencyBench",
        "activity.log");

    public static void Info(string category, string message) => Write("INFO ", category, message);

    public static void Warn(string category, string message) => Write("WARN ", category, message);

    public static void Error(string category, string message, Exception? exception = null) =>
        Write("ERROR", category, exception is null ? message : $"{message} :: {exception}");

    private static void Write(string level, string category, string message)
    {
        try
        {
            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} {1} [{2}] {3}{4}",
                DateTime.Now,
                level,
                category,
                message,
                Environment.NewLine);

            lock (Gate)
            {
                string path = FilePath;
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RotateIfOversizedLocked(path);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never surface as an application failure. There is nowhere useful to report
            // a failure to write the log to, so it is dropped deliberately rather than swallowed by
            // accident - this is the one place in the codebase where that is the correct behaviour.
        }
    }

    /// <summary>Keeps one previous file so a rotation never destroys the session that caused a
    /// problem. Caller must hold <see cref="Gate"/>.</summary>
    private static void RotateIfOversizedLocked(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < MaxBytes)
            {
                return;
            }

            string previous = path + ".1";
            if (File.Exists(previous))
            {
                File.Delete(previous);
            }

            File.Move(path, previous);
        }
        catch (IOException)
        {
            // A locked or vanished file is not worth failing over; the next write appends as normal.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
