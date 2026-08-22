using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalPanel.Core;

public record RemoteVersion(string Component, string Version, string Url);

/// <summary>
/// Discovers downloadable versions of each component from the official sources.
/// Every fetcher degrades to a small hardcoded list if the network/API shape changes.
/// </summary>
public static class VersionCatalog
{
    // ---------------- PHP (windows.php.net) ----------------

    private static string PhpCacheFile =>
        Path.Combine(Paths.Ensure(Path.Combine(Paths.Etc, "cache")), "php-catalog.json");
    private static string NodeCacheFile =>
        Path.Combine(Paths.Ensure(Path.Combine(Paths.Etc, "cache")), "node-catalog.json");

    private static List<RemoteVersion> SortDesc(List<RemoteVersion> list) => list
        .OrderByDescending(v => string.Join(".", v.Version.Split('.').Select(p => p.PadLeft(6, '0'))),
            StringComparer.Ordinal)
        .ToList();

    private static List<RemoteVersion>? ReadCache(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                var c = JsonSerializer.Deserialize<List<RemoteVersion>>(File.ReadAllText(file));
                if (c is { Count: > 0 }) return c;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Instant list: last cached online catalog, else the built-in seed.</summary>
    public static List<RemoteVersion> GetPhpVersionsOffline()
        => SortDesc(ReadCache(PhpCacheFile) ?? PhpSeed());

    private static List<RemoteVersion> PhpSeed()
    {
        const string rel = "https://windows.php.net/downloads/releases/";
        const string arc = "https://windows.php.net/downloads/releases/archives/";
        return new List<RemoteVersion>
        {
            new("php", "8.5.9", rel + "php-8.5.9-nts-Win32-vs17-x64.zip"),
            new("php", "8.4.24", rel + "php-8.4.24-nts-Win32-vs17-x64.zip"),
            new("php", "8.3.33", rel + "php-8.3.33-nts-Win32-vs16-x64.zip"),
            new("php", "8.2.33", arc + "php-8.2.33-nts-Win32-vs16-x64.zip"),
            new("php", "8.1.34", arc + "php-8.1.34-nts-Win32-vs16-x64.zip"),
            new("php", "8.0.30", arc + "php-8.0.30-nts-Win32-vs16-x64.zip"),
            new("php", "7.4.33", arc + "php-7.4.33-nts-Win32-vc15-x64.zip"),
        };
    }

    /// <summary>Fresh list from windows.php.net; null when unreachable. Updates the cache.</summary>
    public static async Task<List<RemoteVersion>?> GetPhpVersionsOnlineAsync()
    {
        var list = new List<RemoteVersion>();
        try
        {
            // Current releases (one latest patch per active branch).
            var json = await Downloader.GetStringAsync("https://windows.php.net/downloads/releases/releases.json");
            using var doc = JsonDocument.Parse(json);
            foreach (var branch in doc.RootElement.EnumerateObject())
            {
                if (!branch.Value.TryGetProperty("version", out var verEl)) continue;
                var version = verEl.GetString();
                if (version is null) continue;

                // Prefer NTS x64 (php-cgi based FastCGI does not need thread safety).
                foreach (var build in branch.Value.EnumerateObject())
                {
                    var key = build.Name; // e.g. "nts-vs16-x64"
                    if (!key.StartsWith("nts-", StringComparison.OrdinalIgnoreCase) ||
                        !key.EndsWith("-x64", StringComparison.OrdinalIgnoreCase)) continue;
                    if (build.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!build.Value.TryGetProperty("zip", out var zip) ||
                        !zip.TryGetProperty("path", out var pathEl)) continue;
                    var path = pathEl.GetString();
                    if (path is null) continue;
                    list.Add(new RemoteVersion("php", version,
                        "https://windows.php.net/downloads/releases/" + path));
                    break;
                }
            }
        }
        catch { return null; }
        if (list.Count == 0) return null;

        // Older branches drop out of releases.json — keep known seed entries for them.
        var branches = list.Select(v => Branch(v.Version)).ToHashSet();
        list.AddRange(PhpSeed().Where(s => !branches.Contains(Branch(s.Version))));

        try { File.WriteAllText(PhpCacheFile, JsonSerializer.Serialize(list)); } catch { }
        return SortDesc(list);

        static string Branch(string v) => string.Join('.', v.Split('.').Take(2));
    }

    // ---------------- Node.js (nodejs.org) ----------------

    /// <summary>Instant list: last cached online catalog, else the built-in seed (LTS/even majors).</summary>
    public static List<RemoteVersion> GetNodeVersionsOffline()
        => SortDesc(ReadCache(NodeCacheFile) ?? new List<RemoteVersion>
        {
            new("node", "26.7.0", "https://nodejs.org/dist/v26.7.0/node-v26.7.0-win-x64.zip"),
            new("node", "24.19.0", "https://nodejs.org/dist/v24.19.0/node-v24.19.0-win-x64.zip"),
            new("node", "22.23.2", "https://nodejs.org/dist/v22.23.2/node-v22.23.2-win-x64.zip"),
            new("node", "20.20.2", "https://nodejs.org/dist/v20.20.2/node-v20.20.2-win-x64.zip"),
            new("node", "18.20.4", "https://nodejs.org/dist/v18.20.4/node-v18.20.4-win-x64.zip"),
        });

    /// <summary>Fresh list from nodejs.org; null when unreachable. Updates the cache.</summary>
    public static async Task<List<RemoteVersion>?> GetNodeVersionsOnlineAsync(int maxPerMajor = 1, int majors = 6)
    {
        var list = new List<RemoteVersion>();
        try
        {
            var json = await Downloader.GetStringAsync("https://nodejs.org/dist/index.json");
            using var doc = JsonDocument.Parse(json);
            var seenPerMajor = new Dictionary<string, int>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var v = el.GetProperty("version").GetString(); // "v20.11.0"
                if (v is null) continue;
                var hasWin = el.TryGetProperty("files", out var files) &&
                             files.EnumerateArray().Any(f => f.GetString() == "win-x64-zip");
                if (!hasWin) continue;
                var major = v.TrimStart('v').Split('.')[0];
                // Even majors only (odd ones never become LTS), and nothing older than 18.
                if (!int.TryParse(major, out var majorNum) || majorNum % 2 != 0 || majorNum < 18) continue;
                seenPerMajor.TryGetValue(major, out var n);
                if (n >= maxPerMajor) continue;
                seenPerMajor[major] = n + 1;
                list.Add(new RemoteVersion("node", v.TrimStart('v'),
                    $"https://nodejs.org/dist/{v}/node-{v}-win-x64.zip"));
                if (seenPerMajor.Count >= majors && seenPerMajor.Values.All(x => x >= maxPerMajor)) break;
            }
        }
        catch { return null; }
        if (list.Count == 0) return null;

        try { File.WriteAllText(NodeCacheFile, JsonSerializer.Serialize(list)); } catch { }
        return SortDesc(list);
    }

