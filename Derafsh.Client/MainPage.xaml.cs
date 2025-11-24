using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Derafsh.Client.Models;
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

        // وضعیت تب فعلی (All, Personal, Online)
        private string CurrentTab = "All";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private Server? _selectedServer;
        public Server? SelectedServer
        {
            get => _selectedServer;
            set { if (_selectedServer != value) { _selectedServer = value; OnPropertyChanged(); } }
        }

        public MainPage()
        {
            InitializeComponent();
            BindingContext = this;
            if (ServerListView != null) ServerListView.ItemsSource = ServerGroups;
            _ = Task.Run(() => MainThread.BeginInvokeOnMainThread(LoadConfigs));
        }

        // --- مدیریت تب‌ها ---
        private void OnTabClicked(object sender, EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is string tab)
            {
                CurrentTab = tab;
                // تغییر رنگ دکمه‌ها
                BtnTabAll.BackgroundColor = tab == "All" ? Colors.Gold : Color.FromArgb("#333");
                BtnTabAll.TextColor = tab == "All" ? Colors.Black : Colors.White;

                BtnTabPersonal.BackgroundColor = tab == "Personal" ? Colors.Gold : Color.FromArgb("#333");
                BtnTabPersonal.TextColor = tab == "Personal" ? Colors.Black : Colors.White;

                BtnTabOnline.BackgroundColor = tab == "Online" ? Colors.Gold : Color.FromArgb("#333");
                BtnTabOnline.TextColor = tab == "Online" ? Colors.Black : Colors.White;

                LoadConfigs();
            }
        }

        // --- لود کردن لیست (بدون تکرار) ---
        private async void LoadConfigs()
        {
            ServerGroups.Clear();
            int idx = 1;

            // 1. شخصی‌ها
            if (CurrentTab == "All" || CurrentTab == "Personal")
            {
                if (File.Exists(UserPath))
                {
                    var list = await ParseAndAddFile(UserPath, true);
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
                if (!File.Exists(CloudPath)) await UpdateOnlineConfigs(false); // false یعنی رفرش نکن، فقط دانلود

                if (File.Exists(CloudPath))
                {
                    var list = await ParseAndAddFile(CloudPath, false);
                    // همیشه 30 تای رندوم (مگر اینکه قبلاً لود شده باشه و نخوایم تغییر کنه)
                    // اینجا برای سادگی هر بار رندوم میکنیم
                    var random = list.OrderBy(x => Guid.NewGuid()).Take(30).ToList();
                    if (random.Any())
                    {
                        foreach (var s in random) s.Index = idx++;
                        ServerGroups.Add(new ServerGroup("🌐 آنلاین", random));
                    }
                }
            }

            // شوک به UI برای رفرش گرافیکی
            if (ServerListView != null) { ServerListView.ItemsSource = null; ServerListView.ItemsSource = ServerGroups; }

            // انتخاب خودکار اگه هیچی انتخاب نیست
            if (AllServers.Any() && SelectedServer == null) SelectServer(AllServers.First());
        }

        private async Task<List<Server>> ParseAndAddFile(string path, bool isUser)
        {
            var list = new List<Server>();
            try
            {
                var fileContent = await File.ReadAllTextAsync(path);
                // این Regex هم هیستریا رو می‌فهمه هم لینک‌های کثیف رو تمیزتر درمیاره
                var regex = new Regex(@"(vmess|vless|ss|trojan|hysteria2?)://[a-zA-Z0-9\-\._~:/\?#@!$&'()*+,;=%]+");
                var matches = regex.Matches(fileContent);

                foreach (Match match in matches)
                {
                    string link = match.Value.Trim().TrimEnd(')', ']', '}', '"', '\'', '`');
                    var s = CreateServerObj(link, isUser);
                    if (s != null) list.Add(s);
                }
            }
            catch { }
            return list;
        }

        private Server? CreateServerObj(string link, bool isUser)
        {
            try
            {
                string name = "Server";
                try
                {
                    if (!link.StartsWith("vmess"))
                    {
                        var uri = new Uri(link);
                        name = !string.IsNullOrEmpty(uri.Fragment) ? HttpUtility.UrlDecode(uri.Fragment.TrimStart('#')) : $"{uri.Host}:{uri.Port}";
                    }
                    else name = "Vmess Server";
                }
                catch { }
                return new Server { Config = link, City = name, IsRemovable = isUser, Ping = -1 };
            }
            catch { return null; }
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
                if (reload)
                {
                    LoadConfigs();
                    StatusLabel.Text = "Updated ✅";
                }
            }
            catch (Exception ex) { if (reload) StatusLabel.Text = "Update Failed"; }
        }

        private async void OnAddCustomConfigClicked(object sender, EventArgs e)
        {
            string initial = "";
            if (Clipboard.Default.HasText) initial = await Clipboard.Default.GetTextAsync();
            string input = await DisplayPromptAsync("Add Config", "Paste here:", "Add", "Cancel", initialValue: initial);

            if (string.IsNullOrWhiteSpace(input)) return;

            var regex = new Regex(@"(vmess|vless|ss|trojan)://[a-zA-Z0-9\-\._~:/\?#@!$&'()*+,;=%]+");
            var matches = regex.Matches(input);
            var links = new List<string>();

            foreach (Match m in matches)
                links.Add(m.Value.Trim().TrimEnd(')', ']', '}', '"', '\'', '`'));

            if (links.Any())
            {
                links = links.Distinct().ToList();
                await File.AppendAllLinesAsync(UserPath, links);
                LoadConfigs();
                await DisplayAlert("OK", $"{links.Count} Added.", "OK");
            }
            else await DisplayAlert("Error", "No link found.", "OK");
        }

        private void OnDeleteSingleItemClicked(object sender, EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is Server s) _ = DeleteServer(s);
        }

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

        // حذف خراب‌ها (بدون ریلود کردن و پروندن پینگ‌ها)
        private async void OnDeleteDeadConfigsClicked(object sender, EventArgs e)
        {
            if (!await DisplayAlert("Cleanup", "Remove dead configs (Ping > 5000)?", "Yes", "No")) return;

            // 1. شناسایی خراب‌ها از لیست فعلی
            var deadConfigs = AllServers.Where(s => s.Ping >= 5000 || s.Ping <= 0).ToList();

            if (!deadConfigs.Any())
            {
                await DisplayAlert("Info", "All configs are good!", "OK");
                return;
            }

            // 2. حذف از فایل شخصی
            if (File.Exists(UserPath))
            {
                var lines = (await File.ReadAllLinesAsync(UserPath)).ToList();
                int removed = lines.RemoveAll(l => deadConfigs.Any(d => l.Contains(d.Config)));
                if (removed > 0) await File.WriteAllLinesAsync(UserPath, lines);
            }

            // 3. حذف از فایل آنلاین (فقط لوکال)
            // برای آنلاین‌ها، چون هر بار رندوم میاد، حذف کردنشون از فایل لوکال موثره
            if (File.Exists(CloudPath))
            {
                var lines = (await File.ReadAllLinesAsync(CloudPath)).ToList();
                lines.RemoveAll(l => deadConfigs.Any(d => l.Contains(d.Config)));
                await File.WriteAllLinesAsync(CloudPath, lines);
            }

            // 4. حذف گرافیکی (بدون ریلود کل صفحه)
            foreach (var group in ServerGroups)
            {
                var toRemove = group.Where(s => deadConfigs.Contains(s)).ToList();
                foreach (var item in toRemove) group.Remove(item);
            }

            StatusLabel.Text = $"Removed {deadConfigs.Count} dead configs 🗑️";
        }

        // *** تغییر مهم: سوییچ سریع (اتصال با کلیک) ***
        private async void OnServerSelected(object sender, SelectedItemChangedEventArgs e)
        {
            if (e.SelectedItem is Server s)
            {
                // اگه روی همون سرور فعلی کلیک کرد، هیچ کاری نکن (حتی اگه وصله)
                if (_currentServer == s) return;

                SelectServer(s);

                // اگه وصل هستیم و سرور عوض شده، باید "سوییچ" کنیم
                if (isConnected)
                {
                    // 1. اول قطع کن
                    // (چون متد دکمه async void هست، نمی‌تونیم await کنیم، پس دستی Disconnect رو صدا می‌زنیم)
                    Disconnect();

                    StatusLabel.Text = "سوییچ سرور...";

                    // 2. یه تنفس کوتاه به Xray میدیم که پورت رو آزاد کنه
                    await Task.Delay(500);

                    // 3. حالا وصل شو
                    // اینجا دکمه اتصال رو صدا می‌زنیم که پروسه وصل شدن طی بشه
                    OnConnectButtonClicked(sender, EventArgs.Empty);
                }
            }
        }
        private void SelectServer(Server s)
        {
            foreach (var item in AllServers) item.IsSelected = false;
            s.IsSelected = true; _currentServer = s; SelectedServer = s;
        }

        private async void OnFindBestServerClicked(object sender, EventArgs e)
        {
            if (!AllServers.Any()) return;
            StatusLabel.Text = "Testing Speed & Location...";

            // 1. پینگ موازی (حداکثر 10 تا همزمان که IP-API بن نکنه)
            using var sem = new SemaphoreSlim(10);
            var tasks = AllServers.Select(async s => {
                await sem.WaitAsync();
                try { await PingSingleServer(s); }
                finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);

            // 2. مرتب‌سازی گروه‌ها (سالم‌ها بالا، 9999 ها پایین)
            foreach (var group in ServerGroups)
            {
                // لیست رو کپی میکنیم، مرتب میکنیم، دوباره میریزیم
                var sorted = group.OrderBy(s => s.Ping).ToList();
                group.Clear();
                foreach (var s in sorted) group.Add(s);
            }

            // 3. انتخاب بهترین
            var best = AllServers.Where(s => s.Ping < 5000).OrderBy(s => s.Ping).FirstOrDefault();

            if (best != null)
            {
                SelectServer(best);
                ServerListView.ScrollTo(best, ScrollToPosition.MakeVisible, true);
                StatusLabel.Text = $"Best: {best.Ping}ms | {best.Country}";
                StatusLabel.TextColor = Colors.Green;
            }
            else
            {
                StatusLabel.Text = "No Live Server ❌";
                StatusLabel.TextColor = Colors.Red;
            }
        }

        private async Task PingSingleServer(Server server)
        {
            server.IsPinging = true;
            // پیش‌فرض رو میذاریم عدد بزرگ که اگه فیلتر بود بره ته لیست
            server.Ping = 9999;

            try
            {
                // استخراج آدرس و پورت (دستی و تمیز)
                string host = "";
                int port = 443;

                // تلاش برای درآوردن آدرس از کانفیگ
                if (server.Config.StartsWith("vmess"))
                {
                    // برای ویمس چون دیکد میخواد، فعلا هاست رو خالی میذاریم که پینگ نگیره یا باید دیکد شه
                    // اینجا یه آدرس فیک میذاریم که کرش نکنه، یا اگه خواستی دیکد کن
                    // فعلا فرض میکنیم پینگ نداره
                    throw new Exception("Skipping VMess Ping");
                }
                else
                {
                    var uri = new Uri(server.Config);
                    host = uri.Host;
                    port = uri.Port == -1 ? 443 : uri.Port;
                }

                // 1. تست اتصال (پینگ)
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); // 2 ثانیه مهلت
                var stopwatch = Stopwatch.StartNew();

                await client.ConnectAsync(host, port, cts.Token);
                stopwatch.Stop();

                // اگه رسیدیم اینجا یعنی وصل شده
                int pingTime = (int)stopwatch.ElapsedMilliseconds;
                server.Ping = pingTime;

                // 2. گرفتن اطلاعات کشور و IP (فقط اگه پینگ موفق بود)
                try
                {
                    using var webClient = new HttpClient();
                    webClient.Timeout = TimeSpan.FromSeconds(2); // سریع چک کن
                    // این API بهمون میگه این IP مال کجاست
                    var json = await webClient.GetStringAsync($"http://ip-api.com/json/{host}?fields=country,query");

                    // پارس ساده دستی (بدون کلاس اضافه)
                    if (!string.IsNullOrEmpty(json))
                    {
                        using var doc = JsonDocument.Parse(json);
                        string country = doc.RootElement.GetProperty("country").GetString() ?? "Unknown";
                        string ip = doc.RootElement.GetProperty("query").GetString() ?? host;

                        // نمایش در لیست: کشور + پرچم
                        server.Country = $"{country} ({ip})";
                    }
                }
                catch
                {
                    // اگه نتونست کشور رو بگیره، فقط بنویسه آنلاین
                    if (server.Country == "") server.Country = "Unknown Location";
                }
            }
            catch
            {
                server.Ping = 9999; // این یعنی مرده
                server.Country = "⛔ در دسترس نیست";
            }
            finally
            {
                server.IsPinging = false;
            }
        }

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
                // ساخت کانفیگ
                File.WriteAllText(configPath, GenerateXrayConfig(_currentServer.Config));

                xrayProcess = new Process { StartInfo = new ProcessStartInfo { FileName = enginePath, Arguments = $"-c \"{configPath}\"", WorkingDirectory = coreDir, CreateNoWindow = true, UseShellExecute = false } };
                xrayProcess.Start();

                await Task.Delay(1500);
                EnableProxy();
                StatusLabel.Text = "Verifying Location...";

                // *** بخش جدید: گرفتن کشور واقعی بعد از اتصال ***
                string ip = "Unknown", country = "";
                try
                {
                    // حتماً از پروکسی 10809 استفاده می‌کنیم
                    var handler = new HttpClientHandler { Proxy = new System.Net.WebProxy("socks5://127.0.0.1:10809"), UseProxy = true };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

                    // این سایت هم IP میده هم اسم کامل کشور
                    var json = await client.GetStringAsync("http://ip-api.com/json");
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.GetProperty("status").GetString() == "success")
                    {
                        ip = doc.RootElement.GetProperty("query").GetString();
                        country = doc.RootElement.GetProperty("country").GetString(); // اسم کشور (مثلاً Germany)
                    }
                }
                catch { }

                if (ip != "Unknown")
                {
                    // نمایش کشور در هدر
                    StatusLabel.Text = $"Connected to {country} 🌍\nIP: {ip}";
                    StatusLabel.TextColor = Colors.Green;
                    ConnectButton.Text = "Disconnect";
                    ConnectButton.BackgroundColor = Colors.Red;
                    isConnected = true;
                }
                else
                {
                    StatusLabel.Text = "Connected (No Data) ⚠️";
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

        private string GenerateXrayConfig(string link)
        {
            if (string.IsNullOrEmpty(link)) throw new FormatException("Link empty");
            link = link.Trim();
            if (link.Contains("#")) link = link.Substring(0, link.IndexOf('#'));

            string header = @"""log"":{""loglevel"":""warning""},""inbounds"":[{""port"":10809,""listen"":""127.0.0.1"",""protocol"":""socks"",""settings"":{""auth"":""noauth"",""udp"":true},""sniffing"":{""enabled"":true,""destOverride"":[""http"",""tls""]}}],""dns"":{""servers"":[""8.8.8.8"",""1.1.1.1""]},";
            string routing = @"""routing"":{""domainStrategy"":""IPIfNonMatch"",""rules"":[{""type"":""field"",""outboundTag"":""proxy"",""network"":""tcp,udp""}]}";
            string outboundObj = "";
            string protocol = link.Contains("://") ? link.Substring(0, link.IndexOf("://")) : "";

            try
            {
                // تنظیمات عمومی MUX (خاموش برای پایداری بیشتر)
                string muxSettings = @",""mux"":{""enabled"":false,""concurrency"":-1}";

                if (protocol == "vless")
                {
                    var uri = new Uri(link);
                    var q = HttpUtility.ParseQueryString(uri.Query);
                    string encryption = q["encryption"] ?? "none";
                    string flow = q["flow"] ?? "";
                    string type = q["type"] ?? "tcp";
                    string security = q["security"] ?? "none";
                    string sni = q["sni"] ?? uri.Host;
                    // تغییر: اگه fp نبود، رندوم بذار
                    string fp = q["fp"] ?? "randomized";
                    string pbk = q["pbk"] ?? "";
                    string sid = q["sid"] ?? "";
                    string path = q["path"] ?? "/";
                    string host = q["host"] ?? sni;
                    string serviceName = q["serviceName"] ?? "";
                    string mode = q["mode"] ?? "auto";

                    string streamSettings = $@"""network"":""{type}"",""security"":""{security}"",";

                    if (security == "reality")
                        streamSettings += $@"""realitySettings"":{{""show"":false,""fingerprint"":""{fp}"",""serverName"":""{sni}"",""publicKey"":""{pbk}"",""shortId"":""{sid}"",""spiderX"":""""}},";
                    else if (security == "tls")
                        streamSettings += $@"""tlsSettings"":{{""serverName"":""{sni}"",""fingerprint"":""{fp}"",""allowInsecure"":true}},";

                    if (type == "ws") streamSettings += $@"""wsSettings"":{{""path"":""{path}"",""headers"":{{""Host"":""{host}""}}}},";
                    else if (type == "grpc") streamSettings += $@"""grpcSettings"":{{""serviceName"":""{serviceName}""}},";
                    else if (type == "http" || type == "xhttp") streamSettings += $@"""httpSettings"":{{""path"":""{path}"",""host"":[""{host}""],""method"":""{mode}""}},";

                    string flowJson = !string.IsNullOrEmpty(flow) ? $@"""flow"":""{flow}""," : "";

                    outboundObj = $@"{{""tag"":""proxy"",""protocol"":""vless"",""settings"":{{""vnext"":[{{""address"":""{uri.Host}"",""port"":{uri.Port},""users"":[{{""id"":""{uri.UserInfo}"",""encryption"":""{encryption}"",{flowJson}""level"":0}}]}}]}},""streamSettings"":{{{streamSettings}""sockopt"":{{""tcpFastOpen"":true}}}}{muxSettings}}}";
                }
                else if (protocol == "vmess")
                {
                    string base64 = link.Substring(8);
                    string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                    var v = JsonSerializer.Deserialize<ConfigDto>(decoded);
                    string port = v.port?.ToString() ?? "443";
                    string aid = v.aid?.ToString() ?? "0";
                    string net = v.net ?? "tcp";
                    string fp = v.fp ?? "randomized"; // رندوم

                    string netSettings = "";
                    if (net == "ws") netSettings = $@"""wsSettings"":{{""path"":""{v.path}"",""headers"":{{""Host"":""{v.host}""}}}},";
                    else if (net == "grpc") netSettings = $@"""grpcSettings"":{{""serviceName"":""{v.path}""}},";
                    else if (net == "http" || net == "h2") netSettings = $@"""httpSettings"":{{""path"":""{v.path}"",""host"":[""{v.host}""]}},";

                    string tlsSettings = v.tls == "tls" ? $@"""security"":""tls"",""tlsSettings"":{{""serverName"":""{v.sni ?? v.host}"",""allowInsecure"":true,""fingerprint"":""{fp}""}}," : $@"""security"":""none"",";

                    outboundObj = $@"{{""tag"":""proxy"",""protocol"":""vmess"",""settings"":{{""vnext"":[{{""address"":""{v.add}"",""port"":{port},""users"":[{{""id"":""{v.id}"",""alterId"":{aid},""security"":""{v.scy ?? "auto"}""}}]}}]}},""streamSettings"":{{""network"":""{net}"",{tlsSettings}{netSettings}""sockopt"":{{""tcpFastOpen"":true}}}}{muxSettings}}}";
                }
                else if (protocol == "ss")
                {
                    link = HttpUtility.UrlDecode(link);
                    var uri = new Uri(link);
                    string userInfo = uri.UserInfo;
                    if (string.IsNullOrEmpty(userInfo)) userInfo = uri.OriginalString.Replace("ss://", "").Split('@')[0];
                    string base64 = userInfo.Split('#')[0].Trim().Replace(" ", "+").Replace("-", "+").Replace("_", "/");
                    switch (base64.Length % 4) { case 2: base64 += "=="; break; case 3: base64 += "="; break; }
                    string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                    var parts = decoded.Split(':');
                    outboundObj = $@"{{""tag"":""proxy"",""protocol"":""shadowsocks"",""settings"":{{""servers"":[{{""address"":""{uri.Host}"",""port"":{uri.Port},""method"":""{parts[0]}"",""password"":""{(parts.Length > 1 ? parts[1] : "")}""}}]}}{muxSettings}}}";
                }
                else if (protocol == "trojan")
                {
                    var uri = new Uri(link);
                    var q = HttpUtility.ParseQueryString(uri.Query);
                    string sni = q["sni"] ?? uri.Host;
                    outboundObj = $@"{{""tag"":""proxy"",""protocol"":""trojan"",""settings"":{{""servers"":[{{""address"":""{uri.Host}"",""port"":{uri.Port},""password"":""{uri.UserInfo}""}}]}},""streamSettings"":{{""network"":""tcp"",""security"":""tls"",""tlsSettings"":{{""serverName"":""{sni}"",""allowInsecure"":true}},""sockopt"":{{""tcpFastOpen"":true}}}}{muxSettings}}}";
                }
                else if (protocol == "hysteria2")
                {
                    var uri = new Uri(link);
                    var q = HttpUtility.ParseQueryString(uri.Query);
                    string sni = q["sni"] ?? uri.Host;
                    string obfs = q["obfs"] ?? "none";
                    string obfsPass = q["obfs-password"] ?? "";
                    string obfsJson = obfs != "none" ? $@",""obfs"":{{""type"":""{obfs}"",""password"":""{obfsPass}""}}" : "";

                    outboundObj = $@"{{""tag"":""proxy"",""protocol"":""hysteria2"",""settings"":{{""servers"":[{{""address"":""{uri.Host}"",""port"":{uri.Port},""password"":""{uri.UserInfo}""{obfsJson}}}]}},""streamSettings"":{{""network"":""udp"",""security"":""tls"",""tlsSettings"":{{""serverName"":""{sni}"",""allowInsecure"":true}}}}{muxSettings}}}";
                }
            }
            catch (Exception ex) { DebugLog.Write("Gen Config Error: " + ex.Message); throw; }

            return $@"{{{header}""outbounds"":[{outboundObj},{{""protocol"":""freedom"",""tag"":""direct""}}],{routing}}}";
        }

#if WINDOWS
        private void EnableProxy() { try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true); key?.SetValue("ProxyEnable", 1); key?.SetValue("ProxyServer", "socks=127.0.0.1:10809"); InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); } catch { } }
        private void DisableProxy() { try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true); key?.SetValue("ProxyEnable", 0); InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); } catch { } }
#else
        private void EnableProxy() {} private void DisableProxy() {}
#endif
    }
}