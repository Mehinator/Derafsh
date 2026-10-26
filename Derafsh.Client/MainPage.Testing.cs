using System.Diagnostics;
using System.Net.Sockets;
using Derafsh.Client.Models;
using Derafsh.Client.Services;

namespace Derafsh.Client
{
    // تست سرورها: پینگ واقعی و جمینی از داخل هر کانفیگ (با یه xray موقت، یا برای WARP با یه هسته‌ی ایتر موقت)،
    // و تست خودکار لیست آنلاین روی همین اینترنت
    public partial class MainPage
    {
        // ---------- xray موقت برای تست یه کانفیگ ----------
        // هر تست پورت آزاد خودش رو از سیستم‌عامل می‌گیره؛ پورت تصادفی بین تست‌های همزمان تکراری می‌شد و
        // نتیجه‌ی یه کانفیگ به اسم کانفیگ دیگه ثبت می‌شد
        private static readonly HashSet<int> s_testPorts = new();
        private static int ReserveTestPort()
        {
            lock (s_testPorts)
            {
                for (int i = 0; i < 20; i++)
                {
                    var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
                    l.Start();
                    int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
                    l.Stop();
                    if (port != XrayProc.SessionPortConst && port != AetherProc.SessionPort && s_testPorts.Add(port)) return port;
                }
                throw new Exception("No free local port for the test.");
            }
        }

        // موتور تست: برای کانفیگ‌های معمولی یه xray موقته؛ برای aether:// یه هسته‌ی ایتر موقته (پورت خودش = موتور تست)
        private sealed class TestEngine : IDisposable
        {
            public int Port;                  // پورت SOCKS موتور
            public bool AetherMode;           // پورت، رزرو ایتره (در هر حالت آزاد می‌شه)
            public XrayProc? Xray;
            public AetherProc? Aether;
            public string? ConfigPath;

            public void Dispose()
            {
                Xray?.Dispose();
                Aether?.Dispose();
                if (ConfigPath != null)
                {
                    try { File.Delete(ConfigPath); } catch { }
                    try { File.Delete(Path.ChangeExtension(ConfigPath, ".log")); } catch { }
                }
                if (AetherMode) AetherProc.ReleasePort(Port);
                lock (s_testPorts) s_testPorts.Remove(Port);
            }
        }

        private static bool IsAether(string config) => config.StartsWith("aether://", StringComparison.OrdinalIgnoreCase);

        private async Task<TestEngine> StartTestEngine(string link)
        {
            if (IsAether(link)) return await StartTestAether(link);
            int port = ReserveTestPort();
            string configPath = Path.Combine(XrayProc.WorkDir, $"test_{port}.json");
            try
            {
                await File.WriteAllTextAsync(configPath, await BuildXrayConfigAsync(link, port));
                var proc = XrayProc.Start(configPath, isTest: true);
                // اگه xray پورت رو باز نکرد، کانفیگ خرابه یا موتور بالا نیومد؛ درخواست به پورت بسته معنی نداره
                if (!await XrayProc.WaitForPortAsync(port, 6000)) { proc.Dispose(); throw new Exception("Xray didn't start with this config."); }
                return new TestEngine { Port = port, Xray = proc, ConfigPath = configPath };
            }
            catch
            {
                var d = new TestEngine { Port = port, ConfigPath = configPath };
                d.Dispose();
                throw;
            }
        }

        // هسته‌ی ایتر موقت برای تست یه پروفایل WARP. اسکن و تونل زمان می‌بره؛ صبر بلندتر از xray.
        // روی اینترنت فیلترشده اسکن‌های همزمان سنگین‌ان؛ هم‌زمان حداکثر ۲ تا.
        private static readonly SemaphoreSlim AetherTestGate = new(2);
        private async Task<TestEngine> StartTestAether(string link)
        {
            var parsed = AetherLink.Parse(link) ?? throw new Exception("Invalid aether link.");
            int port = AetherProc.ReservePort();
            var engine = new TestEngine { Port = port, AetherMode = true };
            try
            {
                await AetherTestGate.WaitAsync();
                engine.Aether = AetherProc.Start(parsed, port, isTest: true, upstreamPort: null);
                // اسکن و تونل WARP زمان می‌بره؛ صبر ۴۰ ثانیه‌ای
                if (!await XrayProc.WaitForPortAsync(port, 40000))
                    throw new Exception("Aether didn't start with this profile.");
                return engine;
            }
            catch
            {
                AetherTestGate.Release();
                engine.Aether = null;
                engine.Dispose();
                throw;
            }
            // قفل تا پایان تست نگه داشته می‌شه؛ StopTestEngine در پایان آزادش می‌کنه
        }

