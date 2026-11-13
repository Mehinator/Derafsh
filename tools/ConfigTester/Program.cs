// Derafsh config tester, run by collector.py on GitHub Actions (see .github/workflows/update_configs.yml).
//
// Every share link is built into an Xray outbound with the app's own ConfigService, so a config is tested exactly the
// way the app connects with it. Hundreds are tested at once through one Xray process: one SOCKS inbound per config,
// routed to that config's outbound. A config passes when Google's generate_204 answers through it twice and it really
// downloads (a server that answers pings but downloads nothing is useless for video, see MainPage.Testing.cs).
//
// The test runs outside Iran, so it can't tell whether Iran's filter blocks a server; it removes the ones that are
// dead everywhere (most of a public list after a few hours), and the phone tests the rest on its own network.
//
// usage: ConfigTester <links.txt> <results.tsv> <xray> [batch size] [minutes]
// results.tsv: one line per input link: link, then "ok <ping ms> <KB/s> <country> <city> <ASN> <exit IP> <YouTube 1/0>
// <Instagram 1/0>", "dead",
// "invalid <why>" or "untested" (tab separated). Where a working config comes out on the internet (what websites see,
// for the app's location choice) is looked up through the config itself; empty fields when the lookup failed.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Derafsh.Client.Services;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: ConfigTester <links.txt> <results.tsv> <xray> [batch size] [minutes]");
    return 2;
}
var links = File.ReadAllLines(args[0]).Select(l => l.Trim()).Where(l => l.Contains("://")).Distinct().ToList();
string resultsPath = args[1], xray = Path.GetFullPath(args[2]);
int batchSize = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 200;
var budget = TimeSpan.FromMinutes(args.Length > 4 ? double.Parse(args[4], CultureInfo.InvariantCulture) : 18);

var results = new ConcurrentDictionary<string, string>();
var service = new ConfigService();
// DERAFSH_FRAGMENT=1: configs built with TLS fragmentation, as the app builds them by default (to check it doesn't break them)
bool fragment = Environment.GetEnvironmentVariable("DERAFSH_FRAGMENT") is { Length: > 0 } frag && frag != "0";
// DERAFSH_FRAGMENT=packets,length,interval (e.g. tlshello,100-200,10-20) tries other fragment settings
if (fragment && Environment.GetEnvironmentVariable("DERAFSH_FRAGMENT")!.Split(',') is { Length: 3 } fp)
    (ConfigService.FragmentPackets, ConfigService.FragmentLength, ConfigService.FragmentInterval) = (fp[0], fp[1], fp[2]);
// DERAFSH_ADB=<adb.exe>: Xray runs on a phone plugged in by USB (at /data/local/tmp/xray), and its SOCKS ports are
// forwarded here, so every config is tested from the phone's own network (e.g. an Iranian mobile operator)
string? adb = Environment.GetEnvironmentVariable("DERAFSH_ADB");
var outbounds = new List<Candidate>();
foreach (var link in links)
{
    try
    {
        var config = JsonNode.Parse(service.GenerateXrayConfig(link, Tester.BasePort, fragment: fragment))!;
        var proxy = config["outbounds"]!.AsArray().First(o => (string?)o!["tag"] == "proxy")!.AsObject();
        outbounds.Add(new Candidate(link, (JsonObject)proxy.DeepClone()));
    }
    catch (Exception ex) { results[link] = Invalid("app: " + ex.Message); }   // the app can't build it either
}
Console.WriteLine($"{links.Count} links, {outbounds.Count} buildable");

var clock = Stopwatch.StartNew();
int batches = (outbounds.Count + batchSize - 1) / batchSize;
for (int b = 0; b < batches; b++)
{
    var batch = outbounds.Skip(b * batchSize).Take(batchSize).ToList();
    if (clock.Elapsed > budget)
    {
        foreach (var x in batch) results[x.Link] = "untested";
        continue;
    }
    await RunBatchAsync(batch);
    int ok = batch.Count(x => results.TryGetValue(x.Link, out var r) && r.StartsWith("ok"));
    Console.WriteLine($"batch {b + 1}/{batches}: {ok} of {batch.Count} work · {clock.Elapsed:mm\\:ss}");
}

