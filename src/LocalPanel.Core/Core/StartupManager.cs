using System.IO;

namespace LocalPanel.Core;

/// <summary>Registers LocalPanel in the user startup (launch at login).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LocalPanel";

    private static string ExePath => Environment.ProcessPath ?? "";

    private static string LinuxAutostartFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "autostart", "localpanel.desktop");

    public static bool IsEnabled
    {
        get
        {
            if (Platform.IsWindows)
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue(ValueName) is not null;
                }
                catch { return false; }
            }

            return File.Exists(LinuxAutostartFile);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (Platform.IsWindows)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled) key.SetValue(ValueName, $"\"{ExePath}\"");
                else key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch { }
            return;
        }

        try
        {
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LinuxAutostartFile)!);
                File.WriteAllText(LinuxAutostartFile, $"""
                    [Desktop Entry]
                    Type=Application
                    Exec="{ExePath}"
                    Hidden=false
                    NoDisplay=false
                    X-GNOME-Autostart-enabled=true
                    Name=LocalPanel
                    Comment=Local Development Environment Manager
                    """);
            }
            else
            {
                if (File.Exists(LinuxAutostartFile)) File.Delete(LinuxAutostartFile);
            }
        }
        catch { }
    }
}
