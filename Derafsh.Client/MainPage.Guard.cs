using System.Runtime.InteropServices;
using System.Text.Json;
using Derafsh.Client.Models;

namespace Derafsh.Client
{
    // کیفیت اتصال: سرور زاپاس آماده، رفتن خودکار به زاپاس وقتی سرور قطع بشه (ConnectionGuard)، و خبر نسخه‌ی تازه
    public partial class MainPage
    {
        // ---------- سرورهای زاپاس ----------
        // تا وقتی وصلیم، دو سرور سالم دیگه (سریع‌ترین‌ها، و اگه گزینه‌ی Gemini روشنه فقط و فقط اون‌هایی که Gemini رو باز
        // می‌کنن) آماده نگه داشته می‌شن: هر ۴ دقیقه دوباره تست می‌شن تا زاپاس کهنه نباشه.
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
#if ANDROID
            // کاربر داره ویدیو می‌بینه یا دانلود می‌کنه: تست سرورهای دیگه (که هر کدوم یه تکه دانلود دارن) همون اینترنت رو
            // ازش می‌گیره و ویدیو گیر می‌کنه. اگه زاپاس داریم، این دور تست نمی‌شه (موقع تعویض، زاپاس دوباره امتحان می‌شه)
            if (_spares.Count > 0 && DerafshVpnService.UserDownKbps >= ConnectionGuard.FlowingSpeed) return;
#endif
            _refreshingSpares = true;
            try
            {
                string? current = _currentServer?.Config;
                var working = WorkingServers().Where(s => s.Config != current).ToList();
                // با گزینه‌ی Gemini: اون‌هایی که Gemini رو باز کردن اول، بعد تست‌نشده‌ها؛ اون‌هایی که باز نکردن اصلاً زاپاس نمی‌شن
                if (RequireGemini)
                    working = working.Where(s => GeminiRank(_geminiCache.GetValueOrDefault(s.Config)) < 2)
                                     .OrderBy(s => GeminiRank(_geminiCache.GetValueOrDefault(s.Config)))
                                     .ThenBy(s => SpeedBucket(_speedCache.GetValueOrDefault(s.Config, -1))).ToList();
                var candidates = working.Take(RequireGemini ? 8 : 6).ToList();
                if (candidates.Count < 3 && !_discovering && !_isUpdating) _ = DiscoverAsync(fresh: false);

                // دوباره تست: نتیجه‌ی قبلی ممکنه چند دقیقه کهنه باشه و کانفیگ رایگان تا حالا خاموش شده باشه
                await Task.WhenAll(candidates.Select(s => PingSingle(s.Config)));
                var alive = candidates.Where(s => _pingCache.TryGetValue(s.Config, out var p) && p is > 0 and < NoReply)
                                      .OrderBy(s => SpeedBucket(_speedCache.GetValueOrDefault(s.Config, -1)))
                                      .ThenBy(s => _pingCache[s.Config]).ToList();
                if (RequireGemini)
                {
                    // تست Gemini کمی طول می‌کشه؛ اگه زاپاسی نداریم، اون‌هایی که قبلاً Gemini رو باز کردن همین الان زاپاس می‌شن
                    var known = alive.Where(s => IsGeminiOk(_geminiCache.GetValueOrDefault(s.Config))).ToList();
                    if (_spares.Count == 0 && known.Count > 0) await PublishSparesAsync(known);

                    using (var gsem = new SemaphoreSlim(3))
                        await Task.WhenAll(alive.Where(s => !_geminiCache.ContainsKey(s.Config)).Take(6)
                            .Select(async s => { await gsem.WaitAsync(); try { await TestGemini(s.Config); } finally { gsem.Release(); } }));
                    alive = alive.Where(s => IsGeminiOk(_geminiCache.GetValueOrDefault(s.Config))).ToList();
                }
                await PublishSparesAsync(alive);
                // هنوز زاپاس نداریم (مثلاً هیچ‌کدوم Gemini رو باز نکرد): یه دقیقه دیگه سرورهای بعدی امتحان می‌شن، نه چهار دقیقه
                if (_spares.Count == 0 && isConnected) Dispatcher.DispatchDelayed(TimeSpan.FromMinutes(1), () => _ = RefreshSparesAsync());
            }
            finally { _refreshingSpares = false; }
        }

        private async Task PublishSparesAsync(List<Server> alive)
        {
            int gen = ++_spareGen;
            var spares = new List<ConnectionGuard.Spare>();
            foreach (var s in alive.Take(2))
            {
                string path = Path.Combine(XrayProc.WorkDir, $"spare_{gen}_{spares.Count}.json");
                try { await File.WriteAllTextAsync(path, await BuildXrayConfigAsync(s.Config, XrayProc.SessionPort)); spares.Add(new(s.City, s.Config, path, Math.Max(0, _speedCache.GetValueOrDefault(s.Config, 0)))); }
                catch { }
            }
            if (!isConnected) { foreach (var x in spares) try { File.Delete(x.ConfigPath); } catch { } return; }

            var old = _spares;
            _spares = spares;
            ConnectionGuard.WriteSpares(spares);
            foreach (var x in old.Where(o => spares.All(n => n.ConfigPath != o.ConfigPath))) try { File.Delete(x.ConfigPath); } catch { }
            UpdateSpareLabel();
        }

