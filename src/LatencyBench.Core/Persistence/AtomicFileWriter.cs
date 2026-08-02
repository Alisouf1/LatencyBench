using System.IO;

namespace LatencyBench.Core.Persistence;

/// <summary>
/// Writes a file so an interruption mid-save can never leave it half-written or truncated: the new
/// content is built in a temp file next to the target, then swapped in atomically.
/// <para>
/// Extracted from <c>TweakBackupStore</c>, which — being the only record of what to restore if a
/// tweak needs to be undone — could not tolerate a plain <c>File.WriteAllText</c> straight onto the
/// real path. That truncates the file first, so a crash or power loss mid-save leaves it empty or
/// partial with no way to recover the data that used to be there. The same failure mode applies to
/// any other JSON store in this app that would rather lose the whole file to corruption than fail
/// loudly, which is why this is a shared helper instead of three copies of the same dance.
/// </para>
/// </summary>
public static class AtomicFileWriter
{
    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="filePath"/> atomically. Throws whatever
    /// <see cref="File.WriteAllText(string, string)"/>, <see cref="File.Replace(string, string, string?, bool)"/>
    /// or <see cref="File.Move(string, string)"/> throw on failure (typically <see cref="IOException"/>
    /// or <see cref="UnauthorizedAccessException"/>) — callers that want a clearer, domain-specific
    /// message should catch and rewrap those rather than this method guessing at one.
    /// </summary>
    public static void Write(string filePath, string contents)
    {
        string temporaryPath = filePath + ".tmp";

        // Write-then-replace. Writing straight onto filePath would truncate it first, so an
        // interruption at that moment leaves an empty or partial file and whatever was in it before
        // is gone for good.
        File.WriteAllText(temporaryPath, contents);

        if (File.Exists(filePath))
        {
            File.Replace(temporaryPath, filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, filePath);
        }
    }
}
