using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LatencyBench.Core.Tweaks;

public sealed class TweakBackupStore
{
	private readonly string _filePath;

	public TweakBackupStore()
	{
		string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LatencyBench");
		Directory.CreateDirectory(text);
		_filePath = Path.Combine(text, "tweak-backups.json");
	}

	public void Save(string key, string value)
	{
		Dictionary<string, string> dictionary = Load();
		dictionary[key] = value;
		Persist(dictionary);
	}

	public string? TryGet(string key)
	{
		return Load().GetValueOrDefault(key);
	}

	public void Remove(string key)
	{
		Dictionary<string, string> dictionary = Load();
		if (dictionary.Remove(key))
		{
			Persist(dictionary);
		}
	}

	private Dictionary<string, string> Load()
	{
		if (!File.Exists(_filePath))
		{
			return new Dictionary<string, string>();
		}
		try
		{
			return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath)) ?? new Dictionary<string, string>();
		}
		catch (JsonException)
		{
			return new Dictionary<string, string>();
		}
	}

	private void Persist(Dictionary<string, string> data)
	{
		File.WriteAllText(_filePath, JsonSerializer.Serialize(data));
	}
}
