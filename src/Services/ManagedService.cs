using System.IO;
using LocalPanel.Core;

namespace LocalPanel.Services;

public abstract class ManagedService
{
    protected readonly AppConfig Config;
    protected ManagedService(AppConfig config) => Config = config;

    /// <summary>Stable id used for pid/log file names ("nginx", "mysql", ...).</summary>
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract bool IsInstalled { get; }
    public abstract string PortInfo { get; }
    public abstract bool AutoStart { get; set; }

    public string LogFile => Path.Combine(Paths.Logs, Id + ".log");

    public virtual bool IsRunning
    {
        get
        {
            var pids = ProcessRunner.ReadPidFile(Id);
            return pids.Length > 0 && pids.Any(p => ProcessRunner.IsAlive(p));
        }
    }

    public abstract Task StartAsync();
    public abstract Task StopAsync();

    public async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(500);
        await StartAsync();
    }
}
