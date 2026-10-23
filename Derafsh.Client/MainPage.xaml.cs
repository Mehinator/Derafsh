using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Derafsh.Client.Models;
using Derafsh.Client.Services;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace Derafsh.Client
{
    // صفحه‌ی اصلی: دو نما روی یک صفحه
    //   HomeView: دکمه‌ی گرد اتصال، گزینه‌ی «فقط سروری که جمینی را باز می‌کند»، سرور (خودکار یا انتخاب کاربر)
    //   ServersView: لیست‌ها (آنلاین / شخصی / ذخیره‌شده‌ها)، تست، ذخیره، افزودن کانفیگ، انتخاب سرور
    // تست سرورها: MainPage.Testing.cs؛ لیست‌های ذخیره‌شده: MainPage.Lists.cs
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
        private bool _isUpdating, _isTestingAll;

        // کدوم لیست توی صفحه‌ی سرورها نمایش داده می‌شه: ListOnline، ListPersonal یا "saved:" + اسم لیست
        private const string ListOnline = "online", ListPersonal = "personal", SavedPrefix = "saved:";
        private string _currentList = ListOnline;
        private readonly List<string> _pickerKeys = new();

        // true: «اتصال» خودش سریع‌ترین سرور سالم رو پیدا می‌کنه؛ false: سروری که کاربر توی لیست زده
        private bool _autoServer = true;

        private readonly ConfigService _configService = new ConfigService();

        // نتیجه تست‌ها در همین اجرای برنامه (با دانلود لیست یا عوض کردن لیست پاک نمی‌شن)
        private readonly Dictionary<string, string> _geminiCache = new();
        private readonly Dictionary<string, int> _pingCache = new();

        public MainPage()
        {
            InitializeComponent();
            BindingContext = this;
            ServerListView.ItemsSource = ServerGroups;
            BrandLabel.Text = $"DERAFSH  ·  {typeof(MainPage).Assembly.GetName().Version?.ToString(3)}";
            GeminiCheck.IsChecked = Preferences.Default.Get("require_gemini", false);
            RefreshListPicker();
            SetConnState(ConnState.Disconnected);
#if WINDOWS
            RestoreProxy();   // اگه دفعه‌ی قبل برنامه وسط اتصال بسته شده، پروکسی قبلی کاربر برمی‌گرده
#endif
#if ANDROID
            // اندروید: VPN بعد از بستن صفحه هم روشن می‌مونه؛ با باز شدن دوباره وضعیتش رو نشون بده
            DerafshVpnService.StateChanged += () => MainThread.BeginInvokeOnMainThread(SyncVpnState);
            if (DerafshVpnService.IsRunning)
            {
                isConnected = true;
                SetConnState(ConnState.Connected, Preferences.Default.Get("connected_name", "") + "  ·  VPN روشن است");
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
        }

        // ---------- چیدمان و جابه‌جایی بین دو صفحه ----------

        // ستون صفحه‌ی سرورها حداکثر 880 پیکسل و وسط پنجره؛ روی گوشی ردیف‌های فشرده
        private bool? _compact;
        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);
            if (width <= 0) return;
#if ANDROID
            ApplySystemBarInsets();
#endif
            bool compact = width < 640;
            double side = compact ? 14 : Math.Max(24, (width - 880) / 2);
            ServersView.Padding = new Thickness(side, compact ? 12 : 16, side, 8);
            if (_compact != compact)
            {
                _compact = compact;
                // ردیف‌هایی که الان نمایش داده می‌شن قالب قبلی رو نگه می‌دارن؛ لیست دوباره وصل می‌شه تا از نو ساخته بشن
                ServerListView.ItemTemplate = (DataTemplate)Resources[compact ? "CompactRow" : "WideRow"];
                ServerListView.ItemsSource = null;
                ServerListView.ItemsSource = ServerGroups;
            }
        }

