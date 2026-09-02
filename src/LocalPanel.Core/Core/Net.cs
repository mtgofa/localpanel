using System.Net.Http;
using System.Net.Sockets;

namespace LocalPanel.Core;

public static class Net
{
    /// <summary>Turn raw network exceptions into a message a human can act on.</summary>
    public static string Friendly(Exception ex)
    {
        var e = ex.GetBaseException();
        if (e is HttpRequestException or SocketException or TaskCanceledException or TimeoutException)
            return "No internet connection — or the download server is unreachable. " +
                   "Check your connection and try again.";
        return ex.Message;
    }
}
