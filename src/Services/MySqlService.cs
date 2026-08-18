using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

public class MySqlService : ManagedService
{
    public MySqlService(AppConfig config) : base(config) { }

    public override string Id => "mysql";
    public override string DisplayName => "MySQL";
    public string ExePath => Path.Combine(Paths.MySqlDir, "bin", "mysqld.exe");
    private string AdminExe => Path.Combine(Paths.MySqlDir, "bin", "mysqladmin.exe");
    public override bool IsInstalled => File.Exists(ExePath);
    public override string PortInfo => Config.MySql.Port.ToString();

    public override bool AutoStart
    {
        get => Config.MySql.AutoStart;
        set { Config.MySql.AutoStart = value; Config.Save(); }
    }

    private string IniFile => Path.Combine(Paths.MySqlEtc, "my.ini");

    private void WriteIni()
    {
        var ini = $"""
            [mysqld]
            basedir="{Paths.MySqlDir.Replace('\\', '/')}"
            datadir="{Paths.MySqlData.Replace('\\', '/')}"
            port={Config.MySql.Port}
            bind-address=127.0.0.1
            max_connections=151
            log-error="{Path.Combine(Paths.Logs, "mysql-error.log").Replace('\\', '/')}"

            [client]
            port={Config.MySql.Port}
            """;
        File.WriteAllText(IniFile, ini);
    }

    public bool IsInitialized => Directory.Exists(Paths.MySqlData) &&
                                 Directory.EnumerateFileSystemEntries(Paths.MySqlData).Any();

    public async Task InitializeAsync()
    {
        WriteIni();
        Paths.Ensure(Paths.MySqlData);
        if (IsInitialized) return;
        // Root with empty password — standard for local dev (same as Laragon/XAMPP).
        var (code, _, err) = await ProcessRunner.RunAsync(
            ExePath, $"--defaults-file=\"{IniFile}\" --initialize-insecure --console",
            Paths.MySqlDir, timeoutMs: 180_000);
        if (code != 0)
            throw new InvalidOperationException("MySQL initialization failed: " + err);
    }

    public override async Task StartAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException("MySQL is not installed yet.");
        if (IsRunning) return;
        WriteIni();
        if (!IsInitialized) await InitializeAsync();

        var p = ProcessRunner.StartBackground(
            ExePath, $"--defaults-file=\"{IniFile}\" --console",
            Path.Combine(Paths.MySqlDir, "bin"), logFile: LogFile);
        ProcessRunner.WritePidFile(Id, p.Id);
        await Task.Delay(1500);
        if (p.HasExited)
        {
            ProcessRunner.DeletePidFile(Id);
            throw new InvalidOperationException("MySQL exited immediately — check logs/mysql.log" + Prereqs.Hint);
        }
    }

    public override async Task StopAsync()
    {
        if (File.Exists(AdminExe))
        {
            // Graceful shutdown protects the data files.
            await ProcessRunner.RunAsync(AdminExe,
                $"--port={Config.MySql.Port} --host=127.0.0.1 -u root shutdown",
                timeoutMs: 30_000);
        }
        await Task.Delay(500);
        foreach (var pid in ProcessRunner.ReadPidFile(Id))
            if (ProcessRunner.IsAlive(pid, "mysqld")) ProcessRunner.KillPid(pid);
        ProcessRunner.DeletePidFile(Id);
    }

    public override bool IsRunning
    {
        get
        {
            var pids = ProcessRunner.ReadPidFile(Id);
            return pids.Any(p => ProcessRunner.IsAlive(p, "mysqld"));
        }
    }
}
