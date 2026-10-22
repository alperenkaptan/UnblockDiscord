using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using UnblockDiscord.Automatic;

namespace UnblockDiscord;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--console" }))
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            await ServiceRun.RunAsync(Console.WriteLine, stop.Token);
            return;
        }
        if (Environment.UserInteractive)
        {
            Console.WriteLine("Install with tools\\Install-Service.ps1 from an Administrator PowerShell. See docs\\SERVICE.md.");
            return;
        }
        ServiceBase.Run(new DiscordService());
    }
}

internal static class ServiceRun
{
    internal static DiscordProcessWatcher? Watcher;

    internal static async Task RunAsync(Action<string> report, CancellationToken token)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "service.json");
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(configPath, token));
        var folder = config.RootElement.GetProperty("DiscordFolder").GetString();
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new IOException("service.json must contain an absolute DiscordFolder path.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var bypass = new BypassController(folder);
        using var watcher = new DiscordProcessWatcher(folder);
        var worker = new AutomaticBypass(() => watcher.Any,
            cancellation => bypass.StartAsync(_ => { }, cancellation), bypass.Dispose, report);
        bypass.EngineExited += worker.EngineFailed;
        watcher.Changed += worker.Signal;
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Failed += ex => failure.TrySetResult(ex);
        Watcher = watcher;
        try
        {
            watcher.Start();
            var working = worker.RunAsync(linked.Token);
            try
            {
                if (await Task.WhenAny(working, failure.Task) == failure.Task) throw await failure.Task;
                await working;
            }
            finally
            {
                await linked.CancelAsync();
                try { await working; } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            }
        }
        finally { Watcher = null; }
    }
}

internal sealed class DiscordService : ServiceBase
{
    private CancellationTokenSource? lifetime;
    private Task? working;

    internal DiscordService()
    {
        ServiceName = "UnblockDiscord";
        AutoLog = false;
        CanShutdown = true;
        CanHandlePowerEvent = true;
    }

    protected override void OnStart(string[] args)
    {
        lifetime = new CancellationTokenSource();
        working = Task.Run(async () =>
        {
            try { await ServiceRun.RunAsync(Report, lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                try { EventLog.WriteEntry(ServiceName, ex.ToString(), EventLogEntryType.Error); } catch { }
                // Nonzero process exit lets SCM recovery restart a failed watcher.
                Environment.Exit(1);
            }
        });
    }

    private void Report(string message)
    {
        try { EventLog.WriteEntry(ServiceName, message, EventLogEntryType.Information); } catch { }
    }

    protected override void OnStop()
    {
        lifetime?.Cancel();
        working?.GetAwaiter().GetResult();
        lifetime?.Dispose();
    }

    protected override void OnShutdown() => OnStop();

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus is PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend)
            ServiceRun.Watcher?.Refresh();
        return true;
    }
}
