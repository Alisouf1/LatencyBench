using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using LatencyBench.Core.MouseTesting.Models;

namespace LatencyBench.Core.MouseTesting;

public sealed class MouseTestHistoryStore
{
    private readonly string _filePath;

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
        Save();
    }

    public void Clear()
    {
        Results.Clear();
        Save();
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
        File.WriteAllText(_filePath, JsonSerializer.Serialize(Results));
    }
}
