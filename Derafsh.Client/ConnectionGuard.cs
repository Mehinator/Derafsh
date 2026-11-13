using System.Text.Json;
using Derafsh.Client.Services;

namespace Derafsh.Client
{
    // نگهبان اتصال: کانفیگ‌های رایگان زود قطع یا کند می‌شن. تا وقتی وصلیم:
    //   • قطعی: وقتی برنامه‌ها چیزی می‌فرستن ولی هیچی برنمی‌گرده، یا دانلودی که داشت رد می‌شد یهو قطع شد (مثلاً ویدیو
    //     ایستاد)، همون لحظه یه درخواست خیلی کوچک از داخل اتصال؛ وگرنه هر ۱۰ ثانیه (گوشی بیکار: ۳۰ ثانیه). دو درخواست
    //     بی‌جواب پشت سر هم، به دو سایت مختلف = قطع. تا وقتی دانلود خود کاربر داره رد می‌شه، درخواستی لازم نیست.
    //   • زاپاس: خود نگهبان (نه صفحه، پس با بسته بودن برنامه هم) هر دو دقیقه چندتا از سرورهای سالم این اینترنت رو با یه
    //     xray با هم امتحان می‌کنه و آماده نگه می‌داره. قبلاً صفحه دوتا زاپاس نگه می‌داشت و موقع پخش ویدیو تستشون نمی‌کرد؛
    //     سرور که می‌مرد، زاپاس‌ها هم اغلب مرده بودن و اتصال مدت‌ها همون‌طور می‌موند.
    //   • عوض کردن: ۸ سرور با هم تست می‌شن (زاپاس‌های آماده اول) و اولین سالم جایگزین می‌شه؛ اگه هیچ‌کدوم، ۸ تای بعدی،
    //     تا ته سالم‌های این اینترنت و بعد بقیه‌ی لیست آنلاین (همون مکان انتخابی). کانفیگ‌های همون سرورِ مرده (همون آدرس)
    //     امتحان نمی‌شن. اگه باز هم چیزی پیدا نشد، هر ۲۰ ثانیه دوباره.
    //   • کندی: دانلود کاربر قطره‌قطره می‌آد (۵ تا ۶۰ کیلوبایت بر ثانیه)؛ تست سرعت، و دو بار کندتر از ویدیو با یه زاپاس
    //     سریع‌تر آماده → زاپاس.
    //   • دکمه‌ی «تعویض سرور» همین مسیر رو دستی صدا می‌زنه.
    //   اندروید: داخل سرویس VPN؛ فقط موتور xray عوض می‌شه، VPN روشن می‌مونه. ویندوز: داخل صفحه؛ پروکسی سیستم دست نمی‌خوره.
    public sealed class ConnectionGuard : IDisposable
    {
        // سرور تازه: کانفیگ xray ساخته شده (ConfigPath)
        public sealed record Spare(string Name, string Link, string ConfigPath, int Speed = 0);
        // یه سرور توی صف زاپاس‌ها. صفحه سالم‌های این اینترنت رو (بهترین اول) توی pool.json می‌نویسه؛ Speed: سرعتی که
        // روی همین اینترنت اندازه گرفته شده (۰: نه)
        public sealed record Candidate(string Name, string Link, int Speed = 0);

        // دلیل رفتن به سرور دیگه (برای پیغام به کاربر)
        // offline: اینترنت خود دستگاه قطعه (هیچ سروری و هیچ سایتی جواب نداد)؛ نگهبان منتظر می‌مونه و دوباره امتحان می‌کنه
        // video: سرور جواب می‌ده ولی ویدیو رو رد نمی‌کنه (googlevideo بسته، یا هر اتصال بعد از چندصد کیلوبایت می‌ایسته)
        public const string ReasonDown = "down", ReasonSlow = "slow", ReasonManual = "manual", ReasonOffline = "offline", ReasonVideo = "video";
        public const int FlowingSpeed = 40;   // کیلوبایت بر ثانیه: ترافیک خود کاربر داره رد می‌شه، پس سرور زنده‌ست

        private readonly Func<Spare, Task<bool>> _switchTo;
        private readonly Func<string?> _currentLink;
        private readonly Func<(long down, long up)>? _tunnelBytes;   // اندروید: بایت‌های رد شده از تونل
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _switchLock = new(1, 1);
        private readonly ConfigService _configs = new();
        private readonly Dictionary<string, string> _hosts = new();
        private readonly Dictionary<string, DateTime> _deadUntil = new();   // اخیراً جواب ندادن؛ تا این زمان امتحان نمی‌شن
        private readonly object _readyLock = new();
        private List<Candidate> _ready = new();                             // همین چند دقیقه پیش جواب دادن
        private Task _upkeep = Task.CompletedTask;
        private int _fileGen;

