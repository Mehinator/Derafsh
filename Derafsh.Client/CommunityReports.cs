using System.Text;
using System.Text.Json;

namespace Derafsh.Client
{
    // گزارش ناشناس و تجربه‌ی بقیه‌ی کاربرها روی همین اینترنت (۲۲ نوامبر ۲۰۲۶).
    //
    //   • گزارش: نتیجه‌ی تست سرورهای لیست آنلاین (نه کانفیگ‌های شخصی) روی همین اینترنت جمع می‌شه: شماره‌ی شرکت اینترنت
    //     (ASN، مثلاً ۴۴۲۴۴ ایرانسل)، برای هر سرور فقط هش کوتاه لینکش (NetworkHistory.KeyOf)، جواب داد یا نه، و سرعت.
    //     وقتی وصلیم، از داخل خود اتصال (SOCKS محلی xray) به ربات درفش روی Cloudflare (bot/worker.js) فرستاده می‌شه؛ پس
    //     اونجا IP کاربر دیده نمی‌شه، و workers.dev که توی ایران فیلتره هم مهم نیست. با گزینه‌ی تنظیمات خاموش می‌شه.
    //   • تجربه‌ی بقیه: جمع‌آوری (collector.py، iran_reports.py) از این گزارش‌ها derafsh_networks.json رو همراه لیست می‌سازه:
    //     برای هر اینترنت، سرورهایی که روی همون اینترنت برای بقیه کار کردن (good، بهترها اول) و اون‌هایی که پشت هم جواب
    //     ندادن (bad). تست اول سراغ good‌ها می‌ره و bad‌ها آخر صف.
    public static class CommunityReports
    {
        private const string ReportUrl = "https://derafsh-bot.ybjsghsjhsj.workers.dev/report";
        private const int MaxPerReport = 80, MaxReportsPerSend = 4, MaxPerNetwork = 400, MaxNetworks = 4;
        private static readonly TimeSpan SendEvery = TimeSpan.FromMinutes(10);

        private static readonly object s_lock = new();
        private static Dictionary<string, Dictionary<string, int[]>>? s_pending;   // اینترنت (AS44244) ← هش سرور ← [جواب داد، سرعت]
        private static DateTime s_lastSent = DateTime.MinValue;
        private static bool s_sending;

        private static Dictionary<string, double>? s_hints;
        private static string s_hintsNetwork = "";
        private static DateTime s_hintsStamp;

        private static string PendingPath => Path.Combine(FileSystem.AppDataDirectory, "reports_pending.json");
        public static string HintsPath => Path.Combine(FileSystem.AppDataDirectory, "networks.json");

        public static bool Enabled
        {
            get => Preferences.Default.Get("community_reports", true);
            set
            {
                Preferences.Default.Set("community_reports", value);
                if (value) return;
                lock (s_lock) s_pending = new();
                try { File.Delete(PendingPath); } catch { }
            }
        }

        private static Dictionary<string, Dictionary<string, int[]>> Pending
        {
            get
            {
                if (s_pending != null) return s_pending;
                try { if (File.Exists(PendingPath)) s_pending = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int[]>>>(File.ReadAllText(PendingPath)); } catch { }
                return s_pending ??= new();
            }
        }

        // یه نتیجه‌ی تست از لیست آنلاین روی همین اینترنت؛ برای هر سرور فقط آخرین نتیجه
        public static void Add(string config, bool ok, int speedKBps)
        {
            string net = NetworkHistory.NetworkKey;
            if (!Enabled || !net.StartsWith("AS")) return;
            string key = NetworkHistory.KeyOf(config);
            lock (s_lock)
            {
                var pending = Pending;
                if (!pending.TryGetValue(net, out var rows))
                {
                    if (pending.Count >= MaxNetworks) return;
                    pending[net] = rows = new();
                }
                if (rows.Count >= MaxPerNetwork && !rows.ContainsKey(key)) return;
                rows[key] = new[] { ok ? 1 : 0, ok ? Math.Max(0, speedKBps) : 0 };
            }
        }

