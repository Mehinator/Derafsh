using System.Diagnostics;
using System.Text.Json.Nodes;
using Derafsh.Client.Services;

namespace Derafsh.Client
{
    // کانفیگ xray اتصال اصلی؛ صفحه و سرویس VPN (نگهبان اتصال) هر دو از همین می‌سازن
    public static class SessionConfig
    {
        private static readonly ConfigService s_service = new();

        // تکه‌تکه کردن TLS (تنظیمات). تست‌ها هم با همین، تا نتیجه‌شون همون اتصال واقعی باشه. پیش‌فرض خاموش: روی همراه اول
        // (تست ۱۳ نوامبر ۲۰۲۶ از خود گوشی) با هر تنظیمی کانفیگ‌های TLS و Reality رو خراب می‌کرد (۱۴ سالم بدون، ۶ تا ۱۰ با)
        public static bool FragmentTls
        {
            get => Preferences.Default.Get("tls_fragment", false);
            set => Preferences.Default.Set("tls_fragment", value);
        }

        // اتصال اصلی (پورت SessionPort) سایت‌های ایرانی رو مستقیم می‌فرسته، اگه گزینه‌اش روشنه؛ تست‌ها نه
        public static async Task<string> BuildAsync(string link, int port)
        {
            bool iranDirect = port == XrayProc.SessionPort && AppBypass.IranDirect;
            if (iranDirect) await LoadIranRangesAsync();
            string json = s_service.GenerateXrayConfig(link, port, iranDirect, FragmentTls);
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
        // خود سرور ویدیوی یوتیوب (ویدیو از googlevideo می‌آد، نه youtube.com). تست روی گوشی، ۱۳ نوامبر ۲۰۲۶: سرورهایی که درفش
        // انتخاب کرده بود youtube.com رو باز می‌کردن ولی googlevideo و ytimg رو بسته بودن، و ویدیو پخش نمی‌شد.
        public const string VideoUrl = "https://rr1---sn-4g5ednsz.googlevideo.com/generate_204";

        // سرورهایی که جواب دادن، با زمان جوابشون (میلی‌ثانیه). needDownload: این‌ها علاوه بر جواب، باید یه کم هم دانلود
        // کنن (سرورهایی که روی این اینترنت تست سرعت نشدن؛ بعضی سرورها جواب کوچک می‌دن ولی هیچی دانلود نمی‌کنن)
        // video: googlevideo هم جواب داد (ویدیوی یوتیوب پخش می‌شه)
        public static async Task<Dictionary<string, (int ping, bool video)>> TestManyAsync(IReadOnlyList<string> links, int timeoutSeconds,
                                                                                    ISet<string>? needDownload, CancellationToken token)
        {
            var result = new Dictionary<string, (int ping, int speed, bool youtube)>();
            await RunAsync(await BuildAsync(links), async (port, link) =>
            {
                var (ping, video) = await ProbeAsync(port, timeoutSeconds, needDownload?.Contains(link) == true, token);
                return (ping, 0, video);
            }, result, token);
            return result.Where(x => x.Value.ping > 0).ToDictionary(x => x.Key, x => (x.Value.ping, x.Value.youtube));
        }

        // تست کامل چند سرور با هم (برای پیدا کردن سرورهای سالم روی همین اینترنت): پینگ و سرعت دانلود هر کدوم.
        // روی همراه اول فقط حدود ۵٪ سرورهای لیست جواب دادن و بیشترشون حدود ۵ کیلوبایت بر ثانیه بودن؛ باید کل لیست
        // تست بشه تا چندتای سریع پیدا بشن. قبلاً هر سرور xray خودش رو داشت و فقط ۱۲۰ تای اول تست می‌شد.
        // youtube: یوتیوب از داخلش باز شد (خیلی از سرورهای رایگان یوتیوب رو بستن و ویدیو باهاشون پخش نمی‌شه)
        public static async Task<Dictionary<string, (int ping, int speed, bool youtube)>> MeasureManyAsync(IReadOnlyList<string> links, CancellationToken token)
        {
            var result = new Dictionary<string, (int ping, int speed, bool youtube)>();
            using var speedSlots = new SemaphoreSlim(3);   // دانلودها با هم نه، تا سرعت‌ها همدیگه رو کم نشون ندن
            await RunAsync(await BuildAsync(links), (port, _) => MeasureAsync(port, speedSlots, token), result, token);
            return result;
        }

        private static async Task<List<(string link, JsonObject outbound)>> BuildAsync(IReadOnlyList<string> links)
        {
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
            return built.Where(x => x.outbound != null).Select(x => (x.link, x.outbound!)).ToList();
        }

        // test(پورت، لینک): نتیجه‌ی یه سرور؛ ping صفر یعنی جواب نداد
        private static async Task RunAsync(List<(string link, JsonObject outbound)> batch, Func<int, string, Task<(int ping, int speed, bool youtube)>> test,
                                           Dictionary<string, (int ping, int speed, bool youtube)> result, CancellationToken token)
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
                // کانفیگ‌هایی که با تکه‌تکه کردن TLS ساخته شدن، خروجی «fragment» رو لازم دارن (یکی برای همه بسه)
                if (outs.Any(o => (string?)o!["streamSettings"]?["sockopt"]?["dialerProxy"] == "fragment"))
                    outs.Add(ConfigService.FragmentOutbound());
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
                    var answers = await Task.WhenAll(batch.Select((x, i) => test(ports[i], x.link)));
                    lock (result) for (int i = 0; i < batch.Count; i++) result[batch[i].link] = answers[i];
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
                await RunAsync(batch.Take(half).ToList(), test, result, token);
                await RunAsync(batch.Skip(half).ToList(), test, result, token);
            }
        }

