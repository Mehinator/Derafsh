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
        private const int OnlineListSize = 20;
        private string CloudPath => Path.Combine(FileSystem.AppDataDirectory, "cloud_configs.txt");
        private string CloudSourcesPath => Path.Combine(FileSystem.AppDataDirectory, "cloud_sources.json");
        private string UserPath => Path.Combine(FileSystem.AppDataDirectory, "user_configs.txt");
        private string FavoritesPath => Path.Combine(FileSystem.AppDataDirectory, "favorites.json");
        // کانفیگ‌های آنلاینی که با «Remove dead» حذف شدن؛ بعد از 24 ساعت برمی‌گردن چون ممکنه دوباره کار کنن
        private string HiddenPath => Path.Combine(FileSystem.AppDataDirectory, "hidden_configs.json");

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
            _ = Task.Run(() => MainThread.BeginInvokeOnMainThread(LoadConfigs));

            // لیست آنلاین هر 4 ساعت روی گیت‌هاب آپدیت می‌شه؛ اگه نسخه ما قدیمی‌تره، خودکار بگیر
            if (!File.Exists(CloudPath) || File.GetLastWriteTime(CloudPath) < DateTime.Now.AddHours(-4))
                MainThread.BeginInvokeOnMainThread(async () => await UpdateOnlineConfigs());
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
            var buttons = new[] { TestAllButton, RefreshButton, RemoveDeadButton, AddConfigButton };
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
                list = list.OrderByDescending(x => x.IsFavorite).ToList();
                if (list.Any())
                {
                    foreach (var s in list) s.Index = idx++;
                    ServerGroups.Add(new ServerGroup($"PERSONAL  ·  {list.Count}", list));
                }
            }

            // 2. آنلاین‌ها (بدون اون‌هایی که کاربر به‌عنوان مرده حذف کرده)
            if (CurrentTab == "All" || CurrentTab == "Online")
            {
                var hidden = ReadHidden();
                var list = _configService.ParseFile(CloudPath, false).Where(s => !hidden.ContainsKey(s.Config)).ToList();
                Decorate(list, ReadJsonDict(CloudSourcesPath), favs);
                var selected = list.OrderByDescending(x => x.IsFavorite).Take(OnlineListSize).ToList();
                if (selected.Any())
                {
                    foreach (var s in selected) s.Index = idx++;
                    ServerGroups.Add(new ServerGroup($"ONLINE  ·  {selected.Count} OF {list.Count}", selected));
                }
            }

            ServerListView.ItemsSource = null; ServerListView.ItemsSource = ServerGroups;

            // انتخاب قبلی بعد از لود دوباره حفظ بشه
            var match = keep == null ? null : AllServers.FirstOrDefault(x => x.Config == keep);
            if (match != null) SelectServer(match);
            else if (_currentServer == null && AllServers.Any()) SelectServer(AllServers.First());

            bool empty = ServerGroups.Count == 0;
            EmptyState.IsVisible = empty;
            ServerListView.IsVisible = !empty;
            EmptyRefreshButton.IsVisible = CurrentTab != "Personal";   // Refresh فقط لیست آنلاین رو پر می‌کنه
            EmptyText.Text = CurrentTab == "Personal"
                ? "Save a config from the Online list, or add your own links."
                : "Refresh the list to download free configs, or add your own links.";
        }

        // --- دکمه‌ها ---
        private async void OnUpdateOnlineClicked(object sender, EventArgs e) => await UpdateOnlineConfigs();
        private async Task UpdateOnlineConfigs()
        {
            if (_isUpdating) return;
            _isUpdating = true;
            StatusLabel.Text = "Downloading the latest configs…";
            try
            {
                // اول از راه پروکسی ویندوز (اگه کاربر به یه VPN وصله)؛ اگه اون پروکسی خراب بود، مستقیم
                (string list, string? sources) data;
                try { data = await DownloadListAsync(useSystemProxy: true); }
                catch { data = await DownloadListAsync(useSystemProxy: false); }

                await File.WriteAllTextAsync(CloudPath, data.list);
                if (data.sources != null) await File.WriteAllTextAsync(CloudSourcesPath, data.sources);
                LoadConfigs();
                int count = data.list.Split('\n').Count(l => l.Contains("://"));
                StatusLabel.Text = $"List updated · {count} online configs · test results kept";
            }
            catch { StatusLabel.Text = "Couldn't download the list. Check your internet connection and press Refresh list again."; }
            finally { _isUpdating = false; }
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
                lines.RemoveAll(l => l.Contains(s.Config));
                await File.WriteAllLinesAsync(UserPath, lines);
                LoadConfigs();
                StatusLabel.Text = $"Deleted {s.City}";
            }
            catch { StatusLabel.Text = "Couldn't delete this config."; }
        }

        // «Remove dead»: کانفیگ‌هایی که تست شدن و جواب ندادن (Ping = -1 یعنی تست نشده)
        // شخصی‌ها پاک می‌شن؛ آنلاین‌ها 24 ساعت پنهان می‌شن و جاشون از لیست پر می‌شه
        private async void OnDeleteDeadConfigsClicked(object sender, EventArgs e)
        {
            var dead = AllServers.Where(s => s.Ping >= 9999).ToList();
            if (!dead.Any())
            {
                await DisplayAlertAsync("Nothing to remove", "No config has failed a test yet. Press “Test all” first, then remove the ones that don't answer.", "OK");
                return;
            }

            var mine = dead.Where(s => s.IsRemovable).ToList();
            var online = dead.Where(s => !s.IsRemovable).ToList();
            var details = new List<string>();
            if (online.Any()) details.Add($"{online.Count} online config{Plural(online.Count)} will be hidden for 24 hours (a config can start working again later).");
            if (mine.Any()) details.Add($"{mine.Count} config{Plural(mine.Count)} will be deleted from your Personal list.");
            if (!await DisplayAlertAsync($"Remove {dead.Count} dead config{Plural(dead.Count)}?", string.Join("\n", details), "Remove", "Cancel")) return;

            try
            {
                if (mine.Any() && File.Exists(UserPath))
                {
                    var gone = mine.Select(s => s.Config).ToList();
                    var lines = (await File.ReadAllLinesAsync(UserPath)).ToList();
                    lines.RemoveAll(l => gone.Any(g => l.Contains(g)));
                    await File.WriteAllLinesAsync(UserPath, lines);
                }
                if (online.Any())
                {
                    var hidden = ReadHidden();
                    foreach (var s in online) hidden[s.Config] = DateTime.UtcNow;
                    await File.WriteAllTextAsync(HiddenPath, JsonSerializer.Serialize(hidden));
                }
                LoadConfigs();
                StatusLabel.Text = $"Removed {dead.Count} dead config{Plural(dead.Count)}" + (online.Any() ? " · new online configs filled their place" : "");
            }
            catch { StatusLabel.Text = "Couldn't remove the dead configs."; }
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
            foreach (var b in new[] { BtnTabAll, BtnTabPersonal, BtnTabOnline })
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

            // فقط گروه همین کانفیگ دوباره مرتب بشه
            foreach (var group in ServerGroups)
            {
                if (!group.Contains(s)) continue;
                var sorted = group.OrderByDescending(x => x.IsFavorite).ToList();
                group.Clear(); foreach (var x in sorted) group.Add(x);
            }
        }

        private HashSet<string> ReadFavorites()
        {
            try { if (File.Exists(FavoritesPath)) return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(FavoritesPath)) ?? new(); } catch { }
            return new();
        }

        private Dictionary<string, DateTime> ReadHidden()
        {
            try
            {
                if (File.Exists(HiddenPath))
                {
                    var all = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(HiddenPath)) ?? new();
                    return all.Where(kv => kv.Value > DateTime.UtcNow.AddHours(-24)).ToDictionary(kv => kv.Key, kv => kv.Value);
                }
            }
            catch { }
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
            }
        }

        // --- xray موقت برای تست یه کانفیگ (پینگ و جمینی) ---
        private async Task<(XrayProc proc, int port, string configPath)> StartTestXray(string link)
        {
            int port = Random.Shared.Next(30000, 60000);
            string configPath = Path.Combine(XrayProc.WorkDir, $"test_{port}.json");
            await File.WriteAllTextAsync(configPath, _configService.GenerateXrayConfig(link, port));
            XrayProc proc;
            try { proc = XrayProc.Start(configPath); }
            catch { try { File.Delete(configPath); } catch { } throw; }
            await XrayProc.WaitForPortAsync(port);
            return (proc, port, configPath);
        }

        // حتماً بعد از هر تست صدا زده بشه، وگرنه xray توی پس‌زمینه باز می‌مونه
        private static void StopTestXray(XrayProc? proc, string? configPath)
        {
            proc?.Dispose();
            try { if (configPath != null) File.Delete(configPath); } catch { }
        }

        // --- تست جمینی (از طریق خود کانفیگ) ---
        private void OnGeminiTestClicked(object sender, EventArgs e) { if (sender is Button btn && btn.CommandParameter is Server s) _ = TestGemini(s); }
        private async Task TestGemini(Server s)
        {
            if (s.GeminiStatus == "testing") return;
            s.GeminiStatus = "testing";
            XrayProc? proc = null; string? configPath = null;
            try
            {
                (proc, int port, configPath) = await StartTestXray(s.Config);
                using var client = XrayProc.SocksClient(port);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var res = await client.GetAsync("https://gemini.google.com/app", HttpCompletionOption.ResponseHeadersRead, cts.Token);
                // اول صفحه کافیه (کد وضعیت و پیام «در کشور شما در دسترس نیست»)؛ دانلود کل صفحه از کانفیگ کند خیلی طول می‌کشه
                string body = await ReadStartAsync(res.Content, 256 * 1024, cts.Token);
                s.GeminiStatus = ClassifyGemini(res.StatusCode, body);
            }
            catch (OperationCanceledException) { s.GeminiStatus = "timeout"; }
            catch { s.GeminiStatus = "failed"; }
            finally { StopTestXray(proc, configPath); }
            _geminiCache[s.Config] = s.GeminiStatus;
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
        private void OnPingSingleItemClicked(object sender, EventArgs e) { if (sender is Button btn && btn.CommandParameter is Server s) _ = PingSingle(s); }

        private async Task PingSingle(Server s)
        {
            if (s.IsPinging) return;
            s.IsPinging = true;
            XrayProc? proc = null; string? configPath = null;
            try
            {
                // اول یه تست سریع TCP (vmess و hysteria2 مستقیم با xray تست می‌شن)
                if (!s.Config.StartsWith("hysteria2") && !s.Config.StartsWith("vmess"))
                {
                    var uri = new Uri(s.Config);
                    using var tcp = new TcpClient();
                    var t = tcp.ConnectAsync(uri.Host, uri.Port == -1 ? 443 : uri.Port);
                    if (await Task.WhenAny(t, Task.Delay(2500)) != t || t.IsFaulted) { s.Ping = 9999; return; }
                }

                // تست واقعی با Xray
                (proc, int port, configPath) = await StartTestXray(s.Config);
                using var client = XrayProc.SocksClient(port, TimeSpan.FromSeconds(4));
                var sw = Stopwatch.StartNew();
                using var res = await client.GetAsync("http://www.gstatic.com/generate_204");
                sw.Stop();
                s.Ping = res.IsSuccessStatusCode ? (int)sw.ElapsedMilliseconds : 9999;
            }
            catch { s.Ping = 9999; }
            finally
            {
                StopTestXray(proc, configPath);
                s.IsPinging = false;
                _pingCache[s.Config] = s.Ping;
                if (s == _currentServer && !isConnected) SetConnState(ConnState.Disconnected);
            }
        }

        private async void OnFindBestServerClicked(object sender, EventArgs e)
        {
            var targets = AllServers;
            if (_isTestingAll || !targets.Any()) return;
            _isTestingAll = true; TestAllButton.IsEnabled = false;
            int done = 0;
            StatusLabel.Text = $"Testing 0 of {targets.Count}…";
            using var sem = new SemaphoreSlim(5);
            await Task.WhenAll(targets.Select(async s =>
            {
                await sem.WaitAsync();
                try { await PingSingle(s); }
                finally { sem.Release(); StatusLabel.Text = $"Testing {++done} of {targets.Count}…"; }
            }));

            foreach (var group in ServerGroups)
            {
                var sorted = group.OrderBy(x => x.Ping <= 0 ? 9999 : x.Ping).ToList();
                group.Clear(); foreach (var x in sorted) group.Add(x);
            }
            var working = AllServers.Where(s => s.Ping > 0 && s.Ping < 9999).OrderBy(s => s.Ping).ToList();
            if (working.Any())
            {
                var best = working.First();
                if (!isConnected) SelectServer(best);
                ServerListView.ScrollTo(best, ScrollToPosition.MakeVisible, true);
                StatusLabel.Text = $"{working.Count} of {targets.Count} configs answered · fastest: {best.City} ({best.Ping} ms)";
            }
            else StatusLabel.Text = "No config answered. Press Refresh list to get new ones.";
            _isTestingAll = false; TestAllButton.IsEnabled = true;
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
                File.WriteAllText(configPath, _configService.GenerateXrayConfig(_currentServer.Config));

#if ANDROID
                // اندروید: سرویس VPN خودش xray و تونل رو بالا میاره
                if (!await VpnController.StartAsync(configPath))
                    throw new Exception(VpnController.LastError ?? "The VPN didn't start.");
#else
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
            foreach (var p in Process.GetProcessesByName("xray")) try { p.Kill(); } catch { }
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
