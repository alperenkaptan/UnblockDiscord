using System.Diagnostics;
using UnblockDiscord.Automatic;

internal static class AutomaticServiceTests
{
    internal static async Task RunAsync()
    {
        Check(string.Equals(DiscordProcessWatcher.ImagePath(Environment.ProcessId), Environment.ProcessPath,
            StringComparison.OrdinalIgnoreCase), "Limited-information process path lookup identifies the correct executable");
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var count = 0;
        var starts = 0;
        var stops = 0;
        var failNext = 0;
        var worker = new AutomaticBypass(() => Volatile.Read(ref count) > 0,
            _ =>
            {
                Interlocked.Increment(ref starts);
                if (Interlocked.Exchange(ref failNext, 0) != 0) throw new IOException("Simulated unavailable network.");
                return Task.CompletedTask;
            }, () => Interlocked.Increment(ref stops), _ => { },
            TimeSpan.FromMilliseconds(180), TimeSpan.FromMilliseconds(50));
        var run = worker.RunAsync(lifetime.Token);
        await Task.Delay(100);
        Check(starts == 0 && stops == 0, "Automatic service stays idle without starting engines");
        count = 1; worker.Signal();
        await Until(() => starts == 1);
        for (var i = 0; i < 100; i++) worker.Signal();
        await Task.Delay(70);
        Check(starts == 1, "Coalesced helper events do not start duplicate engines");
        count = 0; worker.Signal();
        await Task.Delay(60);
        count = 1; worker.Signal();
        await Task.Delay(230);
        Check(starts == 1 && stops == 0, "Discord update handover preserves the active engines");
        count = 0; worker.Signal();
        await Until(() => stops == 1);
        await Task.Delay(200);
        Check(starts == 1 && stops == 1, "Last Discord exit stops engines then waits without polling");
        failNext = 1; count = 1; worker.Signal();
        await Until(() => starts == 3);
        Check(stops == 2, "Startup failure cleans up and retries only while Discord is open");
        worker.EngineFailed();
        await Until(() => starts == 4);
        Check(stops == 3, "Unexpected engine exit triggers a bounded retry");
        lifetime.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        Check(stops == 4, "Stopping the Windows service cleans up active engines");
        Console.WriteLine("PASS Automatic service lifecycle");
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    internal static async Task WatchEventsAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "UnblockDiscord-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var executable = Path.Combine(folder, "DiscordFixture.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), executable);
        try
        {
            using var watcher = new DiscordProcessWatcher(folder);
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Changed += () => { if (watcher.Any) arrived.TrySetResult(); else exited.TrySetResult(); };
            watcher.Start();
            Check(!watcher.Any, "Actual WMI watcher begins idle");
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("/c"); info.ArgumentList.Add("ping -n 4 127.0.0.1 > nul");
            using var fixture = Process.Start(info)!;
            try
            {
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(watcher.Any, "Actual process-start event recognizes an executable inside the configured folder");
                await fixture.WaitForExitAsync();
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!watcher.Any, "Actual process-stop event returns watcher to idle");
            }
            finally { if (!fixture.HasExited) { fixture.Kill(true); await fixture.WaitForExitAsync(); } }
        }
        finally
        {
            File.Delete(executable);
            Directory.Delete(folder);
        }
    }
}
