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
    // صفحه‌ی اصلی: چند نما روی یک صفحه، هر بار یکی
    //   HomeView: دکمه‌ی گرد اتصال، «تعویض سرور»، و مکان سرور (یه کشور یا شهر، یا سروری که کاربر دستی زده)
    //   LocationsView: انتخاب کشور یا شهر (MainPage.Locations.cs)
    //   ServersView: لیست‌ها (آنلاین / شخصی / ذخیره‌شده‌ها)، تست، افزودن کانفیگ، انتخاب سرور
    //   SettingsView: «ایرانی‌ها بدون VPN»، «فقط Gemini»، لینک اشتراک، کانال تلگرام
    //   AppsView (اندروید): کدوم برنامه‌ها از VPN استفاده کنن (از تنظیمات)
    // تست سرورها: MainPage.Testing.cs؛ لیست‌های ذخیره‌شده: MainPage.Lists.cs
    public partial class MainPage : ContentPage
    {
        // لیست آنلاین از چند آینه: روی بعضی اینترنت‌ها (مثلاً وای‌فای مخابرات) گیت‌هاب یا jsDelivr بسته‌ست.
        // همه با هم امتحان می‌شن و اولین جواب سالم برداشته می‌شه (github.io فقط اگه GitHub Pages روشن باشه جواب می‌ده).
        private static readonly string[] ListMirrors =
        {
            "https://cdn.jsdelivr.net/gh/Mehinator/Derafsh@main/",
            "https://fastly.jsdelivr.net/gh/Mehinator/Derafsh@main/",
            "https://gcore.jsdelivr.net/gh/Mehinator/Derafsh@main/",
            "https://testingcf.jsdelivr.net/gh/Mehinator/Derafsh@main/",
            "https://raw.githubusercontent.com/Mehinator/Derafsh/main/",
            "https://raw.githack.com/Mehinator/Derafsh/main/",
            "https://mehinator.github.io/Derafsh/",
        };
        // لینک اشتراک برای برنامه‌های دیگر (v2rayNG، Hiddify، V2Box، Streisand)
        public const string SubscriptionUrl = "https://cdn.jsdelivr.net/gh/Mehinator/Derafsh@main/derafsh_sub.txt";
        private const string TelegramUrl = "https://t.me/DERAFSHVPN", GitHubUrl = "https://github.com/Mehinator/Derafsh";
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
        private readonly Dictionary<string, int> _speedCache = new();   // سرعت دانلود، کیلوبایت بر ثانیه

        public MainPage()
        {
            InitializeComponent();
            BindingContext = this;
            ServerListView.ItemsSource = ServerGroups;
            BrandLabel.Text = $"DERAFSH  ·  {typeof(MainPage).Assembly.GetName().Version?.ToString(3)}";
            VersionLabel.Text = BrandLabel.Text;
            GeminiCheck.IsChecked = Preferences.Default.Get("require_gemini", false);
            InitIranOption();
            RefreshListPicker();
            SetConnState(ConnState.Disconnected);
#if WINDOWS
            RestoreProxy();   // اگه دفعه‌ی قبل برنامه وسط اتصال بسته شده، پروکسی قبلی کاربر برمی‌گرده
#endif
#if ANDROID
            // اندروید: VPN بعد از بستن صفحه هم روشن می‌مونه؛ با باز شدن دوباره وضعیتش رو نشون بده
            DerafshVpnService.StateChanged += () => MainThread.BeginInvokeOnMainThread(SyncVpnState);
            // نگهبان اتصالِ سرویس VPN به زاپاس رفته یا هیچ زاپاسی جواب نداده: صفحه هم بدونه
            DerafshVpnService.ServerSwitched += (spare, reason) => MainThread.BeginInvokeOnMainThread(() => OnServerSwitched(spare, reason));
            DerafshVpnService.ConnectionLost += reason => MainThread.BeginInvokeOnMainThread(() => OnConnectionLost(reason));
            if (DerafshVpnService.IsRunning)
            {
                isConnected = true;
                SetConnState(ConnState.Connected, Preferences.Default.Get("connected_name", "") + "  ·  VPN روشن است");
            }
#endif
            StartUpdateChecks();
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                LoadConfigs();
                // لیست آنلاین هر ۲ ساعت روی گیت‌هاب آپدیت می‌شه؛ اگه نسخه ما قدیمی‌تره اول دانلود (که خودش تست رو شروع می‌کنه)،
                // وگرنه همین الان تست خودکار روی همین اینترنت
                if (!File.Exists(CloudPath) || File.GetLastWriteTime(CloudPath) < DateTime.Now.AddHours(-2)) await UpdateOnlineConfigs();
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

        private void OnOpenServersClicked(object sender, EventArgs e) => ShowView(ServersView);
        private void OnBackToHomeClicked(object sender, EventArgs e) => ShowView(HomeView);
        private void OnOpenSettingsClicked(object sender, EventArgs e) => ShowView(SettingsView);
        private void OnSettingsBackClicked(object sender, EventArgs e) => ShowView(HomeView);

        private void ShowView(View view)
        {
            foreach (var v in new View[] { HomeView, LocationsView, ServersView, SettingsView, AppsView }) v.IsVisible = v == view;
            if (view == HomeView) UpdateHome();
        }

        // دکمه‌ی برگشت اندروید: به صفحه‌ی قبلی، نه بیرون از برنامه
        protected override bool OnBackButtonPressed()
        {
            if (AppsView.IsVisible) { CloseApps(); return true; }
            if (!HomeView.IsVisible) { ShowView(HomeView); return true; }
            return base.OnBackButtonPressed();
        }

        private async void OnOpenTelegramClicked(object sender, EventArgs e) => await OpenLinkAsync(TelegramUrl);
        private async void OnOpenGitHubClicked(object sender, EventArgs e) => await OpenLinkAsync(GitHubUrl);

        private async Task OpenLinkAsync(string url)
        {
            try { await Launcher.OpenAsync(url); }
            catch { StatusLabel.Text = $"باز نشد. این آدرس را خودتان باز کنید: {url}"; }
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
                // فقط کانفیگ‌هایی که روی همین اینترنت جواب دادن (تست خودکار)، توی مکان انتخابی، به‌علاوه‌ی ستاره‌دارها
                var list = _configService.ParseFile(CloudPath, false).DistinctBy(x => x.Config).ToList();
                Decorate(list, _cloudSources, favs);
                _onlineGroup = new ServerGroup("", Ordered(list.Where(x => x.IsFavorite || (x.Ping is > 0 and < NoReply && Locations.Matches(x.Config)))));
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
                ListOnline when _discoveryDone && !_discovering && Locations.Selected.Length > 0 =>
                    ($"در {Locations.Describe(Locations.Selected)} فعلاً سروری روی این اینترنت جواب نداد", "در صفحه‌ی اصلی مکان دیگری انتخاب کنید، یا «تست دوباره» را بزنید."),
                ListOnline when _discovering || (!_discoveryDone && File.Exists(CloudPath)) => ("در حال تست سرورها روی اینترنت شما…", "سرورهایی که جواب بدهند اینجا می‌آیند، سریع‌ترین بالا."),
                ListOnline when !File.Exists(CloudPath) => ("هنوز لیستی دانلود نشده", "«دریافت لیست تازه» را بزنید."),
                ListOnline => ("فعلاً هیچ سروری روی این اینترنت جواب نداد", "«تست دوباره» را بزنید، یا کمی بعد «دریافت لیست تازه»."),
                _ => ("این لیست خالی است", "یک لیست دیگر انتخاب کنید."),
            };
        }

        // ---------- دکمه‌های لیست ----------

        private async void OnUpdateOnlineClicked(object sender, EventArgs e) => await UpdateOnlineConfigs();
        private bool _listDownloadFailed;   // آخرین بار هیچ آینه‌ای جواب نداد؛ لیست تازه از داخل اولین سرور سالم گرفته می‌شه

        private async Task UpdateOnlineConfigs()
        {
            // وسط «تست دوباره» لیست عوض نمی‌شه: نتیجه‌ها به ردیف‌هایی می‌رسید که دیگه نمایش داده نمی‌شدن
            if (_isUpdating || _isTestingAll) return;
            _isUpdating = true;
            RefreshButton.IsEnabled = false;
            UpdateEmptyState();
            StatusLabel.Text = "در حال دانلود جدیدترین کانفیگ‌ها…";
            bool ok = false;
            try
            {
                // اول از راه پروکسی سیستم (اگه کاربر به یه VPN وصله)، بعد مستقیم، بعد از داخل اتصال خود درفش (اگه وصلیم)
                for (int way = 0; way < 3 && !ok; way++)
                {
                    if (way == 2 && !isConnected) break;
                    try { await SaveListAsync(await DownloadListAsync(useSystemProxy: way == 0, socksPort: way == 2 ? XrayProc.SessionPort : null)); ok = true; }
                    catch { }
                }
                // اولین اجرا و هیچ آینه‌ای باز نیست: لیستی که همراه خود برنامه اومده
                if (!ok && !File.Exists(CloudPath)) await UseSeedListAsync();
            }
            finally { _isUpdating = false; RefreshButton.IsEnabled = true; }

            _listDownloadFailed = !ok;
            if (!File.Exists(CloudPath))
            {
                StatusLabel.Text = "لیست دانلود نشد. اتصال اینترنت را بررسی کنید و دوباره «دریافت لیست تازه» را بزنید.";
                return;
            }
            if (!ok) StatusLabel.Text = "لیست تازه دانلود نشد (روی این اینترنت گیت‌هاب و آینه‌هایش بسته‌اند). لیست قبلی تست می‌شود و لیست تازه از داخل اولین سرور سالم گرفته می‌شود.";
            await DiscoverAsync(fresh: false);   // کانفیگ‌هایی که همین الان تست شدن دوباره تست نمی‌شن
            if (!ok) await FetchListThroughServerAsync();
        }

        private async Task SaveListAsync((string list, string? sources, string? locations) data)
        {
            await File.WriteAllTextAsync(CloudPath, data.list);
            if (data.sources != null) await File.WriteAllTextAsync(CloudSourcesPath, data.sources);
            if (data.locations != null) await File.WriteAllTextAsync(Locations.FilePath, data.locations);
            _discoverGen++;   // تست خودکارِ لیست قبلی دیگه ردیف اضافه نمی‌کنه
            _discovering = false;
            LoadConfigs();
        }

        // لیست همون روزی که این نسخه ساخته شده (همراه برنامه). کهنه‌ست ولی برای شروع کافیه: اگه حتی یکی‌ش کار کنه،
        // لیست تازه از داخل همون گرفته می‌شه. تاریخش قدیمی گذاشته می‌شه تا دفعه‌ی بعد دوباره دانلود امتحان بشه.
        private async Task UseSeedListAsync()
        {
            try
            {
                using (var src = await FileSystem.OpenAppPackageFileAsync("seed_servers.txt"))
                using (var dst = File.Create(CloudPath))
                    await src.CopyToAsync(dst);
                File.SetLastWriteTime(CloudPath, DateTime.Now.AddDays(-1));
                _discoverGen++;
                _discovering = false;
                LoadConfigs();
            }
            catch { }
        }

        // آینه‌ها روی این اینترنت بسته‌ان: لیست تازه از داخل اتصال خود درفش، یا اگه وصل نیستیم، از داخل سریع‌ترین سرور سالم
        // (با یه xray موقت). بعدش سرورهای لیست تازه تست می‌شن.
        private async Task FetchListThroughServerAsync()
        {
            if (!_listDownloadFailed || _isUpdating) return;
            var best = isConnected ? null : WorkingServers(anyLocation: true).FirstOrDefault();
            if (!isConnected && best == null) return;
            _isUpdating = true;
            XrayProc? proc = null; int port = XrayProc.SessionPort; string? configPath = null;
            try
            {
                if (best != null) (proc, port, configPath) = await StartTestXray(best.Config);
                var data = await DownloadListAsync(socksPort: port);
                StopTestXray(proc, port, configPath); proc = null;
                await SaveListAsync(data);
                _listDownloadFailed = false;
                if (!_connecting) StatusLabel.Text = "لیست تازه از داخل یک سرور سالم گرفته شد.";
            }
            catch { return; }
            finally
            {
                if (proc != null) StopTestXray(proc, port, configPath);
                _isUpdating = false;
            }
            await DiscoverAsync(fresh: false);
        }

        private static async Task<(string list, string? sources, string? locations)> DownloadListAsync(bool useSystemProxy = false, int? socksPort = null)
        {
            var timeout = TimeSpan.FromSeconds(socksPort != null ? 25 : useSystemProxy ? 12 : 20);
            using var client = socksPort is int port ? XrayProc.SocksClient(port, timeout)
                                                     : new HttpClient(new HttpClientHandler { UseProxy = useSystemProxy }) { Timeout = timeout };
            var (list, mirror) = await FetchNewestListAsync(client);
            // منبع هر کانفیگ و مکانش (کشور و شهر) اختیاری‌ان؛ از همون آینه‌ای که لیست اومد، تا با همون نسخه جور باشن
            string? sources = await FetchJsonAsync(client, mirror, "derafsh_sources.json");
            string? locations = await FetchJsonAsync(client, mirror, "derafsh_locations.json");
            return (list, sources, locations);
        }

        private static bool IsJsonObject(string text) => text.TrimStart().StartsWith('{') && text.TrimEnd().EndsWith('}');

        private static async Task<string?> FetchJsonAsync(HttpClient client, string mirror, string file)
        {
            try
            {
                string text = await client.GetStringAsync(mirror + file);
                if (IsJsonObject(text)) return text;
            }
            catch { }
            try { return await FetchAnyMirrorAsync(client, file, IsJsonObject); } catch { return null; }
        }

        // لیست از همه‌ی آینه‌ها با هم. بعضی آینه‌ها (fastly، gcore، githack) تا مدتی نسخه‌ی قبلی رو می‌دن؛ بعد از اولین جواب
        // سالم، ۲ ثانیه برای بقیه صبر می‌شه و تازه‌ترین برداشته می‌شه (زمان ساخت توی خط اول: «# Derafsh 2026-11-12T15:09:20Z»).
        // لیست کامل ۵۰۰ خطه؛ کمتر از ۲۰۰ یعنی نصفه رسیده (بعضی آینه‌ها فایل بزرگ رو نصفه می‌دن).
        private static async Task<(string text, string mirror)> FetchNewestListAsync(HttpClient client)
        {
            var cts = new CancellationTokenSource(client.Timeout);
            async Task<(string? text, string mirror)> Fetch(string mirror)
            {
                try
                {
                    using var res = await client.GetAsync(mirror + "derafsh_servers.txt", HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (!res.IsSuccessStatusCode) return (null, mirror);
                    string text = await res.Content.ReadAsStringAsync(cts.Token);
                    return (text.Count(c => c == '\n') >= 200 && text.Contains("://") ? text : null, mirror);
                }
                catch { return (null, mirror); }
            }
            var pending = ListMirrors.Select(Fetch).ToList();
            (string text, string mirror)? best = null;
            DateTime? graceEnd = null;
            while (pending.Count > 0)
            {
                var next = Task.WhenAny(pending);
                if (graceEnd is { } end)
                {
                    var left = end - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || await Task.WhenAny(next, Task.Delay(left)) != next) break;
                }
                var done = await next;
                pending.Remove(done);
                var (text, mirror) = await done;
                if (text == null) continue;
                if (best == null || string.CompareOrdinal(Stamp(text), Stamp(best.Value.text)) > 0) best = (text, mirror);
                graceEnd ??= DateTime.UtcNow.AddSeconds(2);
            }
            cts.Cancel();
            return best ?? throw new HttpRequestException("No mirror answered.");

            static string Stamp(string list)
            {
                var m = Regex.Match(list.Length > 200 ? list[..200] : list, @"^# Derafsh (\S+)");
                return m.Success ? m.Groups[1].Value : "";   // لیست‌های قدیمی این خط رو ندارن
            }
        }

        // همه‌ی آینه‌ها با هم؛ اولین جوابی که سالم برسه برداشته می‌شه و بقیه لغو می‌شن
        private static async Task<string> FetchAnyMirrorAsync(HttpClient client, string file, Func<string, bool> valid)
        {
            using var cts = new CancellationTokenSource(client.Timeout);
            var pending = ListMirrors.Select(m => client.GetAsync(m + file, HttpCompletionOption.ResponseHeadersRead, cts.Token)).ToList();
            try
            {
                while (pending.Count > 0)
                {
                    var first = await Task.WhenAny(pending);
                    pending.Remove(first);
                    if (!first.IsCompletedSuccessfully) continue;
                    using var res = first.Result;
                    if (!res.IsSuccessStatusCode) continue;
                    try { string text = await res.Content.ReadAsStringAsync(cts.Token); if (valid(text)) return text; }
                    catch { }
                }
                throw new HttpRequestException("No mirror answered.");
            }
            finally
            {
                cts.Cancel();
                foreach (var t in pending) _ = t.ContinueWith(x => { if (x.IsCompletedSuccessfully) x.Result.Dispose(); });
            }
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

        // ---------- اشتراک‌گذاری کانفیگ‌ها ----------

        // اشتراک یک کانفیگ: QR (برای اسکن با گوشی دیگه) به‌علاوه‌ی «کپی لینک» و «فرستادن»
        private async Task ShareServerAsync(Server s) =>
            await Navigation.PushModalAsync(new QrPage(string.IsNullOrEmpty(s.City) ? "کانفیگ" : s.City, s.Config,
                "با دوربین برنامه‌ی VPN یک گوشی دیگر اسکن کنید، مثلاً v2rayNG یا Hiddify یا V2Box یا Streisand. یا لینک را کپی کنید یا بفرستید."));

        // لینک اشتراک درفش (برای آیفون و برنامه‌های دیگر)، با QR
        private async void OnShareSubscriptionClicked(object sender, EventArgs e) =>
            await Navigation.PushModalAsync(new QrPage("لینک اشتراک درفش", SubscriptionUrl,
                "برای آیفون و برنامه‌های دیگر: این QR را در Hiddify یا V2Box یا Streisand یا v2rayNG اسکن کنید، یا لینک را به‌عنوان اشتراک اضافه کنید. لیست هر ۲ ساعت خودکار تازه می‌شود."));

        // اشتراک همه‌ی سرورهایی که الان جواب می‌دن، یکجا (هر لینک در یک خط)
        private async void OnShareWorkingClicked(object sender, EventArgs e)
        {
            var working = WorkingServers(anyLocation: true).Select(s => s.Config).Distinct().ToList();
            if (!working.Any())
            {
                await DisplayAlertAsync("هنوز سرور سالمی نیست", "فقط سرورهایی اشتراک گذاشته می‌شوند که در تست جواب داده‌اند. صبر کنید تست تمام شود، یا «تست دوباره» را بزنید.", "باشه");
                return;
            }
            try
            {
                await Share.Default.RequestAsync(new ShareTextRequest
                {
                    Text = string.Join("\n", working),
                    Title = $"Derafsh — {working.Count} working server(s)",
                });
                StatusLabel.Text = $"{Fa(working.Count)} سرور سالم برای اشتراک آماده شد";
            }
            catch { StatusLabel.Text = "اشتراک‌گذاری انجام نشد."; }
        }

        // ---------- کارهای هر ردیف (دکمه‌ی «⋯») و کارهای کم‌استفاده‌ی لیست («بیشتر…») ----------

        private async void OnRowMoreClicked(object sender, EventArgs e)
        {
            if (sender is not Button { CommandParameter: Server s }) return;
            const string Connect = "اتصال به همین سرور", Test = "تست سرعت و پینگ", Gemini = "بررسی Gemini", ShareIt = "اشتراک: QR و لینک",
                         Save = "افزودن به لیست شخصی", Delete = "حذف از لیست شخصی";
            string star = s.IsFavorite ? "برداشتن ستاره" : "ستاره (همیشه اول لیست)";
            var buttons = new List<string> { Connect, Test, Gemini, ShareIt, star };
            if (s.ShowSave) buttons.Add(Save);
            string? choice = await DisplayActionSheetAsync(s.City, "لغو", s.IsRemovable ? Delete : null, buttons.ToArray());
            switch (choice)
            {
                case Connect: SelectServer(s); break;
                case Test: _ = PingSingle(s.Config); break;
                case Gemini: _ = TestGemini(s.Config); break;
                case ShareIt: await ShareServerAsync(s); break;
                case Save: await SaveToPersonal(s); break;
                case Delete: await DeleteServer(s); break;
                default: if (choice == star) ToggleFavorite(s); break;
            }
        }

        private async void OnListMoreClicked(object sender, EventArgs e)
        {
            bool online = _currentList == ListOnline, personal = _currentList == ListPersonal;
            const string SaveList = "ذخیره‌ی سالم‌ها به‌عنوان لیست", ShareWorking = "اشتراک همه‌ی سالم‌ها", DeleteList = "حذف این لیست";
            var buttons = new List<string>();
            if (online || personal) buttons.Add(SaveList);
            buttons.Add(ShareWorking);
            string? choice = await DisplayActionSheetAsync("این لیست", "لغو", online || personal ? null : DeleteList, buttons.ToArray());
            switch (choice)
            {
                case SaveList: OnSaveListClicked(sender, e); break;
                case ShareWorking: OnShareWorkingClicked(sender, e); break;
                case DeleteList: OnDeleteSavedListClicked(sender, e); break;
            }
        }

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
            SelectServer(s);
        }

        private void SelectServer(Server s)
        {
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

        private void ToggleFavorite(Server s)
        {
            var favs = ReadFavorites();
            s.IsFavorite = !s.IsFavorite;
            if (s.IsFavorite) favs.Add(FavKey(s.Config)); else favs.Remove(FavKey(s.Config));
            try { File.WriteAllText(FavoritesPath, JsonSerializer.Serialize(favs.ToList())); } catch { }

            foreach (var x in RowsOf(s.Config)) x.IsFavorite = s.IsFavorite;
            ResortList();
        }

        // ستاره‌دارها: لینک بدون اسم، چون اسم کانفیگ‌های لیست آنلاین هر بار ممکنه عوض بشه («🇩🇪 Berlin 2» → «🇩🇪 Berlin 3»)
        private HashSet<string> ReadFavorites()
        {
            try { if (File.Exists(FavoritesPath)) return (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FavoritesPath)) ?? new()).Select(FavKey).ToHashSet(); } catch { }
            return new();
        }

        private static string FavKey(string link) => link.Split('#')[0].Trim();

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
                s.IsFavorite = favs.Contains(FavKey(s.Config));
                s.GeminiStatus = _geminiCache.TryGetValue(s.Config, out var g) ? g : "";
                s.Ping = _pingCache.TryGetValue(s.Config, out var p) ? p : -1;
                s.Speed = _speedCache.TryGetValue(s.Config, out var sp) ? sp : -1;
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
            UpdatePool();   // وصلیم: صف زاپاس‌های نگهبان با همین گزینه
        }

        // متن‌های صفحه‌ی اصلی: «اتصال» کجا وصل می‌شه (یه کشور یا شهر، یا سروری که کاربر دستی زده)
        private void UpdateHome()
        {
            bool manual = !_autoServer && _currentServer != null;
            AutoServerButton.IsVisible = manual;
            LocationCaptionLabel.Text = manual ? "سروری که خودتان انتخاب کردید" : "مکان سرور";
            string selected = Locations.Selected;
            LocationTitleLabel.Text = manual ? _currentServer!.City
                : selected.Length == 0 ? "خودکار، هر کشوری"
                : $"{Locations.Flag(Locations.Split(selected).cc)} {Locations.Describe(selected)}".Trim();
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
                    detail ??= (Locations.Selected.Length > 0 ? $"سرورهای {Locations.Describe(Locations.Selected)} روی همین اینترنت تست می‌شوند" : "سرورها روی همین اینترنت تست می‌شوند")
                               + (RequireGemini ? "، همراه با Gemini" : "");
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
            ManualSwitchButton.IsVisible = on;

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
            if (best != null && !RequireGemini) return $"بهترین سرور الان: {Q(best.City)}  ·  " + (best.Speed > 0 ? $"{Kbs(best.Speed)}  ·  " : "") + Ms(best.Ping);
            if (_discovering)
                return Locations.Selected.Length > 0 ? $"سرورهای {Locations.Describe(Locations.Selected)} روی همین اینترنت تست می‌شوند…" : "سرورها روی همین اینترنت تست می‌شوند…";
            string where = Locations.Selected.Length > 0 ? $" در {Locations.Describe(Locations.Selected)}" : "";
            return RequireGemini ? $"سریع‌ترین سروری{where} که Gemini را باز کند انتخاب می‌شود" : $"سریع‌ترین سروری{where} که روی همین اینترنت جواب بدهد انتخاب می‌شود";
        }

        private async void OnConnectButtonClicked(object sender, EventArgs e)
        {
            if (_connecting) { _cancelFind = true; return; }   // وسط «پیدا کردن سرور» دکمه «لغو» است
            if (isConnected) { Disconnect(); return; }
            _connecting = true; _cancelFind = false;
            try
            {
                if (!_autoServer && _currentServer != null) { await ConnectToAsync(_currentServer, quiet: false); return; }

                // خودکار: اگه سرور انتخاب‌شده بعد از وصل شدن جواب نداد، سرور بعدی (تا سه تا)
                for (int attempt = 1; attempt <= 3 && !_cancelFind; attempt++)
                {
                    var target = await PickBestServerAsync();
                    if (target == null) return;
                    if (await ConnectToAsync(target, quiet: attempt < 3)) return;
                    StatusLabel.Text = $"{Q(target.City)} جواب نداد؛ سرور بعدی امتحان می‌شود…";
                }
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
                    // توی مکان انتخابی هیچی جواب نداد: کل همون کشور، یا هر کشوری
                    string selected = Locations.Selected;
                    if (selected.Length > 0 && !(RequireGemini && working.Any()))
                    {
                        var (cc, city) = Locations.Split(selected);
                        string wider = $"هر شهری در {Locations.CountryName(cc)}", anywhere = "خودکار، هر کشوری";
                        var options = city.Length > 0 ? new[] { wider, anywhere } : new[] { anywhere };
                        string? pick = await DisplayActionSheetAsync($"در {Locations.Describe(selected)} فعلاً سروری روی این اینترنت جواب نداد. کجا وصل شود؟",
                                                                     "لغو", null, options);
                        if (pick != wider && pick != anywhere) { StatusLabel.Text = "اتصال لغو شد. می‌توانید مکان دیگری انتخاب کنید."; return null; }
                        Locations.Selected = pick == anywhere ? "" : cc;
                        UpdateHome();
                        LoadConfigs();
                        _discoveryDone = false;
                        _ = DiscoverAsync(fresh: false);
                        SetConnState(ConnState.Finding);
                        sw.Restart();
                        continue;
                    }
                    await DisplayAlertAsync("سرور مناسبی پیدا نشد",
                        RequireGemini && working.Any()
                            ? "سرورهایی که روی این اینترنت جواب می‌دهند Gemini را باز نکردند. تیک Gemini را بردارید، یا کمی بعد دوباره امتحان کنید."
                            : "فعلاً هیچ سروری روی این اینترنت جواب نداد. کمی بعد دوباره «اتصال» را بزنید؛ لیست هر ۲ ساعت تازه می‌شود.",
                        "باشه");
                    return null;
                }
                await Task.Delay(700);
            }
            StatusLabel.Text = "پیدا کردن سرور لغو شد.";
            return null;
        }

        // سرورهای سالم روی همین اینترنت از همه‌ی لیست‌ها (آنلاین، شخصی، ذخیره‌شده‌ها)، سریع‌ترین اول؛ فقط مکان انتخابی
        private List<Server> WorkingServers(bool anyLocation = false)
        {
            var configs = new List<string>();
            try { if (File.Exists(UserPath)) configs.AddRange(File.ReadAllLines(UserPath).Select(l => l.Trim())); } catch { }
            configs.AddRange(_pingCache.Keys);
            var shown = AllServers;
            // سرعت مهم‌تر از پینگه (ویدیو با سرعت کم وسطش می‌ایسته)؛ بعد اون‌هایی که روی همین اینترنت قبلاً خوب بودن؛ بعد پینگ
            return configs.Distinct()
                .Where(c => _pingCache.TryGetValue(c, out var p) && p is > 0 and < NoReply)
                .Where(c => anyLocation || Locations.Matches(c))
                .OrderBy(c => SpeedBucket(_speedCache.GetValueOrDefault(c, -1)))
                .ThenByDescending(c => NetworkHistory.Score(c) > 0.5)
                .ThenBy(c => _pingCache[c])
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

        // وصل شدن به یه سرور و اطمینان از اینکه واقعاً از داخلش اینترنت باز می‌شه.
        // quiet: اگه نشد پیغام نده (حالت خودکار سرور بعدی رو امتحان می‌کنه)
        private async Task<bool> ConnectToAsync(Server target, bool quiet)
        {
            _currentServer = target;
            SetConnState(ConnState.Connecting);
            try
            {
                if (!XrayProc.EngineExists)
                {
                    await DisplayAlertAsync("موتور Xray پیدا نشد", "فایل موتور Xray کنار برنامه نیست. درفش را دوباره نصب کنید.", "باشه");
                    return false;
                }

                string configPath = Path.Combine(XrayProc.WorkDir, "config.json");
                await File.WriteAllTextAsync(configPath, await BuildXrayConfigAsync(target.Config, XrayProc.SessionPort));
                ConnectionGuard.CleanupFiles();

#if ANDROID
                // اندروید: سرویس VPN خودش xray و تونل رو بالا میاره (و نگهبان اتصال رو)
                if (!await VpnController.StartAsync(configPath, target.City, target.Config))
                    throw new Exception(VpnController.LastError ?? "VPN روشن نشد.");
#else
                xrayProcess?.Dispose(); xrayProcess = null;
                KillXray();
                xrayProcess = XrayProc.Start(configPath);
                if (!await XrayProc.WaitForPortAsync(XrayProc.SessionPort, 5000)) throw new Exception("موتور Xray با این کانفیگ روشن نشد.");
#endif

                // یه درخواست واقعی از داخل همین اتصال؛ سروری که فقط «وصل» می‌شه ولی اینترنت نمی‌ده قبول نیست
                if (!await ConnectionGuard.ProbeAsync(8) && !await ConnectionGuard.ProbeAsync(8))
                    throw new Exception("سرور وصل شد ولی اینترنت از داخلش باز نشد.");
#if WINDOWS
                EnableProxy();   // پروکسی سیستم فقط وقتی سرور واقعاً جواب داد؛ وگرنه اینترنت کاربر قطع می‌شد
#endif

                // IP و شهرِ سرور، از داخل خود اتصال
                var (ip, location) = await ConnectionGuard.ExitLocationAsync();
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
                StartGuard();
                UpdatePool();
                if (_listDownloadFailed) _ = FetchListThroughServerAsync();
                if (_updateTag == null) _ = CheckForUpdateAsync();   // شاید گیت‌هاب قبل از اتصال جواب نداده بود
                return true;
            }
            catch (Exception ex)
            {
                _pingCache[target.Config] = NoReply;   // دفعه‌ی بعد سراغ سرور بعدی بره
                foreach (var x in RowsOf(target.Config)) x.Ping = NoReply;
                Disconnect();
                if (!quiet) await DisplayAlertAsync("وصل نشد", $"{ex.Message}\nدوباره «اتصال» را بزنید تا سرور دیگری امتحان شود.", "باشه");
                return false;
            }
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
            StopGuard();
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
            // xray‌های خود درفش که از اجرای قبلی مونده‌ن؛ xray‌های تستِ همین الان دست نمی‌خورن، و xray برنامه‌های دیگه
            // (مثلاً v2rayN که کاربر کنار درفش داره) هم نه: قبلاً هر xray روی سیستم بسته می‌شد
            string own = Path.GetFullPath(XrayProc.EnginePath);
            foreach (var p in Process.GetProcessesByName("xray"))
                try
                {
                    if (XrayProc.IsTestProcess(p.Id)) continue;
                    string? path = p.MainModule?.FileName;
                    if (path != null && string.Equals(Path.GetFullPath(path), own, StringComparison.OrdinalIgnoreCase)) p.Kill();
                }
                catch { }
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
