using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Derafsh.Client.Models;
using Derafsh.Client.Services; // اتصال به سرویس‌ها
#if WINDOWS
using Microsoft.Win32;
#endif

namespace Derafsh.Client
{
    public partial class MainPage : ContentPage
    {
        private const string OnlineConfigUrl = "https://cdn.jsdelivr.net/gh/Mehinator/Derafsh@main/derafsh_servers.txt";
        private const string OnlineSourcesUrl = "https://cdn.jsdelivr.net/gh/Mehinator/Derafsh@main/derafsh_sources.json";
        private string CloudPath => Path.Combine(FileSystem.AppDataDirectory, "cloud_configs.txt");
        private string CloudSourcesPath => Path.Combine(FileSystem.AppDataDirectory, "cloud_sources.json");
        private string UserPath => Path.Combine(FileSystem.AppDataDirectory, "user_configs.txt");
        private string FavoritesPath => Path.Combine(FileSystem.AppDataDirectory, "favorites.json");

        [DllImport("wininet.dll")] private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        public ObservableCollection<ServerGroup> ServerGroups { get; } = new();
        private List<Server> AllServers => ServerGroups.SelectMany(g => g).ToList();

        private XrayProc? xrayProcess;
        private bool isConnected = false;
        // فقط وقتی خود درفش پروکسی ویندوز رو روشن کرده، موقع بستن برنامه خاموشش می‌کنیم
        private static bool s_proxyEnabled;
        private Server? _currentServer;
        private string CurrentTab = "All";
        private bool _isUpdating, _isTestingAll;

        // *** استفاده از سرویس جداگانه ***
        private readonly ConfigService _configService = new ConfigService();

        // نتیجه تست‌ها در همین اجرای برنامه (با Refresh یا عوض کردن تب پاک نشن)
        private readonly Dictionary<string, string> _geminiCache = new();
        private readonly Dictionary<string, int> _pingCache = new();

