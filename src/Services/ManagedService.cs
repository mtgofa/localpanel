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

    /// <summary>Installed binary version ("1.27.4"), empty when unknown.</summary>
    public virtual Task<string> GetInstalledVersionAsync() => Task.FromResult("");

    /// <summary>
    /// Start/Stop bodies run synchronously up to their first await (process spawning,
    /// pid-file IO, config generation), so UI callers should use these to keep the
    /// dispatcher responsive. Services never touch UI, so Task.Run is safe.
    /// </summary>
    public Task StartOffUiAsync() => Task.Run(StartAsync);
    public Task StopOffUiAsync() => Task.Run(StopAsync);

    public async Task RestartAsync()
    {
        await StopOffUiAsync();
        await Task.Delay(500);
        await StartOffUiAsync();
    }
}