        public event Action<Spare, string>? Switched;   // به این سرور رفت و جواب داد (دلیل)
        public event Action<string>? Lost;              // باید عوض می‌شد ولی سروری جواب نداد (نگهبان به کارش ادامه می‌ده)

        public ConnectionGuard(Func<Spare, Task<bool>> switchTo, Func<string?> currentLink, Func<(long down, long up)>? tunnelBytes = null)
        {
            _switchTo = switchTo;
            _currentLink = currentLink;
            _tunnelBytes = tunnelBytes;
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            var token = _cts.Token;
            var history = new List<(DateTime at, long down, long up)>();
            var now = DateTime.UtcNow;
            DateTime lastProbe = now, lastSpeed = now, lastUpkeep = now.AddSeconds(-105), lostAt = DateTime.MinValue;
            DateTime lastVideo = now.AddSeconds(-105);   // اولین تست ویدیو ۱۵ ثانیه بعد از وصل شدن
            bool firstVideo = true;
            int slow = 0, videoFails = 0;
            bool lost = false;
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(1000, token); } catch (OperationCanceledException) { return; }
                now = DateTime.UtcNow;

                // ترافیک خود کاربر (فقط اندروید معلومه): ۳ ثانیه‌ی اخیر، ۱۰ تا ۴ ثانیه پیش، و یه دقیقه‌ی اخیر
                bool stats = _tunnelBytes != null, flowing = false, active = true, waiting = false, dropped = false;
                double trickle = 0;
                if (stats)
                {
                    var (d, u) = _tunnelBytes!();
                    history.Add((now, d, u));
                    history.RemoveAll(x => now - x.at > TimeSpan.FromSeconds(61));
                    double Rate(int fromAgo, int toAgo, bool down)
                    {
                        var a = At(history, now, fromAgo);
                        var b = At(history, now, toAgo);
                        double secs = (b.at - a.at).TotalSeconds;
                        return secs < 0.5 ? 0 : ((down ? b.down - a.down : b.up - a.up) / 1024.0 / secs);
                    }
                    double down3 = Rate(3, 0, true), up3 = Rate(3, 0, false);
                    flowing = down3 >= FlowingSpeed;
                    active = d - history[0].down >= 100 * 1024;
                    // برنامه‌ها می‌فرستن (درخواست، اتصال تازه) ولی چیزی برنمی‌گرده
                    waiting = history.Count >= 4 && up3 >= 0.3 && down3 < 1;
                    // داشت تند می‌اومد و یهو هیچی (ویدیو ایستاد؛ یا بافرش پر شد، که یه درخواست کوچک ضرری نداره)
                    dropped = history.Count >= 10 && Rate(10, 4, true) >= FlowingSpeed && down3 < 1;
                    trickle = Rate(20, 0, true);
                }

                if (flowing) { lastProbe = now; lost = false; }
                bool due = (now - lastProbe).TotalSeconds >= (active ? 10 : 30);
                bool urgent = (waiting || dropped) && (now - lastProbe).TotalSeconds >= 4;
                if (!flowing && (due || urgent))
                {
                    lastProbe = now;
                    if (!await ProbeAsync(5) && !await ProbeAsync(4, alternate: true))
                    {
                        // سرور جواب نمی‌ده؛ اگه دفعه‌ی قبل هیچ سروری پیدا نشد، هر ۲۰ ثانیه دوباره
                        if (!lost || (now - lostAt).TotalSeconds >= 20)
                        {
                            lost = !await SwitchToNextAsync(ReasonDown);
                            lostAt = DateTime.UtcNow;
                            slow = 0;
                        }
                        continue;
                    }
                    lost = false;
                }