using (var w = new StreamWriter(resultsPath, false, new UTF8Encoding(false)))
    foreach (var link in links) w.WriteLine($"{link}\t{results.GetValueOrDefault(link, "untested")}");
var counts = links.GroupBy(l => results.GetValueOrDefault(l, "untested").Split('\t')[0]).Select(g => $"{g.Key} {g.Count()}");
Console.WriteLine($"done in {clock.Elapsed:mm\\:ss}: {string.Join(", ", counts)}");
return 0;

// One Xray for the whole batch. If Xray refuses to start, the config it names ("with tag oN") is dropped and the rest
// start again; if it names none, the batch is split in half until the broken config is alone.
async Task RunBatchAsync(List<Candidate> batch)
{
    while (batch.Count > 0)
    {
        var (proc, error) = await StartXrayAsync(batch);
        if (proc != null)
        {
            // DERAFSH_BUILD_ONLY=1: only check that Xray accepts the configs (to see why some can't be used), no network
            bool buildOnly = Environment.GetEnvironmentVariable("DERAFSH_BUILD_ONLY") == "1";
            try
            {
                await Task.WhenAll(batch.Select(async (x, i) => results[x.Link] = buildOnly ? "untested" : await Tester.TestAsync(Tester.BasePort + i)));
                // DERAFSH_KEEP=<seconds>: leave Xray running that long (port 21000 + n for the n-th link), for long manual tests
                if (int.TryParse(Environment.GetEnvironmentVariable("DERAFSH_KEEP"), out int keep)) { Console.WriteLine("ready"); await Task.Delay(keep * 1000); }
            }
            finally { StopXray(proc); }
            return;
        }
        var m = Regex.Match(error, @"with tag o(\d+)\b(.*)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out int bad) && bad < batch.Count)
        {
            // the last part of Xray's message is the actual reason, e.g. unknown "fingerprint": xyz
            results[batch[bad].Link] = Invalid("xray: " + m.Groups[2].Value.Split(" > ").Last());
            batch = batch.Where((_, i) => i != bad).ToList();
            continue;
        }
        if (batch.Count == 1)
        {
            results[batch[0].Link] = Invalid("xray: " + error.Split('\n').LastOrDefault(l => l.Contains("Failed") || l.Contains("failed"))?.Split(" > ").Last());
            return;
        }
        int half = batch.Count / 2;
        await RunBatchAsync(batch.Take(half).ToList());
        await RunBatchAsync(batch.Skip(half).ToList());
        return;
    }
}

async Task<(Process? proc, string error)> StartXrayAsync(List<Candidate> batch)
{
    var inbounds = new JsonArray();
    var outs = new JsonArray();
    var rules = new JsonArray();
    for (int i = 0; i < batch.Count; i++)
    {
        inbounds.Add(new JsonObject
        {
            ["tag"] = $"i{i}", ["port"] = Tester.BasePort + i, ["listen"] = "127.0.0.1", ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = false },
        });
        var outbound = (JsonObject)batch[i].Outbound.DeepClone();
        outbound["tag"] = $"o{i}";
        outs.Add(outbound);
        rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray($"i{i}"), ["outboundTag"] = $"o{i}" });
    }
    if (outs.Any(o => (string?)o!["streamSettings"]?["sockopt"]?["dialerProxy"] == "fragment"))
        outs.Add(ConfigService.FragmentOutbound());   // one fragment outbound serves every config in the batch
    var config = new JsonObject
    {
        ["log"] = new JsonObject { ["loglevel"] = "error" },
        ["inbounds"] = inbounds,
        ["outbounds"] = outs,
        ["routing"] = new JsonObject { ["rules"] = rules },
    };
    string path = Path.Combine(Path.GetTempPath(), "derafsh-test.json");
    File.WriteAllText(path, config.ToJsonString());

    var start = new ProcessStartInfo(xray, $"run -c \"{path}\"");
    if (adb != null)
    {
        Adb("shell", "pkill -f derafsh-test.json");
        Adb("forward", "--remove-all");
        Adb("push", path, "/data/local/tmp/derafsh-test.json");
        for (int i = 0; i < batch.Count; i++) Adb("forward", $"tcp:{Tester.BasePort + i}", $"tcp:{Tester.BasePort + i}");
        start = new ProcessStartInfo(adb, "shell /data/local/tmp/xray run -c /data/local/tmp/derafsh-test.json");
    }
    start.RedirectStandardOutput = start.RedirectStandardError = true;
    start.UseShellExecute = false;
    var proc = Process.Start(start)!;
    // the output has to be read all the time, or Xray blocks when the pipe is full
    var output = new StringBuilder();
    void Collect(object _, DataReceivedEventArgs e) { if (e.Data != null) lock (output) if (output.Length < 20_000) output.AppendLine(e.Data); }
    proc.OutputDataReceived += Collect;
    proc.ErrorDataReceived += Collect;
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();

    var wait = Stopwatch.StartNew();
    while (wait.Elapsed < TimeSpan.FromSeconds(15))
    {
        if (proc.HasExited) break;
        // a forwarded port always accepts, so on the phone it counts only once Xray answers the SOCKS greeting
        if (adb != null ? await Tester.SocksReadyAsync(Tester.BasePort + batch.Count - 1) : await Tester.PortOpenAsync(Tester.BasePort + batch.Count - 1))
            return (proc, "");
        await Task.Delay(200);
    }
    StopXray(proc);
    lock (output) return (null, output.ToString());
}