        public MainPage()
        {
            InitializeComponent();
            BindingContext = this;
            ServerListView.ItemsSource = ServerGroups;
            BrandLabel.Text = $"DERAFSH  ·  {typeof(MainPage).Assembly.GetName().Version?.ToString(3)}";
            HighlightTab("All");
            SetConnState(ConnState.Disconnected);
#if ANDROID
            // اندروید: VPN بعد از بستن صفحه هم روشن می‌مونه؛ با باز شدن دوباره وضعیتش رو نشون بده
            if (DerafshVpnService.IsRunning)
            {
                isConnected = true;
                SetConnState(ConnState.Connected, Preferences.Default.Get("connected_name", "") + "  ·  VPN is on");
            }
#endif
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                LoadConfigs();
                // لیست آنلاین هر 4 ساعت روی گیت‌هاب آپدیت می‌شه؛ اگه نسخه ما قدیمی‌تره اول دانلود (که خودش تست رو شروع می‌کنه)،
                // وگرنه همین الان تست خودکار روی همین اینترنت
                if (!File.Exists(CloudPath) || File.GetLastWriteTime(CloudPath) < DateTime.Now.AddHours(-4)) await UpdateOnlineConfigs();
                else await DiscoverAsync(fresh: false);
            });
            DebugLog.Write("=== STARTUP ===");
        }

        // ستون اصلی حداکثر 880 پیکسل و وسط پنجره، تا روی مانیتورهای عریض کش نیاد؛ روی گوشی چیدمان فشرده
        private bool? _compact;
        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);
            if (width <= 0) return;
            bool compact = width < 640;
            double side = compact ? 14 : Math.Max(24, (width - 880) / 2);
            RootGrid.Padding = new Thickness(side, compact ? 14 : 20, side, compact ? 10 : 16);
            if (_compact != compact) { _compact = compact; ApplyLayout(compact); }
        }

        private void ApplyLayout(bool compact)
        {
            // کارت اتصال: روی گوشی دکمه زیر متن
            Grid.SetRow(ConnectButton, compact ? 1 : 0);
            Grid.SetColumn(ConnectButton, compact ? 0 : 1);
            Grid.SetColumnSpan(ConnectButton, compact ? 2 : 1);
            ConnectButton.HorizontalOptions = compact ? LayoutOptions.Fill : LayoutOptions.End;
            ConnectButton.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0);
            ConnGrid.ColumnSpacing = compact ? 0 : 20;

            // نوار ابزار: روی گوشی تب‌ها بالا و چهار دکمه 2×2 زیرش
            Grid.SetRow(ActionsBar, compact ? 1 : 0);
            Grid.SetColumn(ActionsBar, compact ? 0 : 2);
            Grid.SetColumnSpan(ActionsBar, compact ? 3 : 1);
            ToolbarGrid.RowSpacing = compact ? 10 : 0;
            ActionsBar.ColumnDefinitions.Clear();
            ActionsBar.RowDefinitions.Clear();
            var buttons = new[] { TestAllButton, RefreshButton, SaveListButton, AddConfigButton };
            if (compact)
            {
                ActionsBar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                ActionsBar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                ActionsBar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                ActionsBar.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (int i = 0; i < buttons.Length; i++) { Grid.SetColumn(buttons[i], i % 2); Grid.SetRow(buttons[i], i / 2); buttons[i].HeightRequest = 40; }
            }
            else
            {
                for (int i = 0; i < buttons.Length; i++) { ActionsBar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); Grid.SetColumn(buttons[i], i); Grid.SetRow(buttons[i], 0); buttons[i].HeightRequest = 34; }
            }

            // rows already on screen keep the old template, so reattach the list to rebuild them
            ServerListView.ItemTemplate = (DataTemplate)Resources[compact ? "CompactRow" : "WideRow"];
            ServerListView.ItemsSource = null;
            ServerListView.ItemsSource = ServerGroups;
        }

        private static T Res<T>(string key) => Application.Current!.Resources.TryGetValue(key, out var v) && v is T t ? t : default!;
        private static string Plural(int n) => n == 1 ? "" : "s";

        // --- لود کردن لیست (با استفاده از سرویس) ---
        private void LoadConfigs()
        {
            string? keep = _currentServer?.Config;
            ServerGroups.Clear();
            int idx = 1;
            var favs = ReadFavorites();

            // 1. شخصی‌ها
            if (CurrentTab == "All" || CurrentTab == "Personal")
            {
                var list = _configService.ParseFile(UserPath, true);
                Decorate(list, new(), favs);
                foreach (var s in list) s.Source = "Personal";
                list = Ordered(list);
                if (list.Any())
                {
                    foreach (var s in list) s.Index = idx++;
                    ServerGroups.Add(new ServerGroup($"PERSONAL  ·  {list.Count}", list));
                }
            }

            // 2. آنلاین‌ها: فقط کانفیگ‌هایی که روی همین اینترنت جواب دادن (تست خودکار، MainPage.Discovery.cs)،
            //    به‌علاوه‌ی ستاره‌دارها؛ بی‌جواب‌ها و تست‌نشده‌ها نمایش داده نمی‌شن
            _onlineGroup = null;
            if (CurrentTab == "All" || CurrentTab == "Online")
            {
                _cloudSources = ReadJsonDict(CloudSourcesPath);
                var list = _configService.ParseFile(CloudPath, false).DistinctBy(x => x.Config).ToList();
                Decorate(list, _cloudSources, favs);
                var shown = Ordered(list.Where(x => x.IsFavorite || x.Ping is > 0 and < NoReply));
                foreach (var s in shown) s.Index = idx++;
                if (shown.Any() || list.Any())
                {
                    _onlineGroup = new ServerGroup("", shown);
                    ServerGroups.Add(_onlineGroup);
                    UpdateOnlineHeader();
                }
            }

            // 3. لیست‌های ذخیره‌شده (هر کدوم برای یه اینترنت)
            if (CurrentTab == "Saved") AddSavedGroups(_cloudSources.Count > 0 ? _cloudSources : ReadJsonDict(CloudSourcesPath), favs, ref idx);

            ServerListView.ItemsSource = null; ServerListView.ItemsSource = ServerGroups;

            // انتخاب قبلی بعد از لود دوباره حفظ بشه
            var match = keep == null ? null : AllServers.FirstOrDefault(x => x.Config == keep);
            if (match != null) SelectServer(match);
            else if (_currentServer == null && AllServers.Any()) SelectServer(AllServers.First());

            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            bool empty = ServerGroups.All(g => g.Count == 0);
            EmptyState.IsVisible = empty;
            ServerListView.IsVisible = !empty;
            EmptyRefreshButton.IsVisible = CurrentTab is "All" or "Online";   // Refresh فقط لیست آنلاین رو پر می‌کنه
            (EmptyTitle.Text, EmptyText.Text) = CurrentTab switch
            {
                "Personal" => ("No configs here yet", "Save a config from the Online list, or add your own links."),
                "Saved" => ("No saved lists yet", "When configs work on your network, press Save list to keep them under a name, for example your internet provider."),
                _ when _isUpdating => ("Testing configs on your network…", "Downloading the newest configs first. The ones that answer will appear here, fastest first."),
                _ when _discovering || (!_discoveryDone && File.Exists(CloudPath)) => ("Testing configs on your network…", "Configs that answer will appear here, fastest first."),
                _ when !File.Exists(CloudPath) => ("No configs here yet", "Refresh the list to download free configs, or add your own links."),
                _ => ("Nothing works on this network yet", "None of the tested configs answered. Press Test all to try again, or Refresh list later for new ones."),
            };
        }

        // --- دکمه‌ها ---
        private async void OnUpdateOnlineClicked(object sender, EventArgs e) => await UpdateOnlineConfigs();
        private async Task UpdateOnlineConfigs()
        {
            // وسط «Test all» لیست عوض نمی‌شه: نتیجه‌ها به ردیف‌هایی می‌رسید که دیگه نمایش داده نمی‌شدن
            if (_isUpdating || _isTestingAll) return;
            _isUpdating = true;
            UpdateEmptyState();
            StatusLabel.Text = "Downloading the latest configs…";
            try
            {
                // اول از راه پروکسی ویندوز (اگه کاربر به یه VPN وصله)؛ اگه اون پروکسی خراب بود، مستقیم
                (string list, string? sources) data;
                try { data = await DownloadListAsync(useSystemProxy: true); }
                catch { data = await DownloadListAsync(useSystemProxy: false); }

                await File.WriteAllTextAsync(CloudPath, data.list);
                if (data.sources != null) await File.WriteAllTextAsync(CloudSourcesPath, data.sources);
                _discoverGen++;   // تست خودکارِ لیست قبلی دیگه ردیف اضافه نمی‌کنه
                _discovering = false;
                LoadConfigs();
            }
            catch
            {
                StatusLabel.Text = "Couldn't download the list. Check your internet connection and press Refresh list again.";
                return;
            }
            finally { _isUpdating = false; }
            await DiscoverAsync(fresh: false);   // کانفیگ‌هایی که همین الان تست شدن دوباره تست نمی‌شن
        }

        private static async Task<(string list, string? sources)> DownloadListAsync(bool useSystemProxy)
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = useSystemProxy }) { Timeout = TimeSpan.FromSeconds(useSystemProxy ? 12 : 20) };
            string list = await client.GetStringAsync(OnlineConfigUrl);
            string? sources = null;
            // فایل منبع اختیاریه؛ اگه نبود، برنامه بدون اون کار می‌کنه
            try { sources = await client.GetStringAsync(OnlineSourcesUrl); } catch { }
            return (list, sources);
        }

        private async void OnAddCustomConfigClicked(object sender, EventArgs e)
        {
            var regex = new Regex(@"(vmess|vless|ss|trojan|hysteria2?)://[a-zA-Z0-9\-\._~:/\?#@!$&'()*+,;=%]+");

            // فقط اگه توی کلیپ‌بورد لینک کانفیگ هست، از قبل پرش کن (متن‌های دیگه کاربر نمایش داده نشه)
            string initial = "";
            if (Clipboard.Default.HasText)
            {
                string clip = await Clipboard.Default.GetTextAsync() ?? "";
                if (regex.IsMatch(clip)) initial = clip;
            }
            string input = await DisplayPromptAsync("Add configs", "Paste one or more links (vless, vmess, ss, trojan, hysteria2).", "Add", "Cancel", initialValue: initial);
            if (string.IsNullOrWhiteSpace(input)) return;
            var existing = File.Exists(UserPath) ? (await File.ReadAllLinesAsync(UserPath)).Select(l => l.Trim()).ToHashSet() : new HashSet<string>();
            var links = regex.Matches(input).Select(m => m.Value.Trim()).Distinct().Where(l => !existing.Contains(l)).ToList();

            if (links.Any())
            {
                await File.AppendAllLinesAsync(UserPath, links);
                LoadConfigs();
                StatusLabel.Text = $"Added {links.Count} config{Plural(links.Count)} to Personal";
            }
            else await DisplayAlertAsync("No new links found", "Links start with vless://, vmess://, ss://, trojan:// or hysteria2://. Links already in Personal are skipped.", "OK");
        }

        private void OnSaveToPersonalClicked(object sender, EventArgs e) { if (sender is Button btn && btn.CommandParameter is Server s) _ = SaveToPersonal(s); }
        private async Task SaveToPersonal(Server s)
        {
            try
            {
                if (File.Exists(UserPath) && (await File.ReadAllTextAsync(UserPath)).Contains(s.Config)) { StatusLabel.Text = $"Already in Personal: {s.City}"; return; }
                await File.AppendAllLinesAsync(UserPath, new[] { s.Config });
                s.IsRemovable = true; s.Source = "Personal";
                StatusLabel.Text = $"Saved to Personal: {s.City}";
            }
            catch { StatusLabel.Text = "Couldn't save this config."; }
        }

        private void OnDeleteSingleItemClicked(object sender, EventArgs e) { if (sender is Button btn && btn.CommandParameter is Server s) _ = DeleteServer(s); }
        private async Task DeleteServer(Server s)
        {
            if (!await DisplayAlertAsync("Delete this config?", $"“{s.City}” will be removed from your Personal list.", "Delete", "Cancel")) return;
            try
            {
                var lines = (await File.ReadAllLinesAsync(UserPath)).ToList();
                lines.RemoveAll(l => l.Trim() == s.Config);
                await File.WriteAllLinesAsync(UserPath, lines);
                LoadConfigs();
                StatusLabel.Text = $"Deleted {s.City}";
            }
            catch { StatusLabel.Text = "Couldn't delete this config."; }
        }

        private void OnServerSelected(object sender, SelectedItemChangedEventArgs e)
        {
            if (e.SelectedItem is not Server s || _currentServer == s) return;
            SelectServer(s);
            if (isConnected)
            {
                Disconnect();
                StatusLabel.Text = $"Switching to {s.City}…";
                Task.Delay(500).ContinueWith(t => MainThread.BeginInvokeOnMainThread(() => OnConnectButtonClicked(sender, EventArgs.Empty)));
            }
        }

        private void SelectServer(Server s)
        {
            foreach (var item in AllServers) item.IsSelected = false;
            s.IsSelected = true; _currentServer = s;
            if (!isConnected) SetConnState(ConnState.Disconnected);
        }

        // --- تب‌ها ---
        private void OnTabClicked(object sender, EventArgs e)
        {
            if (sender is not Button btn || btn.CommandParameter is not string tab) return;
            CurrentTab = tab;
            HighlightTab(tab);
            LoadConfigs();
        }

        private void HighlightTab(string tab)
        {
            foreach (var b in new[] { BtnTabAll, BtnTabPersonal, BtnTabOnline, BtnTabSaved })
            {
                bool on = (string)b.CommandParameter == tab;
                b.BackgroundColor = on ? Res<Color>("SurfaceRaised") : Colors.Transparent;
                b.TextColor = on ? Res<Color>("TextPrimary") : Res<Color>("TextSecondary");
                b.FontFamily = on ? Res<string>("FontStrong") : Res<string>("FontBody");
            }
        }

        // --- امتیاز شخصی (⭐) ---
        private void OnFavoriteClicked(object sender, EventArgs e) { if (sender is Button btn && btn.CommandParameter is Server s) ToggleFavorite(s); }
        private void ToggleFavorite(Server s)
        {
            var favs = ReadFavorites();
            s.IsFavorite = !s.IsFavorite;
            if (s.IsFavorite) favs.Add(s.Config); else favs.Remove(s.Config);
            try { File.WriteAllText(FavoritesPath, JsonSerializer.Serialize(favs.ToList())); } catch { }

            foreach (var x in RowsOf(s.Config)) x.IsFavorite = s.IsFavorite;
            ResortList();
        }

        private HashSet<string> ReadFavorites()
        {
            try { if (File.Exists(FavoritesPath)) return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FavoritesPath)) ?? new(); } catch { }
            return new();
        }

        private static Dictionary<string, string> ReadJsonDict(string path)
        {
            try { if (File.Exists(path)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new(); } catch { }
            return new();
        }

        // منبع، امتیاز و نتیجه تست‌های قبلی رو به هر کانفیگ می‌ده
        private void Decorate(List<Server> list, Dictionary<string, string> sources, HashSet<string> favs)
        {
            foreach (var s in list)
            {
                s.Source = sources.TryGetValue(s.Config, out var src) ? src : "";
                s.IsFavorite = favs.Contains(s.Config);
                s.GeminiStatus = _geminiCache.TryGetValue(s.Config, out var g) ? g : "";
                s.Ping = _pingCache.TryGetValue(s.Config, out var p) ? p : -1;
                s.IsPinging = _pinging.Contains(s.Config);
            }
        }

        // --- xray موقت برای تست یه کانفیگ (پینگ و جمینی) ---
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
                    if (port != 10809 && s_testPorts.Add(port)) return port;
                }
                throw new Exception("No free local port for the test.");
            }
        }

        private async Task<(XrayProc proc, int port, string configPath)> StartTestXray(string link)
        {
            int port = ReserveTestPort();
            string configPath = Path.Combine(XrayProc.WorkDir, $"test_{port}.json");
            try
            {
                await File.WriteAllTextAsync(configPath, await BuildXrayConfigAsync(link, port));
                var proc = XrayProc.Start(configPath, isTest: true);
                // اگه xray پورت رو باز نکرد، کانفیگ خرابه یا موتور بالا نیومد؛ درخواست به پورت بسته معنی نداره
                if (!await XrayProc.WaitForPortAsync(port, 6000)) { proc.Dispose(); throw new Exception("Xray didn't start with this config."); }
                return (proc, port, configPath);
            }
            catch
            {
                StopTestXray(null, port, configPath);
                throw;
            }
        }

        private async Task<string> BuildXrayConfigAsync(string link, int port)
        {
            string json = _configService.GenerateXrayConfig(link, port);
#if ANDROID
            json = await ConfigService.ResolveServerAddressAsync(json);
#endif
            return await Task.FromResult(json);
        }

        // حتماً بعد از هر تست صدا زده بشه، وگرنه xray توی پس‌زمینه باز می‌مونه
        private static void StopTestXray(XrayProc? proc, int port, string? configPath)
        {
            proc?.Dispose();
            lock (s_testPorts) s_testPorts.Remove(port);
            if (configPath == null) return;
            try { File.Delete(configPath); } catch { }
            try { File.Delete(Path.ChangeExtension(configPath, ".log")); } catch { }
        }

        // نتیجه‌ی تست به همه‌ی ردیف‌هایی که الان همین کانفیگ رو نشون می‌دن می‌رسه (نه فقط ردیفی که تست ازش شروع شد؛
        // اگه وسط تست لیست دوباره لود بشه، ردیف‌ها شیءهای تازه‌ان)
        private IEnumerable<Server> RowsOf(string config) => AllServers.Where(x => x.Config == config);

        // --- تست جمینی (از طریق خود کانفیگ) ---
        private void OnGeminiTestClicked(object sender, EventArgs e) { if (sender is Button btn && btn.CommandParameter is Server s) _ = TestGemini(s.Config); }
        private async Task TestGemini(string config)
        {
            if (_geminiCache.TryGetValue(config, out var now) && now == "testing") return;
            SetGemini(config, "testing");
            string result;
            XrayProc? proc = null; int port = 0; string? configPath = null;
            try
            {
                (proc, port, configPath) = await StartTestXray(config);
                using var client = XrayProc.SocksClient(port);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var res = await client.GetAsync("https://gemini.google.com/app", HttpCompletionOption.ResponseHeadersRead, cts.Token);
                // اول صفحه کافیه (کد وضعیت و پیام «در کشور شما در دسترس نیست»)؛ دانلود کل صفحه از کانفیگ کند خیلی طول می‌کشه
                string body = await ReadStartAsync(res.Content, 256 * 1024, cts.Token);
                result = ClassifyGemini(res.StatusCode, body);
            }
            catch (OperationCanceledException) { result = "timeout"; }
            catch { result = "failed"; }
            finally { StopTestXray(proc, port, configPath); }
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

        // --- پینگ ---
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
                if (_currentServer?.Config == config && !isConnected) SetConnState(ConnState.Disconnected);
            }
        }

        // پینگ واقعی از داخل کانفیگ:
        //   درخواست اول تونل رو باز می‌کنه (اتصال و TLS تا سرور)، درخواست دوم تأخیر واقعی رو اندازه می‌گیره.
        //   اگه هیچ درخواستی جواب نداد یه بار دیگه امتحان می‌شه؛ کانفیگ‌های رایگان گاهی یه درخواست رو گم می‌کنن
        //   و یه بار شکست نباید کانفیگ سالم رو «مرده» نشون بده.
        private async Task<int> MeasurePingAsync(string config)
        {
            if (!await ServerReachableAsync(config)) return NoReply;

            XrayProc? proc = null; int port = 0; string? configPath = null;
            try
            {
                (proc, port, configPath) = await StartTestXray(config);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    using var client = XrayProc.SocksClient(port, TimeSpan.FromSeconds(attempt == 0 ? 8 : 10));
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
            finally { StopTestXray(proc, port, configPath); }
        }

        // تست سریع TCP تا سرور، تا برای سرورهای خاموش یا فیلترشده xray بالا نیاد.
        // vmess (لینک base64) و hysteria2 (UDP) و لینک‌هایی که آدرسشون خونده نمی‌شه مستقیم با xray تست می‌شن.
        private static async Task<bool> ServerReachableAsync(string config)
        {
            if (config.StartsWith("vmess://") || config.StartsWith("hysteria2://") || config.StartsWith("hy2://")) return true;
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

        // «Test all»: کانفیگ‌های شخصی و لیست‌های ذخیره‌شده‌ی همین تب تست می‌شن؛ توی All و Online لیست آنلاین هم از نو
        // روی همین اینترنت گشته می‌شه (فقط سالم‌ها نمایش داده می‌شن)
        private async void OnFindBestServerClicked(object sender, EventArgs e)
        {
            if (_isTestingAll) return;
            _isTestingAll = true;
            TestAllButton.IsEnabled = RefreshButton.IsEnabled = EmptyRefreshButton.IsEnabled = false;
            try
            {
                // دانلود خودکار لیست موقع باز شدن برنامه اول تموم بشه، بعد تست؛ وگرنه لیست وسط تست عوض می‌شد
                if (_isUpdating)
                {
                    StatusLabel.Text = "Downloading the latest configs first…";
                    for (int i = 0; _isUpdating && i < 150; i++) await Task.Delay(200);
                }
                bool online = CurrentTab is "All" or "Online";
                var targets = AllServers.Where(s => !online || s.IsRemovable).Select(s => s.Config).Distinct().ToList();
                int done = 0;
                using var sem = new SemaphoreSlim(5);
                var tasks = targets.Select(async config =>
                {
                    await sem.WaitAsync();
                    try { await PingSingle(config); }
                    finally { sem.Release(); done++; if (!online) StatusLabel.Text = $"Testing {done} of {targets.Count}…"; }
                }).ToList();
                if (online) tasks.Add(DiscoverAsync(fresh: true));
                await Task.WhenAll(tasks);

                // ردیف‌ها فقط یه بار، آخر تست، مرتب می‌شن (وسط تست جابه‌جا نمی‌شن)
                ResortList();
                UpdateOnlineHeader();
                ShowTestSummary();
            }
            finally
            {
                _isTestingAll = false;
                TestAllButton.IsEnabled = RefreshButton.IsEnabled = EmptyRefreshButton.IsEnabled = true;
            }
        }

        // ترتیب ثابت لیست، همه‌جا یکی: ستاره‌دارها اول، بعد کانفیگ‌هایی که جواب دادن (سریع‌ترین اول)،
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

        // --- اتصال ---
        private enum ConnState { Disconnected, Connecting, Connected }

        private void SetConnState(ConnState state, string? detail = null)
        {
            switch (state)
            {
                case ConnState.Connecting:
                    ConnDot.Color = Res<Color>("Warning");
                    ConnStateLabel.Text = "Connecting…";
                    ConnDetailLabel.Text = _currentServer?.City ?? "";
                    ConnectButton.Style = Res<Style>("PrimaryButton");
                    ConnectButton.Text = "Connecting…";
                    ConnectButton.IsEnabled = false;
                    break;
                case ConnState.Connected:
                    ConnDot.Color = Res<Color>("Success");
                    ConnStateLabel.Text = "Connected";
                    ConnDetailLabel.Text = detail ?? "";
                    ConnectButton.Style = Res<Style>("SecondaryButtonLarge");
                    ConnectButton.Text = "Disconnect";
                    ConnectButton.IsEnabled = true;
                    break;
                default:
                    ConnDot.Color = Res<Color>("TextMuted");
                    ConnStateLabel.Text = "Not connected";
                    ConnDetailLabel.Text = _currentServer == null ? "Choose a server from the list below"
                        : $"Selected: {_currentServer.City}" + (_currentServer.Ping is > 0 and < 9999 ? $"  ·  {_currentServer.Ping} ms" : "");
                    ConnectButton.Style = Res<Style>("PrimaryButton");
                    ConnectButton.Text = "Connect";
                    ConnectButton.IsEnabled = true;
                    break;
            }
        }

        private async void OnConnectButtonClicked(object sender, EventArgs e)
        {
            if (isConnected) { Disconnect(); return; }
            if (_currentServer == null) { StatusLabel.Text = "Choose a server from the list first."; return; }
            SetConnState(ConnState.Connecting);
            try
            {
                if (!XrayProc.EngineExists)
                {
                    await DisplayAlertAsync("Xray engine is missing", "Derafsh can't find its Xray engine. Reinstall Derafsh and try again.", "OK");
                    SetConnState(ConnState.Disconnected);
                    return;
                }

                // *** استفاده از سرویس برای تولید کانفیگ نهایی ***
                string configPath = Path.Combine(XrayProc.WorkDir, "config.json");
                await File.WriteAllTextAsync(configPath, await BuildXrayConfigAsync(_currentServer.Config, 10809));

#if ANDROID
                // اندروید: سرویس VPN خودش xray و تونل رو بالا میاره
                if (!await VpnController.StartAsync(configPath))
                    throw new Exception(VpnController.LastError ?? "The VPN didn't start.");
#else
                xrayProcess?.Dispose(); xrayProcess = null;
                KillXray();
                xrayProcess = XrayProc.Start(configPath);
                await XrayProc.WaitForPortAsync(10809, 5000);
                EnableProxy();
#endif

                string? ip = null, country = null;
                try
                {
                    using var client = XrayProc.SocksClient(10809, TimeSpan.FromSeconds(8));
                    using var doc = JsonDocument.Parse(await client.GetStringAsync("https://api.country.is"));
                    ip = doc.RootElement.GetProperty("ip").GetString();
                    country = doc.RootElement.GetProperty("country").GetString();
                }
                catch { }

                isConnected = true;
                Preferences.Default.Set("connected_name", _currentServer.City);
                SetConnState(ConnState.Connected, ip != null
                    ? $"{_currentServer.City}  ·  IP {ip}  ·  {country}"
                    : $"{_currentServer.City}  ·  couldn't check the IP — try opening a website");
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("Couldn't connect", $"{ex.Message}\nTry another config, or press “Test all” to find one that works.", "OK");
                Disconnect();
            }
        }

        private void Disconnect()
        {
#if ANDROID
            VpnController.Stop();
#else
            DisableProxy(); xrayProcess?.Dispose(); xrayProcess = null; KillXray();
#endif
            isConnected = false; SetConnState(ConnState.Disconnected);
        }

        private static void KillXray()
        {
#if WINDOWS
            // xray‌هایی که از اجرای قبلی مونده‌ن؛ xray‌های تستِ همین الان دست نمی‌خورن
            foreach (var p in Process.GetProcessesByName("xray")) try { if (!XrayProc.IsTestProcess(p.Id)) p.Kill(); } catch { }
#endif
        }

        public static void CleanupOnExit()
        {
#if WINDOWS
            if (!s_proxyEnabled) return;   // پروکسی رو درفش روشن نکرده؛ دستش نمی‌زنیم
            try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true); key?.SetValue("ProxyEnable", 0); InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); } catch { }
            KillXray();
            s_proxyEnabled = false;
#endif
        }

#if WINDOWS
        private void EnableProxy() { try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true); key?.SetValue("ProxyEnable", 1); key?.SetValue("ProxyServer", "socks=127.0.0.1:10809"); InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); s_proxyEnabled = true; } catch { } }
        private void DisableProxy() { try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true); key?.SetValue("ProxyEnable", 0); InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); s_proxyEnabled = false; } catch { } }
#else
        private void EnableProxy() {} private void DisableProxy() {}
#endif
    }
}