    // ---------------- nginx ----------------

    public static Task<List<RemoteVersion>> GetNginxVersionsAsync()
    {
        var versions = new[] { "1.27.4", "1.26.3" };
        return Task.FromResult(versions
            .Select(v => new RemoteVersion("nginx", v, $"https://nginx.org/download/nginx-{v}.zip"))
            .ToList());
    }

    // ---------------- MySQL ----------------

    public static Task<List<RemoteVersion>> GetMySqlVersionsAsync()
    {
        // dev.mysql.com/get/... redirects to a mirror automatically.
        // The popular trio: 8.4 (LTS), 8.0, and legacy 5.7 for old projects.
        var list = new List<RemoteVersion>
        {
            new("mysql", "8.4.4", "https://dev.mysql.com/get/Downloads/MySQL-8.4/mysql-8.4.4-winx64.zip"),
            new("mysql", "8.0.41", "https://dev.mysql.com/get/Downloads/MySQL-8.0/mysql-8.0.41-winx64.zip"),
            new("mysql", "5.7.44", "https://downloads.mysql.com/archives/get/p/23/file/mysql-5.7.44-winx64.zip"),
        };
        return Task.FromResult(list);
    }

    // ---------------- MariaDB ----------------

    public static Task<List<RemoteVersion>> GetMariaDbVersionsAsync()
        => Task.FromResult(new List<RemoteVersion>
        {
            // 11.4 = current LTS, 10.11 = previous LTS, 10.6 = older LTS still common.
            new("mariadb", "11.4.5", "https://archive.mariadb.org/mariadb-11.4.5/winx64-packages/mariadb-11.4.5-winx64.zip"),
            new("mariadb", "10.11.11", "https://archive.mariadb.org/mariadb-10.11.11/winx64-packages/mariadb-10.11.11-winx64.zip"),
            new("mariadb", "10.6.21", "https://archive.mariadb.org/mariadb-10.6.21/winx64-packages/mariadb-10.6.21-winx64.zip"),
        });

