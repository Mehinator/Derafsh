using System.Text.Json;

namespace Derafsh.Client
{
    // نگهبان اتصال: کانفیگ‌های رایگان زود قطع یا کند می‌شن. تا وقتی وصلیم:
    //   • هر ۱۰ ثانیه یه درخواست خیلی کوچک (جواب خالی 204) از داخل خود اتصال؛ دو بار بی‌جواب = قطع → زاپاس.
    //   • هر ۴۰ ثانیه (ویندوز: ۲ دقیقه) یه تست سرعت (۲۰۰ کیلوبایت)؛ دو بار کمتر از ۶۰ کیلوبایت بر ثانیه = کند (ویدیو
    //     وسطش می‌ایسته ولی درخواست کوچک هنوز جواب می‌ده) → زاپاس، اگه زاپاس موقع تست دست‌کم دو برابر و برای ویدیو سریع بوده.
    //     اگه ترافیک خودش داره تند رد می‌شه (مثلاً ویدیو خوب پخش می‌شه) یا گوشی یه دقیقه‌ست کاری نکرده، تست سرعت زده
    //     نمی‌شه تا اینترنت هدر نره.
    //   • دکمه‌ی «تعویض سرور» همین مسیر رو دستی صدا می‌زنه.
    //   اندروید: داخل سرویس VPN، تا با بسته بودن برنامه هم کار کنه؛ فقط موتور xray عوض می‌شه، VPN روشن می‌مونه.
    //   ویندوز: داخل صفحه‌ی اصلی؛ پروکسی سیستم دست نمی‌خوره.
    public sealed class ConnectionGuard : IDisposable
    {
        // سرور زاپاس: کانفیگ xray از قبل ساخته شده (ConfigPath) تا موقع قطعی معطل نشیم
        public sealed record Spare(string Name, string Link, string ConfigPath, int Speed = 0);

        // دلیل رفتن به زاپاس (برای پیغام به کاربر)
        public const string ReasonDown = "down", ReasonSlow = "slow", ReasonManual = "manual";

        private const int CheckEverySeconds = 10, RecheckSeconds = 3, FailuresBeforeSwitch = 2;
        private const int SpeedCheckEvery = 4, FlowingSpeed = 100;   // ۴ × ۱۰ ثانیه؛ کیلوبایت بر ثانیه

        private readonly Func<Spare, Task<bool>> _switchTo;
        private readonly Func<IReadOnlyList<Spare>> _spares;
        private readonly Func<int>? _currentDownload;   // سرعت دانلود فعلی ترافیک خود کاربر، اگه معلومه (اندروید)
        private readonly Func<bool>? _recentlyActive;   // توی یه دقیقه‌ی اخیر چیزی دانلود شده (اندروید)
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _switchLock = new(1, 1);

        public event Action<Spare, string>? Switched;   // به این زاپاس رفت و جواب داد (دلیل)
        public event Action<string>? Lost;              // باید عوض می‌شد ولی هیچ زاپاسی جواب نداد (نگهبان به کارش ادامه می‌ده)

        public ConnectionGuard(Func<Spare, Task<bool>> switchTo, Func<IReadOnlyList<Spare>> spares, Func<int>? currentDownloadKbps = null,
                               Func<bool>? recentlyActive = null)
        {
            _switchTo = switchTo;
            _spares = spares;
            _currentDownload = currentDownloadKbps;
            _recentlyActive = recentlyActive;
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            int failures = 0, slow = 0, tick = 0;
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(failures == 0 ? CheckEverySeconds : RecheckSeconds), token); }
                catch (OperationCanceledException) { return; }

                if (!await ProbeAsync(8))   // سرورهای دور گاهی چند ثانیه طول می‌کشن؛ «کند» با تست سرعت جدا سنجیده می‌شه
                {
                    if (++failures < FailuresBeforeSwitch) continue;
                    await SwitchToNextAsync(ReasonDown);
                    failures = slow = 0;
                    continue;
                }
                failures = 0;
                // ویندوز آمار ترافیک نداره، پس تست سرعت کمتر (هر ۲ دقیقه) تا اینترنت هدر نره
                if (++tick % (_currentDownload == null ? SpeedCheckEvery * 3 : SpeedCheckEvery) != 0) continue;

