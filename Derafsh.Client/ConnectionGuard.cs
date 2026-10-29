using System.Text.Json;

namespace Derafsh.Client
{
    // نگهبان اتصال: کانفیگ‌های رایگان زود قطع می‌شن. تا وقتی وصلیم، هر چند ثانیه از داخل خود اتصال (SOCKS محلی)
    // یه درخواست خیلی کوچک (جواب خالی 204) فرستاده می‌شه؛ اگه دو بار پشت سر هم جواب نیومد، اتصال به سرور زاپاسی
    // که از قبل تست شده می‌ره و کاربر وصل می‌مونه.
    //   اندروید: داخل سرویس VPN، تا با بسته بودن برنامه هم کار کنه؛ فقط موتور xray عوض می‌شه، VPN روشن می‌مونه.
    //   ویندوز: داخل صفحه‌ی اصلی؛ پروکسی سیستم دست نمی‌خوره.
    public sealed class ConnectionGuard : IDisposable
    {
        // سرور زاپاس: کانفیگ xray از قبل ساخته شده (ConfigPath) تا موقع قطعی معطل نشیم
        public sealed record Spare(string Name, string Link, string ConfigPath);

        private const int CheckEverySeconds = 10, RecheckSeconds = 3, FailuresBeforeSwitch = 2;

        private readonly Func<Spare, Task<bool>> _switchTo;
        private readonly Func<IReadOnlyList<Spare>> _spares;
        private readonly CancellationTokenSource _cts = new();

        public event Action<Spare>? Switched;   // به این زاپاس رفت و جواب داد
        public event Action? Lost;              // اتصال قطعه و هیچ زاپاسی جواب نداد (نگهبان به کارش ادامه می‌ده)

        public ConnectionGuard(Func<Spare, Task<bool>> switchTo, Func<IReadOnlyList<Spare>> spares)
        {
            _switchTo = switchTo;
            _spares = spares;
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            int failures = 0;
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(failures == 0 ? CheckEverySeconds : RecheckSeconds), token); }
                catch (OperationCanceledException) { return; }

                if (await ProbeAsync()) { failures = 0; continue; }
                if (++failures < FailuresBeforeSwitch) continue;

                bool switched = false;
                foreach (var spare in _spares())
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        if (await _switchTo(spare) && (await ProbeAsync(8) || await ProbeAsync(8)))
                        {
                            Switched?.Invoke(spare);
                            switched = true;
                            break;
                        }
                    }
                    catch { }
                }
                if (!switched) Lost?.Invoke();
                failures = 0;
            }
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
