using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

/// <summary>
/// One FastCGI pool per installed PHP version: N php-cgi.exe workers on
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
    public string ExePath => Path.Combine(VersionDir, "php-cgi.exe");
    public override bool IsInstalled => File.Exists(ExePath);
    // FastCGI plumbing is auto-managed; no need to surface ports in the UI.
    public override string PortInfo => "";

    public override bool AutoStart
    {
        get => Settings.AutoStart;
        set { Settings.AutoStart = value; Config.Save(); }
    }

    public string IniFile => Path.Combine(Paths.PhpEtc, $"php-{Settings.Version}.ini");

    public void EnsureIni()
    {
        if (!File.Exists(IniFile)) WriteIni();
    }

    /// <summary>(Re)generate the php.ini from the bundled template plus the configured limits.</summary>
    public void WriteIni()
    {
        var template = Path.Combine(VersionDir, "php.ini-development");
        var content = File.Exists(template) ? File.ReadAllText(template) : "";

        var extDir = Path.Combine(VersionDir, "ext");
        content = content
            .Replace(";extension_dir = \"ext\"", $"extension_dir = \"{extDir}\"")
            .Replace(";extension=exif", "extension=exif")
            .Replace(";extension=ftp", "extension=ftp")
            .Replace(";extension=curl", "extension=curl")
            .Replace(";extension=fileinfo", "extension=fileinfo")
            .Replace(";extension=gd", "extension=gd")
            .Replace(";extension=gd2", "extension=gd2")
            .Replace(";extension=intl", "extension=intl")
            .Replace(";extension=mbstring", "extension=mbstring")
            .Replace(";extension=mysqli", "extension=mysqli")
            .Replace(";extension=openssl", "extension=openssl")
            .Replace(";extension=pdo_mysql", "extension=pdo_mysql")
            .Replace(";extension=pdo_sqlite", "extension=pdo_sqlite")
            .Replace(";extension=sodium", "extension=sodium")
            .Replace(";extension=sqlite3", "extension=sqlite3")
            .Replace(";extension=zip", "extension=zip");

        // Extra PECL extensions dropped into ext\ (php_redis.dll etc.) get enabled
        // automatically; igbinary must load before redis.
        foreach (var ext in new[] { "igbinary", "redis" })
            if (File.Exists(Path.Combine(extDir, $"php_{ext}.dll")))
                content += $"{Environment.NewLine}extension={ext}";

        content += $"""


            ; ---- LocalPanel overrides ----
            memory_limit = {Settings.MemoryLimitMb}M
            upload_max_filesize = {Settings.UploadMaxMb}M
            post_max_size = {Settings.UploadMaxMb}M
            max_execution_time = {Settings.MaxExecutionSeconds}
            error_log = "{Path.Combine(Paths.Logs, $"php-{Settings.Version}-error.log")}"
            """;
        File.WriteAllText(IniFile, content);
    }

    public override async Task StartAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException($"PHP {Settings.Version} is not installed yet.");
        if (IsRunning) return;
        EnsureIni();

        var env = new Dictionary<string, string>
        {
            // 0 = never recycle; php-cgi exits after N requests otherwise.
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
        if (pids.Any(p => !ProcessRunner.IsAlive(p, "php-cgi")))
        {
            ProcessRunner.DeletePidFile(Id);
            throw new InvalidOperationException(
                $"PHP {Settings.Version} workers exited immediately." + Prereqs.Hint);
        }
    }

    public override Task StopAsync()
    {
        foreach (var pid in ProcessRunner.ReadPidFile(Id))
            if (ProcessRunner.IsAlive(pid, "php-cgi")) ProcessRunner.KillPid(pid);
        ProcessRunner.DeletePidFile(Id);
        return Task.CompletedTask;
    }

    public override bool IsRunning
    {
        get
        {
            var pids = ProcessRunner.ReadPidFile(Id);
            return pids.Length > 0 && pids.All(p => ProcessRunner.IsAlive(p, "php-cgi"));
        }
    }
}