        public static void Save()
        {
            string json;
            lock (s_lock) json = JsonSerializer.Serialize(Pending);
            try { File.WriteAllText(PendingPath, json); } catch { }
        }

        // فقط وقتی وصلیم (از داخل اتصال)؛ حداکثر هر ۱۰ دقیقه یه بار. سرورهایی که جواب دادن اول.
        public static async Task SendAsync()
        {
            if (!Enabled || s_sending || DateTime.UtcNow - s_lastSent < SendEvery) return;
            var batches = new List<(string net, List<KeyValuePair<string, int[]>> rows)>();
            lock (s_lock)
            {
                foreach (var (net, rows) in Pending)
                    foreach (var chunk in rows.OrderByDescending(r => r.Value[0]).Chunk(MaxPerReport))
                        if (batches.Count < MaxReportsPerSend) batches.Add((net, chunk.ToList()));
            }
            if (batches.Count == 0) return;
            s_sending = true;
            s_lastSent = DateTime.UtcNow;
            try
            {
                string app = typeof(CommunityReports).Assembly.GetName().Version?.ToString(3) ?? "";
                using var client = XrayProc.SocksClient(XrayProc.SessionPort, TimeSpan.FromSeconds(20));
                foreach (var (net, rows) in batches)
                {
                    if (!long.TryParse(net.AsSpan(2), out long asn)) { Forget(net, rows); continue; }
                    string body = JsonSerializer.Serialize(new
                    {
                        v = 1,
                        asn,
                        app,
                        r = rows.Select(r => new object[] { r.Key, r.Value[0], r.Value[1] }),
                    });
                    using var res = await client.PostAsync(ReportUrl, new StringContent(body, Encoding.UTF8, "application/json"));
                    if ((int)res.StatusCode >= 500) break;   // سرور گزارش‌ها مشکل داره: دفعه‌ی بعد
                    Forget(net, rows);                        // رسید (یا ردش کرد، که دوباره فرستادنش فایده نداره)
                }
            }
            catch { }   // اتصال قطع شد یا جواب نداد: دفعه‌ی بعد
            finally
            {
                s_sending = false;
                Save();
            }
        }

        private static void Forget(string net, List<KeyValuePair<string, int[]>> sent)
        {
            lock (s_lock)
            {
                if (!Pending.TryGetValue(net, out var rows)) return;
                foreach (var (key, value) in sent)
                    if (rows.TryGetValue(key, out var now) && now.SequenceEqual(value)) rows.Remove(key);   // نتیجه‌ی تازه‌تر می‌مونه
                if (rows.Count == 0) Pending.Remove(net);
            }
        }

        // تجربه‌ی بقیه روی همین اینترنت: از +۳ (بهترینِ good) تا +۱، و −۳ برای bad
        public static double Score(string config)
        {
            var hints = Hints();
            return hints.Count > 0 && hints.TryGetValue(NetworkHistory.KeyOf(config), out var s) ? s : 0;
        }

        private static Dictionary<string, double> Hints()
        {
            string net = NetworkHistory.NetworkKey;
            DateTime stamp = File.Exists(HintsPath) ? File.GetLastWriteTimeUtc(HintsPath) : default;
            if (s_hints != null && s_hintsNetwork == net && s_hintsStamp == stamp) return s_hints;
            var map = new Dictionary<string, double>();
            try
            {
                if (stamp != default)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(HintsPath));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(net, out var e) && e.ValueKind == JsonValueKind.Object)
                    {
                        if (e.TryGetProperty("bad", out var bad) && bad.ValueKind == JsonValueKind.Array)
                            foreach (var k in bad.EnumerateArray())
                                if (k.GetString() is { } key) map[key] = -3;
                        if (e.TryGetProperty("good", out var good) && good.ValueKind == JsonValueKind.Array)
                        {
                            int n = good.GetArrayLength(), i = 0;
                            foreach (var k in good.EnumerateArray())
                                if (k.GetString() is { } key) map[key] = 3 - 2.0 * i++ / Math.Max(1, n);
                        }
                    }
                }
            }
            catch { }
            s_hints = map;
            s_hintsNetwork = net;
            s_hintsStamp = stamp;
            return map;
        }
    }
}
