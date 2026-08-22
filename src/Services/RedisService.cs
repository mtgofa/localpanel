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
        // Remember the port we actually bound so StopAsync targets it even if config changes later.
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
            // Target the port the server was STARTED with — the config port may already hold the
            // user's new value. `shutdown save` forces a final dump.rdb into --dir so sessions/queues
            // survive Stop/Restart/Exit (nosave discarded everything since the last snapshot).
            var port = ProcessRunner.ReadPortFile(Id, Config.Redis.Port);
            // (exit code deliberately not checked: some redis-cli builds report the server
            // closing the connection as an error even though the shutdown was accepted)
            await ProcessRunner.RunAsync(CliExe, $"-p {port} shutdown save", timeoutMs: 10_000);
        }
        // Give the RDB write a moment to finish before the hard-kill fallback.
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
