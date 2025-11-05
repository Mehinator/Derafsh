using Derafsh.Client.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Web;

#if WINDOWS
using Microsoft.Win32;
#endif

namespace Derafsh.Client;

public partial class MainPage : ContentPage
{
    [DllImport("wininet.dll")]
    public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    public const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    public const int INTERNET_OPTION_REFRESH = 37;

    public ObservableCollection<Server> Servers { get; set; }
    private Server? selectedServer;
    private Process? xrayProcess;
    private bool isConnected = false;

    public MainPage()
    {
        InitializeComponent();
        Servers = new ObservableCollection<Server>();
        this.BindingContext = this;
    }

    private void OnServerSelected(object sender, SelectedItemChangedEventArgs e)
    {
        if (e.SelectedItem != null)
        {
            selectedServer = e.SelectedItem as Server;
        }
    }

    private void OnAddConfigClicked(object sender, EventArgs e)
    {
        string link = ConfigInput.Text?.Trim();
        if (string.IsNullOrEmpty(link))
        {
            DisplayAlert("خطا", "لینک خالی است.", "باشه");
            return;
        }

        if (!link.StartsWith("vless://") && !link.StartsWith("vmess://") && !link.StartsWith("ss://"))
        {
            DisplayAlert("خطا", "لینک باید با vless://، vmess:// یا ss:// شروع شود.", "باشه");
            return;
        }

        string displayName = "ناشناخته";
        try
        {
            var uri = new Uri(link);
            displayName = $"{uri.Host} ({uri.Port})";
        }
        catch { }

        var newServer = new Server
        {
            Config = link,
            Country = "آماده اتصال",
            City = displayName,
            FlagUrl = "flag_unknown.png"
        };

        Servers.Add(newServer);
        ConfigInput.Text = string.Empty;
    }