                if (_currentDownload != null)
                {
                    // ترافیک خود کاربر تند رد می‌شه: سالمه، تست سرعت لازم نیست
                    if (_currentDownload() >= FlowingSpeed) { slow = 0; continue; }
                    // گوشی کاری نمی‌کنه (مثلاً صفحه خاموشه): کندی مهم نیست و تست سرعت فقط اینترنت کاربر رو هدر می‌ده
                    if (_recentlyActive?.Invoke() == false) { slow = 0; continue; }
                }
                int kbps = await MeasureSpeedAsync(XrayProc.SessionPort, maxSeconds: 5, maxBytes: 200_000);
                if (kbps >= MainPage.OkSpeed) { slow = 0; continue; }
                if (++slow < 2) continue;
                // دو بار پشت سر هم کندتر از چیزی که ویدیو لازم داره: فقط اگه زاپاسی هست که موقع تست واقعاً سریع‌تر بوده
                if (_spares().Any(x => x.Speed == 0 || x.Speed >= Math.Max(kbps * 2, MainPage.VideoSpeed))) await SwitchToNextAsync(ReasonSlow);
                slow = 0;
            }
        }

        // کانفیگ سروری که الان وصله؛ بعد از هر تعویض موفق، کانفیگ زاپاس اینجا کپی می‌شه
        public static string CurrentConfigPath => Path.Combine(XrayProc.WorkDir, "config.json");

        // رفتن به اولین زاپاسی که جواب بده (قطعی، کندی، یا دکمه‌ی «تعویض سرور»).
        // اگه هیچ‌کدوم جواب نداد، موتور به همون سرور قبلی برمی‌گرده تا کاربر روی یه زاپاس مرده نمونه.
        public async Task<bool> SwitchToNextAsync(string reason)
        {
            if (!await _switchLock.WaitAsync(0)) return false;   // همین الان داره عوض می‌کنه
            try
            {
                bool engineChanged = false;
                foreach (var spare in _spares())
                {
                    if (_cts.IsCancellationRequested) return false;
                    try
                    {
                        engineChanged = true;
                        if (await _switchTo(spare) && (await ProbeAsync(8) || await ProbeAsync(8)))
                        {
                            try { File.Copy(spare.ConfigPath, CurrentConfigPath, overwrite: true); } catch { }
                            Switched?.Invoke(spare, reason);
                            return true;
                        }
                    }
                    catch { }
                }
                if (engineChanged && File.Exists(CurrentConfigPath))
                    try { await _switchTo(new Spare("", "", CurrentConfigPath)); } catch { }
                Lost?.Invoke(reason);
                return false;
            }
            finally { _switchLock.Release(); }
        }

        // سرعت دانلود از راه یه پورت SOCKS (تست سرور یا خود اتصال)، به کیلوبایت بر ثانیه. زمان از اولین بایت حساب می‌شه، تا
        // معطلی شروع اتصال سرعت رو کم نشون نده. ۰ یعنی هیچی نیومد.
        // اگه از کلادفلر چیزی نیومد، یه بار از jsDelivr (لیست اشتراک خود درفش، حدود ۶۵۰ کیلوبایت): بعضی سرورهای رایگان
        // (مثلاً روی Workers کلادفلر) به سایت‌های کلادفلر وصل نمی‌شن ولی بقیه‌ی اینترنت رو خوب رد می‌کنن.
        public static async Task<int> MeasureSpeedAsync(int socksPort, int maxSeconds = 6, int maxBytes = 300_000)
        {
            int kbps = await DownloadSpeedAsync(socksPort, "https://speed.cloudflare.com/__down?bytes=" + maxBytes, maxSeconds, maxBytes);
            if (kbps == 0) kbps = await DownloadSpeedAsync(socksPort, "https://cdn.jsdelivr.net/gh/Mehinator/Derafsh@main/derafsh_sub.txt", maxSeconds, maxBytes);
            return kbps;
        }

        private static async Task<int> DownloadSpeedAsync(int socksPort, string url, int maxSeconds, int maxBytes)
        {
            long total = 0;
            System.Diagnostics.Stopwatch? sw = null;
            try
            {
                using var client = XrayProc.SocksClient(socksPort, TimeSpan.FromSeconds(maxSeconds + 8));
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(maxSeconds + 6));
                using var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!res.IsSuccessStatusCode) return 0;
                await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
                var buf = new byte[16384];
                while (total < maxBytes)
                {
                    int n = await stream.ReadAsync(buf, cts.Token);
                    if (n <= 0) break;
                    if (sw == null) sw = System.Diagnostics.Stopwatch.StartNew();   // اولین تیکه فقط زمان‌سنج رو راه می‌اندازه
                    else total += n;
                    if (sw.Elapsed.TotalSeconds >= maxSeconds) break;
                }
            }
            catch { }
            if (sw == null) return 0;
            return (int)(total / 1024.0 / Math.Max(0.05, sw.Elapsed.TotalSeconds));
        }

        // یه درخواست کوچک از داخل اتصال؛ فقط جواب 204 قبوله
        public static async Task<bool> ProbeAsync(int timeoutSeconds = 6)
        {
            try
            {
                using var client = XrayProc.SocksClient(XrayProc.SessionPort, TimeSpan.FromSeconds(timeoutSeconds));
                using var res = await client.GetAsync("http://www.gstatic.com/generate_204");
                return res.StatusCode == System.Net.HttpStatusCode.NoContent;
            }
            catch { return false; }
        }

        // IP و «شهر، کشور» سرور از راه خود اتصال؛ اول ipwho.is (شهر هم داره)، اگه جواب نداد api.country.is (فقط کشور)
        public static async Task<(string? ip, string? location)> ExitLocationAsync()
        {
            using var client = XrayProc.SocksClient(XrayProc.SessionPort, TimeSpan.FromSeconds(8));
            try
            {
                using var doc = JsonDocument.Parse(await client.GetStringAsync("https://ipwho.is/"));
                var r = doc.RootElement;
                if (r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    string? city = r.TryGetProperty("city", out var c) ? c.GetString() : null;
                    string? cc = r.TryGetProperty("country_code", out var k) ? k.GetString() : null;
                    return (r.GetProperty("ip").GetString(), string.IsNullOrEmpty(city) ? cc : $"{city}, {cc}");
                }
            }
            catch { }
            try
            {
                using var doc = JsonDocument.Parse(await client.GetStringAsync("https://api.country.is"));
                return (doc.RootElement.GetProperty("ip").GetString(), doc.RootElement.GetProperty("country").GetString());
            }
            catch { return (null, null); }
        }

        // لیست زاپاس‌ها روی دیسک (اندروید: صفحه می‌نویسه، سرویس VPN می‌خونه، حتی وقتی صفحه بسته‌ست)
        public static string SparesPath => Path.Combine(XrayProc.WorkDir, "spares.json");

        public static List<Spare> ReadSpares()
        {
            try { if (File.Exists(SparesPath)) return JsonSerializer.Deserialize<List<Spare>>(File.ReadAllText(SparesPath)) ?? new(); } catch { }
            return new();
        }

        public static void WriteSpares(List<Spare> spares)
        {
            try { File.WriteAllText(SparesPath, JsonSerializer.Serialize(spares)); } catch { }
        }

        // همه‌ی فایل‌های زاپاس (از جمله زاپاسی که بعد از قطعی، اتصال اصلی شده بود)
        public static void ClearSpares()
        {
            try { foreach (var f in Directory.GetFiles(XrayProc.WorkDir, "spare_*.json")) try { File.Delete(f); } catch { } } catch { }
            try { File.Delete(SparesPath); } catch { }
        }

        public void Dispose() => _cts.Cancel();
    }
}
