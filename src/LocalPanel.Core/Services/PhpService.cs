using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

/// <summary>
/// One FastCGI pool per installed PHP version: N php-cgi workers on
/// consecutive ports (9000, 9001, ...). Nginx load-balances across them
/// through a generated upstream block.
/// </summary>
public class PhpService : ManagedService
{
    public PhpVersionSettings Settings { get; }

    public PhpService(AppConfig config, PhpVersionSettings settings) : base(config)
        => Settings = settings;

    public override string Id => "php-" + Settings.Version;
    public override string DisplayName => "PHP " + Settings.Version;
    public string VersionDir => Path.Combine(Paths.PhpDir, Settings.Version);

    public string ExePath
    {
        get
        {
            if (Platform.IsWindows)
                return Path.Combine(VersionDir, "php-cgi.exe");

            var cgiBin = Path.Combine(VersionDir, "bin", "php-cgi");
            if (File.Exists(cgiBin)) return cgiBin;
            var cgiRoot = Path.Combine(VersionDir, "php-cgi");
            if (File.Exists(cgiRoot)) return cgiRoot;
            return cgiBin;
        }
    }

    public string CliExePath
    {
        get
        {
            if (Platform.IsWindows)
                return Path.Combine(VersionDir, "php.exe");

            var cliBin = Path.Combine(VersionDir, "bin", "php");
            if (File.Exists(cliBin)) return cliBin;
            return Path.Combine(VersionDir, "php");
        }
    }

    public override bool IsInstalled => File.Exists(ExePath) || File.Exists(CliExePath);
    public override string PortInfo => "";

    public override bool AutoStart
    {
        get => Settings.AutoStart;
        set { Settings.AutoStart = value; Config.Save(); }
    }

    public string IniFile => Path.Combine(Paths.PhpEtc, $"php-{Settings.Version}.ini");

    public static readonly string[] DefaultExtensions =
    {
        "curl", "exif", "fileinfo", "ftp", "gd", "intl", "mbstring", "mysqli",
        "openssl", "pdo_mysql", "pdo_sqlite", "sodium", "sqlite3", "zip",
    };

    public List<string> AvailableExtensions()
    {
        var extDir = Path.Combine(VersionDir, "ext");
        if (!Directory.Exists(extDir)) return new List<string>();
        var pattern = Platform.IsWindows ? "php_*.dll" : "*.so";
        return Directory.GetFiles(extDir, pattern)
            .Select(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return name.StartsWith("php_") ? name[4..] : name;
            })
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<string> EffectiveExtensions()
    {
        var list = (Settings.EnabledExtensions is { Count: > 0 }
            ? Settings.EnabledExtensions : DefaultExtensions.ToList()).Distinct().ToList();
        var extDir = Path.Combine(VersionDir, "ext");
        foreach (var extra in new[] { "igbinary", "redis" })
        {
            var extFile = Platform.IsWindows ? $"php_{extra}.dll" : $"{extra}.so";
            if (File.Exists(Path.Combine(extDir, extFile)) && !list.Contains(extra))
                list.Add(extra);
        }
        if (list.Contains("igbinary") && list.Contains("redis"))
        {
            list.Remove("igbinary");
            list.Insert(list.IndexOf("redis"), "igbinary");
        }
        return list;
    }

    public void EnsureIni()
    {
        if (!File.Exists(IniFile)) { WriteIni(); return; }
        try
        {
            if (!File.ReadAllText(IniFile).Contains(VersionDir, StringComparison.OrdinalIgnoreCase))
                WriteIni();
        }
        catch { }
    }

    public string OpcacheDir => Path.Combine(Paths.Tmp, "opcache", Settings.Version);

