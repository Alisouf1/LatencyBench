using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using LatencyBench.Core.Models;

namespace LatencyBench.Core.PortTesting;

public sealed class PortTestHistoryStore
{
	private readonly string _filePath;

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
		File.WriteAllText(_filePath, JsonSerializer.Serialize(Results));
	}
}
