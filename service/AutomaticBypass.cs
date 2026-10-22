using System.Threading.Channels;

namespace UnblockDiscord.Automatic;

// One event queue and one-shot timers only. No process-list polling while idle.
internal sealed class AutomaticBypass
{
    private readonly Func<bool> anyProcesses;
    private readonly Func<CancellationToken, Task> start;
    private readonly Action stop;
    private readonly Action<string> report;
    private readonly TimeSpan stopDelay;
    private readonly TimeSpan retryDelay;
    private readonly Channel<byte> changes = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private int engineFailed;

    internal AutomaticBypass(Func<bool> anyProcesses, Func<CancellationToken, Task> start,
        Action stop, Action<string> report, TimeSpan? stopDelay = null, TimeSpan? retryDelay = null)
    {
        this.anyProcesses = anyProcesses;
        this.start = start;
        this.stop = stop;
        this.report = report;
        this.stopDelay = stopDelay ?? TimeSpan.FromSeconds(10);
        this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(15);
    }

    internal void Signal() => changes.Writer.TryWrite(0);
    internal void EngineFailed() { Interlocked.Exchange(ref engineFailed, 1); Signal(); }

    internal async Task RunAsync(CancellationToken token)
    {
        var running = false;
        var retryAt = DateTime.MinValue;
        DateTime? idleUntil = null;
        var failures = 0;
        report("Waiting for Discord.");
        try
        {
            while (!token.IsCancellationRequested)
            {
                while (changes.Reader.TryRead(out _)) { }
                if (Interlocked.Exchange(ref engineFailed, 0) != 0)
                {
                    stop(); running = false;
                    retryAt = DateTime.UtcNow + retryDelay;
                    report("Engine stopped unexpectedly; retrying while Discord is open.");
                }
                var active = anyProcesses();
                if (active)
                {
                    idleUntil = null;
                    if (!running && DateTime.UtcNow >= retryAt)
                    {
                        try
                        {
                            await start(token);
                            running = true; failures = 0;
                            report("Bypass active for Discord.");
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            stop();
                            var delay = Math.Min(120, retryDelay.TotalSeconds * Math.Pow(2, Math.Min(failures++, 3)));
                            retryAt = DateTime.UtcNow.AddSeconds(delay);
                            report("Could not activate: " + ex.Message + $" Retrying in {delay:0.##} seconds while Discord is open.");
                        }
                        // Re-check events that arrived during the startup probes.
                        continue;
                    }
                }
                else
                {
                    failures = 0; retryAt = DateTime.MinValue;
                    if (running)
                    {
                        idleUntil ??= DateTime.UtcNow + stopDelay;
                        if (DateTime.UtcNow >= idleUntil)
                        {
                            stop(); running = false; idleUntil = null;
                            report("Waiting for Discord.");
                            // Discard expected engine-exit notifications caused by Stop.
                            Interlocked.Exchange(ref engineFailed, 0);
                        }
                    }
                }
                var deadline = active && !running ? retryAt : idleUntil;
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (deadline.HasValue)
                    wait.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, (deadline.Value - DateTime.UtcNow).TotalMilliseconds)));
                try { await changes.Reader.WaitToReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
        }
        finally { stop(); }
    }
}