        // مثل تست تکی صفحه: درخواست اول تونل رو باز می‌کنه، دوتا درخواست بعدی پینگ رو می‌دن، بعد سرعت دانلود.
        // سروری که جواب کوچک می‌ده ولی دانلود نمی‌کنه، سالم حساب نمی‌شه.
        private static async Task<(int ping, int speed, bool youtube)> MeasureAsync(int port, SemaphoreSlim speedSlots, CancellationToken token)
        {
            const string url = "https://www.gstatic.com/generate_204";
            try
            {
                using var client = XrayProc.SocksClient(port, TimeSpan.FromSeconds(8));
                using (var warm = await client.GetAsync(url, token))
                    if (warm.StatusCode != System.Net.HttpStatusCode.NoContent) return (0, 0, false);
                int ping = int.MaxValue;
                for (int i = 0; i < 2; i++)
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        cts.CancelAfter(TimeSpan.FromSeconds(5));
                        var sw = Stopwatch.StartNew();
                        using var res = await client.GetAsync(url, cts.Token);
                        if (res.StatusCode == System.Net.HttpStatusCode.NoContent) ping = Math.Min(ping, Math.Max(1, (int)sw.ElapsedMilliseconds));
                    }
                    catch { }
                }
                if (ping == int.MaxValue) return (0, 0, false);
                await speedSlots.WaitAsync(token);
                int speed;
                try { speed = await ConnectionGuard.MeasureSpeedAsync(port, maxSeconds: 5, maxBytes: 200_000); }
                finally { speedSlots.Release(); }
                if (speed <= 0) return (0, 0, false);
                bool youtube = false;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(TimeSpan.FromSeconds(8));
                    using var yt = await client.GetAsync(VideoUrl, cts.Token);
                    youtube = yt.StatusCode == System.Net.HttpStatusCode.NoContent;
                }
                catch { }
                return (ping, speed, youtube);
            }
            catch { return (0, 0, false); }
        }

        // درخواست اول کل مسیر رو باز می‌کنه (اتصال و رمزنگاری تا سرور)؛ زمانش «پینگ» این تست حساب می‌شه. همزمان googlevideo:
        // سروری که فقط اون رو باز نمی‌کنه هنوز زنده‌ست، ولی ویدیو باهاش پخش نمی‌شه
        private static async Task<(int ping, bool video)> ProbeAsync(int port, int timeoutSeconds, bool needDownload, CancellationToken token)
        {
            async Task<int> Get204(string url, int seconds)
            {
                try
                {
                    using var client = XrayProc.SocksClient(port, TimeSpan.FromSeconds(seconds));
                    var sw = Stopwatch.StartNew();
                    using var res = await client.GetAsync(url, token);
                    return res.StatusCode == System.Net.HttpStatusCode.NoContent ? Math.Max(1, (int)sw.ElapsedMilliseconds) : 0;
                }
                catch { return 0; }
            }
            var plain = Get204("http://www.gstatic.com/generate_204", timeoutSeconds);
            var video = Get204(VideoUrl, timeoutSeconds + 2);
            int ms = await plain, videoMs = await video;
            if (ms == 0) ms = videoMs;
            if (ms == 0) return (0, false);
            if (needDownload && await ConnectionGuard.MeasureSpeedAsync(port, maxSeconds: 3, maxBytes: 64_000) <= 0) return (0, false);
            return (ms, videoMs > 0);
        }
    }
}