                // ویدیو: سرور به درخواست‌های کوچک جواب می‌ده ولی ویدیو رو رد نمی‌کنه. تست روی گوشی (۱۳ نوامبر ۲۰۲۶): سروری که
                // درفش انتخاب کرده بود googlevideo رو بسته بود و هر دانلود بعد از یه ثانیه (حدود ۳۶۵ کیلوبایت) برای همیشه صفر می‌موند؛
                // درخواست کوچک نگهبان جواب می‌گرفت و تست سرعت هم اجرا نمی‌شد (دانلود صفر بود، نه کند). حالا یه بار بعد از وصل
                // شدن، و هر بار که دانلود کاربر یهو قطع می‌شه (حداکثر هر ۲ دقیقه): googlevideo و یه دانلود ۶۰۰ کیلوبایتی. دو بار
                // پشت سر هم رد شد → سروری که ویدیو رو رد می‌کنه.
                bool videoDue = (now - lastVideo).TotalSeconds >= 120 && (dropped || videoFails > 0 || firstVideo);
                if (!flowing && videoDue && _switchLock.CurrentCount > 0)
                {
                    lastVideo = now;
                    firstVideo = false;
                    if (await VideoOkAsync()) videoFails = 0;
                    else if (++videoFails >= 2)
                    {
                        videoFails = 0;
                        await SwitchToNextAsync(ReasonVideo);
                    }
                    else lastVideo = now.AddSeconds(-100);   // ۲۰ ثانیه بعد دوباره
                    continue;
                }

                // زاپاس‌ها: هر ۲ دقیقه، و تا وقتی کمتر از دوتا آماده‌ست هر ۴۵ ثانیه؛ گوشی بیکار نه
                if (active && _upkeep.IsCompleted && (now - lastUpkeep).TotalSeconds >= (ReadyCount >= 2 ? 120 : 45))
                {
                    lastUpkeep = now;
                    _upkeep = Task.Run(() => RefreshReadyAsync(token));   // کنار، تا تشخیص قطعی معطل نشه
                }

