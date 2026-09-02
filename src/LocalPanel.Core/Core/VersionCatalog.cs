using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalPanel.Core;

public record RemoteVersion(string Component, string Version, string Url);

/// <summary>
/// Discovers downloadable versions of each component from official sources for Windows and Linux.
/// </summary>
public static class VersionCatalog
{
    private static string PhpCacheFile =>
        Path.Combine(Paths.Ensure(Path.Combine(Paths.Etc, "cache")), $"php-catalog-{(Platform.IsWindows ? "win" : "linux")}.json");
    private static string NodeCacheFile =>
        Path.Combine(Paths.Ensure(Path.Combine(Paths.Etc, "cache")), $"node-catalog-{(Platform.IsWindows ? "win" : "linux")}.json");

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

    // ---------------- PHP ----------------

    public static List<RemoteVersion> GetPhpVersionsOffline()
        => SortDesc(ReadCache(PhpCacheFile) ?? PhpSeed());

    private static List<RemoteVersion> PhpSeed()
    {
        if (Platform.IsWindows)
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

        // Linux static/portable PHP packages
        const string gh = "https://github.com/crazywhalecc/static-php-cli/releases/download/";
        return new List<RemoteVersion>
        {
            new("php", "8.4.4", gh + "php-8.4-latest/php-8.4.4-cli-fpm-linux-x86_64.tar.gz"),
            new("php", "8.3.17", gh + "php-8.3-latest/php-8.3.17-cli-fpm-linux-x86_64.tar.gz"),
            new("php", "8.2.27", gh + "php-8.2-latest/php-8.2.27-cli-fpm-linux-x86_64.tar.gz"),
            new("php", "8.1.31", gh + "php-8.1-latest/php-8.1.31-cli-fpm-linux-x86_64.tar.gz"),
        };
    }

