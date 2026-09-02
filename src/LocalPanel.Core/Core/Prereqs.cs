using System.IO;

namespace LocalPanel.Core;

/// <summary>System prerequisite checks.</summary>
public static class Prereqs
{
    public static bool VcRuntimeMissing =>
        Platform.IsWindows &&
        !File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "vcruntime140.dll"));

    /// <summary>Appended to service-start failures so the real cause is obvious.</summary>
    public static string Hint =>
        VcRuntimeMissing
            ? "\n\nLikely cause: Microsoft Visual C++ Runtime (2015-2022) is not installed." +
              "\nDownload: https://aka.ms/vs/17/release/vc_redist.x64.exe"
            : "";
}