        private void StopTestEngine(TestEngine? engine)
        {
            if (engine == null) return;
            bool wasAether = engine.AetherMode;
            engine.Dispose();
            if (wasAether) AetherTestGate.Release();
        }

        private async Task<string> BuildXrayConfigAsync(string link, int port)
        {
            string json = _configService.GenerateXrayConfig(link, port);
#if ANDROID
            json = await ConfigService.ResolveServerAddressAsync(json);
#endif
            return await Task.FromResult(json);
        }

        // نتیجه‌ی تست به همه‌ی ردیف‌هایی که الان همین کانفیگ رو نشون می‌دن می‌رسه (اگه وسط تست لیست دوباره لود بشه،
        // ردیف‌ها شیءهای تازه‌ان)
        private IEnumerable<Server> RowsOf(string config) => AllServers.Where(x => x.Config == config);

        // ---------- جمینی ----------
        private void OnGeminiTestClicked(object sender, EventArgs e) { if (sender is Button btn && btn.CommandParameter is Server s) _ = TestGemini(s.Config); }
        private async Task TestGemini(string config)
        {
            if (_geminiCache.TryGetValue(config, out var now) && now == "testing") return;
            SetGemini(config, "testing");
            string result;
            TestEngine? engine = null;
            try
            {
                engine = await StartTestEngine(config);
                using var client = XrayProc.SocksClient(engine.Port);
                // ایتر تازه تونل رو بالا میاره؛ جمینی صبورتر
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(IsAether(config) ? 60 : 15));
                using var res = await client.GetAsync("https://gemini.google.com/app", HttpCompletionOption.ResponseHeadersRead, cts.Token);
                // اول صفحه کافیه (کد وضعیت و پیام «در کشور شما در دسترس نیست»)؛ دانلود کل صفحه از کانفیگ کند خیلی طول می‌کشه
                string body = await ReadStartAsync(res.Content, 256 * 1024, cts.Token);
                result = ClassifyGemini(res.StatusCode, body);
            }
            catch (OperationCanceledException) { result = "timeout"; }
            catch { result = "failed"; }
            finally { StopTestEngine(engine); }
            SetGemini(config, result);
        }

        private void SetGemini(string config, string status)
        {
            _geminiCache[config] = status;
            foreach (var x in RowsOf(config)) x.GeminiStatus = status;
        }