#if ANDROID
        // نوار وضعیت (ساعت، باتری) و نوار پایین اندروید روی صفحه می‌افتن؛ اندازه‌ی واقعی‌شون از خود اندروید خونده می‌شه
        private void ApplySystemBarInsets()
        {
            var view = Platform.CurrentActivity?.Window?.DecorView;
            if (view == null) return;
            var insets = AndroidX.Core.View.ViewCompat.GetRootWindowInsets(view);
            if (insets == null) return;
            var bars = insets.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars() | AndroidX.Core.View.WindowInsetsCompat.Type.DisplayCutout());
            double d = DeviceDisplay.MainDisplayInfo.Density;
            if (d <= 0) return;
            var pad = new Thickness(bars.Left / d, bars.Top / d, bars.Right / d, bars.Bottom / d);
            if (RootGrid.Padding != pad) RootGrid.Padding = pad;
        }
#endif

        private void OnOpenServersClicked(object sender, EventArgs e) => ShowServers(true);
        private void OnBackToHomeClicked(object sender, EventArgs e) => ShowServers(false);

        private void ShowServers(bool show)
        {
            ServersView.IsVisible = show;
            HomeView.IsVisible = !show;
            if (!show) UpdateHome();
        }

        // دکمه‌ی برگشت اندروید: از صفحه‌ی سرورها به صفحه‌ی اصلی، نه بیرون از برنامه
        protected override bool OnBackButtonPressed()
        {
            if (ServersView.IsVisible) { ShowServers(false); return true; }
            return base.OnBackButtonPressed();
        }

        private static T Res<T>(string key) => Application.Current!.Resources.TryGetValue(key, out var v) && v is T t ? t : default!;

        // ---------- لیست‌ها ----------

        private void RefreshListPicker()
        {
            _pickerKeys.Clear();
            var titles = new List<string> { "آنلاین — سالم روی همین اینترنت", "شخصی — کانفیگ‌های خودتان" };
            _pickerKeys.AddRange(new[] { ListOnline, ListPersonal });
            foreach (var list in ReadSavedLists().OrderByDescending(l => l.Saved))
            {
                titles.Add($"لیست {Q(list.Name)}");
                _pickerKeys.Add(SavedPrefix + list.Name);
            }
            if (!_pickerKeys.Contains(_currentList)) _currentList = ListOnline;
            _pickerUpdating = true;
            ListPicker.ItemsSource = titles;
            ListPicker.SelectedIndex = _pickerKeys.IndexOf(_currentList);
            _pickerUpdating = false;
            UpdateListActions();
        }
        private bool _pickerUpdating;

        private void OnListPicked(object sender, EventArgs e)
        {
            if (_pickerUpdating || ListPicker.SelectedIndex < 0) return;
            _currentList = _pickerKeys[ListPicker.SelectedIndex];
            UpdateListActions();
            LoadConfigs();
            if (_currentList != ListOnline) _ = TestRowsAsync(onlyUntested: true);
        }

        // هر لیست فقط دکمه‌هایی رو نشون می‌ده که براش معنی دارن
        private void UpdateListActions()
        {
            bool online = _currentList == ListOnline, personal = _currentList == ListPersonal;
            RefreshButton.IsVisible = online;
            AddConfigButton.IsVisible = personal;
            SaveListButton.IsVisible = online || personal;
            DeleteListButton.IsVisible = !online && !personal;
        }

        private void LoadConfigs()
        {
            string? keep = _currentServer?.Config;
            ServerGroups.Clear();
            _onlineGroup = null;
            var favs = ReadFavorites();
            _cloudSources = ReadJsonDict(CloudSourcesPath);

            if (_currentList == ListPersonal)
            {
                var list = PersonalServers(favs);
                ServerGroups.Add(new ServerGroup($"شخصی  ·  {Fa(list.Count)} کانفیگ", Ordered(list)));
            }
            else if (_currentList == ListOnline)
            {
                // فقط کانفیگ‌هایی که روی همین اینترنت جواب دادن (تست خودکار) به‌علاوه‌ی ستاره‌دارها
                var list = _configService.ParseFile(CloudPath, false).DistinctBy(x => x.Config).ToList();
                Decorate(list, _cloudSources, favs);
                _onlineGroup = new ServerGroup("", Ordered(list.Where(x => x.IsFavorite || x.Ping is > 0 and < NoReply)));
                ServerGroups.Add(_onlineGroup);
                UpdateOnlineHeader();
            }
            else AddSavedGroup(_currentList[SavedPrefix.Length..], favs);

            ServerListView.ItemsSource = null; ServerListView.ItemsSource = ServerGroups;

            // سرور انتخابی کاربر بعد از لود دوباره هم علامت‌دار بمونه
            var match = keep == null || _autoServer ? null : AllServers.FirstOrDefault(x => x.Config == keep);
            if (match != null) { foreach (var x in AllServers) x.IsSelected = false; match.IsSelected = true; }

            UpdateEmptyState();
        }

        private List<Server> PersonalServers(HashSet<string> favs)
        {
            var list = _configService.ParseFile(UserPath, true).DistinctBy(x => x.Config).ToList();
            Decorate(list, new(), favs);
            foreach (var s in list) s.Source = "شخصی";
            return list;
        }

        private void UpdateEmptyState()
        {
            bool empty = ServerGroups.All(g => g.Count == 0);
            EmptyState.IsVisible = empty;
            ServerListView.IsVisible = !empty;
            (EmptyTitle.Text, EmptyText.Text) = _currentList switch
            {
                ListPersonal => ("هنوز کانفیگ شخصی ندارید", "با «افزودن کانفیگ» لینک‌های خودتان را اضافه کنید، یا در لیست آنلاین روی «+ شخصی» بزنید."),
                ListOnline when _isUpdating => ("در حال تست سرورها روی اینترنت شما…", "اول جدیدترین کانفیگ‌ها دانلود می‌شوند. سرورهایی که جواب بدهند اینجا می‌آیند، سریع‌ترین بالا."),
                ListOnline when _discovering || (!_discoveryDone && File.Exists(CloudPath)) => ("در حال تست سرورها روی اینترنت شما…", "سرورهایی که جواب بدهند اینجا می‌آیند، سریع‌ترین بالا."),
                ListOnline when !File.Exists(CloudPath) => ("هنوز لیستی دانلود نشده", "«دریافت لیست تازه» را بزنید."),
                ListOnline => ("فعلاً هیچ سروری روی این اینترنت جواب نداد", "«تست دوباره» را بزنید، یا کمی بعد «دریافت لیست تازه»."),
                _ => ("این لیست خالی است", "یک لیست دیگر انتخاب کنید."),
            };
        }

        // ---------- دکمه‌های لیست ----------

        private async void OnUpdateOnlineClicked(object sender, EventArgs e) => await UpdateOnlineConfigs();
        private async Task UpdateOnlineConfigs()
        {
            // وسط «تست دوباره» لیست عوض نمی‌شه: نتیجه‌ها به ردیف‌هایی می‌رسید که دیگه نمایش داده نمی‌شدن
            if (_isUpdating || _isTestingAll) return;
            _isUpdating = true;
            RefreshButton.IsEnabled = false;
            UpdateEmptyState();
            StatusLabel.Text = "در حال دانلود جدیدترین کانفیگ‌ها…";
            try
            {
                // اول از راه پروکسی سیستم (اگه کاربر به یه VPN وصله)؛ اگه اون پروکسی خراب بود، مستقیم
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
                StatusLabel.Text = "لیست دانلود نشد. اتصال اینترنت را بررسی کنید و دوباره «دریافت لیست تازه» را بزنید.";
                return;
            }
            finally { _isUpdating = false; RefreshButton.IsEnabled = true; }
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
            string input = await DisplayPromptAsync("افزودن کانفیگ", "یک یا چند لینک را اینجا بچسبانید (vless، vmess، ss، trojan، hysteria2).", "افزودن", "لغو", initialValue: initial);
            if (string.IsNullOrWhiteSpace(input)) return;
            var existing = File.Exists(UserPath) ? (await File.ReadAllLinesAsync(UserPath)).Select(l => l.Trim()).ToHashSet() : new HashSet<string>();
            var links = regex.Matches(input).Select(m => m.Value.Trim()).Distinct().Where(l => !existing.Contains(l)).ToList();

            if (links.Any())
            {
                await File.AppendAllLinesAsync(UserPath, links);
                LoadConfigs();
                StatusLabel.Text = $"{Fa(links.Count)} کانفیگ به لیست شخصی اضافه شد";
                foreach (var l in links) _ = PingSingle(l);
            }
            else await DisplayAlertAsync("لینک تازه‌ای پیدا نشد", "لینک‌ها باید با vless://، vmess://، ss://، trojan:// یا hysteria2:// شروع شوند. لینک‌هایی که از قبل در لیست شخصی هستند دوباره اضافه نمی‌شوند.", "باشه");
        }

        // ---------- دکمه‌های هر ردیف ----------

        private void OnSaveToPersonalClicked(object sender, EventArgs e) { if (sender is Button btn && btn.CommandParameter is Server s) _ = SaveToPersonal(s); }
        private async Task SaveToPersonal(Server s)
        {
            try
            {
                var lines = File.Exists(UserPath) ? await File.ReadAllLinesAsync(UserPath) : Array.Empty<string>();
                if (lines.Any(l => l.Trim() == s.Config)) { StatusLabel.Text = $"{Q(s.City)} از قبل در لیست شخصی هست"; return; }
                await File.AppendAllLinesAsync(UserPath, new[] { s.Config });
                StatusLabel.Text = $"{Q(s.City)} به لیست شخصی اضافه شد";
            }
            catch { StatusLabel.Text = "این کانفیگ ذخیره نشد."; }
        }

        private void OnDeleteSingleItemClicked(object sender, EventArgs e) { if (sender is Button btn && btn.CommandParameter is Server s) _ = DeleteServer(s); }
        private async Task DeleteServer(Server s)
        {
            if (!await DisplayAlertAsync("این کانفیگ حذف شود؟", $"{Q(s.City)} از لیست شخصی شما حذف می‌شود.", "حذف", "لغو")) return;
            try
            {
                var lines = (await File.ReadAllLinesAsync(UserPath)).ToList();
                lines.RemoveAll(l => l.Trim() == s.Config);
                await File.WriteAllLinesAsync(UserPath, lines);
                LoadConfigs();
                StatusLabel.Text = $"{Q(s.City)} حذف شد";
            }
            catch { StatusLabel.Text = "این کانفیگ حذف نشد."; }
        }

        // زدن روی یه ردیف: همین سرور برای اتصال انتخاب می‌شه (انتخاب دستی)
        private void OnServerSelected(object sender, SelectedItemChangedEventArgs e)
        {
            if (e.SelectedItem is not Server s) return;
            ServerListView.SelectedItem = null;   // انتخاب رو خودمون با حاشیه‌ی طلایی نشون می‌دیم
            if (!_autoServer && _currentServer?.Config == s.Config) return;
            ChooseServer(s);
            if (isConnected)
            {
                StatusLabel.Text = $"در حال رفتن به {Q(s.City)}…";
                Disconnect();
                Task.Delay(500).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() => OnConnectButtonClicked(this, EventArgs.Empty)));
            }
            else StatusLabel.Text = $"{Q(s.City)} انتخاب شد. «اتصال» را بزنید.";
        }

        private void ChooseServer(Server s)
        {
            _autoServer = false;
            foreach (var item in AllServers) item.IsSelected = item.Config == s.Config;
            _currentServer = s;
            UpdateHome();
        }

        private void OnAutoServerClicked(object sender, EventArgs e)
        {
            _autoServer = true;
            foreach (var item in AllServers) item.IsSelected = false;
            if (!isConnected) _currentServer = null;
            UpdateHome();
            StatusLabel.Text = "سرور خودکار انتخاب می‌شود: سریع‌ترین سروری که روی این اینترنت جواب بدهد.";
        }

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

        // منبع، ستاره و نتیجه تست‌های قبلی رو به هر کانفیگ می‌ده
        private void Decorate(List<Server> list, Dictionary<string, string> sources, HashSet<string> favs)
        {
            foreach (var s in list)
            {
                if (sources.TryGetValue(s.Config, out var src)) s.Source = src;
                s.IsFavorite = favs.Contains(s.Config);
                s.GeminiStatus = _geminiCache.TryGetValue(s.Config, out var g) ? g : "";
                s.Ping = _pingCache.TryGetValue(s.Config, out var p) ? p : -1;
                s.IsPinging = _pinging.Contains(s.Config);
                s.IsSelected = !_autoServer && _currentServer?.Config == s.Config;
            }
        }

        // ---------- صفحه‌ی اصلی ----------

        private bool RequireGemini => GeminiCheck.IsChecked;

        private void OnGeminiLabelTapped(object sender, TappedEventArgs e) => GeminiCheck.IsChecked = !GeminiCheck.IsChecked;
        private void OnGeminiOptionChanged(object sender, CheckedChangedEventArgs e)
        {
            Preferences.Default.Set("require_gemini", e.Value);
            UpdateHome();
        }

        // متن‌های صفحه‌ی اصلی: کدوم سرور استفاده می‌شه
        private void UpdateHome()
        {
            AutoServerButton.IsVisible = !_autoServer;
            ServerChoiceLabel.Text = _currentServer == null ? "خودکار (سریع‌ترین)"
                : _autoServer ? (isConnected ? $"خودکار  ·  {Q(_currentServer.City)}" : "خودکار (سریع‌ترین)")
                : _currentServer.City;
            if (!isConnected && !_connecting) SetConnState(ConnState.Disconnected);
        }

        // ---------- اتصال ----------

        private enum ConnState { Disconnected, Finding, Connecting, Connected }
        private bool _connecting, _cancelFind;

        private void SetConnState(ConnState state, string? detail = null)
        {
            string text, stateText, mini;
            bool on = state == ConnState.Connected, busy = state is ConnState.Finding or ConnState.Connecting;
            switch (state)
            {
                case ConnState.Finding:
                    text = "لغو"; stateText = "در حال پیدا کردن سرور…";
                    detail ??= RequireGemini ? "سرورها روی همین اینترنت تست می‌شوند، با جمینی" : "سرورها روی همین اینترنت تست می‌شوند";
                    mini = stateText;
                    break;
                case ConnState.Connecting:
                    text = "در حال اتصال…"; stateText = "در حال اتصال…";
                    detail ??= _currentServer?.City ?? "";
                    mini = $"در حال اتصال به {Q(_currentServer?.City)}…";
                    break;
                case ConnState.Connected:
                    text = "قطع اتصال"; stateText = "وصل هستید";
                    detail ??= "";
                    mini = $"وصل به {Q(_currentServer?.City ?? Preferences.Default.Get("connected_name", ""))}";
                    break;
                default:
                    text = "اتصال"; stateText = "وصل نیستید";
                    detail ??= DisconnectedDetail();
                    mini = _autoServer || _currentServer == null ? "وصل نیستید · سرور خودکار" : $"وصل نیستید · {Q(_currentServer.City)}";
                    break;
            }

            ConnectButton.Style = Res<Style>(on ? "ConnectCircleOn" : "ConnectCircle");
            ConnectButton.Text = text;
            SemanticProperties.SetDescription(ConnectButton, text);
            ConnectButton.IsEnabled = state != ConnState.Connecting;
            ConnRing.Stroke = new SolidColorBrush(Res<Color>(on ? "Success" : busy ? "Warning" : "Hairline"));
            ConnStateLabel.Text = stateText;
            ConnDetailLabel.Text = detail;

            MiniDot.Color = Res<Color>(on ? "Success" : busy ? "Warning" : "TextMuted");
            MiniStateLabel.Text = mini;
            MiniConnectButton.Style = Res<Style>(on ? "SecondaryButton" : "PrimaryButton");
            MiniConnectButton.Text = text;
            MiniConnectButton.IsEnabled = state != ConnState.Connecting;
        }

        private string DisconnectedDetail()
        {
            if (!_autoServer && _currentServer != null)
                return $"سرور انتخابی شما: {Q(_currentServer.City)}" + (_currentServer.Ping is > 0 and < NoReply ? $"  ·  {Ms(_currentServer.Ping)}" : "");
            var best = WorkingServers().FirstOrDefault();
            if (best != null && !RequireGemini) return $"سریع‌ترین سرور الان: {Q(best.City)}  ·  {Ms(best.Ping)}";
            if (_discovering) return "سرورها روی همین اینترنت تست می‌شوند…";
            return RequireGemini ? "سریع‌ترین سروری که جمینی را باز کند انتخاب می‌شود" : "سریع‌ترین سروری که روی همین اینترنت جواب بدهد انتخاب می‌شود";
        }

        private async void OnConnectButtonClicked(object sender, EventArgs e)
        {
            if (_connecting) { _cancelFind = true; return; }   // وسط «پیدا کردن سرور» دکمه «لغو» است
            if (isConnected) { Disconnect(); return; }
            _connecting = true; _cancelFind = false;
            try
            {
                Server? target = _autoServer || _currentServer == null ? await PickBestServerAsync() : _currentServer;
                if (target != null) await ConnectToAsync(target);
            }
            finally
            {
                _connecting = false;
                if (!isConnected) SetConnState(ConnState.Disconnected);
            }
        }

        // سریع‌ترین سرور سالم (و اگه گزینه‌ی جمینی روشنه، سریع‌ترینی که جمینی رو باز می‌کنه).
        // اگه هنوز چیزی پیدا نشده، صبر می‌کنه تا تست خودکار اولین سرورهای سالم رو پیدا کنه.
        private async Task<Server?> PickBestServerAsync()
        {
            SetConnState(ConnState.Finding);
            var sw = Stopwatch.StartNew();
            while (!_cancelFind)
            {
                var working = WorkingServers();
                if (working.Any())
                {
                    if (!RequireGemini) return working.First();
                    var withGemini = await FirstWithGeminiAsync(working);
                    if (withGemini != null) return withGemini;
                }
                bool searching = _discovering || _isUpdating;
                if (!searching && !_discoveryDone) { _ = DiscoverAsync(fresh: false); searching = true; }
                if (!searching || sw.Elapsed > TimeSpan.FromMinutes(3))
                {
                    await DisplayAlertAsync("سرور مناسبی پیدا نشد",
                        RequireGemini && working.Any()
                            ? "سرورهایی که روی این اینترنت جواب می‌دهند جمینی را باز نکردند. تیک جمینی را بردارید، یا کمی بعد دوباره امتحان کنید."
                            : "فعلاً هیچ سروری روی این اینترنت جواب نداد. کمی بعد دوباره «اتصال» را بزنید؛ لیست هر ۴ ساعت تازه می‌شود.",
                        "باشه");
                    return null;
                }
                await Task.Delay(700);
            }
            StatusLabel.Text = "پیدا کردن سرور لغو شد.";
            return null;
        }

        // سرورهای سالم روی همین اینترنت از همه‌ی لیست‌ها (آنلاین، شخصی، ذخیره‌شده‌ها)، سریع‌ترین اول
        private List<Server> WorkingServers()
        {
            var configs = new List<string>();
            try { if (File.Exists(UserPath)) configs.AddRange(File.ReadAllLines(UserPath).Select(l => l.Trim())); } catch { }
            configs.AddRange(_pingCache.Keys);
            var shown = AllServers;
            return configs.Distinct()
                .Where(c => _pingCache.TryGetValue(c, out var p) && p is > 0 and < NoReply)
                .OrderBy(c => _pingCache[c])
                .Select(c => shown.FirstOrDefault(x => x.Config == c) ?? NewServer(c))
                .OfType<Server>()
                .ToList();
        }

        private Server? NewServer(string config)
        {
            var s = _configService.CreateServerObj(config, false);
            if (s != null) Decorate(new List<Server> { s }, _cloudSources, ReadFavorites());
            return s;
        }

        private async Task ConnectToAsync(Server target)
        {
            _currentServer = target;
            SetConnState(ConnState.Connecting);
            try
            {
                if (!XrayProc.EngineExists)
                {
                    await DisplayAlertAsync("موتور Xray پیدا نشد", "فایل موتور Xray کنار برنامه نیست. درفش را دوباره نصب کنید.", "باشه");
                    return;
                }

                string configPath = Path.Combine(XrayProc.WorkDir, "config.json");
                await File.WriteAllTextAsync(configPath, await BuildXrayConfigAsync(target.Config, 10809));

#if ANDROID
                // اندروید: سرویس VPN خودش xray و تونل رو بالا میاره
                if (!await VpnController.StartAsync(configPath, target.City))
                    throw new Exception(VpnController.LastError ?? "VPN روشن نشد.");
#else
                xrayProcess?.Dispose(); xrayProcess = null;
                KillXray();
                xrayProcess = XrayProc.Start(configPath);
                await XrayProc.WaitForPortAsync(10809, 5000);
                EnableProxy();
#endif

                // IP و شهرِ سرور، از داخل خود اتصال
                var (ip, location) = await ExitLocationAsync();
#if ANDROID
                DerafshVpnService.SetLocation(location);   // توی اعلان ثابت VPN هم نشون داده می‌شه
#endif

                isConnected = true;
                Preferences.Default.Set("connected_name", target.City);
                SetConnState(ConnState.Connected, ip != null
                    ? $"{Q(target.City)}  ·  {location}  ·  IP {ip}"
                    : $"{Q(target.City)}  ·  IP بررسی نشد؛ یک سایت را باز کنید تا مطمئن شوید");
                StatusLabel.Text = "";
                UpdateHome();
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("وصل نشد", $"{ex.Message}\nدوباره «اتصال» را بزنید تا سرور دیگری امتحان شود.", "باشه");
                if (_pingCache.ContainsKey(target.Config)) _pingCache[target.Config] = NoReply;   // دفعه‌ی بعد سراغ سرور بعدی بره
                Disconnect();
            }
        }

        // IP و «شهر، کشور» سرور از راه خود اتصال؛ اول ipwho.is (شهر هم داره)، اگه جواب نداد api.country.is (فقط کشور)
        private static async Task<(string? ip, string? location)> ExitLocationAsync()
        {
            using var client = XrayProc.SocksClient(10809, TimeSpan.FromSeconds(8));
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

#if ANDROID
        // VPN از اعلان («قطع اتصال») یا تنظیمات اندروید قطع شده: صفحه هم «وصل نیستید» بشه
        private void SyncVpnState()
        {
            if (isConnected && !_connecting && !DerafshVpnService.IsRunning)
            {
                isConnected = false;
                if (_autoServer) _currentServer = null;
                SetConnState(ConnState.Disconnected);
                UpdateHome();
            }
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            SyncVpnState();
            // اندازه‌ی نوارهای سیستم گاهی بعد از اولین چیدمان آماده می‌شه
            ApplySystemBarInsets();
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(400), ApplySystemBarInsets);
        }
