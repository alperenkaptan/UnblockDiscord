using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UnblockDiscord;

if (args.Length == 2 && args[0] == "--job-worker")
{
    // Deliberately omit Dispose. Parent kills this worker to simulate a crash.
    var job = new ChildProcesses();
    var child = job.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
        ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"]);
    await File.WriteAllTextAsync(args[1], child.Id.ToString());
    await Task.Delay(30000);
    GC.KeepAlive(job);
    return;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

static byte[] Hello(string host)
{
    var name = Encoding.ASCII.GetBytes(host);
    var sni = new byte[] { 0, (byte)(name.Length + 3), 0, 0, (byte)name.Length }.Concat(name).ToArray();
    var extensions = new byte[] { 0, 0, 0, (byte)sni.Length }.Concat(sni).ToArray();
    var body = new byte[34].Concat(new byte[] { 0, 0, 2, 0x13, 1, 1, 0, 0, (byte)extensions.Length }).Concat(extensions).ToArray();
    return new byte[] { 1, 0, 0, (byte)body.Length }.Concat(body).ToArray();
}

foreach (var host in new[] { "discord.com", "UPDATES.DISCORD.COM", "stable.dl2.discordapp.net", "us-east123.discord.media" })
    Check(DiscordTraffic.IsDiscordHost(host), "Discord domain " + host);
foreach (var host in new[] { "notdiscord.com", "discord.com.evil.test", "discord.com/", "google.com", "" })
    Check(!DiscordTraffic.IsDiscordHost(host), "Unrelated domain excluded: " + host);
var hello = Hello("updates.discord.com");
Check(DiscordTraffic.ServerName(hello) == "updates.discord.com", "ClientHello SNI extraction");
for (var i = 0; i < hello.Length; i++)
    if (DiscordTraffic.ServerName(hello.AsSpan(0, i)) != null) throw new Exception("Truncated ClientHello accepted.");
Console.WriteLine("PASS All truncated ClientHello prefixes rejected");
var random = new Random(1);
for (var i = 0; i < 10000; i++)
{
    var data = new byte[random.Next(0, 400)]; random.NextBytes(data);
    DiscordTraffic.ServerName(data);
}
Console.WriteLine("PASS 10000 malformed TLS handshakes do not throw");
using (var config = JsonDocument.Parse(BypassController.Configuration(BypassController.DiscordFolder, 12345)))
{
    var rule = config.RootElement.GetProperty("proxies")[0];
    var apps = rule.GetProperty("appNames");
    Check(apps.GetArrayLength() == 1 && apps[0].GetString() == BypassController.DiscordFolder &&
        apps[0].GetString()!.EndsWith("Discord\\"), "Only the Discord folder and its versioned helpers match");
    Check(rule.GetProperty("supportedProtocols").GetArrayLength() == 2, "TCP and UDP both enabled");
}
var pidFile = Path.Combine(Path.GetTempPath(), "UnblockDiscord-job-test-" + Guid.NewGuid().ToString("N"));
try
{
    var workerInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    workerInfo.ArgumentList.Add("--job-worker"); workerInfo.ArgumentList.Add(pidFile);
    using var worker = Process.Start(workerInfo)!;
    for (var i = 0; i < 100 && !File.Exists(pidFile); i++) await Task.Delay(50);
    if (!File.Exists(pidFile)) { worker.Kill(true); throw new Exception("Job worker failed."); }
    using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile)));
    worker.Kill();
    await worker.WaitForExitAsync();
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
    Check(child.HasExited, "Engine job children terminate when parent is force-killed");
}
finally { File.Delete(pidFile); }
if (!args.Contains("--network")) return;

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = timeout.Token;
var engines = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "engines"));
using var children = new ChildProcesses();
var dpiPort = 18983;
var profiles = new[]
{
    new[] { "--split", "1", "--disorder", "3+s", "--tlsrec", "1+s", "--udp-fake", "6" },
    new[] { "--split", "1+s", "--disorder", "3+s", "--udp-fake", "6" },
    new[] { "--split", "1", "--tlsrec", "1+s", "--udp-fake", "6" }
};
Process? dpi = null;
DiscordRelay? relay = null;
for (var index = 0; index < profiles.Length; index++)
{
    dpi = children.Start(Path.Combine(engines, "byedpi", "ciadpi.exe"),
        new[] { "--ip", "127.0.0.1", "--port", dpiPort.ToString() }.Concat(profiles[index]));
    await Task.Delay(300, token);
    relay = new DiscordRelay(dpiPort);
    try
    {
        await BypassController.ProbeAsync(relay.Port, token);
        Console.WriteLine($"PASS Public endpoints through profile {index + 1}");
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"PROFILE {index + 1}: {ex.Message}");
        relay.Dispose(); relay = null;
        if (!dpi.HasExited) dpi.Kill(true);
        dpi.Dispose(); dpi = null;
    }
}
if (relay == null || dpi == null) throw new Exception("No profile passed public endpoint probes.");
using (relay)
using (dpi)
{
    // Force the poisoned IP into SOCKS, so the test cannot accidentally rely on
    // HttpClient sending a domain name or the machine having clean DNS.
    using var forced = new HttpClient(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectCallback = async (_, cancellation) =>
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, relay.Port, cancellation);
                var stream = client.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, cancellation);
                var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, cancellation);
                await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 195, 175, 254, 2, 1, 187 }, cancellation);
                var reply = new byte[10]; await stream.ReadExactlyAsync(reply, cancellation);
                return stream;
            }
            catch { client.Dispose(); throw; }
        }
    }) { Timeout = TimeSpan.FromSeconds(15) };
    var gateway = await forced.GetStringAsync("https://discord.com/api/v10/gateway", token);
    Check(gateway.Contains("wss://gateway.discord.gg"), "Poisoned IP corrected using original TLS SNI; certificate validated");

    // Exercise SOCKS5 UDP association through our relay, with no Discord login.
    using var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var echoPort = ((IPEndPoint)echo.Client.LocalEndPoint!).Port;
    using var control = new TcpClient();
    await control.ConnectAsync(IPAddress.Loopback, relay.Port, token);
    var connection = control.GetStream();
    await connection.WriteAsync(new byte[] { 5, 1, 0 }, token);
    var two = new byte[2]; await connection.ReadExactlyAsync(two, token);
    await connection.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
    var association = new byte[10]; await connection.ReadExactlyAsync(association, token);
    Check(association[1] == 0 && association[3] == 1, "UDP associate succeeds");
    var udpEndpoint = new IPEndPoint(new IPAddress(association.AsSpan(4, 4)), BinaryPrimitives.ReadUInt16BigEndian(association.AsSpan(8)));
    using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    var payload = Encoding.ASCII.GetBytes("discord-relay-test");
    var packet = new byte[] { 0, 0, 0, 1, 127, 0, 0, 1, (byte)(echoPort >> 8), (byte)echoPort }.Concat(payload).ToArray();
    await sender.SendAsync(packet, udpEndpoint, token);
    using var udpTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
    udpTimeout.CancelAfter(TimeSpan.FromSeconds(5));
    UdpReceiveResult received;
    do { received = await echo.ReceiveAsync(udpTimeout.Token); } while (!received.Buffer.SequenceEqual(payload));
    await echo.SendAsync(payload, received.RemoteEndPoint, udpTimeout.Token);
    var returned = await sender.ReceiveAsync(udpTimeout.Token);
    Check(returned.Buffer.AsSpan(10).SequenceEqual(payload), "UDP echo passes through ByeDPI relay unchanged");

    if (args.Contains("--driver"))
    {
        var routerPath = Path.Combine(engines, "proxifyre", "ProxiFyre.exe");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(routerPath)!, "app-config.json"),
            BypassController.Configuration(Environment.ProcessPath!, relay.Port), token);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var router = children.Start(routerPath, ["--allow-not-admin"], line =>
        {
            Console.WriteLine("ENGINE " + line);
            if (line.Contains("ProxiFyre Service is running")) ready.TrySetResult();
        });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        using var routed = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
        Check((await routed.GetStringAsync("https://discord.com/api/v10/gateway", token)).Contains("wss://gateway.discord.gg"),
            "Driver routes the selected process through poisoned DNS correction");
        var curl = new ProcessStartInfo("curl.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "--noproxy", "*", "-I", "--max-time", "8", "https://discord.com/api/v10/gateway" }) curl.ArgumentList.Add(argument);
        using var unrelated = Process.Start(curl)!;
        await unrelated.WaitForExitAsync(token);
        Check(unrelated.ExitCode == 60, "Unrelated curl process remains on its original poisoned DNS connection");
        children.Dispose();
        await Task.Delay(500, token);
        Check(router.HasExited && dpi.HasExited, "Job cleanup terminates both engines");
        using var restored = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            await restored.GetStringAsync("https://discord.com/api/v10/gateway", token);
            throw new Exception("Routing unexpectedly persisted after shutdown.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        { Console.WriteLine("PASS Selected process returns to default connection after Stop"); }
    }
}