        // ترتیب برای انتخاب زاپاس با گزینه‌ی Gemini: باز می‌کنه، تست نشده، رد شده
        private static int GeminiRank(string? status) => IsGeminiOk(status) ? 0 : string.IsNullOrEmpty(status) || status is "testing" or "timeout" or "failed" ? 1 : 2;

        private void UpdateSpareLabel()
        {
            SpareLabel.IsVisible = ManualSwitchButton.IsVisible = isConnected;
            SpareLabel.Text = _spares.Count == 0
                ? (RequireGemini ? "در حال پیدا کردن سرور زاپاسی که Gemini را باز کند…" : "در حال آماده کردن سرور زاپاس…")
                : $"سرور زاپاس آماده: {Q(_spares[0].Name)}" + (_spares[0].Speed > 0 ? $" · {Kbs(_spares[0].Speed)}" : "")
                  + "  ·  اگر این سرور قطع یا کند شود، خودکار به زاپاس می‌رود";
        }

        // ---------- رفتن خودکار به زاپاس ----------
        // (روی اندروید سرویس VPN خودش عوض می‌کنه و اینجا فقط صفحه به‌روز می‌شه)
        private void OnServerSwitched(ConnectionGuard.Spare spare, string reason)
        {
            if (!isConnected) return;
            string? oldName = _currentServer?.City;
            if (_currentServer != null)
            {
                _pingCache[_currentServer.Config] = NoReply;   // سرور قطع‌شده دیگه انتخاب نمی‌شه
                foreach (var x in RowsOf(_currentServer.Config)) x.Ping = NoReply;
                NetworkHistory.Record(_currentServer.Config, ok: false);   // روی این اینترنت خوب نبود
                NetworkHistory.Save();
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
            UpdateSpareLabel();
            StatusLabel.Text = reason switch
            {
                ConnectionGuard.ReasonManual => $"به سرور {Q(spare.Name)} رفتید",
                ConnectionGuard.ReasonSlow => $"{Q(oldName)} کند شده بود؛ خودکار به {Q(spare.Name)} رفت",
                _ => $"{Q(oldName)} قطع شد؛ خودکار به {Q(spare.Name)} رفت و وصل ماندید",
            };
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

        private void OnConnectionLost(string reason)
        {
            if (!isConnected) return;
            StatusLabel.Text = reason == ConnectionGuard.ReasonManual
                ? "سرور زاپاس جواب نداد؛ دنبال سرور سالم دیگری… چند لحظه بعد دوباره «تعویض سرور» را بزنید."
                : "سرور جواب نمی‌دهد و زاپاس‌ها هم جواب ندادند؛ دنبال سرور سالم…";
            _ = RefreshSparesAsync();
        }

        // «تعویض سرور»: همین الان برو روی سرور زاپاس (اگه آماده نیست، اول یکی پیدا می‌شه)
        private async void OnManualSwitchClicked(object sender, EventArgs e)
        {
            if (!isConnected) return;
            ManualSwitchButton.IsEnabled = false;
            try
            {
                if (_spares.Count == 0)
                {
                    StatusLabel.Text = "سرور زاپاس هنوز آماده نیست؛ در حال پیدا کردن…";
                    await RefreshSparesAsync();
                    if (_spares.Count == 0) { StatusLabel.Text = RequireGemini ? "فعلاً سرور دیگری که Gemini را باز کند پیدا نشد. کمی بعد دوباره امتحان کنید." : "فعلاً سرور سالم دیگری پیدا نشد. کمی بعد دوباره امتحان کنید."; return; }
                }
                StatusLabel.Text = $"در حال رفتن به {Q(_spares[0].Name)}…";
#if ANDROID
                VpnController.SwitchNow();   // سرویس VPN عوض می‌کنه و نتیجه با ServerSwitched / ConnectionLost برمی‌گرده
                await Task.Delay(1500);
#else
                if (_guard != null) await _guard.SwitchToNextAsync(ConnectionGuard.ReasonManual);
#endif
            }
            finally { ManualSwitchButton.IsEnabled = true; }
        }

#if WINDOWS
        private ConnectionGuard? _guard;

        private void StartGuard()
        {
            _guard?.Dispose();
            _guard = new ConnectionGuard(SwitchEngineAsync, () => _spares.Where(x => x.Link != _currentServer?.Config).ToList());
            _guard.Switched += (spare, reason) => MainThread.BeginInvokeOnMainThread(() => OnServerSwitched(spare, reason));
            _guard.Lost += reason => MainThread.BeginInvokeOnMainThread(() => OnConnectionLost(reason));
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
                // اول از راه پروکسی سیستم (اگه وصلیم)، بعد مستقیم، بعد از داخل اتصال خود درفش (اندروید: خود برنامه بیرون از VPN
                // است و وقتی گیت‌هاب روی این اینترنت بسته‌ست، فقط از داخل اتصال جواب می‌ده)
                for (int way = 0; way < 3 && doc == null; way++)
                {
                    if (way == 2 && !isConnected) break;
                    try
                    {
                        using var client = way == 2 ? XrayProc.SocksClient(XrayProc.SessionPort, TimeSpan.FromSeconds(15))
                                                    : new HttpClient(new HttpClientHandler { UseProxy = way == 0 }) { Timeout = TimeSpan.FromSeconds(12) };
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("Derafsh-App");   // گیت‌هاب بدون User-Agent جواب نمی‌ده
                        doc = JsonDocument.Parse(await client.GetStringAsync(LatestReleaseApi));
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
