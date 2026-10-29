using System.Runtime.InteropServices;
using System.Text.Json;
using Derafsh.Client.Models;

namespace Derafsh.Client
{
    // کیفیت اتصال: سرور زاپاس آماده، رفتن خودکار به زاپاس وقتی سرور قطع بشه (ConnectionGuard)، و خبر نسخه‌ی تازه
    public partial class MainPage
    {
        // ---------- سرورهای زاپاس ----------
        // تا وقتی وصلیم، دو سرور سالم دیگه (سریع‌ترین‌ها، و اگه گزینه‌ی جمینی روشنه فقط اون‌هایی که جمینی رو باز می‌کنن)
        // آماده نگه داشته می‌شن: هر ۴ دقیقه دوباره تست می‌شن تا زاپاس کهنه نباشه.
        private List<ConnectionGuard.Spare> _spares = new();
        private bool _refreshingSpares;
        private int _spareGen;
        private IDispatcherTimer? _spareTimer;

        private void StartSpareUpkeep()
        {
            _spareTimer?.Stop();
            _spareTimer = Dispatcher.CreateTimer();
            _spareTimer.Interval = TimeSpan.FromMinutes(4);
            _spareTimer.Tick += async (_, _) => await RefreshSparesAsync();
            _spareTimer.Start();
            _ = RefreshSparesAsync();
        }

        private void StopSpareUpkeep()
        {
            _spareTimer?.Stop();
            _spareTimer = null;
            _spares = new();
            ConnectionGuard.ClearSpares();
            UpdateSpareLabel();
        }

        private async Task RefreshSparesAsync()
        {
            if (!isConnected || _refreshingSpares) return;
            _refreshingSpares = true;
            try
            {
                string? current = _currentServer?.Config;
                var working = WorkingServers().Where(s => s.Config != current).ToList();
                // با گزینه‌ی جمینی: اول اون‌هایی که جمینی رو باز می‌کنن، بعد تست‌نشده‌ها؛ اون‌هایی که قبلاً جمینی‌شون رد شده آخر
                if (RequireGemini)
                    working = working.OrderBy(s => GeminiRank(_geminiCache.GetValueOrDefault(s.Config))).ThenBy(s => _pingCache[s.Config]).ToList();
                var candidates = working.Take(6).ToList();
                if (candidates.Count < 3 && !_discovering && !_isUpdating) _ = DiscoverAsync(fresh: false);

                // دوباره تست: نتیجه‌ی قبلی ممکنه چند دقیقه کهنه باشه و کانفیگ رایگان تا حالا خاموش شده باشه
                using (var sem = new SemaphoreSlim(3))
                    await Task.WhenAll(candidates.Select(async s => { await sem.WaitAsync(); try { await PingSingle(s.Config); } finally { sem.Release(); } }));
                var alive = candidates.Where(s => _pingCache.TryGetValue(s.Config, out var p) && p is > 0 and < NoReply)
                                      .OrderBy(s => _pingCache[s.Config]).ToList();
                bool withoutGemini = false;
                if (RequireGemini)
                {
                    using (var gsem = new SemaphoreSlim(3))
                        await Task.WhenAll(alive.Where(s => !_geminiCache.ContainsKey(s.Config)).Take(6)
                            .Select(async s => { await gsem.WaitAsync(); try { await TestGemini(s.Config); } finally { gsem.Release(); } }));
                    var gemini = alive.Where(s => IsGeminiOk(_geminiCache.GetValueOrDefault(s.Config))).ToList();
                    // هیچ زاپاسی با جمینی نیست: وصل ماندن مهم‌تره، زاپاس بدون جمینی بهتر از هیچه
                    withoutGemini = gemini.Count == 0 && alive.Count > 0;
                    alive = withoutGemini ? alive : gemini;
                }

                int gen = ++_spareGen;
                var spares = new List<ConnectionGuard.Spare>();
                foreach (var s in alive.Take(2))
                {
                    string path = Path.Combine(XrayProc.WorkDir, $"spare_{gen}_{spares.Count}.json");
                    try { await File.WriteAllTextAsync(path, await BuildXrayConfigAsync(s.Config, XrayProc.SessionPort)); spares.Add(new(s.City, s.Config, path)); }
                    catch { }
                }
                if (!isConnected) { foreach (var x in spares) try { File.Delete(x.ConfigPath); } catch { } return; }

                var old = _spares;
                _spares = spares;
                _sparesWithoutGemini = withoutGemini;
                ConnectionGuard.WriteSpares(spares);
                foreach (var x in old.Where(o => spares.All(n => n.ConfigPath != o.ConfigPath))) try { File.Delete(x.ConfigPath); } catch { }
                UpdateSpareLabel();
            }
            finally { _refreshingSpares = false; }
        }

