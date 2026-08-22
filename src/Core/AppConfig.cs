using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalPanel.Core;

public class AppConfig
{
    public string Tld { get; set; } = "test";


    public NginxSettings Nginx { get; set; } = new();
    public MySqlSettings MySql { get; set; } = new();
    public MariaDbSettings MariaDb { get; set; } = new();
    public PostgresSettings Postgres { get; set; } = new();
    public MongoSettings Mongo { get; set; } = new();
    public RedisSettings Redis { get; set; } = new();
    public MailpitSettings Mailpit { get; set; } = new();
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

    /// <summary>Services the user chose to show on the Services page (installed ones are always shown).</summary>
    public List<string> EnabledServices { get; set; } = new();

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

    /// <summary>Save() runs from the UI thread and several background tasks
    /// (shims, park-import, auto-updates) — serialize the writes.</summary>
    private static readonly object SaveGate = new();

    public static AppConfig Load()
    {
        // Prefer the live file, fall back to the last good copy kept by Save().
        foreach (var path in new[] { Paths.ConfigFile, Paths.ConfigFile + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpts);
                if (cfg is null) continue;
                foreach (var site in cfg.Sites) site.EnsureRoutes(); // configs written before routes
                return cfg;
            }
            catch { /* corrupt config should not brick the app — try the next candidate */ }
        }
        // Keep the unreadable file for the user instead of silently clobbering it.
        if (File.Exists(Paths.ConfigFile))
        {
            try { File.Move(Paths.ConfigFile, Paths.ConfigFile + ".corrupt", overwrite: true); } catch { }
        }
        var fresh = new AppConfig();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        lock (SaveGate)
        {
            var json = JsonSerializer.Serialize(this, JsonOpts);
            var tmp = Paths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(Paths.ConfigFile))
                // Atomic on NTFS; the previous good copy survives as config.json.bak.
                File.Replace(tmp, Paths.ConfigFile, Paths.ConfigFile + ".bak");
            else
                File.Move(tmp, Paths.ConfigFile);
        }
    }
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
    public string User { get; set; } = "root";
    public string Password { get; set; } = "";
}

public class MariaDbSettings
{
    public bool AutoStart { get; set; } = false;
    public int Port { get; set; } = 3307;
    public string User { get; set; } = "root";
    public string Password { get; set; } = "";
}

public class PostgresSettings
{
    public bool AutoStart { get; set; } = false;
    public int Port { get; set; } = 5432;
    public string User { get; set; } = "postgres";
    public string Password { get; set; } = "postgres";
}

public class MongoSettings
{
    public bool AutoStart { get; set; } = false;
    public int Port { get; set; } = 27017;
}

public class RedisSettings
{
    public bool AutoStart { get; set; } = true;
    public int Port { get; set; } = 6379;
}

public class MailpitSettings
{
    public bool AutoStart { get; set; } = true;
    /// <summary>Where sites send their mail (SMTP catcher).</summary>
    public int SmtpPort { get; set; } = 1025;
    /// <summary>The inbox web UI.</summary>
    public int UiPort { get; set; } = 8025;
}

public class PhpVersionSettings
{
    /// <summary>Folder name under bin\php, e.g. "8.3.12".</summary>
    public string Version { get; set; } = "";

    /// <summary>First FastCGI port; workers use consecutive ports.</summary>
    public int FastCgiPort { get; set; } = 9000;

    /// <summary>php-cgi processes in the pool. Each one serves a single request at a
    /// time, so this is the site's PHP concurrency; they share one opcache segment.</summary>
    public int Workers { get; set; } = 8;
    public bool AutoStart { get; set; } = true;

    // php.ini values (regenerated into etc\php\php-<version>.ini on save)
    public int MemoryLimitMb { get; set; } = 512;
    public int UploadMaxMb { get; set; } = 512;
    public int MaxExecutionSeconds { get; set; } = 300;
    public int MaxInputVars { get; set; } = 1000;
    public string TimeZone { get; set; } = "UTC";
    public bool DisplayErrors { get; set; } = true;

    /// <summary>Extensions to enable; null/empty = the sensible default set.</summary>
    public List<string>? EnabledExtensions { get; set; }
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

public enum RouteKind
{
    /// <summary>Files served through the site's PHP pool.</summary>
    Php,
    /// <summary>Files served as-is.</summary>
    Static,
    /// <summary>Reverse proxy to a local app.</summary>
    Proxy
}

/// <summary>
/// One nginx location inside a site. A site is a list of these, so a single domain
/// can serve a built frontend at "/", a PHP backend at "/api" and a socket server
/// at "/socket.io" the way a real deployment does.
/// </summary>
public class SiteRoute
{
    /// <summary>URI prefix, "/" for the site itself.</summary>
    public string Path { get; set; } = "/";

