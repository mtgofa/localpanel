using System.ComponentModel;
using LocalPanel.Services;

namespace LocalPanel.UI;

/// <summary>Row view-model for the Services grid, refreshed by a timer.</summary>
public class ServiceRow : INotifyPropertyChanged
{
    public ManagedService Service { get; }

    public ServiceRow(ManagedService service)
    {
        Service = service;
        _installed = service.IsInstalled;
        _ = LoadVersionAsync();
    }

    private string _versionText = "";
    public string VersionText
    {
        get => _versionText;
        private set { _versionText = value; Notify(nameof(VersionText)); }
    }

    private async Task LoadVersionAsync()
    {
        try
        {
            if (!Service.IsInstalled) return;
            var v = await Service.GetInstalledVersionAsync();
            if (v.Length > 0) VersionText = v;
        }
        catch { }
    }

    public string Name => Service.DisplayName;
    public string Ports => Service.PortInfo.Length == 0 ? "" : "Port " + Service.PortInfo;

    private string _status = "";
    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            Notify(nameof(Status));
            Notify(nameof(IsRunning));
            Notify(nameof(ToggleText));
            Notify(nameof(DashText));
        }
    }

    /// <summary>One button that flips between Start and Stop with the service state.</summary>
    public string ToggleText => IsRunning ? "Stop" : "Start";

    /// <summary>Dashboard line: port when running, Idle otherwise.</summary>
    public string DashText => IsRunning ? (Ports.Length > 0 ? Ports : "Running") : "Idle";

    public bool IsRunning => _status == "Running";

    // Probed once per Refresh() (the 2 s tick) instead of on every binding read.
    private bool _installed;
    public bool IsInstalled => _installed;

    public bool AutoStart
    {
        get => Service.AutoStart;
        set { Service.AutoStart = value; Notify(nameof(AutoStart)); }
    }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set { _busy = value; Notify(nameof(Busy)); Notify(nameof(NotBusy)); }
    }
    public bool NotBusy => !_busy;

    public void Refresh()
    {
        var installed = Service.IsInstalled;
        var status = !installed ? "Not installed"
            : Service.IsRunning ? "Running"
            : "Stopped";
        Status = status;
        if (installed != _installed)
        {
            _installed = installed;
            Notify(nameof(IsInstalled));
        }
        Notify(nameof(Ports));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
