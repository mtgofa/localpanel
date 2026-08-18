using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

public class RedisService : ManagedService
{
    public RedisService(AppConfig config) : base(config) { }

    public override string Id => "redis";
    public override string DisplayName => "Redis";
    public string ExePath => Path.Combine(Paths.RedisDir, "redis-server.exe");
    private string CliExe => Path.Combine(Paths.RedisDir, "redis-cli.exe");
    public override bool IsInstalled => File.Exists(ExePath);
    public override string PortInfo => Config.Redis.Port.ToString();

    public override bool AutoStart
    {
        get => Config.Redis.AutoStart;
        set { Config.Redis.AutoStart = value; Config.Save(); }
    }

    public override async Task StartAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException("Redis is not installed yet.");
        if (IsRunning) return;
        var p = ProcessRunner.StartBackground(
            ExePath,
            $"--port {Config.Redis.Port} --dir \"{Paths.RedisData}\" --bind 127.0.0.1",
            Paths.RedisDir, logFile: LogFile);
        ProcessRunner.WritePidFile(Id, p.Id);
        await Task.Delay(500);
        if (p.HasExited)
        {
            ProcessRunner.DeletePidFile(Id);
            throw new InvalidOperationException("Redis exited immediately — check logs/redis.log" + Prereqs.Hint);
        }
    }

    public override async Task StopAsync()
    {
        if (File.Exists(CliExe))
            await ProcessRunner.RunAsync(CliExe, $"-p {Config.Redis.Port} shutdown nosave", timeoutMs: 10_000);
        await Task.Delay(300);
        foreach (var pid in ProcessRunner.ReadPidFile(Id))
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