        private bool _sparesWithoutGemini;

        // ترتیب برای انتخاب زاپاس با گزینه‌ی جمینی: باز می‌کنه، تست نشده، رد شده
        private static int GeminiRank(string? status) => IsGeminiOk(status) ? 0 : string.IsNullOrEmpty(status) || status is "testing" or "timeout" or "failed" ? 1 : 2;

        private void UpdateSpareLabel()
        {
            SpareLabel.IsVisible = isConnected;
            SpareLabel.Text = _spares.Count == 0 ? "در حال آماده کردن سرور زاپاس…"
                : $"سرور زاپاس آماده: {Q(_spares[0].Name)}" + (_sparesWithoutGemini ? " (جمینی را باز نمی‌کند)" : "")
                  + "  ·  اگر این سرور قطع شود، خودکار به آن می‌رود";
        }

        // ---------- رفتن خودکار به زاپاس ----------
        // (روی اندروید سرویس VPN خودش عوض می‌کنه و اینجا فقط صفحه به‌روز می‌شه)
        private void OnServerSwitched(ConnectionGuard.Spare spare)
        {
            if (!isConnected) return;
            string? oldName = _currentServer?.City;
            if (_currentServer != null)
            {
                _pingCache[_currentServer.Config] = NoReply;   // سرور قطع‌شده دیگه انتخاب نمی‌شه
                foreach (var x in RowsOf(_currentServer.Config)) x.Ping = NoReply;
            }
            var next = AllServers.FirstOrDefault(x => x.Config == spare.Link) ?? NewServer(spare.Link);
            if (next != null)
            {
                _currentServer = next;
                if (!_autoServer) foreach (var x in AllServers) x.IsSelected = x.Config == next.Config;
            }
            Preferences.Default.Set("connected_name", spare.Name);
            _spares = _spares.Where(x => x.Link != spare.Link).ToList();
            SetConnState(ConnState.Connected, $"{Q(spare.Name)}  ·  سرور زاپاس");
            StatusLabel.Text = $"{Q(oldName)} قطع شد؛ خودکار به {Q(spare.Name)} رفت و وصل ماندید";
            UpdateHome();
            _ = Task.Run(async () =>
            {
                var (ip, location) = await ConnectionGuard.ExitLocationAsync();
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (isConnected && _currentServer?.Config == spare.Link && ip != null)
                        SetConnState(ConnState.Connected, $"{Q(spare.Name)}  ·  {location}  ·  IP {ip}");
                });
            });
            _ = RefreshSparesAsync();
        }

        private void OnConnectionLost()
        {
            if (!isConnected) return;
            StatusLabel.Text = "سرور جواب نمی‌دهد و زاپاس‌ها هم جواب ندادند؛ دنبال سرور سالم…";
            _ = RefreshSparesAsync();
        }

