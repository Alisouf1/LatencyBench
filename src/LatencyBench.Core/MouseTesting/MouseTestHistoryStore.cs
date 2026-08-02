using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using LatencyBench.Core.MouseTesting.Models;
using LatencyBench.Core.Persistence;

namespace LatencyBench.Core.MouseTesting;

public sealed class MouseTestHistoryStore
{
    private readonly string _filePath;

    /// <summary>Guards Save() the same way TweakBackupStore serialises its writes — this store isn't
    /// currently called from more than one thread in practice (ObservableCollection itself is
    /// UI-thread-only), but a torn write from two overlapping saves is exactly the failure mode the
    /// atomic-write pattern below exists to rule out, so the write itself is still serialised.</summary>
    private readonly object _gate = new();

    public ObservableCollection<MouseTestSession> Results { get; } = new ObservableCollection<MouseTestSession>();

    public MouseTestHistoryStore(string? filePath = null)
    {
        if (filePath != null)
        {
            _filePath = filePath;
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        else
        {
            string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LatencyBench");
            Directory.CreateDirectory(text);
            _filePath = Path.Combine(text, "mouse-test-history.json");
        }
        Load();
    }

    public void Add(MouseTestSession session)
    {
        Results.Add(session);
        try
        {
            Save();
        }
        catch
        {
            // A failed save must not leave the UI showing a run that was never actually recorded —
            // that's a worse lie than the crash this whole fix replaced.
            Results.Remove(session);
            throw;
        }
    }

    public void Clear()
    {
        List<MouseTestSession> previous = new List<MouseTestSession>(Results);
        Results.Clear();
        try
        {
            Save();
        }
        catch
        {
            // Same reasoning as Add(): if the clear couldn't actually be persisted, the in-memory
            // list must not claim it was. Restored in the original order.
            foreach (MouseTestSession item in previous)
            {
                Results.Add(item);
            }

            throw;
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }
        try
        {
            List<MouseTestSession>? list = JsonSerializer.Deserialize<List<MouseTestSession>>(File.ReadAllText(_filePath));
            if (list == null)
            {
                return;
            }
            foreach (MouseTestSession item in list)
            {
                Results.Add(item);
            }
        }
        catch (Exception ex) when (((ex is JsonException || ex is IOException || ex is UnauthorizedAccessException) ? 1 : 0) != 0)
        {
            Results.Clear();
        }
    }

    private void Save()
    {
        string json = JsonSerializer.Serialize(Results);
        lock (_gate)
        {
            try
            {
                AtomicFileWriter.Write(_filePath, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Your mouse test history could not be saved to {_filePath}. This run isn't recorded — " +
                    "check that the file isn't open elsewhere and that there's room on the drive, then try again.",
                    ex);
            }
        }
    }
}