    // ---------------- PostgreSQL (EDB portable binaries) ----------------

    public static Task<List<RemoteVersion>> GetPostgresVersionsAsync()
        => Task.FromResult(new List<RemoteVersion>
        {
            new("postgres", "17.4", "https://get.enterprisedb.com/postgresql/postgresql-17.4-1-windows-x64-binaries.zip"),
            new("postgres", "16.8", "https://get.enterprisedb.com/postgresql/postgresql-16.8-1-windows-x64-binaries.zip"),
            new("postgres", "15.12", "https://get.enterprisedb.com/postgresql/postgresql-15.12-1-windows-x64-binaries.zip"),
        });

    // ---------------- MongoDB Community ----------------

    public static Task<List<RemoteVersion>> GetMongoVersionsAsync()
        => Task.FromResult(new List<RemoteVersion>
        {
            new("mongodb", "8.0.9", "https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.9.zip"),
            new("mongodb", "7.0.19", "https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-7.0.19.zip"),
            new("mongodb", "6.0.22", "https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-6.0.22.zip"),
        });

    // ---------------- Redis (Windows community builds) ----------------

    public static async Task<List<RemoteVersion>> GetRedisVersionsAsync()
    {
        // Modern Redis Windows builds, actively maintained — newest release of
        // each major (8.x / 7.x / 6.x) so LTS-minded users can pick their line.
        try
        {
            var json = await Downloader.GetStringAsync("https://api.github.com/repos/redis-windows/redis-windows/releases?per_page=40");
            using var doc = JsonDocument.Parse(json);
            var perMajor = new Dictionary<string, RemoteVersion>();
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                var tag = (rel.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
                if (tag.Length == 0 || !char.IsDigit(tag[0])) continue;
                var major = tag.Split('.')[0];
                if (perMajor.ContainsKey(major)) continue; // releases come newest-first
                string? best = null;
                foreach (var asset in rel.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!name.Contains("Windows-x64", StringComparison.OrdinalIgnoreCase)) continue;
                    var url = asset.GetProperty("browser_download_url").GetString();
                    if (url is null) continue;
                    if (name.Contains("msys2", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("with-Service", StringComparison.OrdinalIgnoreCase)) { best = url; break; }
                    best ??= url;
                }
                if (best is not null) perMajor[major] = new RemoteVersion("redis", tag, best);
                if (perMajor.Count >= 3) break;
            }
            if (perMajor.Count > 0)
            {
                var list = perMajor.Values.ToList();
                // Legacy 5.x (tporadowski) stays available for old projects.
                list.Add(new RemoteVersion("redis", "5.0.14.1",
                    "https://github.com/tporadowski/redis/releases/download/v5.0.14.1/Redis-x64-5.0.14.1.zip"));
                return SortDesc(list);
            }
        }
        catch { }

        // Fallback: tporadowski 5.x build.
        try
        {
            var json = await Downloader.GetStringAsync("https://api.github.com/repos/tporadowski/redis/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "latest";
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("Redis-x64", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var url = asset.GetProperty("browser_download_url").GetString()!;
                    return new List<RemoteVersion> { new("redis", tag.TrimStart('v'), url) };
                }
            }
        }
        catch { }
        return new List<RemoteVersion>
        {
            new("redis", "5.0.14.1",
                "https://github.com/tporadowski/redis/releases/download/v5.0.14.1/Redis-x64-5.0.14.1.zip")
        };
    }

