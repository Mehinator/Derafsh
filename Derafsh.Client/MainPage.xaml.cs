using Derafsh.Client.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices; // ۱. کتابخانه‌ی پیام‌رسان سلطنتی

#if WINDOWS
using Microsoft.Win32;
#endif

namespace Derafsh.Client;

public partial class MainPage : ContentPage
{
    // ۲. استخدام پیام‌رسان سلطنتی
    [DllImport("wininet.dll")]
    public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    public const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    public const int INTERNET_OPTION_REFRESH = 37;

    public ObservableCollection<Server> Servers { get; set; }
    private Server selectedServer;
    private Process xrayProcess;
    private bool isConnected = false;

    public MainPage()
    {
        InitializeComponent();

        Servers = new ObservableCollection<Server>
        {
            new Server
            {
                Country = "@Daily_Configs",
                City = "WebSocket",
                FlagUrl = "usa_flag.png",
                Config = "vless://c2e177d4-e610-4abd-85cd-1a869d158751@188.114.97.202:8443?encryption=none&security=tls&sni=teslakit1.pages.dev&type=ws&host=teslakit1.pages.dev&path=%2F%3FTELEGRAM-MARAMBASHI_MARAMBASHI_MARAMBASHI_MARAMBASHI%3Fed%3D512#%40Daily_Configs"
            }
        };

        this.BindingContext = this;
    }

    private void OnServerSelected(object sender, SelectedItemChangedEventArgs e)
    {
        if (e.SelectedItem != null)
        {
            selectedServer = e.SelectedItem as Server;
        }
    }

    private async void OnConnectButtonClicked(object sender, EventArgs e)
    {
        if (isConnected == false)
        {
            if (selectedServer == null)
            {
                await DisplayAlert("خطا", "لطفاً ابتدا یک سرور را از لیست انتخاب کنید.", "باشه");
                return;
            }

            try
            {
                // حالا ژنرال، استاد کدشکن را صدا می‌زند
                string newConfigJson = GenerateConfigFromVless(selectedServer);

                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string coreDirectory = Path.Combine(baseDirectory, "Core");
                string enginePath = Path.Combine(coreDirectory, "xray.exe");
                string configPath = Path.Combine(coreDirectory, "config.json");

                await File.WriteAllTextAsync(configPath, newConfigJson);

                if (!File.Exists(enginePath))
                {
                    await DisplayAlert("خطا", "فایل موتور (xray.exe) پیدا نشد!", "باشه");
                    return;
                }

                xrayProcess = new Process();
                xrayProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                xrayProcess.StartInfo.FileName = enginePath;
                xrayProcess.StartInfo.Arguments = $"-c \"{configPath}\"";
                xrayProcess.StartInfo.WorkingDirectory = coreDirectory;
                xrayProcess.Start();

                await Task.Delay(1500);

                if (xrayProcess.HasExited)
                {
                    await DisplayAlert("خطا", "موتور نتوانست با کانفیگ واقعی اجرا شود. کانفیگ یا ساعت سیستم را چک کنید.", "باشه");
                    return;
                }

                EnableProxy();
                StatusLabel.Text = "وضعیت: متصل";
                ConnectButton.Text = "قطع اتصال";
                isConnected = true;
            }
            catch (Exception ex)
            {
                await DisplayAlert("خطای بحرانی", ex.Message, "باشه");
            }
        }
        else
        {
            DisableProxy();
            if (xrayProcess != null && !xrayProcess.HasExited)
            {
                xrayProcess.Kill();
            }
            StatusLabel.Text = "وضعیت: قطع";
            ConnectButton.Text = "اتصال";
            isConnected = false;
        }
    }

    // ۳. این همان استاد کدشکن است که فراموش شده بود!
    private string GenerateConfigFromVless(Server server)
    {
        // در آینده اینجا یک کدشکن واقعی می‌سازیم که اطلاعات را از server.Config استخراج کند
        var uuid = "c2e177d4-e610-4abd-85cd-1a869d158751";
        var address = "188.114.97.202";
        var port = 8443;
        var sni = "teslakit1.pages.dev";
        var hostHeader = "teslakit1.pages.dev";
        var path = "/";

        var configJson = $@"{{""log"":{{""loglevel"":""warning""}},""inbounds"":[{{""port"":10808,""listen"":""127.0.0.1"",""protocol"":""socks"",""settings"":{{""auth"":""noauth"",""udp"":true}}}},{{""port"":10809,""listen"":""127.0.0.1"",""protocol"":""http"",""settings"":{{""auth"":""noauth"",""udp"":true}}}}],""outbounds"":[{{""protocol"":""vless"",""settings"":{{""vnext"":[{{""address"":""{address}"",""port"":{port},""users"":[{{""id"":""{uuid}"",""encryption"":""none""}}]}}]}},""streamSettings"":{{""network"":""ws"",""security"":""tls"",""tlsSettings"":{{""serverName"":""{sni}""}},""wsSettings"":{{""path"":""{path}"",""headers"":{{""Host"":""{hostHeader}""}}}}}},""tag"":""proxy""}},{{""protocol"":""freedom"",""tag"":""direct""}},{{""protocol"":""blackhole"",""tag"":""block""}}],""routing"":{{""rules"":[{{""type"":""field"",""ip"":[""geoip:private""],""outboundTag"":""direct""}},{{""type"":""field"",""domain"":[""geosite:cn""],""outboundTag"":""direct""}},{{""type"":""field"",""protocol"":[""bittorrent""],""outboundTag"":""block""}},{{""type"":""field"",""network"":""tcp,udp"",""outboundTag"":""proxy""}}]}}}}";
        return configJson;
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