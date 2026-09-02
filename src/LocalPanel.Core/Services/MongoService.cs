using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

/// <summary>MongoDB Community server portable service.</summary>
public class MongoService : ManagedService
{
    public MongoService(AppConfig config) : base(config) { }

    public override string Id => "mongodb";
    public override string DisplayName => "MongoDB";
    public string ExePath => Path.Combine(Paths.MongoDir, "bin", Platform.Exe("mongod"));
    public override bool IsInstalled => File.Exists(ExePath);
    public override string PortInfo => Config.Mongo.Port.ToString();

    public override bool AutoStart
    {
        get => Config.Mongo.AutoStart;
        set { Config.Mongo.AutoStart = value; Config.Save(); }
    }

    public override Task<string> GetInstalledVersionAsync()
        => ToolVersionCache.GetAsync(ExePath, async () =>
        {
            var (_, so, se) = await ProcessRunner.RunAsync(ExePath, "--version", timeoutMs: 15_000);
            var m = System.Text.RegularExpressions.Regex.Match(so + se, @"db version v?([\d.]+)");
            return m.Success ? m.Groups[1].Value : "";
        });

    public override async Task StartAsync()
    {
        if (!IsInstalled) throw new InvalidOperationException("MongoDB is not installed yet.");
        if (IsRunning) return;
        Paths.Ensure(Paths.MongoData);
        var p = ProcessRunner.StartBackground(
            ExePath,
            $"--dbpath \"{Paths.MongoData}\" --port {Config.Mongo.Port} --bind_ip 127.0.0.1",
            Path.Combine(Paths.MongoDir, "bin"), logFile: LogFile);
        ProcessRunner.WritePidFile(Id, p.Id);
        await Task.Delay(1500);
        if (p.HasExited)
        {
            ProcessRunner.DeletePidFile(Id);
            throw new InvalidOperationException("MongoDB exited immediately — check logs/mongodb.log" + Prereqs.Hint);
        }
    }

    public override async Task StopAsync()
    {
        foreach (var pid in ProcessRunner.ReadPidFile(Id))
        {
            if (!ProcessRunner.IsAlive(pid, "mongod")) continue;
            var exited = await ProcessRunner.SendCtrlCAsync(pid, timeoutMs: 20_000);
            if (!exited && ProcessRunner.IsAlive(pid, "mongod")) ProcessRunner.KillPid(pid);
        }
        ProcessRunner.DeletePidFile(Id);
        await Task.Delay(300);
    }

    public override bool IsRunning
    {
        get
        {
            var pids = ProcessRunner.ReadPidFile(Id);
            return pids.Any(p => ProcessRunner.IsAlive(p, "mongod"));
        }
    }
}
