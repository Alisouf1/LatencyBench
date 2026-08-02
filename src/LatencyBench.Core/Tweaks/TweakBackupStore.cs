using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LatencyBench.Core.Persistence;

namespace LatencyBench.Core.Tweaks;

/// <summary>
/// The record of what every setting looked like before LatencyBench changed it. This file is the
/// only thing standing between the user and an unrevertable machine, so it is treated as the most
/// safety-critical piece of state in the app:
/// <list type="bullet">
/// <item>writes are atomic — a crash or power loss mid-save can never leave a half-written file;</item>
/// <item>a file that exists but cannot be read is an error, never an empty dictionary;</item>
/// <item>all access is serialised, so applying several tweaks at once cannot lose an entry.</item>
/// </list>
/// </summary>
public sealed class TweakBackupStore
{
    private readonly string _filePath;

    private readonly object _gate = new object();

    /// <summary>
    /// In-memory mirror of the file, loaded once. Every previous read went to disk — including one
    /// full file read per <see cref="TryGet"/> — which put a JSON parse on the UI thread for every
    /// tweak on every refresh.
    /// </summary>
    private Dictionary<string, string>? _cache;

    public TweakBackupStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LatencyBench",
            "tweak-backups.json"))
    {
    }

    public TweakBackupStore(string filePath)
    {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    }

    public void Save(string key, string value)
    {
        lock (_gate)
        {
            Dictionary<string, string> data = LoadLocked();
            data[key] = value;
            PersistLocked(data);
        }
    }

    public string? TryGet(string key)
    {
        lock (_gate)
        {
            return LoadLocked().GetValueOrDefault(key);
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            Dictionary<string, string> data = LoadLocked();
            if (data.Remove(key))
            {
                PersistLocked(data);
            }
        }
    }

    /// <summary>Every recorded backup, for the restore-everything path and for showing the user what
    /// is currently reversible.</summary>
    public IReadOnlyDictionary<string, string> GetAll()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(LoadLocked());
        }
    }

    private Dictionary<string, string> LoadLocked()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        if (!File.Exists(_filePath))
        {
            _cache = new Dictionary<string, string>();
            return _cache;
        }

        string json;
        try
        {
            json = File.ReadAllText(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Refusing to continue is the safe outcome. Returning an empty dictionary here — which is
            // what this used to do for malformed JSON — makes every Revert silently do nothing while
            // reporting success, which is far worse than surfacing the failure.
            throw new InvalidOperationException(
                $"The tweak backup file at {_filePath} could not be read, so reverting is not safe right now. " +
                "Close any other copy of LatencyBench and try again.", ex);
        }

        try
        {
            _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException ex)
        {
            // A corrupt file is preserved rather than overwritten: it is the only record of the user's
            // original settings and may still be recoverable by hand.
            string quarantine = _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            TryQuarantine(quarantine);
            throw new InvalidOperationException(
                $"The tweak backup file was corrupt and has been kept at {quarantine}. " +
                "LatencyBench cannot revert previously applied tweaks until it is restored or removed.", ex);
        }

        return _cache;
    }

    private void TryQuarantine(string destination)
    {
        try
        {
            File.Move(_filePath, destination, overwrite: false);
        }
        catch (Exception)
        {
            // Best effort — the caller is already throwing with the details.
        }
    }

    private void PersistLocked(Dictionary<string, string> data)
    {
        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        AtomicFileWriter.Write(_filePath, json);
        _cache = data;
    }
}