void StopXray(Process proc)
{
    try { proc.Kill(true); proc.WaitForExit(5000); } catch { }
    proc.Dispose();
    if (adb != null) Adb("shell", "pkill -f derafsh-test.json");   // killing the local adb leaves Xray running on the phone
}

void Adb(params string[] args)
{
    var info = new ProcessStartInfo(adb!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in args) info.ArgumentList.Add(a);
    using var p = Process.Start(info)!;
    p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    p.WaitForExit(30_000);
}

// "invalid" plus why (one line, for the collector's summary of what the app can't use yet)
static string Invalid(string? reason)
{
    string why = Regex.Replace(reason ?? "", @"\s+", " ").Trim();
    return "invalid\t" + (why.Length > 140 ? why[..140] : why);
}

record Candidate(string Link, JsonObject Outbound);

static class Tester
{
    public const int BasePort = 21000;
    private const string PingUrl = "https://www.gstatic.com/generate_204";
    // like the app: Cloudflare first, then Derafsh's own list on jsDelivr (some servers on Cloudflare Workers can't
    // reach Cloudflare's sites but pass everything else)
    private const string SpeedUrl = "https://speed.cloudflare.com/__down?bytes=150000";
    private const string SpeedFallbackUrl = "https://cdn.jsdelivr.net/gh/Mehinator/Derafsh@main/derafsh_sub.txt";
    private const int SpeedBytes = 150_000, MinBytes = 32 * 1024;

    public static async Task<string> TestAsync(int port)
    {
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),   // host names are resolved by the server, as in the app
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = false,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36");

        // the first request opens the tunnel (TLS to the server and to the site), the next two give the ping
        if (await StatusAsync(client, PingUrl, 10) != HttpStatusCode.NoContent) return "dead";
        int ping = int.MaxValue;
        for (int i = 0; i < 2; i++)
        {
            var sw = Stopwatch.StartNew();
            if (await StatusAsync(client, PingUrl, 5) == HttpStatusCode.NoContent) ping = Math.Min(ping, (int)sw.ElapsedMilliseconds);
        }
        if (ping == int.MaxValue) return "dead";