#if WINDOWS
        private ConnectionGuard? _guard;

        private void StartGuard()
        {
            _guard?.Dispose();
            _guard = new ConnectionGuard(SwitchEngineAsync, () => _spares.Where(x => x.Link != _currentServer?.Config).ToList());
            _guard.Switched += spare => MainThread.BeginInvokeOnMainThread(() => OnServerSwitched(spare));
            _guard.Lost += () => MainThread.BeginInvokeOnMainThread(OnConnectionLost);
        }

        private void StopGuard()
        {
            _guard?.Dispose();
            _guard = null;
        }

        // ویندوز: فقط xray عوض می‌شه، روی همون پورت؛ پروکسی سیستم همون‌طور روشن می‌مونه
        private Task<bool> SwitchEngineAsync(ConnectionGuard.Spare spare) => MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (!isConnected || !File.Exists(spare.ConfigPath)) return false;
            xrayProcess?.Dispose(); xrayProcess = null;
            KillXray();
            xrayProcess = XrayProc.Start(spare.ConfigPath);
            return await XrayProc.WaitForPortAsync(XrayProc.SessionPort, 6000);
        });
#else
        // اندروید: نگهبان داخل سرویس VPN است (DerafshVpnService)
        private void StartGuard() { }
        private void StopGuard() { }
#endif

        // ---------- نسخه‌ی تازه ----------
        private const string LatestReleaseApi = "https://api.github.com/repos/Mehinator/Derafsh/releases/latest";
        private string? _updateUrl, _updateTag;

        private void StartUpdateChecks()
        {
            var timer = Dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromHours(12);
            timer.Tick += async (_, _) => await CheckForUpdateAsync();
            timer.Start();
            Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(5), async () => await CheckForUpdateAsync());
        }

        private async Task CheckForUpdateAsync()
        {
            try
            {
                var current = typeof(MainPage).Assembly.GetName().Version;
                if (current == null) return;
                JsonDocument? doc = null;
                foreach (bool viaProxy in new[] { true, false })   // اول از راه پروکسی سیستم (اگه وصلیم)، بعد مستقیم
                {
                    try
                    {
                        using var client = new HttpClient(new HttpClientHandler { UseProxy = viaProxy }) { Timeout = TimeSpan.FromSeconds(12) };
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("Derafsh-App");   // گیت‌هاب بدون User-Agent جواب نمی‌ده
                        doc = JsonDocument.Parse(await client.GetStringAsync(LatestReleaseApi));
                        break;
                    }
                    catch { }
                }
                if (doc == null) return;
                using (doc)
                {
                    var root = doc.RootElement;
                    string tag = root.GetProperty("tag_name").GetString() ?? "";
                    if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return;
                    if (latest <= new Version(current.Major, current.Minor, Math.Max(0, current.Build))) return;
                    if (Preferences.Default.Get("update_dismissed", "") == tag) return;

                    // فایل مخصوص همین دستگاه؛ اگه پیدا نشد، صفحه‌ی ریلیز
                    string want = OperatingSystem.IsAndroid()
                        ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "-Android-arm64.apk" : "-Android-armv7.apk")
                        : "-Windows-x64.zip";
                    string url = root.GetProperty("html_url").GetString() ?? "https://github.com/Mehinator/Derafsh/releases/latest";
                    if (root.TryGetProperty("assets", out var assets))
                        foreach (var a in assets.EnumerateArray())
                            if ((a.GetProperty("name").GetString() ?? "").EndsWith(want, StringComparison.OrdinalIgnoreCase))
                                url = a.GetProperty("browser_download_url").GetString() ?? url;

                    _updateUrl = url;
                    _updateTag = tag;
                    UpdateText.Text = $"نسخه‌ی تازه‌ی درفش آمده: {latest.ToString(3)}";
                    UpdateBanner.IsVisible = true;
                }
            }
            catch { }
        }

        private async void OnUpdateDownloadClicked(object sender, EventArgs e)
        {
            if (_updateUrl == null) return;
            try { await Launcher.OpenAsync(_updateUrl); }
            catch { StatusLabel.Text = "صفحه‌ی دانلود باز نشد. از github.com/Mehinator/Derafsh/releases دانلود کنید."; }
        }

        private void OnUpdateLaterClicked(object sender, EventArgs e)
        {
            if (_updateTag != null) Preferences.Default.Set("update_dismissed", _updateTag);   // برای همین نسخه دیگه نشون داده نمی‌شه
            UpdateBanner.IsVisible = false;
        }
    }
}
