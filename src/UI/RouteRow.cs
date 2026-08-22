using LocalPanel.Core;

namespace LocalPanel.UI;

/// <summary>
/// One editable row in the site form's route list. Plain properties on purpose:
/// WPF writes TwoWay bindings straight back into them, and the form reads the whole
/// list on Save — nothing needs to push changes the other way.
/// </summary>
public class RouteRow
{
    public static readonly string[] KindNames = { "PHP", "Static", "Proxy" };

    /// <summary>Bound by the row's ComboBox; static lists are not reachable through a DataContext path.</summary>
    public string[] Kinds => KindNames;

    public string Path { get; set; } = "/";
    public string Kind { get; set; } = "PHP";
    public string Folder { get; set; } = "";
    public string Port { get; set; } = "3000";
    public bool Spa { get; set; }

    /// <summary>Unix-socket upstream from an older config: kept as-is so saving the
    /// site in the UI does not silently drop it (the form only offers ports).</summary>
    public string? SocketPath { get; set; }

    public static RouteRow From(SiteRoute r) => new()
    {
        Path = r.Path,
        Kind = r.Kind switch { RouteKind.Static => "Static", RouteKind.Proxy => "Proxy", _ => "PHP" },
        Folder = r.Folder ?? "",
        Port = r.Port.ToString(),
        Spa = r.SpaFallback,
        SocketPath = r.SocketPath,
    };

    public SiteRoute ToRoute() => new()
    {
        Path = string.IsNullOrWhiteSpace(Path) ? "/" : Path.Trim(),
        Kind = Kind switch { "Static" => RouteKind.Static, "Proxy" => RouteKind.Proxy, _ => RouteKind.Php },
        Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim(),
        Port = int.TryParse(Port?.Trim(), out var p) && p is > 0 and < 65536 ? p : 3000,
        SpaFallback = Spa,
        SocketPath = SocketPath,
    };
}