    public RouteKind Kind { get; set; } = RouteKind.Php;

    /// <summary>Folder this route serves. Empty = the site's own document root;
    /// a relative path is taken from that root.</summary>
    public string? Folder { get; set; }

    /// <summary>Static only: unmatched URIs fall back to index.html so a client-side
    /// router (Vue/React/Angular) handles them instead of returning 404.</summary>
    public bool SpaFallback { get; set; }

    /// <summary>Proxy only: local TCP port.</summary>
    public int Port { get; set; } = 3000;

    /// <summary>Proxy only: unix socket instead of a port. Carried over from older
    /// configs that used one; the UI offers ports only.</summary>
    public string? SocketPath { get; set; }
}

public class Site
{
    public string Name { get; set; } = "";
    /// <summary>Full host name, e.g. "myapp.test".</summary>
    public string Domain { get; set; } = "";
    /// <summary>Document root (absolute, or relative to the www folder).</summary>
    public string RootPath { get; set; } = "";
    /// <summary>Legacy, pre-Routes: only read by EnsureRoutes() to build the "/" route.</summary>
    public SiteType Type { get; set; } = SiteType.Php;
    public string? PhpVersion { get; set; }
    public bool Ssl { get; set; } = true;

    /// <summary>Extra domains served by this site (subdomains etc.).</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>The site's nginx locations, in no particular order (nginx matches the
    /// longest prefix). Empty only until EnsureRoutes() has migrated an older config.</summary>
    public List<SiteRoute> Routes { get; set; } = new();

    /// <summary>
    /// Builds the route list of a site written before routes existed: its single Type
    /// becomes the "/" route and the old Socket.IO switch becomes a proxy route. Runs
    /// once — as soon as the config is saved again the routes are what is stored.
    /// </summary>
    public void EnsureRoutes()
    {
        if (Routes.Count > 0) return;
        Routes.Add(new SiteRoute
        {
            Path = "/",
            Kind = Type switch
            {
                SiteType.Static => RouteKind.Static,
                SiteType.Proxy => RouteKind.Proxy,
                _ => RouteKind.Php
            },
            Port = UpstreamPort,
            SocketPath = UpstreamKind == UpstreamKind.Socket ? UpstreamSocketPath : null,
        });
        if (SocketEnabled)
            Routes.Add(new SiteRoute { Path = SocketPath, Kind = RouteKind.Proxy, Port = SocketPort });
    }

    /// <summary>Absolute folder a route serves: its own Folder (absolute, or relative
    /// to the site root), or the site root when it has none of its own.</summary>
    public string ResolvedFolder(SiteRoute route)
    {
        var f = route.Folder?.Trim();
        if (string.IsNullOrEmpty(f)) return ResolvedRoot;
        return Path.IsPathRooted(f) ? f : Path.GetFullPath(Path.Combine(ResolvedRoot, f));
    }

    // ---- legacy, pre-Routes: read once by EnsureRoutes() and ignored afterwards ----

    // Reverse-proxy upstream (only for Type == Proxy)
    public UpstreamKind UpstreamKind { get; set; } = UpstreamKind.Port;
    public int UpstreamPort { get; set; } = 3000;
    /// <summary>Path to the unix socket file when UpstreamKind == Socket.</summary>
    public string? UpstreamSocketPath { get; set; }

    // WebSocket / Socket.IO location — works for any site type: a PHP or static
    // site keeps its normal handling while one path is proxied to a local ws
    // server (socket.io, Laravel Reverb, ws, …).
    /// <summary>Proxy <see cref="SocketPath"/> to 127.0.0.1:<see cref="SocketPort"/> with WebSocket upgrade.</summary>
    public bool SocketEnabled { get; set; }
    /// <summary>URI prefix handed to the socket server, e.g. "/socket.io".</summary>
    public string SocketPath { get; set; } = "/socket.io";
    /// <summary>Local port the socket server listens on.</summary>
    public int SocketPort { get; set; } = 3000;

    [JsonIgnore]
    public string ResolvedRoot =>
        Path.IsPathRooted(RootPath) ? RootPath : Path.Combine(Paths.Www, RootPath);
}
