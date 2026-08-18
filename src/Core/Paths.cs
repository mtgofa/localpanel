using System.IO;

namespace LocalPanel.Core;

/// <summary>
/// All paths are resolved relative to the portable root folder so the whole
/// app (binaries, services, data, config, logs, sites) lives in one directory.
/// </summary>
public static class Paths
{
    private static string? _root;

    public static string Root => _root ??= ResolveRoot();

    private static string ResolveRoot()
    {
        // Explicit override wins (useful for development).
        var env = Environment.GetEnvironmentVariable("LOCALPANEL_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);

        // When running from a build output inside src\bin\..., walk up to the
        // folder that contains "src" so dev runs use the repo root as the
        // portable root. In a published build the exe sits directly in Root.
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var probe = dir;
        while (probe is not null)
        {
            var name = Path.GetFileName(probe);
            if (string.Equals(name, "src", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(probe)!;
            probe = Path.GetDirectoryName(probe);
        }
        return dir;
    }

    public static string Bin => Ensure(Path.Combine(Root, "bin"));
    public static string Etc => Ensure(Path.Combine(Root, "etc"));
    public static string Data => Ensure(Path.Combine(Root, "data"));
    public static string Logs => Ensure(Path.Combine(Root, "logs"));
    public static string Run => Ensure(Path.Combine(Logs, "run"));
    public static string Www => Ensure(Path.Combine(Root, "www"));
    public static string Tmp => Ensure(Path.Combine(Root, "tmp"));

    public static string PhpDir => Ensure(Path.Combine(Bin, "php"));
    public static string NodeDir => Ensure(Path.Combine(Bin, "node"));
    public static string ShimsDir => Ensure(Path.Combine(Bin, "shims"));
    public static string ComposerDir => Path.Combine(Bin, "composer");
    public static string ComposerPhar => Path.Combine(ComposerDir, "composer.phar");
    /// <summary>Junction that always points at the current (default) PHP version.</summary>
    public static string PhpCurrent => Path.Combine(PhpDir, "current");
    /// <summary>Junction that always points at the current Node version (nvm-style switching).</summary>
    public static string NodeCurrent => Path.Combine(NodeDir, "current");

    /// <summary>Version folders inside a component dir, excluding the "current" junction.</summary>
    public static IEnumerable<string> VersionDirs(string componentDir)
        => Directory.Exists(componentDir)
            ? Directory.GetDirectories(componentDir)
                .Where(d => !string.Equals(Path.GetFileName(d), "current", StringComparison.OrdinalIgnoreCase))
            : Enumerable.Empty<string>();
    public static string NginxDir => Path.Combine(Bin, "nginx");
    public static string MySqlDir => Path.Combine(Bin, "mysql");
    public static string RedisDir => Path.Combine(Bin, "redis");
    public static string PhpMyAdminDir => Path.Combine(Bin, "phpmyadmin");
    public static string MkcertExe => Path.Combine(Bin, "mkcert", "mkcert.exe");

    public static string NginxEtc => Ensure(Path.Combine(Etc, "nginx"));
    public static string NginxSites => Ensure(Path.Combine(NginxEtc, "sites"));
    public static string NginxTemp => Ensure(Path.Combine(Tmp, "nginx"));
    public static string SslDir => Ensure(Path.Combine(Etc, "ssl"));
    public static string MySqlEtc => Ensure(Path.Combine(Etc, "mysql"));
    public static string MySqlData => Path.Combine(Data, "mysql");
    public static string RedisData => Ensure(Path.Combine(Data, "redis"));
    public static string PhpEtc => Ensure(Path.Combine(Etc, "php"));

    public static string ConfigFile => Path.Combine(Etc, "config.json");

    public static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
