using System;
using Avalonia;
using LocalPanel.Core;

namespace LocalPanel.Desktop;

public static class Program
{
    public const string InstanceName = "localpanel-gui";

    [STAThread]
    public static void Main(string[] args)
    {
        // One GUI per user: two of them would fight over the same services, ports and
        // config file. The lock is taken before Avalonia starts so a second launch never
        // flashes a window. Released by the OS when this process exits.
        var instance = SingleInstanceLock.TryAcquire(InstanceName, out var ownerPid);
        if (instance is null)
        {
            // Second launch acts as "bring the running one to the front" — the same thing
            // the Windows build does, and the way back when the window is hidden.
            SingleInstanceLock.RequestShow(InstanceName);
            Console.Error.WriteLine(ownerPid > 0
                ? $"LocalPanel is already running (pid {ownerPid}) — bringing its window to the front."
                : "LocalPanel is already running — bringing its window to the front.");
            Environment.Exit(0);
            return;
        }

        using (instance)
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