    private async void OnConnectButtonClicked(object sender, EventArgs e)
    {
        if (!isConnected)
        {
            if (selectedServer == null)
            {
                await DisplayAlert("خطا", "لطفاً یک سرور انتخاب کنید.", "باشه");
                return;
            }

            try
            {
                string configJson = ParseConfigToXrayJson(selectedServer.Config);
                string coreDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core");
                Directory.CreateDirectory(coreDir);

                string configPath = Path.Combine(coreDir, "config.json");
                string enginePath = Path.Combine(coreDir, "xray.exe");

#if WINDOWS
                if (xrayProcess?.HasExited == false)
                {
                    xrayProcess.Kill();
                    xrayProcess.WaitForExit(2000);
                }

                await File.WriteAllTextAsync(configPath, configJson);

                if (!File.Exists(enginePath))
                {
                    await DisplayAlert("خطا", "xray.exe پیدا نشد!", "باشه");
                    return;
                }

                xrayProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = enginePath,
                        Arguments = $"-c \"{configPath}\"",
                        WorkingDirectory = coreDir,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    }
                };

                xrayProcess.Start();
                await Task.Delay(2000);

                if (xrayProcess.HasExited)
                {
                    await DisplayAlert("خطا", "Xray اجرا نشد. سرور ممکنه مرده باشه.", "باشه");
                    return;
                }

                EnableProxy();
                await Task.Delay(1000);

                // تشخیص کشور و شهر واقعی از روی IP
                using var client = new HttpClient();
                var json = await client.GetStringAsync("https://ipapi.co/json/");
                using var doc = JsonDocument.Parse(json);

                var ip = doc.RootElement.GetProperty("ip").GetString() ?? "نامشخص";
                var country = doc.RootElement.GetProperty("country_name").GetString() ?? "ناشناخته";
                var city = doc.RootElement.GetProperty("city").GetString() ?? "ناشناخته";

                selectedServer.Country = country;
                selectedServer.City = $"{city} ({ip})";

                StatusLabel.Text = "وضعیت: متصل";
                ConnectButton.Text = "قطع اتصال";
                isConnected = true;
#endif
            }
            catch (Exception ex)
            {
                await DisplayAlert("خطا", ex.Message, "باشه");
            }
        }
        else
        {
#if WINDOWS
            DisableProxy();
            xrayProcess?.Kill();
#endif
            StatusLabel.Text = "وضعیت: قطع";
            ConnectButton.Text = "اتصال";
            isConnected = false;
        }
    }

    // === پارسر کامل کانفیگ‌ها ===
    private string ParseConfigToXrayJson(string configLink)
    {
        if (configLink.StartsWith("ss://"))
            return ParseShadowsocks(configLink);
        if (configLink.StartsWith("vmess://"))
            return ParseVmess(configLink);
        if (configLink.StartsWith("vless://"))
            return ParseVless(configLink);
        throw new NotSupportedException("فرمت کانفیگ پشتیبانی نمی‌شود.");
    }

    private string ParseShadowsocks(string link)
    {
        // URL Decoding کامل
        link = HttpUtility.UrlDecode(link);

        var uri = new Uri(link);
        // استخراج بخش Base64 از UserInfo یا Path
        string base64Part;

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            base64Part = uri.UserInfo;
        }
        else
        {
            // پردازش دستی اگر UserInfo خالی باشد
            var path = uri.GetComponents(UriComponents.Path, UriFormat.Unescaped);
            var atIndex = path.IndexOf('@');
            if (atIndex < 0)
                throw new FormatException("فرمت Shadowsocks نامعتبر است.");
            base64Part = path.Substring(0, atIndex);
        }

        // حذف query string و fragment
        base64Part = base64Part.Split('?')[0]; // حذف query string
        base64Part = base64Part.Split('#')[0]; // حذف fragment

        // تصحیح padding Base64
        base64Part = base64Part.Trim();
        base64Part = FixBase64Padding(base64Part);

        // Decode Base64
        string decoded;
        try
        {
            var bytes = Convert.FromBase64String(base64Part);
            decoded = System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"خطا در تبدیل Base64. لینک معتبر نیست: {ex.Message}");
        }

        // فرمت: method:password@host:port
        var lastAt = decoded.LastIndexOf('@');
        if (lastAt == -1) throw new FormatException("فرمت Shadowsocks نامعتبر است.");

        var auth = decoded.Substring(0, lastAt);
        var server = decoded.Substring(lastAt + 1);

        var authParts = auth.Split(':');
        var method = authParts[0];
        var password = string.Join(":", authParts.Skip(1)); // رمز ممکنه شامل : باشه

        var serverParts = server.Split(':');
        var address = serverParts[0];
        var port = int.Parse(serverParts[1]);

        return $@"{{
  ""log"": {{ ""loglevel"": ""warning"" }},
  ""inbounds"": [{{
    ""port"": 10808,
    ""listen"": ""127.0.0.1"",
    ""protocol"": ""socks"",
    ""settings"": {{ ""auth"": ""noauth"", ""udp"": true }}
  }}],
  ""outbounds"": [{{
    ""protocol"": ""shadowsocks"",
    ""settings"": {{
      ""servers"": [{{
        ""address"": ""{address}"",
        ""port"": {port},
        ""method"": ""{method}"",
        ""password"": ""{password}""
      }}]
    }},
    ""tag"": ""proxy""
  }}, {{
    ""protocol"": ""freedom"",
    ""tag"": ""direct""
  }}],
  ""routing"": {{
    ""rules"": [{{
      ""type"": ""field"",
      ""network"": ""tcp,udp"",
      ""outboundTag"": ""proxy""
    }}]
  }}
}}";
    }

    private string FixBase64Padding(string base64)
    {
        // حذف کاراکترهای غیر Base64
        base64 = base64.Trim().Replace(" ", "+");

        // محاسبه padding لازم
        int mod4 = base64.Length % 4;
        if (mod4 > 0)
        {
            base64 += new string('=', 4 - mod4);
        }

        // حداکثر دو کاراکتر padding مجاز است
        if (base64.EndsWith("==="))
            base64 = base64.Substring(0, base64.Length - 1);
        else if (base64.EndsWith("===="))
            base64 = base64.Substring(0, base64.Length - 2);

        return base64;
    }

    private string ParseVmess(string link)
    {
        var base64 = link.Substring("vmess://".Length);
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var address = root.GetProperty("add").GetString();
        var port = root.GetProperty("port").GetInt32();
        var id = root.GetProperty("id").GetString();
        var net = root.GetProperty("net").GetString() ?? "tcp";
        var host = root.GetProperty("host").GetString() ?? "";
        var path = root.GetProperty("path").GetString() ?? "/";
        var tls = root.GetProperty("tls").GetString() ?? "";
        var sni = root.GetProperty("sni").GetString() ?? address;

        var wsSettings = net == "ws" ? $@",
    ""wsSettings"": {{
      ""path"": ""{path}"",
      ""headers"": {{ ""Host"": ""{host}"" }}
    }}" : "";

        return $@"{{
  ""log"": {{ ""loglevel"": ""warning"" }},
  ""inbounds"": [{{
    ""port"": 10808,
    ""listen"": ""127.0.0.1"",
    ""protocol"": ""socks"",
    ""settings"": {{ ""auth"": ""noauth"", ""udp"": true }}
  }}],
  ""outbounds"": [{{
    ""protocol"": ""vmess"",
    ""settings"": {{
      ""vnext"": [{{
        ""address"": ""{address}"",
        ""port"": {port},
        ""users"": [{{
          ""id"": ""{id}"",
          ""security"": ""auto""
        }}]
      }}]
    }},
    ""streamSettings"": {{
      ""network"": ""{net}"",
      ""security"": ""{(tls == "tls" ? "tls" : "none")}"",
      ""tlsSettings"": {{ ""serverName"": ""{sni}"" }}{wsSettings}
    }},
    ""tag"": ""proxy""
  }}, {{
    ""protocol"": ""freedom"",
    ""tag"": ""direct""
  }}],
  ""routing"": {{
    ""rules"": [{{
      ""type"": ""field"",
      ""network"": ""tcp,udp"",
      ""outboundTag"": ""proxy""
    }}]
  }}
}}";
    }

    private string ParseVless(string link)
    {
        // URL Decoding کامل
        link = HttpUtility.UrlDecode(link);

        var uri = new Uri(link);
        var uuid = uri.UserInfo;
        var address = uri.Host;
        var port = uri.Port;
        var query = HttpUtility.ParseQueryString(uri.Query);
        var security = query["security"] ?? "none";
        var type = query["type"] ?? "tcp";
        var encryption = query["encryption"] ?? "none";
        var sni = query["sni"] ?? address;
        var host = query["host"] ?? sni;
        var path = query["path"] ?? "/";

        var tlsOrReality = "";
        if (security == "tls")
            tlsOrReality = $@",
      ""tlsSettings"": {{ ""serverName"": ""{sni}"" }}";
        else if (security == "reality")
        {
            var fp = query["fp"] ?? "chrome";
            var pbk = query["pbk"] ?? "";
            var sid = query["sid"] ?? "";
            var spx = query["spx"] ?? "/";
            tlsOrReality = $@",
      ""realitySettings"": {{
        ""serverName"": ""{sni}"",
        ""fingerprint"": ""{fp}"",
        ""publicKey"": ""{pbk}"",
        ""shortId"": ""{sid}"",
        ""spiderX"": ""{spx}""
      }}";
        }

        var wsOrGrpc = "";
        if (type == "ws")
            wsOrGrpc = $@",
      ""wsSettings"": {{
        ""path"": ""{path}"",
        ""headers"": {{ ""Host"": ""{host}"" }}
      }}";
        else if (type == "grpc")
            wsOrGrpc = $@",
      ""grpcSettings"": {{ ""serviceName"": ""{path}"" }}";

        return $@"{{
  ""log"": {{ ""loglevel"": ""warning"" }},
  ""inbounds"": [{{
    ""port"": 10808,
    ""listen"": ""127.0.0.1"",
    ""protocol"": ""socks"",
    ""settings"": {{ ""auth"": ""noauth"", ""udp"": true }}
  }}],
  ""outbounds"": [{{
    ""protocol"": ""vless"",
    ""settings"": {{
      ""vnext"": [{{
        ""address"": ""{address}"",
        ""port"": {port},
        ""users"": [{{
          ""id"": ""{uuid}"",
          ""encryption"": ""{encryption}""
        }}]
      }}]
    }},
    ""streamSettings"": {{
      ""network"": ""{type}"",
      ""security"": ""{security}""{tlsOrReality}{wsOrGrpc}
    }},
    ""tag"": ""proxy""
  }}, {{
    ""protocol"": ""freedom"",
    ""tag"": ""direct""
  }}],
  ""routing"": {{
    ""rules"": [{{
      ""type"": ""field"",
      ""network"": ""tcp,udp"",
      ""outboundTag"": ""proxy""
    }}]
  }}
}}";
    }

    private async Task<(string country, string city, string ip)> GetServerInfoAsync()
    {
        try
        {
            using var client = new HttpClient();
            var json = await client.GetStringAsync("https://ipapi.co/json/");
            using var doc = JsonDocument.Parse(json);

            var ip = doc.RootElement.GetProperty("ip").GetString() ?? "نامشخص";
            var country = doc.RootElement.GetProperty("country_name").GetString() ?? "ناشناخته";
            var city = doc.RootElement.GetProperty("city").GetString() ?? "ناشناخته";

            return (country, city, ip);
        }
        catch
        {
            return ("ناموفق", "ناموفق", "ناموفق");
        }
    }

#if WINDOWS
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private void EnableProxy()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true))
        {
            key?.SetValue("ProxyEnable", 1);
            key?.SetValue("ProxyServer", "127.0.0.1:10808");
        }
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    private void DisableProxy()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, true))
        {
            key?.SetValue("ProxyEnable", 0);
        }
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }
#else
    private void EnableProxy() { }
    private void DisableProxy() { }
#endif
}