        int kbps = await DownloadKbpsAsync(client, SpeedUrl, 8);
        if (kbps <= 0) kbps = await DownloadKbpsAsync(client, SpeedFallbackUrl, 8);
        if (kbps <= 0) return "dead";
        var (country, city, asn, ip) = await LocateAsync(client);
        // the sites people actually open: in a test from a phone, 4 of the 6 fastest configs passed everything above but
        // could not open YouTube (many free servers block it to save bandwidth), so the video just didn't play
        int youtube = await StatusAsync(client, "https://www.youtube.com/generate_204", 8) == HttpStatusCode.NoContent ? 1 : 0;
        int instagram = await StatusAsync(client, "https://www.instagram.com/", 8) is HttpStatusCode s && (int)s < 500 ? 1 : 0;
        return $"ok\t{ping}\t{kbps}\t{Clean(country)}\t{Clean(city)}\t{asn}\t{Clean(ip)}\t{youtube}\t{instagram}";
    }

    // Where the config comes out on the internet: Cloudflare's own lookup of the exit IP first (its city is the most
    // accurate one in tests, and the connection to speed.cloudflare.com is already open), then ipwho.is for servers that
    // can't reach Cloudflare's sites (e.g. ones running on Cloudflare Workers)
    private static async Task<(string? country, string? city, long asn, string? ip)> LocateAsync(HttpClient client)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://speed.cloudflare.com/meta");
            request.Headers.Referrer = new Uri("https://speed.cloudflare.com/");   // without it the answer is {}
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var res = await client.SendAsync(request, cts.Token);
            var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            string? country = (string?)j?["country"];
            if (!string.IsNullOrEmpty(country))
                return (country, (string?)j?["city"], j?["asn"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number ? (long)j["asn"]! : 0, (string?)j?["clientIp"]);
        }
        catch { }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var res = await client.GetAsync("https://ipwho.is/", cts.Token);
            var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            if (j?["success"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)
                return ((string?)j["country_code"], (string?)j["city"],
                        j["connection"]?["asn"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number ? (long)j["connection"]!["asn"]! : 0, (string?)j["ip"]);
        }
        catch { }
        return (null, null, 0, null);
    }

    private static string Clean(string? s) => Regex.Replace(s ?? "", @"[\t\r\n]+", " ").Trim();

    private static async Task<HttpStatusCode?> StatusAsync(HttpClient client, string url, int seconds)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            using var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return res.StatusCode;
        }
        catch { return null; }
    }

    // KB/s from the first byte on (so a slow start of the connection doesn't count), 0 when almost nothing came
    private static async Task<int> DownloadKbpsAsync(HttpClient client, string url, int seconds)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            using var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!res.IsSuccessStatusCode) return 0;
            await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[16384];
            int n = await stream.ReadAsync(buffer, cts.Token);
            if (n <= 0) return 0;
            var sw = Stopwatch.StartNew();
            long total = n;
            try { while (total < SpeedBytes && (n = await stream.ReadAsync(buffer, cts.Token)) > 0) total += n; }
            catch (OperationCanceledException) { }
            if (total < MinBytes) return 0;
            return (int)(total / 1024.0 / Math.Max(0.05, sw.Elapsed.TotalSeconds));
        }
        catch { return 0; }
    }

    public static async Task<bool> SocksReadyAsync(int port)
    {
        try
        {
            using var c = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await c.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var s = c.GetStream();
            await s.WriteAsync(new byte[] { 5, 1, 0 }, cts.Token);
            var reply = new byte[2];
            int n = await s.ReadAsync(reply, cts.Token);
            return n == 2 && reply[0] == 5 && reply[1] == 0;
        }
        catch { return false; }
    }

    public static async Task<bool> PortOpenAsync(int port)
    {
        try
        {
            using var c = new TcpClient();
            var connect = c.ConnectAsync(IPAddress.Loopback, port);
            if (await Task.WhenAny(connect, Task.Delay(500)) != connect) return false;
            await connect;
            return true;
        }
        catch { return false; }
    }
}
