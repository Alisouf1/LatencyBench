using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using LatencyBench.Core.Models;
using LatencyBench.Core.Persistence;

namespace LatencyBench.Core.PortTesting;

public sealed class PortTestHistoryStore
{
    private readonly string _filePath;

    /// <summary>Guards Save() the same way TweakBackupStore serialises its writes — see the identical
    /// comment on MouseTestHistoryStore, its sibling store.</summary>
    private readonly object _gate = new();

    public ObservableCollection<PortRankResult> Results { get; } = new ObservableCollection<PortRankResult>();

    public PortTestHistoryStore(string? filePath = null)
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
            _filePath = Path.Combine(text, "port-test-history.json");
        }
        Load();
    }

    public void Add(PortRankResult result)
    {
        Results.Add(result);
        try
        {
            Save();
        }
        catch
        {
            // A failed save must not leave the UI showing a run that was never actually recorded —
            // that's a worse lie than the crash this whole fix replaced.
            Results.Remove(result);
            throw;
        }
    }

    public void Clear()
    {
        List<PortRankResult> previous = new List<PortRankResult>(Results);
        Results.Clear();
        try
        {
            Save();
        }
        catch
        {
            // Same reasoning as Add(): if the clear couldn't actually be persisted, the in-memory
            // list must not claim it was. Restored in the original order.
            foreach (PortRankResult item in previous)
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
            List<PortRankResult>? list = JsonSerializer.Deserialize<List<PortRankResult>>(File.ReadAllText(_filePath));
            if (list == null)
            {
                return;
            }
            foreach (PortRankResult item in list)
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
                    $"Your port test history could not be saved to {_filePath}. This run isn't recorded — " +
                    "check that the file isn't open elsewhere and that there's room on the drive, then try again.",
                    ex);
            }
        }
    }
}