        private static async Task<string> ReadStartAsync(HttpContent content, int maxBytes, CancellationToken ct)
        {
            await using var stream = await content.ReadAsStreamAsync(ct);
            var buffer = new byte[maxBytes];
            int total = 0, read;
            while (total < maxBytes && (read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), ct)) > 0) total += read;
            return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
        }

        private static string ClassifyGemini(System.Net.HttpStatusCode code, string body)
        {
            if (body.Contains("not available in your country", StringComparison.OrdinalIgnoreCase)) return "region";
            if (code == System.Net.HttpStatusCode.Forbidden) return "blocked";
            if (code == System.Net.HttpStatusCode.OK) return "ok";
            return $"http:{(int)code}";
        }

        // اولین سرور (به ترتیب سرعت) که جمینی رو باز می‌کنه؛ سه‌تا سه‌تا تست می‌شن و نتیجه‌ی قبلی هر سرور دوباره استفاده می‌شه
        private async Task<Server?> FirstWithGeminiAsync(List<Server> working)
        {
            foreach (var batch in working.Take(12).Chunk(3))
            {
                if (_cancelFind) return null;
                var untested = batch.Where(s => !_geminiCache.ContainsKey(s.Config) || _geminiCache[s.Config] == "testing").ToList();
                if (untested.Any())
                {
                    StatusLabel.Text = $"بررسی جمینی روی {Fa(untested.Count)} سرور…";
                    await Task.WhenAll(untested.Select(s => TestGemini(s.Config)));
                }
                var ok = batch.FirstOrDefault(s => _geminiCache.TryGetValue(s.Config, out var g) && g == "ok");
                if (ok != null) { ok.GeminiStatus = "ok"; StatusLabel.Text = ""; return ok; }
            }
            StatusLabel.Text = "";
            return null;
        }

        // ---------- پینگ ----------
        private const int NoReply = 9999;
        private const string PingUrl = "http://www.gstatic.com/generate_204";
        private readonly HashSet<string> _pinging = new();

        private void OnPingSingleItemClicked(object sender, EventArgs e) { if (sender is Button btn && btn.CommandParameter is Server s) _ = PingSingle(s.Config); }

        private async Task PingSingle(string config)
        {
            if (!_pinging.Add(config)) return;   // همین کانفیگ همین الان در حال تسته
            foreach (var x in RowsOf(config)) x.IsPinging = true;
            int ping = NoReply;
            try { ping = await MeasurePingAsync(config); }
            catch { }
            finally
            {
                _pinging.Remove(config);
                _pingCache[config] = ping;
                foreach (var x in RowsOf(config)) { x.Ping = ping; x.IsPinging = false; }
            }
        }

        // پینگ واقعی از داخل کانفیگ:
        //   درخواست اول تونل رو باز می‌کنه (اتصال و TLS تا سرور)، درخواست دوم تأخیر واقعی رو اندازه می‌گیره.
        //   اگه هیچ درخواستی جواب نداد یه بار دیگه امتحان می‌شه؛ کانفیگ‌های رایگان گاهی یه درخواست رو گم می‌کنن
        //   و یه بار شکست نباید کانفیگ سالم رو «مرده» نشون بده.
        // ایتر: تونل WARP با اسکن بالا میاد؛ درخواست اول صبر ۴۰ ثانیه‌ای داره.
        private async Task<int> MeasurePingAsync(string config)
        {
            if (!await ServerReachableAsync(config)) return NoReply;

            bool aether = IsAether(config);
            TestEngine? engine = null;
            try
            {
                engine = await StartTestEngine(config);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    using var client = XrayProc.SocksClient(engine.Port, TimeSpan.FromSeconds(attempt == 0 ? (aether ? 40 : 8) : (aether ? 20 : 10)));
                    int first;
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        using (var warm = await client.GetAsync(PingUrl)) if (!warm.IsSuccessStatusCode) continue;
                        first = (int)sw.ElapsedMilliseconds;
                    }
                    catch { continue; }

                    try
                    {
                        var sw = Stopwatch.StartNew();
                        using var res = await client.GetAsync(PingUrl);
                        if (res.IsSuccessStatusCode) return Math.Max(1, (int)sw.ElapsedMilliseconds);
                    }
                    catch { }
                    return Math.Max(1, first);   // درخواست اول جواب داده، پس کانفیگ زنده‌ست
                }
                return NoReply;
            }
            catch { return NoReply; }
            finally { StopTestEngine(engine); }
        }

        // تست سریع TCP تا سرور، تا برای سرورهای خاموش یا فیلترشده xray بالا نیاد.
        // vmess (لینک base64) و hysteria2 (UDP) و لینک‌هایی که آدرسشون خونده نمی‌شه مستقیم با xray تست می‌شن.
        // ایتر: نشانی می‌تونه نداشته باشه (اسکن) و UDP/QUIC هست؛ مستقیم با هسته‌ی ایتر تست می‌شه.
        private static async Task<bool> ServerReachableAsync(string config)
        {
            if (IsAether(config) || config.StartsWith("vmess://") || config.StartsWith("hysteria2://") || config.StartsWith("hy2://")) return true;
            if (config.StartsWith("ss://") && !config.Split('#')[0].Contains('@')) return true;   // ss قدیمی: آدرس داخل base64
            if (!Uri.TryCreate(config, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return true;
            try
            {
                using var tcp = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await tcp.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 443, cts.Token);
                return true;
            }
            catch { return false; }
        }

        // ---------- «تست دوباره» ----------
        // آنلاین: از نو روی همین اینترنت گشته می‌شه (فقط سالم‌ها نمایش داده می‌شن)؛ شخصی و ذخیره‌شده‌ها: همه‌ی ردیف‌ها تست می‌شن
        private async void OnTestAgainClicked(object sender, EventArgs e)
        {
            if (_isTestingAll) return;
            _isTestingAll = true;
            TestAllButton.IsEnabled = RefreshButton.IsEnabled = false;
            try
            {
                if (_isUpdating)
                {
                    StatusLabel.Text = "اول دانلود لیست تمام شود…";
                    for (int i = 0; _isUpdating && i < 150; i++) await Task.Delay(200);
                }
                if (_currentList == ListOnline)
                {
                    await DiscoverAsync(fresh: true);
                    ResortList();
                    UpdateOnlineHeader();
                    ShowTestSummary();
                }
                else await TestRowsAsync(onlyUntested: false);
            }
            finally
            {
                _isTestingAll = false;
                TestAllButton.IsEnabled = RefreshButton.IsEnabled = true;
            }
        }

        // تست ردیف‌های لیست شخصی یا ذخیره‌شده (با باز شدن لیست فقط اون‌هایی که تو این اجرا تست نشدن)
        private async Task TestRowsAsync(bool onlyUntested)
        {
            string list = _currentList;
            var targets = AllServers.Select(s => s.Config).Distinct().Where(c => !onlyUntested || !_pingCache.ContainsKey(c)).ToList();
            if (!targets.Any()) return;
            int done = 0;
            using var sem = new SemaphoreSlim(5);
            await Task.WhenAll(targets.Select(async config =>
            {
                await sem.WaitAsync();
                try { await PingSingle(config); }
                finally { sem.Release(); done++; if (_currentList == list && !_connecting) StatusLabel.Text = $"تست {Fa(done)} از {Fa(targets.Count)}…"; }
            }));
            if (_currentList != list) return;   // کاربر وسط تست لیست دیگه‌ای رو باز کرده
            // ردیف‌ها فقط یه بار، آخر تست، مرتب می‌شن (وسط تست جابه‌جا نمی‌شن)
            ResortList();
            if (!_connecting) ShowTestSummary();
            UpdateHome();
        }

        // ---------- تست خودکار لیست آنلاین ----------
        // از جدیدترین‌ها (ستاره‌دارها اول) تست می‌شه تا TargetWorking تا سرور سالم پیدا بشه یا MaxTested تا تست بشه.
        private const int TargetWorking = 20;
#if ANDROID
        private const int MaxTested = 120, DiscoverParallel = 5;
#else
        private const int MaxTested = 200, DiscoverParallel = 8;
#endif
        private bool _discovering, _discoveryDone;   // _discoveryDone: یه بار تست تا آخر رفته (قبلش «هیچی جواب نداد» نشون داده نمی‌شه)
        private int _discoverGen;
        private ServerGroup? _onlineGroup;
        private Dictionary<string, string> _cloudSources = new();

        // fresh: نتیجه‌های قبلی کانفیگ‌های آنلاین پاک می‌شن و همه از نو تست می‌شن («تست دوباره»)
        private async Task DiscoverAsync(bool fresh)
        {
            int gen = ++_discoverGen;   // اجرای قبلی (اگه هنوز در جریانه) دیگه ردیف اضافه نمی‌کنه
            var cloud = _configService.ParseFile(CloudPath, false).Select(s => s.Config).Distinct().ToList();
            if (cloud.Count == 0) return;
            if (fresh)
            {
                foreach (var c in cloud) _pingCache.Remove(c);
                LoadConfigs();
            }

            var favs = ReadFavorites();
            var queue = cloud.OrderByDescending(favs.Contains).Where(c => !_pingCache.ContainsKey(c)).ToList();   // لیست از جدید به قدیمه
            int found = cloud.Count(c => _pingCache.TryGetValue(c, out var p) && p is > 0 and < NoReply);
            int planned = Math.Min(queue.Count, MaxTested), done = 0, started = 0;

            _discovering = true;
            UpdateOnlineHeader();
            UpdateEmptyState();
            UpdateHome();
            StatusLabel.Text = $"در حال پیدا کردن سرورهای سالم روی این اینترنت · {Fa(found)} پیدا شد";
            try
            {
                using var sem = new SemaphoreSlim(DiscoverParallel);
                var running = new List<Task>();
                foreach (var config in queue)
                {
                    if (gen != _discoverGen || found >= TargetWorking || started >= MaxTested) break;
                    await sem.WaitAsync();
                    if (gen != _discoverGen || found >= TargetWorking) { sem.Release(); break; }
                    started++;
                    running.Add(TestOne(config));
                }
                await Task.WhenAll(running);

                async Task TestOne(string config)
                {
                    try { await PingSingle(config); }
                    finally { sem.Release(); }
                    done++;
                    if (gen != _discoverGen) return;
                    if (_pingCache.TryGetValue(config, out var ping) && ping is > 0 and < NoReply)
                    {
                        found++;
                        AddOnlineRow(config);   // پیدا شده‌ها پایین لیست اضافه می‌شن؛ مرتب‌سازی فقط یه بار، آخر کار
                        if (!_connecting && !isConnected) UpdateHome();
                    }
                    if (!_connecting) StatusLabel.Text = $"در حال پیدا کردن سرورهای سالم روی این اینترنت · {Fa(found)} پیدا شد · {Fa(done)} از {Fa(planned)} تست شد";
                }
            }
            finally
            {
                if (gen == _discoverGen)
                {
                    _discovering = false;
                    _discoveryDone = true;
                    ResortList();
                    UpdateOnlineHeader();
                    UpdateEmptyState();
                    UpdateHome();
                    if (!_isTestingAll && !_connecting) ShowTestSummary();
                }
            }
        }

        private void AddOnlineRow(string config)
        {
            if (_onlineGroup == null || !ServerGroups.Contains(_onlineGroup) || _onlineGroup.Any(x => x.Config == config)) return;
            var s = NewServer(config);
            if (s == null) return;
            _onlineGroup.Add(s);
            UpdateOnlineHeader();
            UpdateEmptyState();
        }

        private void UpdateOnlineHeader()
        {
            if (_onlineGroup == null) return;
            int n = _onlineGroup.Count(x => x.Ping is > 0 and < NoReply);
            _onlineGroup.Name = $"آنلاین  ·  {Fa(n)} سرور سالم روی این اینترنت" + (_discovering ? "  ·  در حال تست…" : "");
        }

        private void ShowTestSummary()
        {
            var working = AllServers.Where(s => s.Ping is > 0 and < NoReply).OrderBy(s => s.Ping).ToList();
            if (!working.Any())
            {
                StatusLabel.Text = _currentList == ListOnline
                    ? "فعلاً هیچ سروری روی این اینترنت جواب نداد. کمی بعد «دریافت لیست تازه» را بزنید."
                    : "هیچ سروری از این لیست روی این اینترنت جواب نداد.";
                return;
            }
            var best = working.First();
            StatusLabel.Text = $"{Fa(working.Count)} سرور روی این اینترنت جواب می‌دهند · سریع‌ترین: {Q(best.City)} · {Ms(best.Ping)}";
        }

        // ترتیب ثابت لیست، همه‌جا یکی: ستاره‌دارها اول، بعد سرورهایی که جواب دادن (سریع‌ترین اول)،
        // بعد تست‌نشده‌ها، آخر سر بی‌جواب‌ها. بین هم‌رتبه‌ها ترتیب خود لیست حفظ می‌شه.
        private static List<Server> Ordered(IEnumerable<Server> list) => list
            .OrderByDescending(x => x.IsFavorite)
            .ThenBy(x => x.Ping is > 0 and < NoReply ? 0 : x.Ping < 0 ? 1 : 2)
            .ThenBy(x => x.Ping is > 0 and < NoReply ? x.Ping : 0)
            .ToList();

        private void ResortList()
        {
            var groups = ServerGroups.Select(g =>
            {
                var sorted = new ServerGroup(g.Name, Ordered(g)) { SavedListName = g.SavedListName };
                if (g == _onlineGroup) _onlineGroup = sorted;
                return sorted;
            }).ToList();
            ServerGroups.Clear();
            foreach (var g in groups) ServerGroups.Add(g);
            ServerListView.ItemsSource = null; ServerListView.ItemsSource = ServerGroups;
        }
    }
}
