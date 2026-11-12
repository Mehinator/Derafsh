using System.Runtime.InteropServices;
using System.Text.Json;
using Derafsh.Client.Models;

namespace Derafsh.Client
{
    // کیفیت اتصال: صف سرورهای زاپاس برای نگهبان اتصال (ConnectionGuard)، پیغام وقتی خودکار عوض شد، و خبر نسخه‌ی تازه
    public partial class MainPage
    {
        // ---------- صف زاپاس‌ها ----------
        // صفحه فقط می‌گه کدوم سرورها روی این اینترنت سالم بودن: بهترین اول، فقط مکان انتخابی، و با گزینه‌ی Gemini اون‌هایی
        // که Gemini رو باز کردن اول (رد شده‌ها نه). خود نگهبان زاپاس‌ها رو تازه نگه می‌داره و عوض می‌کنه، حتی وقتی صفحه بسته‌ست.
        private void UpdatePool()
        {
            if (!isConnected) return;
            string? current = CurrentLink();
            IEnumerable<Server> working = WorkingServers().Where(s => s.Config != current);
            if (RequireGemini)
                working = working.Where(s => GeminiRank(_geminiCache.GetValueOrDefault(s.Config)) < 2)
                                 .OrderBy(s => GeminiRank(_geminiCache.GetValueOrDefault(s.Config)));
            ConnectionGuard.WritePool(working.Take(60).Select(s => new ConnectionGuard.Candidate(s.City, s.Config, Math.Max(0, _speedCache.GetValueOrDefault(s.Config, 0)))));
        }

        // لینک سروری که الان وصله (اندروید: سرویس VPN بعد از رفتن به زاپاس عوضش می‌کنه)
        private string? CurrentLink() =>
#if ANDROID
            DerafshVpnService.CurrentLink ?? _currentServer?.Config;
#else
            _currentServer?.Config;
#endif

        // ترتیب صف زاپاس با گزینه‌ی Gemini: باز می‌کنه، تست نشده، رد شده
        private static int GeminiRank(string? status) => IsGeminiOk(status) ? 0 : string.IsNullOrEmpty(status) || status is "testing" or "timeout" or "failed" ? 1 : 2;

        // ---------- رفتن خودکار به سرور دیگه ----------
        // (روی اندروید سرویس VPN خودش عوض می‌کنه و اینجا فقط صفحه به‌روز می‌شه)
        private void OnServerSwitched(ConnectionGuard.Spare spare, string reason)
        {
            if (!isConnected) return;
            string? oldName = _currentServer?.City;
            if (_currentServer != null)
            {
                // سرور قبلی دیگه خودکار انتخاب نمی‌شه؛ فقط قطعی واقعی توی حافظه‌ی این اینترنت «بد» ثبت می‌شه
                _pingCache[_currentServer.Config] = NoReply;
                foreach (var x in RowsOf(_currentServer.Config)) x.Ping = NoReply;
                if (reason == ConnectionGuard.ReasonDown)
                {
                    NetworkHistory.Record(_currentServer.Config, ok: false);
                    NetworkHistory.Save();
                }
            }
            var next = AllServers.FirstOrDefault(x => x.Config == spare.Link) ?? NewServer(spare.Link);
            if (next != null)
            {
                _currentServer = next;
                if (!_autoServer) foreach (var x in AllServers) x.IsSelected = x.Config == next.Config;
            }
            Preferences.Default.Set("connected_name", spare.Name);
            SetConnState(ConnState.Connected, Q(spare.Name));
            StatusLabel.Text = reason switch
            {
                ConnectionGuard.ReasonManual => $"به سرور {Q(spare.Name)} رفتید",
                ConnectionGuard.ReasonSlow => $"{Q(oldName)} کند شده بود؛ خودکار به {Q(spare.Name)} رفت",
                _ => $"{Q(oldName)} قطع شد؛ خودکار به {Q(spare.Name)} رفت و وصل ماندید",
            };
            UpdateHome();
            UpdatePool();
            _ = Task.Run(async () =>
            {
                var (ip, location) = await ConnectionGuard.ExitLocationAsync();
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (isConnected && _currentServer?.Config == spare.Link && ip != null)
                        SetConnState(ConnState.Connected, $"{Q(spare.Name)}  ·  {location}  ·  IP {ip}");
                });
            });
        }

        private void OnConnectionLost(string reason)
        {
            if (!isConnected) return;
            switch (reason)
            {
                case ConnectionGuard.ReasonSlow:
                    return;   // زاپاس سریع‌تری جواب نداد؛ روی همین سرور می‌مونه
                case ConnectionGuard.ReasonOffline:
                    StatusLabel.Text = "اینترنت دستگاه قطع است. درفش منتظر می‌ماند و وقتی اینترنت برگشت، خودش سرور سالم پیدا می‌کند.";
                    return;
                case ConnectionGuard.ReasonManual:
                    StatusLabel.Text = "فعلاً سرور سالم دیگری پیدا نشد. چند لحظه بعد دوباره «تعویض سرور» را بزنید.";
                    return;
            }
            StatusLabel.Text = "سرور جواب نمی‌دهد و سرورهای سالم قبلی هم جواب ندادند؛ سرورهای لیست از نو تست می‌شوند…";
            // سالم‌های قبلی مردن: لیست از نو روی همین اینترنت تست می‌شه و صف نگهبان پر می‌شه (نگهبان هم خودش بقیه‌ی لیست رو می‌گرده)
            if (!_discovering && !_isUpdating && !_isTestingAll)
                _ = DiscoverAsync(fresh: true).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(UpdatePool));
        }

        // «تعویض سرور»: همین الان برو روی یه سرور سالم دیگه (مثلاً وقتی ویدیو پخش نمی‌شه)
        private async void OnManualSwitchClicked(object sender, EventArgs e)
        {
            if (!isConnected) return;
            ManualSwitchButton.IsEnabled = false;
            StatusLabel.Text = "در حال پیدا کردن سرور سالم دیگری…";
            try
            {
                UpdatePool();
#if ANDROID
                VpnController.SwitchNow();   // سرویس VPN عوض می‌کنه و نتیجه با ServerSwitched / ConnectionLost برمی‌گرده
                await Task.Delay(3000);
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
            _guard = new ConnectionGuard(SwitchEngineAsync, () => _currentServer?.Config);
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
