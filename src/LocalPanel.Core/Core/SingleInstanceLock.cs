using System.Text;

namespace LocalPanel.Core;

/// <summary>
/// Cross-process lock that keeps one LocalPanel instance per user.
/// Backed by an exclusive OS lock on a file under logs/run: the kernel releases it the
/// moment the owning process dies — including a kill -9 — so there is never a stale lock
/// to clean up, unlike a bare "is this recorded PID still alive?" check. Works the same
/// on Linux (flock) and Windows (share mode).
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _path;

    private SingleInstanceLock(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
    }

    public static string PathFor(string name) => Path.Combine(Paths.Run, name + ".lock");

    private static string ShowRequestPath(string name) => Path.Combine(Paths.Run, name + ".show");

    /// <summary>Asks the instance that owns the lock to surface its window. This is the
    /// guaranteed way back when the window is hidden and no system tray is available.</summary>
    public static void RequestShow(string name)
    {
        try { File.WriteAllText(ShowRequestPath(name), Environment.ProcessId.ToString()); }
        catch { }
    }

    /// <summary>Runs <paramref name="onRequested"/> whenever another launch asks this
    /// instance to show itself. Caller owns the returned watcher.</summary>
    public static FileSystemWatcher WatchShowRequests(string name, Action onRequested)
    {
        var watcher = new FileSystemWatcher(Paths.Run, name + ".show")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };

        void Handle(object? sender, FileSystemEventArgs e)
        {
            try { File.Delete(ShowRequestPath(name)); } catch { }
            onRequested();
        }

        watcher.Created += Handle;
        watcher.Changed += Handle;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>
    /// Takes the lock, or returns null when another instance already holds it —
    /// <paramref name="ownerPid"/> then carries that instance's PID, or 0 if it could not be read.
    /// </summary>
    public static SingleInstanceLock? TryAcquire(string name, out int ownerPid)
    {
        ownerPid = 0;
        var path = PathFor(name);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            stream.Write(Encoding.UTF8.GetBytes(Environment.ProcessId.ToString()));
            stream.Flush(true);
            // A request left behind by a crashed run would otherwise fire immediately.
            try { File.Delete(ShowRequestPath(name)); } catch { }
            return new SingleInstanceLock(stream, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ownerPid = ReadOwnerPid(path);
            return null;
        }
    }

    /// <summary>FileShare.ReadWrite so reading the PID does not itself contend for the lock.</summary>
    private static int ReadOwnerPid(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return int.TryParse(reader.ReadToEnd().Trim(), out var pid) ? pid : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { }
        try { File.Delete(_path); } catch { }
    }
}
