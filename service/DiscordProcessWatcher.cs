using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UnblockDiscord.Automatic;

internal sealed class DiscordProcessWatcher : IDisposable
{
    private readonly string folder;
    private readonly HashSet<int> processes = [];
    private readonly object gate = new();
    private readonly ManagementEventWatcher starts = new(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace WHERE SessionID <> 0"));
    private readonly ManagementEventWatcher exits = new(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace WHERE SessionID <> 0"));
    private bool disposed;
    internal event Action? Changed;
    internal event Action<Exception>? Failed;
    internal bool Any { get { lock (gate) return processes.Count > 0; } }

    internal DiscordProcessWatcher(string folder)
    {
        this.folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        starts.EventArrived += (_, e) =>
        {
            bool added;
            lock (gate) added = !disposed && Include(Convert.ToInt32(e.NewEvent["ProcessID"]));
            if (added) Changed?.Invoke();
        };
        exits.EventArrived += (_, e) =>
        {
            bool removed;
            lock (gate) removed = !disposed && processes.Remove(Convert.ToInt32(e.NewEvent["ProcessID"]));
            if (removed) Changed?.Invoke();
        };
        starts.Stopped += WatcherStopped;
        exits.Stopped += WatcherStopped;
    }

    internal void Start()
    {
        starts.Start();
        exits.Start();
        Refresh();
    }

    internal void Refresh()
    {
        lock (gate)
        {
            if (disposed) return;
            processes.Clear();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try { if (process.SessionId != 0) Include(process.Id); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
                }
            }
        }
        Changed?.Invoke();
    }

    private bool Include(int id)
    {
        try
        {
            return ImagePath(id)?.StartsWith(folder, StringComparison.OrdinalIgnoreCase) == true && processes.Add(id);
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException or NotSupportedException)
        { return false; }
    }

    internal static string? ImagePath(int id)
    {
        using var process = OpenProcess(0x1000, false, id);
        if (process.IsInvalid) return null;
        var path = new StringBuilder(1024);
        var size = path.Capacity;
        return QueryFullProcessImageNameW(process, 0, path, ref size) ? path.ToString() : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);

    private void WatcherStopped(object sender, StoppedEventArgs e)
    {
        lock (gate) { if (disposed) return; }
        Failed?.Invoke(new IOException("Windows process event monitoring stopped: " + e.Status));
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        starts.Dispose(); exits.Dispose();
    }
}
