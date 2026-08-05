using System.IO;
using System.Text;

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
        //
        // Flush(flushToDisk: true) rather than File.WriteAllText: WriteAllText only flushes to the
        // operating system's write cache when it closes the handle, so the data can still be in
        // volatile memory when the replace below commits. NTFS journals the rename but not the file
        // contents, so a power loss in that window leaves the metadata pointing at a file whose data
        // never reached the platter - an intact-looking JSON file that is empty or truncated. Since
        // this store is the only record of what to restore when a tweak is undone, losing it silently
        // is worse than any cost of the flush. FlushFileBuffers is what makes the ordering real.
        // UTF8Encoding(false), not Encoding.UTF8: the latter emits a byte-order mark, which
        // File.WriteAllText did not. Switching to a StreamWriter without pinning this down would
        // silently prepend a BOM to every store this writes, and a strict JSON reader treats those
        // three bytes as content rather than an encoding hint.
        using (var stream = new FileStream(
            temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

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