    // ---------------- phpMyAdmin ----------------

    public static Task<List<RemoteVersion>> GetPhpMyAdminVersionsAsync()
        => Task.FromResult(new List<RemoteVersion>
        {
            // Stable alias that always points at the latest release.
            new("phpmyadmin", "latest", "https://www.phpmyadmin.net/downloads/phpMyAdmin-latest-all-languages.zip")
        });

    // ---------------- Mailpit ----------------

    public static async Task<RemoteVersion> GetMailpitAsync()
    {
        // The /latest/download/ URL is stable; the API call only names the version.
        const string url = "https://github.com/axllent/mailpit/releases/latest/download/mailpit-windows-amd64.zip";
        try
        {
            var json = await Downloader.GetStringAsync("https://api.github.com/repos/axllent/mailpit/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = (doc.RootElement.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
            if (tag.Length > 0) return new RemoteVersion("mailpit", tag, url);
        }
        catch { }
        return new RemoteVersion("mailpit", "latest", url);
    }

    // ---------------- phpredis extension (PECL) ----------------

    /// <summary>
    /// Find the latest PECL phpredis build matching a PHP version
    /// (e.g. 8.4.24 → php_redis-x.y.z-8.4-nts-vsNN-x64.zip).
    /// </summary>
    public static async Task<RemoteVersion?> FindPhpRedisAsync(string phpVersion)
    {
        var branch = string.Join('.', phpVersion.Split('.').Take(2));
        string[] mirrors =
        {
            "https://downloads.php.net/~windows/pecl/releases/redis",
            "https://windows.php.net/downloads/pecl/releases/redis",
        };
        foreach (var mirror in mirrors)
        {
            try
            {
                var listing = await Downloader.GetStringAsync(mirror + "/");
                var versions = Regex.Matches(listing, "href=\"(\\d+\\.\\d+\\.\\d+)/\"")
                    .Select(m => m.Groups[1].Value).Distinct()
                    .OrderByDescending(v => Version.Parse(v)).Take(8);
                foreach (var v in versions)
                {
                    string files;
                    try { files = await Downloader.GetStringAsync($"{mirror}/{v}/"); }
                    catch { continue; }
                    var m = Regex.Match(files,
                        $@"php_redis-[\d.]+-{Regex.Escape(branch)}-nts-vs\d+-x64\.zip");
                    if (m.Success)
                        return new RemoteVersion("php-redis-ext", v, $"{mirror}/{v}/{m.Value}");
                }
            }
            catch { /* try the next mirror */ }
        }
        return null;
    }

    // ---------------- Composer ----------------

    public static Task<RemoteVersion> GetComposerAsync()
        => Task.FromResult(new RemoteVersion("composer", "stable",
            "https://getcomposer.org/composer-stable.phar"));

    // ---------------- mkcert ----------------

    public static async Task<RemoteVersion> GetMkcertAsync()
    {
        try
        {
            var json = await Downloader.GetStringAsync("https://api.github.com/repos/FiloSottile/mkcert/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "latest";
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith("windows-amd64.exe", StringComparison.OrdinalIgnoreCase))
                    return new RemoteVersion("mkcert", tag.TrimStart('v'),
                        asset.GetProperty("browser_download_url").GetString()!);
            }
        }
        catch { }
        return new RemoteVersion("mkcert", "1.4.4",
            "https://github.com/FiloSottile/mkcert/releases/download/v1.4.4/mkcert-v1.4.4-windows-amd64.exe");
    }
}