    public static async Task<List<RemoteVersion>?> GetPhpVersionsOnlineAsync()
    {
        if (!Platform.IsWindows)
        {
            return GetPhpVersionsOffline();
        }

        var list = new List<RemoteVersion>();
        try
        {
            var json = await Downloader.GetStringAsync("https://windows.php.net/downloads/releases/releases.json");
            using var doc = JsonDocument.Parse(json);
            foreach (var branch in doc.RootElement.EnumerateObject())
            {
                if (!branch.Value.TryGetProperty("version", out var verEl)) continue;
                var version = verEl.GetString();
                if (version is null) continue;

                foreach (var build in branch.Value.EnumerateObject())
                {
                    var key = build.Name;
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

        var branches = list.Select(v => Branch(v.Version)).ToHashSet();
        list.AddRange(PhpSeed().Where(s => !branches.Contains(Branch(s.Version))));

        try { File.WriteAllText(PhpCacheFile, JsonSerializer.Serialize(list)); } catch { }
        return SortDesc(list);

        static string Branch(string v) => string.Join('.', v.Split('.').Take(2));
    }

    // ---------------- Node.js (nodejs.org) ----------------

    public static List<RemoteVersion> GetNodeVersionsOffline()
    {
        var ext = Platform.IsWindows ? "win-x64.zip" : "linux-x64.tar.gz";
        return SortDesc(ReadCache(NodeCacheFile) ?? new List<RemoteVersion>
        {
            new("node", "26.7.0", $"https://nodejs.org/dist/v26.7.0/node-v26.7.0-{ext}"),
            new("node", "24.19.0", $"https://nodejs.org/dist/v24.19.0/node-v24.19.0-{ext}"),
            new("node", "22.23.2", $"https://nodejs.org/dist/v22.23.2/node-v22.23.2-{ext}"),
            new("node", "20.20.2", $"https://nodejs.org/dist/v20.20.2/node-v20.20.2-{ext}"),
            new("node", "18.20.4", $"https://nodejs.org/dist/v18.20.4/node-v18.20.4-{ext}"),
        });
    }

    public static async Task<List<RemoteVersion>?> GetNodeVersionsOnlineAsync(int maxPerMajor = 1, int majors = 6)
    {
        var list = new List<RemoteVersion>();
        var targetFileKind = Platform.IsWindows ? "win-x64-zip" : "linux-x64-tar";
        var targetFileExt = Platform.IsWindows ? "win-x64.zip" : "linux-x64.tar.gz";
        try
        {
            var json = await Downloader.GetStringAsync("https://nodejs.org/dist/index.json");
            using var doc = JsonDocument.Parse(json);
            var seenPerMajor = new Dictionary<string, int>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var v = el.GetProperty("version").GetString();
                if (v is null) continue;
                var hasTarget = el.TryGetProperty("files", out var files) &&
                                files.EnumerateArray().Any(f => f.GetString() == targetFileKind || (Platform.IsLinux && f.GetString() == "linux-x64"));
                if (!hasTarget) continue;
                var major = v.TrimStart('v').Split('.')[0];
                if (!int.TryParse(major, out var majorNum) || majorNum % 2 != 0 || majorNum < 18) continue;
                seenPerMajor.TryGetValue(major, out var n);
                if (n >= maxPerMajor) continue;
                seenPerMajor[major] = n + 1;
                list.Add(new RemoteVersion("node", v.TrimStart('v'),
                    $"https://nodejs.org/dist/{v}/node-{v}-{targetFileExt}"));
                if (seenPerMajor.Count >= majors && seenPerMajor.Values.All(x => x >= maxPerMajor)) break;
            }
        }
        catch { return null; }
        if (list.Count == 0) return null;

        try { File.WriteAllText(NodeCacheFile, JsonSerializer.Serialize(list)); } catch { }
        return SortDesc(list);
    }

    // ---------------- Nginx ----------------

    public static Task<List<RemoteVersion>> GetNginxVersionsAsync()
    {
        if (Platform.IsWindows)
        {
            var versions = new[] { "1.27.4", "1.26.3" };
            return Task.FromResult(versions
                .Select(v => new RemoteVersion("nginx", v, $"https://nginx.org/download/nginx-{v}.zip"))
                .ToList());
        }

        return Task.FromResult(new List<RemoteVersion>
        {
            new("nginx", "1.27.4", "https://nginx.org/download/nginx-1.27.4.tar.gz"),
        });
    }

    // ---------------- MySQL ----------------

    public static Task<List<RemoteVersion>> GetMySqlVersionsAsync()
    {
        if (Platform.IsWindows)
        {
            return Task.FromResult(new List<RemoteVersion>
            {
                new("mysql", "8.4.4", "https://dev.mysql.com/get/Downloads/MySQL-8.4/mysql-8.4.4-winx64.zip"),
                new("mysql", "8.0.41", "https://dev.mysql.com/get/Downloads/MySQL-8.0/mysql-8.0.41-winx64.zip"),
                new("mysql", "5.7.44", "https://downloads.mysql.com/archives/get/p/23/file/mysql-5.7.44-winx64.zip"),
            });
        }

        return Task.FromResult(new List<RemoteVersion>
        {
            new("mysql", "8.4.4", "https://dev.mysql.com/get/Downloads/MySQL-8.4/mysql-8.4.4-linux-glibc2.28-x86_64.tar.xz"),
            new("mysql", "8.0.41", "https://dev.mysql.com/get/Downloads/MySQL-8.0/mysql-8.0.41-linux-glibc2.28-x86_64.tar.xz"),
        });
    }

    // ---------------- MariaDB ----------------

    public static Task<List<RemoteVersion>> GetMariaDbVersionsAsync()
    {
        if (Platform.IsWindows)
        {
            return Task.FromResult(new List<RemoteVersion>
            {
                new("mariadb", "11.4.5", "https://archive.mariadb.org/mariadb-11.4.5/winx64-packages/mariadb-11.4.5-winx64.zip"),
                new("mariadb", "10.11.11", "https://archive.mariadb.org/mariadb-10.11.11/winx64-packages/mariadb-10.11.11-winx64.zip"),
            });
        }

        return Task.FromResult(new List<RemoteVersion>
        {
            new("mariadb", "11.4.5", "https://archive.mariadb.org/mariadb-11.4.5/bintar-linux-systemd-x86_64/mariadb-11.4.5-linux-systemd-x86_64.tar.gz"),
            new("mariadb", "10.11.11", "https://archive.mariadb.org/mariadb-10.11.11/bintar-linux-systemd-x86_64/mariadb-10.11.11-linux-systemd-x86_64.tar.gz"),
        });
    }

    // ---------------- PostgreSQL ----------------

    public static Task<List<RemoteVersion>> GetPostgresVersionsAsync()
    {
        if (Platform.IsWindows)
        {
            return Task.FromResult(new List<RemoteVersion>
            {
                new("postgres", "17.4", "https://get.enterprisedb.com/postgresql/postgresql-17.4-1-windows-x64-binaries.zip"),
                new("postgres", "16.8", "https://get.enterprisedb.com/postgresql/postgresql-16.8-1-windows-x64-binaries.zip"),
            });
        }

        return Task.FromResult(new List<RemoteVersion>
        {
            new("postgres", "17.4", "https://get.enterprisedb.com/postgresql/postgresql-17.4-1-linux-x64-binaries.tar.gz"),
            new("postgres", "16.8", "https://get.enterprisedb.com/postgresql/postgresql-16.8-1-linux-x64-binaries.tar.gz"),
        });
    }

    // ---------------- MongoDB ----------------

    public static Task<List<RemoteVersion>> GetMongoVersionsAsync()
    {
        if (Platform.IsWindows)
        {
            return Task.FromResult(new List<RemoteVersion>
            {
                new("mongodb", "8.0.9", "https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.9.zip"),
                new("mongodb", "7.0.19", "https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-7.0.19.zip"),
            });
        }

        return Task.FromResult(new List<RemoteVersion>
        {
            new("mongodb", "8.0.9", "https://fastdl.mongodb.org/linux/mongodb-linux-x86_64-ubuntu2204-8.0.9.tgz"),
            new("mongodb", "7.0.19", "https://fastdl.mongodb.org/linux/mongodb-linux-x86_64-ubuntu2204-7.0.19.tgz"),
        });
    }

    // ---------------- Redis ----------------

    public static async Task<List<RemoteVersion>> GetRedisVersionsAsync()
    {
        if (Platform.IsWindows)
        {
            return new List<RemoteVersion>
            {
                new("redis", "5.0.14.1", "https://github.com/tporadowski/redis/releases/download/v5.0.14.1/Redis-x64-5.0.14.1.zip")
            };
        }

        return new List<RemoteVersion>
        {
            new("redis", "7.4.2", "https://github.com/redis/redis/archive/refs/tags/7.4.2.tar.gz")
        };
    }

    // ---------------- phpMyAdmin ----------------

    public static Task<List<RemoteVersion>> GetPhpMyAdminVersionsAsync()
        => Task.FromResult(new List<RemoteVersion>
        {
            new("phpmyadmin", "latest", "https://www.phpmyadmin.net/downloads/phpMyAdmin-latest-all-languages.zip")
        });

    // ---------------- Mailpit ----------------

    public static async Task<RemoteVersion> GetMailpitAsync()
    {
        var file = Platform.IsWindows ? "mailpit-windows-amd64.zip" : "mailpit-linux-amd64.tar.gz";
        var url = $"https://github.com/axllent/mailpit/releases/latest/download/{file}";
        return new RemoteVersion("mailpit", "latest", url);
    }

    // ---------------- Composer ----------------

    public static Task<RemoteVersion> GetComposerAsync()
        => Task.FromResult(new RemoteVersion("composer", "stable", "https://getcomposer.org/composer-stable.phar"));

    // ---------------- mkcert ----------------

    public static Task<RemoteVersion> GetMkcertAsync()
    {
        var binary = Platform.IsWindows ? "mkcert-v1.4.4-windows-amd64.exe" : "mkcert-v1.4.4-linux-amd64";
        return Task.FromResult(new RemoteVersion("mkcert", "1.4.4",
            $"https://github.com/FiloSottile/mkcert/releases/download/v1.4.4/{binary}"));
    }

    public static Task<RemoteVersion?> FindPhpRedisAsync(string phpVersion)
    {
        return Task.FromResult<RemoteVersion?>(null);
    }
}
