using System.ComponentModel;
using LocalPanel.Core;

namespace LocalPanel.UI;

/// <summary>
/// Row view-model for the PHP / Node version tables.
/// States: not installed → "Install"; older patch of the same branch installed →
/// "Update" (carries UpdateFrom); exactly installed → ✓ plus optional "Use".
/// </summary>
public class VersionRow : INotifyPropertyChanged
{
    public RemoteVersion Remote { get; }

    /// <summary>Set when an older version of the same branch is installed.</summary>
    public string? UpdateFrom { get; }

    public VersionRow(RemoteVersion remote, bool installed, string? updateFrom,
        bool isCurrent, string detail = "")
    {
        Remote = remote;
        _installed = installed;
        UpdateFrom = updateFrom;
        _isCurrent = isCurrent;
        _detail = detail;
    }

    public string Version => Remote.Version;
    public string ActionText => UpdateFrom is null ? "Install" : "Update";

    private bool _installed;
    public bool Installed
    {
        get => _installed;
        set { _installed = value; NotifyAll(); }
    }

    /// <summary>Show the Install/Update button?</summary>
    public bool ShowAction => !_installed;

    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set { _isCurrent = value; NotifyAll(); }
    }

    /// <summary>Show the "Use" button? (installed but not the current one)</summary>
    public bool CanUse => _installed && !_isCurrent;

    private string _detail;
    public string Detail
    {
        get => _detail;
        set { _detail = value; Notify(nameof(Detail)); }
    }

    /// <summary>Show the per-version Settings button (installed PHP rows only).</summary>
    public bool ShowSettings { get; set; }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set { _busy = value; Notify(nameof(Busy)); Notify(nameof(NotBusy)); }
    }
    public bool NotBusy => !_busy;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void NotifyAll()
    {
        foreach (var n in new[] { nameof(Installed), nameof(ShowAction), nameof(IsCurrent), nameof(CanUse), nameof(ActionText) })
            Notify(n);
    }
}
