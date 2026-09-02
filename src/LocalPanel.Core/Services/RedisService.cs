using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

public class RedisService : ManagedService
{
    public RedisService(AppConfig config) : base(config) { }

    public override string Id => "redis";
    public override string DisplayName => "Redis";

    public string ExePath
    {
        get
        {
            var p1 = Path.Combine(Paths.RedisDir, Platform.Exe("redis-server"));
            if (File.Exists(p1)) return p1;
            var p2 = Path.Combine(Paths.RedisDir, "src", Platform.Exe("redis-server"));
            if (File.Exists(p2)) return p2;
            return p1;
        }
    }

    private string CliExe
    {
        get
        {
            var p1 = Path.Combine(Paths.RedisDir, Platform.Exe("redis-cli"));
            if (File.Exists(p1)) return p1;
            var p2 = Path.Combine(Paths.RedisDir, "src", Platform.Exe("redis-cli"));
            if (File.Exists(p2)) return p2;
            return p1;
        }
    }

    public override bool IsInstalled => File.Exists(ExePath);
    public override string PortInfo => Config.Redis.Port.ToString();

    public override bool AutoStart
    {
        get => Config.Redis.AutoStart;
        set { Config.Redis.AutoStart = value; Config.Save(); }
    }

    public override Task<string> GetInstalledVersionAsync()
        => ToolVersionCache.GetAsync(ExePath, async () =>
        {
            var (_, so, se) = await ProcessRunner.RunAsync(ExePath, "--version", timeoutMs: 10_000);
            var m = System.Text.RegularExpressions.Regex.Match(so + se, @"v=([\d.]+)");
            return m.Success ? m.Groups[1].Value : "";
        });

    public override async Task StartAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException("Redis is not installed yet.");
        if (IsRunning) return;
        var p = ProcessRunner.StartBackground(
            ExePath,
            $"--port {Config.Redis.Port} --dir \"{Paths.RedisData}\" --bind 127.0.0.1",
            Paths.RedisDir, logFile: LogFile);
        ProcessRunner.WritePidFile(Id, p.Id);
        ProcessRunner.WritePortFile(Id, Config.Redis.Port);
        await Task.Delay(500);
        if (p.HasExited)
        {
            ProcessRunner.DeletePidFile(Id);
            throw new InvalidOperationException("Redis exited immediately — check logs/redis.log" + Prereqs.Hint);
        }
    }

    public override async Task StopAsync()
    {
        var pids = ProcessRunner.ReadPidFile(Id);
        var graceful = File.Exists(CliExe);
        if (graceful)
        {
            var port = ProcessRunner.ReadPortFile(Id, Config.Redis.Port);
            await ProcessRunner.RunAsync(CliExe, $"-p {port} shutdown save", timeoutMs: 10_000);
        }
        for (var i = 0; graceful && i < 40 && pids.Any(p => ProcessRunner.IsAlive(p, "redis-server")); i++)
            await Task.Delay(250);
        foreach (var pid in pids)
            if (ProcessRunner.IsAlive(pid, "redis-server")) ProcessRunner.KillPid(pid);
        ProcessRunner.DeletePidFile(Id);
    }

    public override bool IsRunning
    {
        get
        {
            var pids = ProcessRunner.ReadPidFile(Id);
            return pids.Any(p => ProcessRunner.IsAlive(p, "redis-server"));
        }
    }
}
