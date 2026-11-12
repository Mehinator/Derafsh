using System.Diagnostics;
using System.Text.Json.Nodes;
using Derafsh.Client.Services;

namespace Derafsh.Client
{
    // کانفیگ xray اتصال اصلی؛ صفحه و سرویس VPN (نگهبان اتصال) هر دو از همین می‌سازن
    public static class SessionConfig
    {
        private static readonly ConfigService s_service = new();

        // اتصال اصلی (پورت SessionPort) سایت‌های ایرانی رو مستقیم می‌فرسته، اگه گزینه‌اش روشنه؛ تست‌ها نه
        public static async Task<string> BuildAsync(string link, int port)
        {
            bool iranDirect = port == XrayProc.SessionPort && AppBypass.IranDirect;
            if (iranDirect) await LoadIranRangesAsync();
            string json = s_service.GenerateXrayConfig(link, port, iranDirect);
#if ANDROID
            json = await ConfigService.ResolveServerAddressAsync(json);
#endif
            return json;
        }

        // IPهای ایران (Resources/Raw/iran_ip.txt، از geoip.dat خود xray)
        private static async Task LoadIranRangesAsync()
        {
            if (ConfigService.IranIpRanges.Count > 0) return;
            try
            {
                using var stream = await FileSystem.OpenAppPackageFileAsync("iran_ip.txt");
                using var reader = new StreamReader(stream);
                ConfigService.IranIpRanges = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            catch { }
        }
    }

    // تست همزمان چند سرور با یه xray: برای هر سرور یه پورت SOCKS که فقط به همون سرور می‌ره (مثل تست لیست روی گیت‌هاب).
    // نگهبان اتصال با این، زاپاس‌ها رو تازه نگه می‌داره و موقع قطعی چندتا سرور رو با هم امتحان می‌کنه، نه یکی یکی
    // (قبلاً هر زاپاس مرده تا ۲۰ ثانیه معطلی داشت).
    public static class ServerProbe
    {
        // سرورهایی که جواب دادن، با زمان جوابشون (میلی‌ثانیه). needDownload: این‌ها علاوه بر جواب، باید یه کم هم دانلود
        // کنن (سرورهایی که روی این اینترنت تست سرعت نشدن؛ بعضی سرورها جواب کوچک می‌دن ولی هیچی دانلود نمی‌کنن)
        public static async Task<Dictionary<string, int>> TestManyAsync(IReadOnlyList<string> links, int timeoutSeconds,
                                                                         ISet<string>? needDownload, CancellationToken token)
        {
            var result = new Dictionary<string, int>();
            // با هم، چون روی اندروید آدرس هر سرور اینجا با DNS پیدا می‌شه
            var built = await Task.WhenAll(links.Select(async link =>
            {
                try
                {
                    var config = JsonNode.Parse(await SessionConfig.BuildAsync(link, 1080))!;
                    var proxy = config["outbounds"]!.AsArray().First(o => (string?)o!["tag"] == "proxy")!.AsObject();
                    return (link, outbound: (JsonObject?)proxy.DeepClone());
                }
                catch { return (link, outbound: (JsonObject?)null); }   // ساخته نشد (یا آدرس سرور پیدا نشد): جواب نداده حساب می‌شه
            }));
            var outbounds = built.Where(x => x.outbound != null).Select(x => (x.link, x.outbound!)).ToList();
            await RunAsync(outbounds, timeoutSeconds, needDownload, result, token);
            return result;
        }

        private static async Task RunAsync(List<(string link, JsonObject outbound)> batch, int timeoutSeconds, ISet<string>? needDownload,
                                           Dictionary<string, int> result, CancellationToken token)
        {
            if (batch.Count == 0 || token.IsCancellationRequested) return;
            var ports = new List<int>();
            XrayProc? proc = null;
            string path = Path.Combine(XrayProc.WorkDir, $"probe_{Guid.NewGuid():N}.json");
            bool started = false;
            try
            {
                for (int i = 0; i < batch.Count; i++) ports.Add(XrayProc.ReservePort());
                var inbounds = new JsonArray();
                var outs = new JsonArray();
                var rules = new JsonArray();
                for (int i = 0; i < batch.Count; i++)
                {
                    inbounds.Add(new JsonObject
                    {
                        ["tag"] = $"i{i}", ["port"] = ports[i], ["listen"] = "127.0.0.1", ["protocol"] = "socks",
                        ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = false },
                    });
                    var outbound = (JsonObject)batch[i].outbound.DeepClone();
                    outbound["tag"] = $"o{i}";
                    outs.Add(outbound);
                    rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray($"i{i}"), ["outboundTag"] = $"o{i}" });
                }
                var config = new JsonObject
                {
                    ["log"] = new JsonObject { ["loglevel"] = "none" },
                    ["inbounds"] = inbounds,
                    ["outbounds"] = outs,
                    ["routing"] = new JsonObject { ["rules"] = rules },
                };
                await File.WriteAllTextAsync(path, config.ToJsonString(), token);
                proc = XrayProc.Start(path, isTest: true);
                started = await XrayProc.WaitForPortAsync(ports[^1], 6000) && await XrayProc.WaitForPortAsync(ports[0], 1000);
                if (started)
                {
                    var answers = await Task.WhenAll(batch.Select((x, i) => ProbeAsync(ports[i], timeoutSeconds, needDownload?.Contains(x.link) == true, token)));
                    for (int i = 0; i < batch.Count; i++) if (answers[i] > 0) result[batch[i].link] = answers[i];
                }
            }
            catch { }
            finally
            {
                proc?.Dispose();
                foreach (var p in ports) XrayProc.ReleasePort(p);
                try { File.Delete(path); } catch { }
                try { File.Delete(Path.ChangeExtension(path, ".log")); } catch { }
            }
            // xray با این دسته روشن نشد: یکی از کانفیگ‌ها خرابه؛ نصف نصف، تا خرابه تنها بمونه
            if (!started && batch.Count > 1 && !token.IsCancellationRequested)
            {
                int half = batch.Count / 2;
                await RunAsync(batch.Take(half).ToList(), timeoutSeconds, needDownload, result, token);
                await RunAsync(batch.Skip(half).ToList(), timeoutSeconds, needDownload, result, token);
            }
        }

        // درخواست اول کل مسیر رو باز می‌کنه (اتصال و رمزنگاری تا سرور)؛ زمانش «پینگ» این تست حساب می‌شه
        private static async Task<int> ProbeAsync(int port, int timeoutSeconds, bool needDownload, CancellationToken token)
        {
            try
            {
                using var client = XrayProc.SocksClient(port, TimeSpan.FromSeconds(timeoutSeconds));
                var sw = Stopwatch.StartNew();
                using var res = await client.GetAsync("http://www.gstatic.com/generate_204", token);
                if (res.StatusCode != System.Net.HttpStatusCode.NoContent) return 0;
                int ms = Math.Max(1, (int)sw.ElapsedMilliseconds);
                if (needDownload && await ConnectionGuard.MeasureSpeedAsync(port, maxSeconds: 3, maxBytes: 64_000) <= 0) return 0;
                return ms;
            }
            catch { return 0; }
        }
    }
}
