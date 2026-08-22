using System.IO;
using System.Text.Json;

namespace LocalPanel.Core;

/// <summary>
/// Detected tool versions, cached persistently and keyed by the binary's
/// timestamp — each exe is probed exactly once until it actually changes,
/// so pages render instantly instead of spawning processes on every visit.
/// </summary>
public static class ToolVersionCache
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _cache;

    private static string CacheFile =>
        Path.Combine(Paths.Ensure(Path.Combine(Paths.Etc, "cache")), "tool-versions.json");

    public static async Task<string> GetAsync(string exePath, Func<Task<string>> probe)
    {
        if (!File.Exists(exePath)) return "";
        string key;
        try { key = exePath.ToLowerInvariant() + "|" + File.GetLastWriteTimeUtc(exePath).Ticks; }
        catch { return await probe(); }

        lock (Gate)
        {
            _cache ??= Load();
            if (_cache.TryGetValue(key, out var hit)) return hit;
        }

        var v = await probe();
        if (v.Length > 0)
        {
            lock (Gate)
            {
                _cache![key] = v;
                try { File.WriteAllText(CacheFile, JsonSerializer.Serialize(_cache)); } catch { }
            }
        }
        return v;
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(CacheFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CacheFile))
                       ?? new Dictionary<string, string>();
        }
        catch { }
        return new Dictionary<string, string>();
    }
}
