using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace UnblockDiscord;

internal sealed class BypassController : IDisposable
{
    private ChildProcesses? children;
    private Process? dpi;
    private Process? router;
    private DiscordRelay? relay;
    internal event Action? EngineExited;
    internal bool IsRunning => router is { HasExited: false } && dpi is { HasExited: false };

    internal static string DiscordFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord") + Path.DirectorySeparatorChar;

    internal static string Configuration(string folder, int port) => JsonSerializer.Serialize(new
    {
        logLevel = "Info",
        bypassLan = false,
        proxies = new[] { new {
            appNames = new[] { folder }, socks5ProxyEndpoint = $"127.0.0.1:{port}",
            supportedProtocols = new[] { "TCP", "UDP" },
            supportedAddressFamilies = new[] { "IPv4", "IPv6" }
        } }
    }, new JsonSerializerOptions { WriteIndented = true });

    internal async Task StartAsync(Action<string> progress, CancellationToken token)
    {
        if (children != null) throw new InvalidOperationException("The bypass is already active.");
        if (!File.Exists(Path.Combine(DiscordFolder, "Update.exe")))
            throw new IOException("Discord was not found in its default installation folder.");
        foreach (var name in new[] { "ProxiFyre", "ciadpi", "goodbyedpi", "winws", "winws2" })
        {
            var processes = Process.GetProcessesByName(name);
            var found = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            if (found) throw new InvalidOperationException($"Close the existing {name} bypass before starting.");
        }
        var basePath = Path.Combine(AppContext.BaseDirectory, "engines");
        var dpiPath = Path.Combine(basePath, "byedpi", "ciadpi.exe");
        var routerPath = Path.Combine(basePath, "proxifyre", "ProxiFyre.exe");
        if (!File.Exists(dpiPath) || !File.Exists(routerPath))
            throw new IOException("Bypass engines are missing. Restore the engines folder beside this app.");
        children = new ChildProcesses();
        try
        {
            // Test actual unauthenticated endpoints on this connection rather than
            // assuming a strategy works for every Turk Telekom installation.
            string[][] profiles =
            [
                ["--split", "1", "--disorder", "3+s", "--tlsrec", "1+s", "--udp-fake", "6"],
                ["--split", "1+s", "--disorder", "3+s", "--udp-fake", "6"],
                ["--split", "1", "--tlsrec", "1+s", "--udp-fake", "6"]
            ];
            Exception? lastFailure = null;
            for (var index = 0; index < profiles.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                progress($"Testing {index + 1}/{profiles.Length}...");
                var port = FreePort();
                dpi = children.Start(dpiPath, new[] { "--ip", "127.0.0.1", "--port", port.ToString() }.Concat(profiles[index]));
                try
                {
                    await WaitForPortAsync(dpi, port, token);
                    relay = new DiscordRelay(port);
                    await ProbeAsync(relay.Port, token);
                    lastFailure = null;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    lastFailure = ex;
                    relay?.Dispose();
                    relay = null;
                    Kill(dpi);
                    dpi = null;
                }
            }
            if (lastFailure != null || relay == null)
                throw new IOException("No tested strategy reached Discord securely on this connection. " +
                    "No network settings were changed. " + lastFailure?.Message, lastFailure);

            progress("Starting...");
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(routerPath)!, "app-config.json"),
                Configuration(DiscordFolder, relay.Port), token);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            router = children.Start(routerPath, [], line =>
            {
                if (line.Contains("ProxiFyre Service is running", StringComparison.Ordinal)) ready.TrySetResult();
                if (line.Contains("Failed to", StringComparison.OrdinalIgnoreCase))
                    ready.TrySetException(new IOException(line));
            });
            router.Exited += (_, _) =>
            {
                ready.TrySetException(new IOException("ProxiFyre stopped. Verify the Windows Packet Filter driver is installed and running."));
                EngineExited?.Invoke();
            };
            dpi!.Exited += (_, _) => EngineExited?.Invoke();
            if (router.HasExited) throw new IOException("ProxiFyre could not start. Check the Windows Packet Filter driver.");
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(12), token);
            if (!IsRunning) throw new IOException("A bypass engine stopped during startup.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static async Task ProbeAsync(int proxyPort, CancellationToken token)
    {
        using var client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{proxyPort}"), UseProxy = true,
            AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8)
        }) { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync("https://discord.com/api/v10/gateway", token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!json.RootElement.TryGetProperty("url", out var gateway) ||
            gateway.GetString() != "wss://gateway.discord.gg") throw new IOException("Unexpected Discord gateway response.");
        foreach (var host in new[] { "updates.discord.com", "cdn.discordapp.com", "gateway.discord.gg" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, $"https://{host}/");
            using var check = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            // A 404/403 at the root is normal. TLS certificate validation remains mandatory.
            if ((int)check.StatusCode >= 500) throw new IOException(host + " is unavailable.");
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForPortAsync(Process process, int port, CancellationToken token)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (process.HasExited) throw new IOException("ByeDPI could not start.");
            using var client = new TcpClient();
            try { await client.ConnectAsync(IPAddress.Loopback, port, token); return; }
            catch (SocketException) { await Task.Delay(50, token); }
        }
        throw new IOException("ByeDPI did not open its local proxy.");
    }

    private static void Kill(Process? process)
    {
        if (process == null) return;
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); } }
        finally { process.Dispose(); }
    }

    public void Dispose()
    {
        children?.Dispose();
        children = null;
        relay?.Dispose();
        relay = null;
        Kill(router);
        router = null;
        Kill(dpi);
        dpi = null;
    }
}