                // کندی: اندروید وقتی دانلود کاربر قطره‌قطره می‌آد؛ ویندوز (آمار نداره) هر ۲ دقیقه
                bool checkSpeed = stats ? trickle is >= 5 and < 60 && (now - lastSpeed).TotalSeconds >= 30
                                        : (now - lastSpeed).TotalSeconds >= 120;
                if (!checkSpeed) continue;
                lastSpeed = now;
                int kbps = await MeasureSpeedAsync(XrayProc.SessionPort, maxSeconds: 5, maxBytes: 200_000);
                if (kbps >= MainPage.OkSpeed) { slow = 0; continue; }
                if (++slow < 2) continue;
                slow = 0;
                // دو بار پشت سر هم کندتر از چیزی که ویدیو لازم داره: فقط اگه زاپاسی آماده‌ست که روی این اینترنت سریع‌تر بوده
                int need = Math.Max(kbps * 2, MainPage.VideoSpeed);
                if (Ready().Any(x => x.Speed >= need))
                    await SwitchToNextAsync(ReasonSlow, accept: x => x.Speed >= need);
            }
        }

        private static (DateTime at, long down, long up) At(List<(DateTime at, long down, long up)> h, DateTime now, int secondsAgo)
        {
            for (int i = h.Count - 1; i >= 0; i--) if ((now - h[i].at).TotalSeconds >= secondsAgo - 0.2) return h[i];
            return h[0];
        }

        // کانفیگ سروری که الان وصله؛ بعد از هر تعویض موفق، کانفیگ سرور تازه اینجا کپی می‌شه
        public static string CurrentConfigPath => Path.Combine(XrayProc.WorkDir, "config.json");

        // رفتن به یه سرور سالم دیگه (قطعی، کندی، یا دکمه‌ی «تعویض سرور»). accept: فقط سرورهایی که این رو دارن (کندی: سریع‌تر).
        // اگه هیچ‌کدوم نشد، موتور به همون سرور قبلی برمی‌گرده (برای «کند» و «دستی» هنوز کار می‌کنه).
        public async Task<bool> SwitchToNextAsync(string reason, Func<Candidate, bool>? accept = null)
        {
            if (!await _switchLock.WaitAsync(0)) return false;   // همین الان داره عوض می‌کنه
            try
            {
                var tried = new HashSet<string>();
                bool engineChanged = false;
                int rounds = reason == ReasonDown ? 6 : reason is ReasonManual or ReasonVideo ? 3 : 1;
                for (int round = 0; round < rounds && !_cts.IsCancellationRequested; round++)
                {
                    var batch = Candidates(8, tried, includeReady: true);
                    if (accept != null) batch = batch.Where(accept).ToList();
                    if (batch.Count == 0) break;
                    foreach (var c in batch) tried.Add(c.Link);
                    var alive = await ServerProbe.TestManyAsync(batch.Select(c => c.Link).ToList(), 6,
                                                                batch.Where(c => c.Speed <= 0).Select(c => c.Link).ToHashSet(), _cts.Token);
                    // هیچ‌کدوم جواب نداد: شاید اینترنت خود دستگاه قطعه، نه سرورها؛ اون‌وقت مرده حساب نمی‌شن
                    if (alive.Count == 0 && batch.Count >= 3 && !await InternetUpAsync())
                    {
                        if (engineChanged && File.Exists(CurrentConfigPath)) try { await _switchTo(new Spare("", "", CurrentConfigPath)); } catch { }
                        Lost?.Invoke(ReasonOffline);
                        return false;
                    }
                    MarkDead(batch.Where(c => !alive.ContainsKey(c.Link)));
                    // اون‌هایی که ویدیو (googlevideo) رو هم باز می‌کنن اول؛ برای «ویدیو» فقط همون‌ها
                    var order = batch.Where(c => alive.ContainsKey(c.Link) && (reason != ReasonVideo || alive[c.Link].video))
                                     .OrderByDescending(c => alive[c.Link].video).ToList();
                    foreach (var c in order)
                    {
                        if (_cts.IsCancellationRequested) return false;
                        string path = Path.Combine(XrayProc.WorkDir, $"spare_{++_fileGen % 3}.json");
                        try
                        {
                            await File.WriteAllTextAsync(path, await SessionConfig.BuildAsync(c.Link, XrayProc.SessionPort));
                            var spare = new Spare(c.Name, c.Link, path, c.Speed);
                            engineChanged = true;
                            if (await _switchTo(spare) && (await ProbeAsync(6) || await ProbeAsync(6, alternate: true)))
                            {
                                try { File.Copy(path, CurrentConfigPath, overwrite: true); } catch { }
                                lock (_readyLock) _ready = _ready.Where(x => x.Link != c.Link).ToList();
                                Switched?.Invoke(spare, reason);
                                return true;
                            }
                        }
                        catch { }
                        MarkDead(new[] { c });
                    }
                }
                if (engineChanged && File.Exists(CurrentConfigPath))
                    try { await _switchTo(new Spare("", "", CurrentConfigPath)); } catch { }
                Lost?.Invoke(reason);
                return false;
            }
            finally { _switchLock.Release(); }
        }

        // زاپاس‌های آماده: ۶ تای بعدی صف با هم تست می‌شن؛ سالم‌ها (حداکثر سه‌تا) آماده می‌مونن، مرده‌ها ۱۰ دقیقه کنار می‌رن
        private async Task RefreshReadyAsync(CancellationToken token)
        {
            if (_switchLock.CurrentCount == 0) return;
            try
            {
                var batch = Candidates(6, new HashSet<string>(), includeReady: false);
                var alive = batch.Count == 0 ? new() : await ServerProbe.TestManyAsync(batch.Select(c => c.Link).ToList(), 6, null, token);
                if (alive.Count > 0) MarkDead(batch.Where(c => !alive.ContainsKey(c.Link)));   // هیچ‌کدوم: شاید اینترنت قطعه
                lock (_readyLock) _ready = batch.Where(c => alive.ContainsKey(c.Link)).OrderByDescending(c => alive[c.Link].video).Take(3).ToList();
            }
            catch { }
        }

        private List<Candidate> Ready() { lock (_readyLock) return _ready.ToList(); }
        private int ReadyCount { get { lock (_readyLock) return _ready.Count; } }

        // اینترنت خود دستگاه (بدون VPN و پروکسی) وصله؟ یه سایت ایرانی یا کلادفلر جواب بده، با هر کدی
        private static async Task<bool> InternetUpAsync()
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(6) };
            var tries = new[] { "https://www.digikala.com/", "https://www.aparat.com/", "http://cp.cloudflare.com/generate_204" }.Select(async url =>
            {
                try { using var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead); return true; }
                catch { return false; }
            }).ToList();
            while (tries.Count > 0)
            {
                var done = await Task.WhenAny(tries);
                if (await done) return true;
                tries.Remove(done);
            }
            return false;
        }

        // صف: زاپاس‌های آماده، سالم‌های این اینترنت (pool.json، به ترتیب صفحه)، بعد بقیه‌ی لیست آنلاین با همون مکان انتخابی.
        // سرور فعلی، سرورهای اخیراً مرده و کانفیگ‌های همون آدرسِ سرور فعلی کنار می‌رن؛ توی هر دسته از هر آدرس یکی.
        private List<Candidate> Candidates(int take, HashSet<string> tried, bool includeReady)
        {
            string? current = _currentLink();
            string? currentHost = current == null ? null : IdentityOf(current);
            var hosts = new HashSet<string>();
            var list = new List<Candidate>();
            var source = (includeReady ? Ready() : new List<Candidate>()).Concat(ReadPool()).Concat(CloudTail());
            foreach (var c in source)
            {
                if (list.Count >= take) break;
                if (c.Link == current || tried.Contains(c.Link) || IsDead(c.Link)) continue;
                string host = IdentityOf(c.Link);
                if ((host.Length > 0 && host == currentHost) || !hosts.Add(host.Length > 0 ? host : c.Link)) continue;
                list.Add(c);
            }
            return list;
        }

        // «کدوم سرور»: آدرس، به‌علاوه‌ی اسم سایت پشتش (sni/host)؛ چندتا سرور مختلف پشت یه IP کلادفلر یکی حساب نمی‌شن
        private string IdentityOf(string link)
        {
            lock (_hosts)
            {
                if (_hosts.TryGetValue(link, out var id)) return id;
                string address = (_configs.CreateServerObj(link, false)?.Address ?? "").ToLowerInvariant(), front = "";
                try
                {
                    if (!link.StartsWith("vmess://"))
                    {
                        var q = System.Web.HttpUtility.ParseQueryString(new Uri(link.Split('#')[0]).Query);
                        front = (q["sni"] ?? q["host"] ?? q["peer"] ?? "").ToLowerInvariant();
                    }
                }
                catch { }
                return _hosts[link] = address.Length == 0 ? "" : address + "|" + front;
            }
        }

        private void MarkDead(IEnumerable<Candidate> dead)
        {
            lock (_deadUntil) foreach (var c in dead) _deadUntil[c.Link] = DateTime.UtcNow.AddMinutes(10);
            lock (_readyLock) _ready = _ready.Where(x => !IsDead(x.Link)).ToList();
        }

        private bool IsDead(string link) { lock (_deadUntil) return _deadUntil.TryGetValue(link, out var t) && t > DateTime.UtcNow; }

        // ---------- صف زاپاس‌ها روی دیسک (صفحه می‌نویسه، سرویس VPN می‌خونه، حتی وقتی صفحه بسته‌ست) ----------
        public static string PoolPath => Path.Combine(XrayProc.WorkDir, "pool.json");

        public static void WritePool(IEnumerable<Candidate> pool)
        {
            try { File.WriteAllText(PoolPath, JsonSerializer.Serialize(pool.ToList())); } catch { }
        }

        private static List<Candidate> ReadPool()
        {
            try { if (File.Exists(PoolPath)) return JsonSerializer.Deserialize<List<Candidate>>(File.ReadAllText(PoolPath)) ?? new(); } catch { }
            return new();
        }

        // بقیه‌ی لیست آنلاین، به ترتیب خود لیست (بهترین‌ها اول، از گیت‌هاب)، فقط مکان انتخابی؛ وقتی سالم‌های این اینترنت تموم شدن
        private static string CloudPath => Path.Combine(FileSystem.AppDataDirectory, "cloud_configs.txt");
        private List<string>? _cloud;
        private DateTime _cloudTime;

        private IEnumerable<Candidate> CloudTail()
        {
            try
            {
                var time = File.Exists(CloudPath) ? File.GetLastWriteTimeUtc(CloudPath) : DateTime.MinValue;
                if (_cloud == null || time != _cloudTime)
                {
                    _cloud = _configs.ParseFile(CloudPath, false).Select(s => s.Config).Distinct().ToList();
                    _cloudTime = time;
                }
            }
            catch { _cloud = new(); }
            string selected = Locations.Selected;
            return _cloud.Where(c => Locations.Matches(c, selected))
                         .Select(c => new Candidate(_configs.CreateServerObj(c, false)?.City ?? "", c));
        }

        // فایل‌های زاپاس و تست که از اتصال قبلی مونده‌ن
        public static void CleanupFiles()
        {
            try
            {
                foreach (var pattern in new[] { "spare_*.json", "spare_*.log", "probe_*.json", "probe_*.log", "spares.json", "pool.json" })
                    foreach (var f in Directory.GetFiles(XrayProc.WorkDir, pattern)) try { File.Delete(f); } catch { }
            }
            catch { }
        }

        // ---------- اندازه‌گیری ----------

        // سرعت دانلود از راه یه پورت SOCKS (تست سرور یا خود اتصال)، به کیلوبایت بر ثانیه. زمان از اولین بایت حساب می‌شه، تا
        // معطلی شروع اتصال سرعت رو کم نشون نده. ۰ یعنی هیچی نیومد.
        // اگه از کلادفلر چیزی نیومد، یه بار از jsDelivr (لیست اشتراک خود درفش): بعضی سرورهای رایگان (مثلاً روی Workers
        // کلادفلر) به سایت‌های کلادفلر وصل نمی‌شن ولی بقیه‌ی اینترنت رو خوب رد می‌کنن.
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

        // ویدیو از داخل اتصال: googlevideo جواب بده و از یه دانلود ۶۰۰ کیلوبایتی دست‌کم ۴۰۰ کیلوبایت تا ۸ ثانیه برسه
        private static async Task<bool> VideoOkAsync()
        {
            try
            {
                using var client = XrayProc.SocksClient(XrayProc.SessionPort, TimeSpan.FromSeconds(8));
                using var res = await client.GetAsync(ServerProbe.VideoUrl);
                if (res.StatusCode != System.Net.HttpStatusCode.NoContent) return false;
            }
            catch { return false; }
            long got = await DownloadBytesAsync(XrayProc.SessionPort, "https://speed.cloudflare.com/__down?bytes=600000", 8);
            if (got == 0) got = await DownloadBytesAsync(XrayProc.SessionPort, "https://cdn.jsdelivr.net/gh/Mehinator/Derafsh@main/derafsh_sub.txt", 8);
            return got >= 400_000;
        }

        private static async Task<long> DownloadBytesAsync(int socksPort, string url, int seconds)
        {
            long total = 0;
            try
            {
                using var client = XrayProc.SocksClient(socksPort, TimeSpan.FromSeconds(seconds + 2));
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                using var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!res.IsSuccessStatusCode) return 0;
                await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
                var buf = new byte[16384];
                int n;
                while (total < 600_000 && (n = await stream.ReadAsync(buf, cts.Token)) > 0) total += n;
            }
            catch { }
            return total;
        }

        // یه درخواست کوچک از داخل اتصال؛ فقط جواب 204 قبوله. alternate: یه سایت دیگه (کلادفلر)، تا قطعی یه سایت قطعی
        // سرور حساب نشه
        public static async Task<bool> ProbeAsync(int timeoutSeconds = 6, bool alternate = false)
        {
            try
            {
                using var client = XrayProc.SocksClient(XrayProc.SessionPort, TimeSpan.FromSeconds(timeoutSeconds));
                using var res = await client.GetAsync(alternate ? "http://cp.cloudflare.com/generate_204" : "http://www.gstatic.com/generate_204");
                return res.StatusCode == System.Net.HttpStatusCode.NoContent;
            }
            catch { return false; }
        }

        // IP و «شهر، کشور» سرور از راه خود اتصال: اول کلادفلر (شهرش در تست دقیق‌تر بود)، بعد ipwho.is، بعد api.country.is
        public static async Task<(string? ip, string? location)> ExitLocationAsync()
        {
            using var client = XrayProc.SocksClient(XrayProc.SessionPort, TimeSpan.FromSeconds(8));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://speed.cloudflare.com/meta");
                request.Headers.Referrer = new Uri("https://speed.cloudflare.com/");   // بدونش جواب خالیه
                using var res = await client.SendAsync(request);
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var r = doc.RootElement;
                string? cc = Str(r, "country"), ip = Str(r, "clientIp"), city = Str(r, "city");
                if (!string.IsNullOrEmpty(cc) && !string.IsNullOrEmpty(ip)) return (ip, string.IsNullOrEmpty(city) ? cc : $"{city}, {cc}");
            }
            catch { }
            try
            {
                using var doc = JsonDocument.Parse(await client.GetStringAsync("https://ipwho.is/"));
                var r = doc.RootElement;
                if (r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    string? city = Str(r, "city"), cc = Str(r, "country_code");
                    return (Str(r, "ip"), string.IsNullOrEmpty(city) ? cc : $"{city}, {cc}");
                }
            }
            catch { }
            try
            {
                using var doc = JsonDocument.Parse(await client.GetStringAsync("https://api.country.is"));
                return (Str(doc.RootElement, "ip"), Str(doc.RootElement, "country"));
            }
            catch { return (null, null); }
        }

        private static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public void Dispose() => _cts.Cancel();
    }
}
