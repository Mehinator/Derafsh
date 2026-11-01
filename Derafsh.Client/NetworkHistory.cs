using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Derafsh.Client
{
    // حافظه‌ی هر اینترنت: کدوم سرورها روی همین اینترنت (ایرانسل، همراه اول، شاتل، وای‌فای خونه، ...) کار کردن و کدوم‌ها نه.
    // اینترنت از روی شماره‌ی شرکت ارائه‌دهنده (ASN) شناخته می‌شه. دفعه‌ی بعد اول سرورهایی تست و انتخاب می‌شن که روی همین
    // اینترنت خوب بودن، و اون‌هایی که اخیراً چند بار جواب ندادن آخر صف می‌رن. فقط روی همین دستگاه ذخیره می‌شه.
    public static class NetworkHistory
    {
        public sealed class Entry
        {
            public int Ok { get; set; }
            public int Fail { get; set; }
            public long LastOk { get; set; }     // UTC ticks
            public long LastFail { get; set; }
            public int Speed { get; set; }       // KB/s، میانگین نرم
        }

        private const int MaxPerNetwork = 600;
        private static Dictionary<string, Dictionary<string, Entry>>? s_data;
        private static int s_unsaved;

        public static string NetworkKey { get; private set; } = "unknown";   // مثلاً AS44244
        public static string NetworkName { get; private set; } = "";          // مثلاً Irancell
        private static DateTime s_detectedAt = DateTime.MinValue;

        private static string FilePath => Path.Combine(FileSystem.AppDataDirectory, "network_history.json");

        private static Dictionary<string, Dictionary<string, Entry>> Data
        {
            get
            {
                if (s_data != null) return s_data;
                try { if (File.Exists(FilePath)) s_data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Entry>>>(File.ReadAllText(FilePath)); } catch { }
                return s_data ??= new();
            }
        }

        // کلید سرور: خود لینک بدون اسم (#...)؛ هش کوتاه، تا لینک‌ها توی فایل نمونن
        public static string KeyOf(string config)
        {
            string body = config.Split('#')[0].Trim();
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(body)), 0, 8);
        }

        private static Dictionary<string, Entry> Current
        {
            get
            {
                if (!Data.TryGetValue(NetworkKey, out var net)) Data[NetworkKey] = net = new();
                return net;
            }
        }

        public static void Record(string config, bool ok, int speedKBps = 0)
        {
            var net = Current;
            string key = KeyOf(config);
            if (!net.TryGetValue(key, out var e)) net[key] = e = new Entry();
            long now = DateTime.UtcNow.Ticks;
            if (ok)
            {
                e.Ok++;
                e.LastOk = now;
                if (speedKBps > 0) e.Speed = e.Speed == 0 ? speedKBps : (e.Speed * 2 + speedKBps) / 3;
            }
            else
            {
                e.Fail++;
                e.LastFail = now;
            }
            if (net.Count > MaxPerNetwork)
                foreach (var old in net.OrderBy(kv => Math.Max(kv.Value.LastOk, kv.Value.LastFail)).Take(net.Count - MaxPerNetwork).Select(kv => kv.Key).ToList())
                    net.Remove(old);
            if (++s_unsaved >= 20) Save();
        }

        // امتیاز روی همین اینترنت: مثبت = قبلاً خوب کار کرده، منفی = اخیراً جواب نداده. نتیجه‌های قدیمی کم‌اثر می‌شن.
        public static double Score(string config)
        {
            if (!Current.TryGetValue(KeyOf(config), out var e)) return 0;
            double okDays = e.LastOk == 0 ? 99 : (DateTime.UtcNow.Ticks - e.LastOk) / (double)TimeSpan.TicksPerDay;
            double failDays = e.LastFail == 0 ? 99 : (DateTime.UtcNow.Ticks - e.LastFail) / (double)TimeSpan.TicksPerDay;
            double score = Math.Min(e.Ok, 10) * Math.Exp(-okDays / 7) - Math.Min(e.Fail, 10) * 1.5 * Math.Exp(-failDays / 2);
            if (e.Speed >= 150 && okDays < 7) score += 1;
            return score;
        }

        public static int LastSpeed(string config) => Current.TryGetValue(KeyOf(config), out var e) ? e.Speed : 0;

        public static void Save()
        {
            s_unsaved = 0;
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Data)); } catch { }
        }

        // کدوم اینترنت؟ مستقیم (نه از راه VPN یا پروکسی) از ipwho.is؛ هر ۱۰ دقیقه یه بار کافیه
        private static readonly Dictionary<long, string> KnownIsps = new()
        {
            [44244] = "Irancell", [197207] = "Hamrah-e Aval", [57218] = "RighTel", [58224] = "Mokhaberat",
            [31549] = "Shatel", [16322] = "ParsOnline", [43754] = "Asiatech", [50810] = "Mobinnet",
        };

        // اگه تشخیص نشد (سرویس جواب نداد)، آخرین اینترنت شناخته‌شده؛ وگرنه تاریخچه زیر «unknown» می‌رفت و استفاده نمی‌شد
        public static async Task DetectAsync(bool force = false)
        {
            if (!force && DateTime.UtcNow - s_detectedAt < TimeSpan.FromMinutes(10)) return;
            var (asn, isp) = await LookupAsync("https://ipwho.is/?fields=connection", r =>
                r.TryGetProperty("connection", out var c) ? (Num(c, "asn"), Str(c, "isp")) : (0, null));
            if (asn == 0)
                (asn, isp) = await LookupAsync("https://api.ip.sb/geoip", r => (Num(r, "asn"), Str(r, "isp") ?? Str(r, "asn_organization")));
            if (asn != 0)
            {
                NetworkKey = $"AS{asn}";
                NetworkName = KnownIsps.TryGetValue(asn, out var known) ? known : isp ?? "";
                s_detectedAt = DateTime.UtcNow;
                Preferences.Default.Set("last_network", NetworkKey);
                Preferences.Default.Set("last_network_name", NetworkName);
            }
            else if (NetworkKey == "unknown")
            {
                NetworkKey = Preferences.Default.Get("last_network", "unknown");
                NetworkName = Preferences.Default.Get("last_network_name", "");
            }
        }

        private static async Task<(long asn, string? isp)> LookupAsync(string url, Func<JsonElement, (long, string?)> read)
        {
            try
            {
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(6) };
                using var doc = JsonDocument.Parse(await client.GetStringAsync(url));
                return read(doc.RootElement);
            }
            catch { return (0, null); }
        }

        private static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static long Num(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    }
}
