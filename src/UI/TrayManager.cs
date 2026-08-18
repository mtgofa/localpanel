using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace LocalPanel.UI;

/// <summary>System-tray icon with a quick menu (open / start all / stop all / exit).</summary>
public sealed class TrayManager : IDisposable
{
    private readonly WF.NotifyIcon _icon;

    public TrayManager(Action open, Func<Task> startAll, Func<Task> stopAll, Action exit)
    {
        _icon = new WF.NotifyIcon
        {
            Text = "LocalPanel v" + App.Version,
            Visible = true,
        };
        try
        {
            _icon.Icon = SD.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                         ?? SD.SystemIcons.Application;
        }
        catch { _icon.Icon = SD.SystemIcons.Application; }

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Open LocalPanel", null, (_, _) => open());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Start all services", null, async (_, _) => await startAll());
        menu.Items.Add("Stop all services", null, async (_, _) => await stopAll());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => open();
    }

    public void Notify(string message)
        => _icon.ShowBalloonTip(2500, "LocalPanel", message, WF.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
