using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
    public partial class MainPage : ContentPage, INotifyPropertyChanged
    {
        private const string OnlineConfigUrl = "https://cdn.jsdelivr.net/gh/Mehinator/Derafsh@main/derafsh_servers.txt";
        private string CloudPath => Path.Combine(FileSystem.AppDataDirectory, "cloud_configs.txt");
        private string UserPath => Path.Combine(FileSystem.AppDataDirectory, "user_configs.txt");

        [DllImport("wininet.dll")] private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        public ObservableCollection<ServerGroup> ServerGroups { get; } = new();
        private List<Server> AllServers => ServerGroups.SelectMany(g => g).ToList();

        private Process? xrayProcess;
        private bool isConnected = false;
        private Server? _currentServer;
        private string CurrentTab = "All";

        // *** استفاده از سرویس جداگانه ***
        private readonly ConfigService _configService = new ConfigService();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private Server? _selectedServer;
        public Server? SelectedServer { get => _selectedServer; set { if (_selectedServer != value) { _selectedServer = value; OnPropertyChanged(); } } }

        // فیلترها
        private bool _showOnline = true; public bool ShowOnline { get => _showOnline; set { if (_showOnline != value) { _showOnline = value; OnPropertyChanged(); OnFilterChanged(); } } }
        private bool _showPersonal = true; public bool ShowPersonal { get => _showPersonal; set { if (_showPersonal != value) { _showPersonal = value; OnPropertyChanged(); OnFilterChanged(); } } }

        public MainPage()
        {
            InitializeComponent();
            BindingContext = this;
            if (ServerListView != null) ServerListView.ItemsSource = ServerGroups;
            _ = Task.Run(() => MainThread.BeginInvokeOnMainThread(LoadConfigs));
            DebugLog.Write("=== STARTUP ===");
        }

        private void OnFilterChanged() => LoadConfigs();

        // --- لود کردن لیست (با استفاده از سرویس) ---
        private void LoadConfigs()
        {
            ServerGroups.Clear();
            int idx = 1;

            // 1. شخصی‌ها
            if (CurrentTab == "All" || CurrentTab == "Personal")
            {
                if (File.Exists(UserPath))
                {
                    // استفاده از متد سرویس
                    var list = _configService.ParseFile(UserPath, true);
                    if (list.Any())
                    {
                        foreach (var s in list) s.Index = idx++;
                        ServerGroups.Add(new ServerGroup("👤 شخصی", list));
                    }
                }
            }

            // 2. آنلاین‌ها
            if (CurrentTab == "All" || CurrentTab == "Online")
            {
                if (!File.Exists(CloudPath)) _ = UpdateOnlineConfigs(false);
                if (File.Exists(CloudPath))
                {
                    // استفاده از متد سرویس
                    var list = _configService.ParseFile(CloudPath, false);
                    var selected = list.Take(20).ToList();
                    if (selected.Any())
                    {
                        foreach (var s in selected) s.Index = idx++;
                        ServerGroups.Add(new ServerGroup("🌐 آنلاین", selected));
                    }
                }
            }

            if (ServerListView != null) { ServerListView.ItemsSource = null; ServerListView.ItemsSource = ServerGroups; }
            if (AllServers.Any() && SelectedServer == null) SelectServer(AllServers.First());
        }

        // --- دکمه‌ها ---
        private async void OnUpdateOnlineClicked(object sender, EventArgs e) => await UpdateOnlineConfigs(true);
        private async Task UpdateOnlineConfigs(bool reload = true)
        {
            if (reload) StatusLabel.Text = "Downloading...";
            try
            {
                using var client = new HttpClient(); client.Timeout = TimeSpan.FromSeconds(20);
                var content = await client.GetStringAsync(OnlineConfigUrl);
                await File.WriteAllTextAsync(CloudPath, content);
                if (reload) { LoadConfigs(); StatusLabel.Text = "Updated ✅"; }
            }
            catch (Exception ex) { if (reload) StatusLabel.Text = "Update Error"; }
        }

        private async void OnAddCustomConfigClicked(object sender, EventArgs e)
        {
            string initial = ""; if (Clipboard.Default.HasText) initial = await Clipboard.Default.GetTextAsync();
            string input = await DisplayPromptAsync("Add", "Paste Configs:", "Add", "Cancel", initialValue: initial);
            if (string.IsNullOrWhiteSpace(input)) return;

            var regex = new Regex(@"(vmess|vless|ss|trojan|hysteria2?)://[a-zA-Z0-9\-\._~:/\?#@!$&'()*+,;=%]+");
            var matches = regex.Matches(input);
            var links = new List<string>();
            foreach (Match m in matches) links.Add(m.Value.Trim());

            if (links.Any())
            {
                links = links.Distinct().ToList();
                await File.AppendAllLinesAsync(UserPath, links);
                LoadConfigs();
                await DisplayAlert("OK", $"{links.Count} added.", "OK");
            }
            else await DisplayAlert("Error", "No valid link.", "OK");
        }

        private void OnSaveToPersonalClicked(object sender, EventArgs e) { if (sender is Button btn && btn.CommandParameter is Server s) _ = SaveToPersonal(s); }
        private async Task SaveToPersonal(Server s)
        {
            try
            {
                if (File.Exists(UserPath))
                {
                    var txt = await File.ReadAllTextAsync(UserPath);
                    if (txt.Contains(s.Config)) { await DisplayAlert("Info", "Already saved", "OK"); return; }
                }
                await File.AppendAllLinesAsync(UserPath, new[] { s.Config });
                s.IsRemovable = true; s.Country = "👤 Saved";
                await DisplayAlert("Saved", "Added to Personal.", "OK");
                // LoadConfigs(); // برای سرعت بیشتر رفرش نمیکنیم
            }
            catch { }
        }

        private void OnDeleteSingleItemClicked(object sender, EventArgs e) { if (sender is Button btn && btn.CommandParameter is Server s) _ = DeleteServer(s); }
        private async Task DeleteServer(Server s)
        {
            if (!await DisplayAlert("Delete", $"Remove {s.City}?", "Yes", "No")) return;
            try
            {
                var lines = (await File.ReadAllLinesAsync(UserPath)).ToList();
                lines.RemoveAll(l => l.Contains(s.Config));
                await File.WriteAllLinesAsync(UserPath, lines);
                LoadConfigs();
            }
            catch { }
        }

        private async void OnDeleteDeadConfigsClicked(object sender, EventArgs e)
        {
            if (!await DisplayAlert("Cleanup", "Delete dead configs?", "Yes", "No")) return;
            if (File.Exists(UserPath))
            {
                var deadList = AllServers.Where(s => s.IsRemovable && (s.Ping >= 9999 || s.Ping <= 0)).Select(s => s.Config).ToHashSet();
                if (deadList.Any())
                {
                    var lines = (await File.ReadAllLinesAsync(UserPath)).ToList();
                    lines.RemoveAll(l => deadList.Contains(l.Trim()));
                    await File.WriteAllLinesAsync(UserPath, lines);
                    LoadConfigs();
                    StatusLabel.Text = $"Cleaned {deadList.Count} items";
                }
                else await DisplayAlert("Info", "No dead personal configs.", "OK");
            }
        }

        private void OnServerSelected(object sender, SelectedItemChangedEventArgs e)
        {
            if (e.SelectedItem is Server s)
            {
                if (_currentServer == s) return;
                SelectServer(s);
                if (isConnected)
                {
                    Disconnect();
                    StatusLabel.Text = "Switching...";
                    Task.Delay(500).ContinueWith(t => MainThread.BeginInvokeOnMainThread(() => OnConnectButtonClicked(sender, EventArgs.Empty)));
                }
            }
        }
        private void SelectServer(Server s) { foreach (var item in AllServers) item.IsSelected = false; s.IsSelected = true; _currentServer = s; SelectedServer = s; }

        // --- تب‌ها ---
        private void OnTabClicked(object sender, EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is string tab)
            {
                CurrentTab = tab;
                if (BtnTabAll != null) { BtnTabAll.BackgroundColor = tab == "All" ? Colors.Gold : Color.FromArgb("#333"); BtnTabAll.TextColor = tab == "All" ? Colors.Black : Colors.White; }
                if (BtnTabPersonal != null) { BtnTabPersonal.BackgroundColor = tab == "Personal" ? Colors.Gold : Color.FromArgb("#333"); BtnTabPersonal.TextColor = tab == "Personal" ? Colors.Black : Colors.White; }
                if (BtnTabOnline != null) { BtnTabOnline.BackgroundColor = tab == "Online" ? Colors.Gold : Color.FromArgb("#333"); BtnTabOnline.TextColor = tab == "Online" ? Colors.Black : Colors.White; }
                LoadConfigs();
            }
        }

        // --- پینگ ---
        private void OnPingSingleItemClicked(object sender, EventArgs e) { if (sender is Button btn && btn.CommandParameter is Server s) _ = PingSingle(s); }

        private async Task PingSingle(Server s)
        {
            s.IsPinging = true; s.Ping = -1;
            try
            {
                // تغییر: استفاده از متد جدید تولید کانفیگ از سرویس
                string host = ""; int port = 443;

                // اینجا برای پینگ ساده از TCP استفاده میکنیم (مگر اینکه hysteria باشه)
                bool skipTcp = s.Config.StartsWith("hysteria2") || s.Config.StartsWith("vmess");

                if (!skipTcp)
                {
                    var uri = new Uri(s.Config); host = uri.Host; port = uri.Port == -1 ? 443 : uri.Port;
                    using var tcp = new TcpClient();
                    var t = tcp.ConnectAsync(host, port);
                    if (await Task.WhenAny(t, Task.Delay(2000)) != t)
                    {
                        s.Ping = 9999;
                        s.IsPinging = false;
                        return;
                    }
                }

                // تست واقعی با Xray
                int testPort = new Random().Next(30000, 60000);
                string coreDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core");
                string configPath = Path.Combine(coreDir, $"ping_{testPort}.json");

                // *** استفاده از سرویس ***
                string config = _configService.GenerateXrayConfig(s.Config).Replace("10809", testPort.ToString());
                await File.WriteAllTextAsync(configPath, config);

                var proc = new Process { StartInfo = new ProcessStartInfo { FileName = Path.Combine(coreDir, "xray.exe"), Arguments = $"-c \"{configPath}\"", CreateNoWindow = true, UseShellExecute = false } };
                proc.Start();

                await Task.Delay(400);

                var handler = new HttpClientHandler { Proxy = new System.Net.WebProxy($"socks5://127.0.0.1:{testPort}"), UseProxy = true };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };

                var sw = Stopwatch.StartNew();
                var res = await client.GetAsync("http://www.gstatic.com/generate_204");
                sw.Stop();

                if (res.IsSuccessStatusCode)
                {
                    s.Ping = (int)sw.ElapsedMilliseconds;
                    // اگه خواستی کشور رو اینجا بگیری، کدشو اضافه کن
                }
                else s.Ping = 9999;

                try { proc.Kill(); File.Delete(configPath); } catch { }
            }
            catch { s.Ping = 9999; }
            finally { s.IsPinging = false; }
        }

        private async void OnFindBestServerClicked(object sender, EventArgs e)
        {
            if (!AllServers.Any()) return;
            StatusLabel.Text = "Testing Speed...";
            using var sem = new SemaphoreSlim(5);
            var tasks = AllServers.Select(async s => {
                await sem.WaitAsync();
                try { await PingSingle(s); } finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);

            foreach (var group in ServerGroups)
            {
                var sorted = group.OrderBy(x => x.Ping <= 0 ? 9999 : x.Ping).ToList();
                group.Clear(); foreach (var x in sorted) group.Add(x);
            }
            var best = AllServers.OrderBy(s => s.Ping <= 0 ? 9999 : s.Ping).FirstOrDefault(s => s.Ping < 5000);
            if (best != null) { SelectServer(best); ServerListView.ScrollTo(best, ScrollToPosition.MakeVisible, true); StatusLabel.Text = $"Best: {best.Ping}ms"; }
            else StatusLabel.Text = "No active server";
        }

        // --- اتصال ---
        private async void OnConnectButtonClicked(object sender, EventArgs e)
        {
            if (isConnected) { Disconnect(); return; }
            if (_currentServer == null) return;
            StatusLabel.Text = "Connecting...";
            try
            {
                string coreDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core");
                string configPath = Path.Combine(coreDir, "config.json");
                string enginePath = Path.Combine(coreDir, "xray.exe");
                if (!File.Exists(enginePath)) { await DisplayAlert("Error", "xray missing", "OK"); return; }

                KillXray();

                // *** استفاده از سرویس برای تولید کانفیگ نهایی ***
                File.WriteAllText(configPath, _configService.GenerateXrayConfig(_currentServer.Config));

                xrayProcess = new Process { StartInfo = new ProcessStartInfo { FileName = enginePath, Arguments = $"-c \"{configPath}\"", WorkingDirectory = coreDir, CreateNoWindow = true, UseShellExecute = false } };
                xrayProcess.Start();
                await Task.Delay(1000);
                EnableProxy();
                StatusLabel.Text = "Verifying...";

                string ip = "Unknown", country = "";
                try
                {
                    var handler = new HttpClientHandler { Proxy = new System.Net.WebProxy("socks5://127.0.0.1:10809"), UseProxy = true };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                    var json = await client.GetStringAsync("https://api.country.is");
                    using var doc = JsonDocument.Parse(json);
                    ip = doc.RootElement.GetProperty("ip").GetString();
                    country = doc.RootElement.GetProperty("country").GetString();
                }
                catch { }

                if (ip != "Unknown")
                {
                    StatusLabel.Text = $"Connected: {country}\nIP: {ip}";
                    StatusLabel.TextColor = Colors.Green;
                    ConnectButton.Text = "Disconnect";
                    ConnectButton.BackgroundColor = Colors.Red;
                    isConnected = true;
                }
                else
                {
                    StatusLabel.Text = "Connected (No Data)";
                    ConnectButton.Text = "Disconnect";
                    ConnectButton.BackgroundColor = Colors.Red;
                    isConnected = true;
                }
            }
            catch (Exception ex) { await DisplayAlert("Error", ex.Message, "OK"); Disconnect(); }
        }

        private void Disconnect() { DisableProxy(); KillXray(); StatusLabel.Text = "Disconnected"; StatusLabel.TextColor = Colors.Gray; ConnectButton.Text = "Connect"; ConnectButton.BackgroundColor = Color.FromArgb("#00E676"); isConnected = false; }
        private static void KillXray() { foreach (var p in Process.GetProcessesByName("xray")) try { p.Kill(); } catch { } }
        public static void CleanupOnExit()
        {
#if WINDOWS
            try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true); key?.SetValue("ProxyEnable", 0); KillXray(); } catch { }
#endif
        }

#if WINDOWS
        private void EnableProxy() { try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true); key?.SetValue("ProxyEnable", 1); key?.SetValue("ProxyServer", "socks=127.0.0.1:10809"); InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); } catch { } }
        private void DisableProxy() { try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true); key?.SetValue("ProxyEnable", 0); InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); } catch { } }
#else
        private void EnableProxy() {} private void DisableProxy() {}
#endif
    }
}