using System;
using System.IO;
using LatencyBench.Core.Persistence;

namespace LatencyBench.Tests;

/// <summary>
/// The shared atomic-write helper extracted from TweakBackupStore and now also used by
/// MouseTestHistoryStore and PortTestHistoryStore. Every test here runs against a throwaway temp
/// directory rather than any of the app's real data files.
/// </summary>
public sealed class AtomicFileWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LatencyBenchTests_" + Guid.NewGuid().ToString("N"));

    public AtomicFileWriterTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup only — leaving a stray temp directory behind must not fail the test.
        }
    }

    [Fact]
    public void WritesTheFileWhenNoneExistedBefore()
    {
        string path = Path.Combine(_directory, "new.json");

        AtomicFileWriter.Write(path, "hello");

        Assert.True(File.Exists(path));
        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public void ReplacesTheFileWhenOneAlreadyExisted()
    {
        string path = Path.Combine(_directory, "existing.json");
        File.WriteAllText(path, "old content that must be fully gone afterward");

        AtomicFileWriter.Write(path, "new content");

        Assert.Equal("new content", File.ReadAllText(path));
    }

    [Fact]
    public void NeverLeavesTheTemporaryFileBehindOnSuccess()
    {
        string path = Path.Combine(_directory, "clean.json");

        AtomicFileWriter.Write(path, "first");
        AtomicFileWriter.Write(path, "second");

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("second", File.ReadAllText(path));
    }

    [Fact]
    public void ThrowsRatherThanSilentlySucceedingWhenTheTargetIsADirectory()
    {
        // A deterministic, portable way to force the final swap to fail: point the "file" path at a
        // path that is actually an existing directory. File.Exists is false for a directory, so this
        // exercises the File.Move branch specifically, and Windows refuses to move a file onto an
        // existing directory.
        string path = Path.Combine(_directory, "actually-a-directory");
        Directory.CreateDirectory(path);

        // Exactly which exception type Windows raises here (IOException vs. UnauthorizedAccessException)
        // isn't part of the contract this test cares about — only that a failed swap surfaces loudly
        // rather than silently reporting success while nothing actually landed at `path`.
        Exception? exception = Record.Exception(() => AtomicFileWriter.Write(path, "won't land"));
        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"Expected an IOException or UnauthorizedAccessException, got {exception?.GetType().FullName ?? "no exception"}.");
    }
}
