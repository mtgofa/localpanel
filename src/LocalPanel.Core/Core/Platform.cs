using System.Runtime.InteropServices;

namespace LocalPanel.Core;

public static class Platform
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static string ExeExt => IsWindows ? ".exe" : "";
    public static string ScriptExt => IsWindows ? ".cmd" : "";

    public static string Exe(string baseName) => IsWindows ? $"{baseName}.exe" : baseName;
    public static string Script(string baseName) => IsWindows ? $"{baseName}.cmd" : baseName;
}
