using System.ComponentModel;
using LocalPanel.Services;

namespace LocalPanel.UI;

/// <summary>Row view-model for the Services grid, refreshed by a timer.</summary>
public class ServiceRow : INotifyPropertyChanged
{
    public ManagedService Service { get; }

    public ServiceRow(ManagedService service) => Service = service;

    public string Name => Service.DisplayName;
    public string Ports => Service.PortInfo.Length == 0 ? "" : "Port " + Service.PortInfo;

    private string _status = "";
    public string Status
    {
        get => _status;
        private set { if (_status != value) { _status = value; Notify(nameof(Status)); Notify(nameof(IsRunning)); } }
    }

    public bool IsRunning => _status == "Running";
    public bool IsInstalled => Service.IsInstalled;

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
        var status = !Service.IsInstalled ? "Not installed"
            : Service.IsRunning ? "Running"
            : "Stopped";
        Status = status;
        Notify(nameof(IsInstalled));
        Notify(nameof(Ports));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
