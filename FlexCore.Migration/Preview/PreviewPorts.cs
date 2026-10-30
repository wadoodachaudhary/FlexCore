#pragma warning disable CA1416
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Fx.ControlKit.Preview;

internal static class PreviewPorts
{
    private static readonly object Gate = new();

    public static (int Port, string LockPath) Allocate(ProgramPreviewOptions options, int? requested)
    {
        var directory = Path.Combine(Path.GetTempPath(), "flexcore-preview", "ports");
        Directory.CreateDirectory(directory);
        lock (Gate)
        {
            if (requested is int exact)
            {
                if (!CanBind(exact) || !TryLock(directory, exact, out var exactLock))
                    throw new InvalidOperationException($"Port {exact} is already in use.");
                return (exact, exactLock);
            }
            for (var port = options.FirstPreviewPort; port < options.FirstPreviewPort + options.PreviewPortCount; port++)
            {
                if (!CanBind(port) || !TryLock(directory, port, out var path)) continue;
                return (port, path);
            }
        }
        throw new InvalidOperationException("No preview ports are free.");
    }

    public static void Release(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); } catch { }
    }

    private static bool TryLock(string directory, int port, out string path)
    {
        path = Path.Combine(directory, port + ".lock");
        if (File.Exists(path))
        {
            if (LockIsLive(path)) return false;
            try { File.Delete(path); } catch { return false; }
        }
        try
        {
            using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(created);
            writer.Write(Environment.ProcessId);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool CanBind(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LockIsLive(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            if (!int.TryParse(text, out var pid)) return false;
            return !Process.GetProcessById(pid).HasExited;
        }
        catch
        {
            return false;
        }
    }
}