#endif

        private void Disconnect()
        {
#if ANDROID
            VpnController.Stop();
#else
            DisableProxy(); xrayProcess?.Dispose(); xrayProcess = null; KillXray();
#endif
            isConnected = false;
            if (_autoServer) _currentServer = null;
            SetConnState(ConnState.Disconnected);
            UpdateHome();
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
            RestoreProxy();   // فقط اگه خود درفش پروکسی رو عوض کرده بود
            KillXray();
#endif
        }

#if WINDOWS
        // پروکسی ویندوز: موقع اتصال تنظیم قبلی کاربر (مثلاً پروکسی یه VPN دیگه) ذخیره می‌شه و موقع قطع همون برمی‌گرده؛
        // قبلاً پروکسی کلاً خاموش می‌شد و تنظیم کاربر از دست می‌رفت. اگه برنامه وسط اتصال بسته بشه، دفعه‌ی بعد برمی‌گرده.
        private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private const string DerafshProxy = "socks=127.0.0.1:10809";

        private static void EnableProxy()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(InternetSettings, true);
                if (key == null) return;
                if (!s_proxyEnabled && key.GetValue("ProxyServer") as string != DerafshProxy)
                {
                    Preferences.Default.Set("prev_proxy_enable", key.GetValue("ProxyEnable") is int on ? on : 0);
                    Preferences.Default.Set("prev_proxy_server", key.GetValue("ProxyServer") as string ?? "");
                    Preferences.Default.Set("prev_proxy_saved", true);
                }
                key.SetValue("ProxyEnable", 1);
                key.SetValue("ProxyServer", DerafshProxy);
                s_proxyEnabled = true;
                RefreshProxy();
            }
            catch { }
        }

        private static void DisableProxy() => RestoreProxy();

        private static void RestoreProxy()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(InternetSettings, true);
                if (key == null) return;
                // پروکسی الان مال درفش نیست (کاربر یا برنامه‌ی دیگه عوضش کرده): دست نمی‌زنیم
                if (key.GetValue("ProxyServer") as string != DerafshProxy) { s_proxyEnabled = false; return; }
                if (Preferences.Default.Get("prev_proxy_saved", false))
                {
                    key.SetValue("ProxyEnable", Preferences.Default.Get("prev_proxy_enable", 0));
                    key.SetValue("ProxyServer", Preferences.Default.Get("prev_proxy_server", ""));
                }
                else key.SetValue("ProxyEnable", 0);
                Preferences.Default.Remove("prev_proxy_saved");
                s_proxyEnabled = false;
                RefreshProxy();
            }
            catch { }
        }

        private static void RefreshProxy() { InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); }
#else
        private static void EnableProxy() {} private static void DisableProxy() {}
#endif

        // اسم سرورها و لیست‌ها اغلب لاتینه («@filembad»)؛ داخل جمله‌ی فارسی بدون «جداکننده‌ی جهت» (FSI…PDI)
        // ترتیب کلمه‌ها به هم می‌ریخت. FSI…PDI برای اندروید، و علامت راست‌به‌چپ (RLM) بعدش برای ویندوز که FSI رو نمی‌شناسه
        public static string Q(string? name) => $"\u200F«\u2068\u200E{name}\u200E\u2069»\u200F";
        public static string Ms(int ms) => $"\u200E{ms}\u00A0ms\u200E\u200F";

        // عدد با رقم فارسی، برای جمله‌های فارسی (پینگ‌ها لاتین می‌مونن تا ستونی مرتب باشن)
        public static string Fa(int n) => string.Concat(n.ToString().Select(c => char.IsAsciiDigit(c) ? (char)('۰' + (c - '0')) : c));
    }
}
