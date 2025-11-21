using Derafsh.Client;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Maui.Controls;
using System.Windows.Input;
using System;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace Derafsh.Client
{
    public static class DebugLog
    {
        private static readonly string LogPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "derafsh_debug.txt");
        public static void Write(string msg)
        {
            try
            {
                string fullMsg = $"{DateTime.Now:HH:mm:ss} - {msg}{Environment.NewLine}";
                File.AppendAllText(LogPath, fullMsg);
                // نیروی فلاش برای ساخت فوری فایل
                using (var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.Flush(true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"لاگ شکست: {ex.Message} - مسیر: {LogPath}");
            }
        }
    }
    public partial class MainPage : ContentPage, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        [DllImport("wininet.dll")]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        public ObservableCollection<Server> Servers { get; } = new();

        private Server? _selectedServer;
        public Server? SelectedServer
        {
            get => _selectedServer;
            set
            {
                if (_selectedServer != value)
                {
                    _selectedServer = value;
                    DebugLog.Write($"SelectedServer ست شد (دائمی): {_selectedServer?.City ?? "NULL"}");
                    OnPropertyChanged();
                }
            }
        }

        private Process? xrayProcess;
        private bool isConnected = false;

        private bool _isRefreshing;
        public bool IsRefreshing
        {
            get => _isRefreshing;
            set
            {
                _isRefreshing = value;
                OnPropertyChanged();
                DebugLog.Write($"IsRefreshing تغییر کرد به: {value}");
            }
        }

        public MainPage()
        {
            InitializeComponent();
            BindingContext = this;
            // *** این خط رو اضافه کن ***
            LoadDefaultConfigs();

            DebugLog.Write("کانفیگ‌های پیش‌فرض لود شدند");
        }
        private void OnServerSelected(object sender, SelectedItemChangedEventArgs e)
        {
            DebugLog.Write($"OnServerSelected کال شد - e.SelectedItem نوع: {(e.SelectedItem?.GetType().Name ?? "NULL")}");

            if (e.SelectedItem is Server server)
            {
                SelectedServer = server;
                DebugLog.Write($"سرور انتخاب شد (دائمی): {server.City} - پینگ: {server.Ping} ms");
                MainThread.BeginInvokeOnMainThread(async () => await DisplayAlert("انتخاب شد ✅", $"سرور: {server.City}\nپینگ: {server.Ping} ms", "OK"));
            }
            // اگر null بود → هیچ کاری نکن! (مهم‌ترین قسمت)
            // دی‌سلکت هم نکن! MAUI خودش هایلایت رو برمی‌داره
        }
        //private async void OnAddConfigClicked(object sender, EventArgs e)
        //{
        //    DebugLog.Write("دکمه 'اضافه کن' زده شد");
        //    string link = ConfigInput.Text?.Trim() ?? "";
        //    if (string.IsNullOrEmpty(link) || (!link.StartsWith("vmess://") && !link.StartsWith("vless://") && !link.StartsWith("trojan://") && !link.StartsWith("ss://")))
        //    {
        //        await DisplayAlert("خطا", "لینک معتبر نیست!", "باشه");
        //        DebugLog.Write("لینک نامعتبر - اضافه نشد");
        //        return;
        //    }

        //    string displayName = "ناشناخته";
        //    try
        //    {
        //        var uri = new Uri(link);
        //        displayName = $"{uri.Host}:{uri.Port}";
        //    }
        //    catch { }

        //    var newServer = new Server
        //    {
        //        Config = link,
        //        Country = "در حال تست",
        //        City = displayName,
        //        FlagUrl = "flag_unknown.png"
        //    };

        //    Servers.Add(newServer);
        //    ConfigInput.Text = string.Empty;
        //    await PingSingleServer(newServer);
        //    DebugLog.Write($"سرور اضافه شد: {displayName} - مجموع سرورها: {Servers.Count}");
        //}
        private void LoadDefaultConfigs()
        {
            // مسیر فایل configs.txt کنار فایل اجرایی
            string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configs.txt");

            if (!File.Exists(configFilePath))
            {
                DebugLog.Write("فایل configs.txt پیدا نشد! ساخت فایل نمونه...");
                try
                {
                    // یه فایل نمونه می‌سازیم که کاربر گیج نشه
                    File.WriteAllText(configFilePath, "vmess://...\nvless://...");
                }
                catch { }
                return;
            }

            try
            {
                // خواندن تمام خطوط فایل
                var lines = File.ReadAllLines(configFilePath);

                int count = 0;
                foreach (var line in lines)
                {
                    string link = line.Trim();
                    // رد کردن خطوط خالی یا کامنت (اگه با # شروع بشه)
                    if (string.IsNullOrEmpty(link) || link.StartsWith("#")) continue;

                    // استفاده از همون متد قبلی برای اضافه کردن به لیست
                    try
                    {
                        // لاجیک دستی اضافه کردن (چون متد AddServerToList پرایوت بود، کدش رو اینجا میاریم یا اونو پابلیک کن)
                        // اینجا کد خلاصه‌ش رو میذارم:

                        string displayName = "Server " + (count + 1);
                        try
                        {
                            if (!link.StartsWith("vmess"))
                            {
                                var uri = new Uri(link);
                                displayName = uri.Fragment.TrimStart('#');
                                if (string.IsNullOrEmpty(displayName)) displayName = $"{uri.Host}:{uri.Port}";
                            }
                            else displayName = "Vmess Server";
                        }
                        catch { }

                        Servers.Add(new Server
                        {
                            Config = link,
                            City = HttpUtility.UrlDecode(displayName), // دیکد کردن نام فارسی
                            Country = "---",
                            FlagUrl = "flag_unknown.png"
                        });
                        count++;
                    }
                    catch { }
                }
                DebugLog.Write($"{count} کانفیگ از فایل لود شد.");

                if (Servers.Count > 0) SelectedServer = Servers[0];
            }
            catch (Exception ex)
            {
                DebugLog.Write("خطا در خواندن فایل کانفیگ: " + ex.Message);
            }
        }
        private async void OnConnectButtonClicked(object sender, EventArgs e)
        {
            // 1. اگه متصل بودیم، حالا قطعش کن
            if (isConnected)
            {
                DisableProxy();
                foreach (var proc in Process.GetProcessesByName("xray")) { try { proc.Kill(); proc.WaitForExit(1000); } catch { } }

                StatusLabel.Text = "قطع";
                StatusLabel.TextColor = Colors.Red;
                ConnectButton.Text = "اتصال";
                ConnectButton.BackgroundColor = Colors.Green; // سبز برای اتصال مجدد

                if (SelectedServer != null)
                {
                    SelectedServer.Country = "قطع شده";
                    SelectedServer.City = "---";
                }

                isConnected = false;
                return;
            }

            if (SelectedServer == null)
            {
                // اگه کاربر هیچی انتخاب نکرده بود، ما اولین سرور لیست رو برمیداریم
                if (Servers.Count > 0)
                {
                    SelectedServer = Servers[0];
                    StatusLabel.Text = "انتخاب خودکار سرور...";
                }
                else
                {
                    await DisplayAlert("خطا", "لیست کانفیگ‌ها خالیه فرمانده!", "باشه");
                    return;
                }
            }

            StatusLabel.Text = "در حال راه‌اندازی...";
            StatusLabel.TextColor = Colors.Orange;

            try
            {
                // 3. راه‌اندازی Xray (همون کدهای قبلی که سالم بود)
                string coreDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core");
                string configPath = Path.Combine(coreDir, "config.json");
                string enginePath = Path.Combine(coreDir, "xray.exe");

                if (!File.Exists(enginePath))
                {
                    await DisplayAlert("خطا", "موتور xray.exe پیدا نشد!", "باشه");
                    return;
                }

                // کشتن پروسه‌های قبلی برای جلوگیری از تداخل
                foreach (var proc in Process.GetProcessesByName("xray")) { try { proc.Kill(); } catch { } }

                string configJson = ParseConfig(SelectedServer.Config);
                await File.WriteAllTextAsync(configPath, configJson);

                // پاک کردن لاگ قدیمی
                string logPath = Path.Combine(coreDir, "xray_log.txt");
                if (File.Exists(logPath)) File.Delete(logPath);

                xrayProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = enginePath,
                        Arguments = $"-c \"{configPath}\"",
                        WorkingDirectory = coreDir,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        RedirectStandardInput = true
                    }
                };

                xrayProcess.Start();

                // لاگ کردن خطاها برای دیباگ (اختیاری ولی مفید)
                xrayProcess.ErrorDataReceived += (s, args) => { if (args.Data != null) DebugLog.Write("Xray Error: " + args.Data); };
                xrayProcess.BeginErrorReadLine();

                // یکم صبر کنیم تا موتور گرم بشه
                await Task.Delay(2000);

                // 4. فعال کردن پروکسی ویندوز
                EnableProxy();
                StatusLabel.Text = "بررسی اتصال...";

                // صبر بیشتر برای اعمال پروکسی
                await Task.Delay(2000);

                // 5. *** بخش جدید: چک کردن IP از داخل تونل ***
                string newIp = "نامشخص";
                string country = "";

                try
                {
                    // اینجا به برنامه میگیم حتماً از پورت 10809 (پورت درفش) استفاده کنه
                    var proxy = new System.Net.WebProxy("socks5://127.0.0.1:10809");
                    var handler = new HttpClientHandler { Proxy = proxy, UseProxy = true };

                    using var client = new HttpClient(handler);
                    client.Timeout = TimeSpan.FromSeconds(20); // 10 ثانیه وقت میدیم

                    // این سایت IP و کشور رو به صورت JSON میده
                    var jsonResponse = await client.GetStringAsync("http://ip-api.com/json");

                    // تحلیل JSON
                    using var doc = JsonDocument.Parse(jsonResponse);
                    var root = doc.RootElement;

                    if (root.GetProperty("status").GetString() == "success")
                    {
                        newIp = root.GetProperty("query").GetString();
                        country = root.GetProperty("country").GetString();
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Write("خطا در اتصال به اینترنت از طریق پروکسی: " + ex.Message);
                    newIp = "خطا";
                }

                // 6. نتیجه نهایی
                if (newIp != "خطا" && newIp != "نامشخص")
                {
                    StatusLabel.Text = $"متصل به {country}\nIP: {newIp}";
                    StatusLabel.TextColor = Colors.Green;
                    ConnectButton.Text = "قطع اتصال";
                    ConnectButton.BackgroundColor = Colors.Red; // قرمز برای قطع

                    SelectedServer.Country = country;
                    SelectedServer.City = newIp; // نمایش IP جای شهر
                    isConnected = true;
                }
                else
                {
                    // اگه IP رو نتونستیم بگیریم، یعنی اتصال برقرار نشده
                    StatusLabel.Text = "اتصال ناموفق (پروکسی کار نمی‌کند)";
                    StatusLabel.TextColor = Colors.Red;
                    // برای اینکه کاربر گیج نشه، پروکسی رو خاموش می‌کنیم
                    DisableProxy();
                    try { xrayProcess.Kill(); } catch { }
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("خطا کلی", ex.Message, "باشه");
                DisableProxy();
            }
        }
        private async Task PingSingleServer(Server server)
        {
            server.IsPinging = true;
            server.Ping = -1;

            try
            {
                var uri = new Uri(server.Config);
                string host = uri.Host;
                int port = uri.Port == -1 ? 443 : uri.Port;

                using var client = new TcpClient();
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4.5));
                var stopwatch = Stopwatch.StartNew();

                await client.ConnectAsync(host, port, cts.Token);
                stopwatch.Stop();

                int ping = (int)stopwatch.ElapsedMilliseconds;
                server.Ping = ping > 0 ? ping : 1;
                DebugLog.Write($"پینگ موفق برای {server.City}: {ping} ms");
            }
            catch
            {
                server.Ping = 999;
                DebugLog.Write($"پینگ ناموفق برای {server.City}");
            }
            finally
            {
                server.IsPinging = false;
            }
        }

        private async Task PingAllServers()
        {
            DebugLog.Write("شروع پینگ همه - PullToRefresh یا دکمه - تعداد: " + Servers.Count);
            IsRefreshing = true;

            var tasks = Servers.Select(s => PingSingleServer(s).ContinueWith(_ => Task.Delay(300)));
            await Task.WhenAll(tasks);

            var sorted = Servers.OrderBy(s => s.Ping == 999 ? 999999 : s.Ping).ToList();
            Servers.Clear();
            foreach (var s in sorted) Servers.Add(s);

            IsRefreshing = false;
            DebugLog.Write("پینگ همه تمام شد - لیست مرتب شد");
        }
        private async void OnFindBestServerClicked(object sender, EventArgs e)
        {
            // اگه لیستمون خالیه، ضایع نشیم
            if (Servers.Count == 0)
            {
                await DisplayAlert("خالیه!", "اول یه کانفیگ اضافه کن مهران جان.", "باشه");
                return;
            }

            StatusLabel.Text = "در حال تست سرعت...";
            StatusLabel.TextColor = Colors.Orange;

            // 1. پینگ همه رو می‌گیریم و لیست رو مرتب می‌کنیم (از متد قبلی استفاده می‌کنیم)
            await PingAllServers();

            StatusLabel.Text = "بروزرسانی شد";
            StatusLabel.TextColor = Colors.Gray;

            // 2. انتخاب هوشمند بهترین سرور
            // اولین سرور توی لیست الان کمترین پینگ رو داره (چون مرتب شده)
            var bestServer = Servers.FirstOrDefault();

            if (bestServer != null && bestServer.Ping < 1000) // اگه پینگش معقول بود
            {
                SelectedServer = bestServer;

                // یه پیغام خوشحال‌کننده
                await DisplayAlert("پیدا شد! ⚡",
                    $"بهترین سرور: {bestServer.City}\nپینگ: {bestServer.Ping} ms\n\nآماده اتصال!",
                    "بزن بریم");
            }
            else
            {
                await DisplayAlert("نتیجه", "سرور خوبی پیدا نشد. شاید اینترنتت ضعیفه؟", "باشه");
            }
        }
        private string ParseConfig(string link)
        {
            // 1. تمیزکاری لینک
            if (string.IsNullOrEmpty(link)) throw new FormatException("لینک خالی است.");
            link = link.Trim();
            // حذف توضیحات (هر چیزی بعد از #)
            if (link.Contains("#")) link = link.Substring(0, link.IndexOf('#'));

            // 2. هدر ثابت (تنظیمات حیاتی شبکه)
            // Sniffing روشن: برای باز شدن یوتیوب/تلگرام
            // DNS گوگل: برای دور زدن آلودگی DNS
            string header = @"
              ""log"": { ""loglevel"": ""warning"" },
              ""inbounds"": [
                {
                  ""port"": 10809,
                  ""listen"": ""127.0.0.1"",
                  ""protocol"": ""socks"",
                  ""settings"": { ""auth"": ""noauth"", ""udp"": true },
                  ""sniffing"": {
                    ""enabled"": true,
                    ""destOverride"": [""http"", ""tls""]
                  }
                }
              ],
              ""dns"": {
                ""servers"": [""8.8.8.8"", ""1.1.1.1""]
              },";

            // 3. روتینگ (هدایت ترافیک)
            string routing = @"
              ""routing"": {
                ""domainStrategy"": ""IPIfNonMatch"",
                ""rules"": [
                  { ""type"": ""field"", ""outboundTag"": ""proxy"", ""network"": ""tcp,udp"" }
                ]
              }";

            // 4. ساخت موتور (Outbound) بر اساس نوع لینک
            string outboundObj = "";
            string protocol = link.Contains("://") ? link.Substring(0, link.IndexOf("://")) : "";

            try
            {
                if (protocol == "vmess")
                {
                    // --- دیکد VMESS ---
                    string base64 = link.Substring(8);
                    string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                    var vmess = JsonSerializer.Deserialize<VmessConfig>(decoded);

                    // هندل کردن پورت (چون گاهی رشته است گاهی عدد)
                    string port = vmess.port?.ToString() ?? "443";

                    // تنظیمات امنیت و شبکه
                    string streamSettings = "";
                    if (vmess.net == "ws")
                        streamSettings = $@"""wsSettings"": {{ ""path"": ""{vmess.path}"", ""headers"": {{ ""Host"": ""{vmess.host}"" }} }},";
                    else if (vmess.net == "grpc")
                        streamSettings = $@"""grpcSettings"": {{ ""serviceName"": ""{vmess.path}"" }},";

                    string tlsSettings = "";
                    if (vmess.tls == "tls")
                    {
                        tlsSettings = $@"""tlsSettings"": {{ 
                            ""serverName"": ""{vmess.sni ?? vmess.host}"", 
                            ""allowInsecure"": false, 
                            ""fingerprint"": ""{vmess.fp ?? "chrome"}"" 
                        }},";
                    }

                    outboundObj = $@"{{
                        ""protocol"": ""vmess"",
                        ""settings"": {{
                            ""vnext"": [{{
                                ""address"": ""{vmess.add}"",
                                ""port"": {port},
                                ""users"": [{{ ""id"": ""{vmess.id}"", ""alterId"": {vmess.aid ?? 0}, ""security"": ""{vmess.scy ?? "auto"}"" }}]
                            }}]
                        }},
                        ""streamSettings"": {{
                            ""network"": ""{vmess.net}"",
                            ""security"": ""{vmess.tls}"",
                            {streamSettings}
                            {tlsSettings}
                            ""sockopt"": {{ ""tcpFastOpen"": true }}
                        }},
                        ""tag"": ""proxy""
                    }}";
                }
                else if (protocol == "vless")
                {
                    // --- دیکد VLESS (پادشاه جدید) ---
                    var uri = new Uri(link);
                    var query = HttpUtility.ParseQueryString(uri.Query);

                    string id = uri.UserInfo;
                    string address = uri.Host;
                    int port = uri.Port;
                    string type = query["type"] ?? "tcp";
                    string security = query["security"] ?? "none";
                    string flow = query["flow"] ?? "";
                    string sni = query["sni"] ?? address;
                    string fp = query["fp"] ?? "chrome";
                    string pbk = query["pbk"] ?? ""; // کلید Reality
                    string sid = query["sid"] ?? ""; // ShortId Reality
                    string path = query["path"] ?? "/";

                    // تنظیمات پیچیده TLS / Reality
                    string securitySettings = "";
                    if (security == "reality")
                    {
                        securitySettings = $@"
                            ""realitySettings"": {{
                                ""show"": false,
                                ""fingerprint"": ""{fp}"",
                                ""serverName"": ""{sni}"",
                                ""publicKey"": ""{pbk}"",
                                ""shortId"": ""{sid}"",
                                ""spiderX"": """"
                            }},";
                    }
                    else if (security == "tls")
                    {
                        securitySettings = $@"
                            ""tlsSettings"": {{
                                ""serverName"": ""{sni}"",
                                ""fingerprint"": ""{fp}"",
                                ""allowInsecure"": false
                            }},";
                    }

                    string flowJson = !string.IsNullOrEmpty(flow) ? $@"""flow"": ""{flow}""," : "";

                    string netSettings = "";
                    if (type == "ws") netSettings = $@"""wsSettings"": {{ ""path"": ""{path}"", ""headers"": {{ ""Host"": ""{query["host"] ?? sni}"" }} }},";
                    else if (type == "grpc") netSettings = $@"""grpcSettings"": {{ ""serviceName"": ""{query["serviceName"] ?? path}"" }},";

                    outboundObj = $@"{{
                        ""protocol"": ""vless"",
                        ""settings"": {{
                            ""vnext"": [{{
                                ""address"": ""{address}"",
                                ""port"": {port},
                                ""users"": [{{ ""id"": ""{id}"", ""encryption"": ""none"", {flowJson} ""level"": 0 }}]
                            }}]
                        }},
                        ""streamSettings"": {{
                            ""network"": ""{type}"",
                            ""security"": ""{security}"",
                            {securitySettings}
                            {netSettings}
                            ""sockopt"": {{ ""tcpFastOpen"": true }}
                        }},
                        ""tag"": ""proxy""
                    }}";
                }
                else if (protocol == "trojan")
                {
                    // --- دیکد Trojan ---
                    var uri = new Uri(link);
                    var query = HttpUtility.ParseQueryString(uri.Query);
                    string sni = query["sni"] ?? uri.Host;

                    outboundObj = $@"{{
                        ""protocol"": ""trojan"",
                        ""settings"": {{
                            ""servers"": [{{
                                ""address"": ""{uri.Host}"",
                                ""port"": {uri.Port},
                                ""password"": ""{uri.UserInfo}""
                            }}]
                        }},
                        ""streamSettings"": {{
                            ""network"": ""tcp"",
                            ""security"": ""tls"",
                            ""tlsSettings"": {{ ""serverName"": ""{sni}"", ""allowInsecure"": false }},
                            ""sockopt"": {{ ""tcpFastOpen"": true }}
                        }},
                        ""tag"": ""proxy""
                    }}";
                }
                else if (protocol == "ss")
                {
                    // --- دیکد ShadowSocks (همون کد قبلی سالم) ---
                    link = HttpUtility.UrlDecode(link);
                    var uri = new Uri(link);
                    string userInfo = uri.UserInfo;
                    if (string.IsNullOrEmpty(userInfo)) userInfo = uri.OriginalString.Replace("ss://", "").Split('@')[0];

                    // فیکس کردن بیس64 ناقص
                    string base64 = userInfo.Split('#')[0].Trim().Replace(" ", "+").Replace("-", "+").Replace("_", "/");
                    switch (base64.Length % 4) { case 2: base64 += "=="; break; case 3: base64 += "="; break; }

                    string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                    var parts = decoded.Split(':');

                    outboundObj = $@"{{
                        ""protocol"": ""shadowsocks"",
                        ""settings"": {{
                            ""servers"": [{{
                                ""address"": ""{uri.Host}"",
                                ""port"": {uri.Port},
                                ""method"": ""{parts[0]}"",
                                ""password"": ""{(parts.Length > 1 ? parts[1] : "")}""
                            }}]
                        }},
                        ""tag"": ""proxy""
                    }}";
                }
                else
                {
                    throw new Exception($"پروتکل {protocol} پشتیبانی نمی‌شود.");
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write($"خطا در پارس کانفیگ {protocol}: {ex.Message}");
                throw;
            }

            // 5. ترکیب نهایی JSON
            return $@"{{
        {header}
                ""outbounds"": [
                    {outboundObj},
                    {{ ""protocol"": ""freedom"", ""tag"": ""direct"" }}
                ],
                {routing}
            }}";
        }       
#if WINDOWS
        private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private void EnableProxy()
        {
            DebugLog.Write("EnableProxy کال شد");
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            key?.SetValue("ProxyEnable", 1);
            key?.SetValue("ProxyServer", "socks=127.0.0.1:10809");
            key?.SetValue("ProxyOverride", "<local>");

            for (int i = 0; i < 4; i++)
            {
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
                Task.Delay(600).Wait();
            }
            DebugLog.Write("پراکسی فعال شد");
        }

        private void DisableProxy()
        {
            DebugLog.Write("DisableProxy کال شد");
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true);
            key?.SetValue("ProxyEnable", 0);
            key?.DeleteValue("ProxyServer", false);
            key?.DeleteValue("ProxyOverride", false);

            for (int i = 0; i < 4; i++)
            {
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
                Task.Delay(600).Wait();
            }
            DebugLog.Write("پراکسی غیرفعال شد");
        }
#else
        private void EnableProxy() { DebugLog.Write("EnableProxy روی غیرویندوز - نادیده گرفته شد"); }
        private void DisableProxy() { DebugLog.Write("DisableProxy روی غیرویندوز - نادیده گرفته شد"); }
#endif
    }
}