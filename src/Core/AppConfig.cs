using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalPanel.Core;

public class AppConfig
{
    public string Tld { get; set; } = "test";

    public NginxSettings Nginx { get; set; } = new();
    public MySqlSettings MySql { get; set; } = new();
    public RedisSettings Redis { get; set; } = new();
    public List<PhpVersionSettings> Php { get; set; } = new();
    public string? DefaultPhpVersion { get; set; }
    /// <summary>Node version the bin\node\current junction points at (nvm-style "use").</summary>
    public string? CurrentNodeVersion { get; set; }
    public List<Site> Sites { get; set; } = new();

    /// <summary>
    /// Herd-style parked folders: every subfolder automatically becomes a
    /// {name}.{tld} site (the portable www folder is always parked).
    /// </summary>
    public List<string> ParkedPaths { get; set; } = new();

    /// <summary>Start the enabled services automatically when the app launches.</summary>
    public bool AutoStartServicesOnLaunch { get; set; } = true;

    /// <summary>Start hidden in the system tray instead of showing the window.</summary>
    public bool StartMinimizedToTray { get; set; }

    /// <summary>Check GitHub Releases for a newer LocalPanel at startup (needs internet).</summary>
    public bool CheckUpdatesOnStart { get; set; } = true;

    // ---------- persistence ----------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppConfig Load()
    {
        if (File.Exists(Paths.ConfigFile))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Paths.ConfigFile), JsonOpts);
                if (cfg is not null) return cfg;
            }
            catch { /* fall through to defaults; corrupt config should not brick the app */ }
        }
        var fresh = new AppConfig();
        fresh.Save();
        return fresh;
    }

    public void Save()
        => File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(this, JsonOpts));
}

public class NginxSettings
{
    public bool AutoStart { get; set; } = true;
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
}

public class MySqlSettings
{
    public bool AutoStart { get; set; } = true;
    public int Port { get; set; } = 3306;
}

public class RedisSettings
{
    public bool AutoStart { get; set; } = true;
    public int Port { get; set; } = 6379;
}

public class PhpVersionSettings
{
    /// <summary>Folder name under bin\php, e.g. "8.3.12".</summary>
    public string Version { get; set; } = "";

    /// <summary>First FastCGI port; workers use consecutive ports.</summary>
    public int FastCgiPort { get; set; } = 9000;

    public int Workers { get; set; } = 4;
    public bool AutoStart { get; set; } = true;

    // php.ini values (regenerated into etc\php\php-<version>.ini on save)
    public int MemoryLimitMb { get; set; } = 512;
    public int UploadMaxMb { get; set; } = 512;
    public int MaxExecutionSeconds { get; set; } = 300;
}

public enum SiteType
{
    /// <summary>PHP site served through FastCGI (php-cgi pool).</summary>
    Php,
    /// <summary>Static files only.</summary>
    Static,
    /// <summary>Reverse proxy to a local app (Node, etc.) via TCP port or unix socket.</summary>
    Proxy
}

public enum UpstreamKind { Port, Socket }

public class Site
{
    public string Name { get; set; } = "";
    /// <summary>Full host name, e.g. "myapp.test".</summary>
    public string Domain { get; set; } = "";
    /// <summary>Document root (absolute, or relative to the www folder).</summary>
    public string RootPath { get; set; } = "";
    public SiteType Type { get; set; } = SiteType.Php;
    public string? PhpVersion { get; set; }
    public bool Ssl { get; set; } = true;

    /// <summary>Extra domains served by this site (subdomains etc.).</summary>
    public List<string> Aliases { get; set; } = new();

    // Reverse-proxy upstream (only for Type == Proxy)
    public UpstreamKind UpstreamKind { get; set; } = UpstreamKind.Port;
    public int UpstreamPort { get; set; } = 3000;
    /// <summary>Path to the unix socket file when UpstreamKind == Socket.</summary>
    public string? UpstreamSocketPath { get; set; }

    [JsonIgnore]
    public string ResolvedRoot =>
        Path.IsPathRooted(RootPath) ? RootPath : Path.Combine(Paths.Www, RootPath);
}