    public void WriteIni()
    {
        var template = Path.Combine(VersionDir, "php.ini-development");
        var content = File.Exists(template) ? File.ReadAllText(template) : "";
        var opcacheDir = Paths.Ensure(OpcacheDir).Replace(Path.DirectorySeparatorChar, '/');

        var extDir = Path.Combine(VersionDir, "ext");
        content = content.Replace(";extension_dir = \"ext\"", $"extension_dir = \"{extDir}\"");

        foreach (var ext in EffectiveExtensions())
        {
            var esc = System.Text.RegularExpressions.Regex.Escape(ext);
            var replaced = System.Text.RegularExpressions.Regex.Replace(content,
                $@"(?m)^;extension={esc}\s*$", $"extension={ext}");
            if (replaced != content)
                content = replaced;
            else if (!System.Text.RegularExpressions.Regex.IsMatch(content, $@"(?m)^extension={esc}\s*$"))
                content += $"{Environment.NewLine}extension={ext}";
        }

        content += $"""


            ; ---- LocalPanel overrides ----
            memory_limit = {Settings.MemoryLimitMb}M
            upload_max_filesize = {Settings.UploadMaxMb}M
            post_max_size = {Settings.UploadMaxMb}M
            max_execution_time = {Settings.MaxExecutionSeconds}
            max_input_vars = {Settings.MaxInputVars}
            date.timezone = "{Settings.TimeZone}"
            display_errors = {(Settings.DisplayErrors ? "On" : "Off")}
            error_log = "{Path.Combine(Paths.Logs, $"php-{Settings.Version}-error.log").Replace('\\', '/')}"

            ; ---- speed (safe for development) ----
            zend_extension = opcache
            opcache.enable = 1
            opcache.enable_cli = 0
            opcache.validate_timestamps = 1
            opcache.revalidate_freq = 0
            opcache.memory_consumption = 256
            opcache.interned_strings_buffer = 16
            opcache.max_accelerated_files = 20000
            opcache.file_cache = "{opcacheDir}"
            opcache.file_cache_consistency_checks = 1
            realpath_cache_size = 4096K
            realpath_cache_ttl = 600
            """;

        if (File.Exists(Paths.MailpitExe))
            content += $"""

                SMTP = 127.0.0.1
                smtp_port = {Config.Mailpit.SmtpPort}
                """;

        File.WriteAllText(IniFile, content);
        try { File.WriteAllText(Path.Combine(VersionDir, "php.ini"), content); } catch { }
        try { File.WriteAllText(Path.Combine(VersionDir, "bin", "php.ini"), content); } catch { }
    }

    public override async Task StartAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException($"PHP {Settings.Version} is not installed yet.");
        var existing = ProcessRunner.ReadPidFile(Id);
        if (existing.Length > 0 && existing.All(p => ProcessRunner.IsAlive(p, "php-cgi") || ProcessRunner.IsAlive(p, "php"))) return;
        if (existing.Length > 0) await StopAsync();
        EnsureIni();

        var env = new Dictionary<string, string>
        {
            ["PHP_FCGI_MAX_REQUESTS"] = "0",
        };

        var pids = new List<int>();
        for (var i = 0; i < Settings.Workers; i++)
        {
            var port = Settings.FastCgiPort + i;
            var p = ProcessRunner.StartBackground(
                ExePath, $"-b 127.0.0.1:{port} -c \"{IniFile}\"",
                VersionDir, env, logFile: LogFile);
            pids.Add(p.Id);
        }
        ProcessRunner.WritePidFile(Id, pids.ToArray());
        await Task.Delay(400);
        if (pids.Any(p => !ProcessRunner.IsAlive(p, "php-cgi") && !ProcessRunner.IsAlive(p, "php")))
        {
            foreach (var pid in pids) ProcessRunner.KillPid(pid);
            ProcessRunner.DeletePidFile(Id);
            throw new InvalidOperationException(
                $"PHP {Settings.Version} workers exited immediately." + Prereqs.Hint);
        }
    }

    public override Task StopAsync()
    {
        foreach (var pid in ProcessRunner.ReadPidFile(Id))
            if (ProcessRunner.IsAlive(pid)) ProcessRunner.KillPid(pid);
        ProcessRunner.DeletePidFile(Id);
        return Task.CompletedTask;
    }

    public override bool IsRunning
    {
        get
        {
            var pids = ProcessRunner.ReadPidFile(Id);
            return pids.Length > 0 && pids.Any(p => ProcessRunner.IsAlive(p, "php-cgi") || ProcessRunner.IsAlive(p, "php"));
        }
    }
}
