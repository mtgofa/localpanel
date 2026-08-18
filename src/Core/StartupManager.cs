using Microsoft.Win32;

namespace LocalPanel.Core;

/// <summary>Registers LocalPanel in the current user's Run key (launch at login).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LocalPanel";

    private static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{ExePath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
