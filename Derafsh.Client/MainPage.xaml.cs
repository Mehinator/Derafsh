using Derafsh.Client.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Sockets; // ۱. این کتابخانه‌ی جدید، ابزار کماندوی ماست

namespace Derafsh.Client;

public partial class MainPage : ContentPage
{
    public ObservableCollection<Server> Servers { get; set; }
    private bool isConnected = false;

    public MainPage()
    {
        InitializeComponent();
        Servers = new ObservableCollection<Server>
        {
            // ۲. سرورها حالا آدرس و پورت واقعی دارن
            new Server { Country = "Free v2ray", City = "US", FlagUrl = "usa_flag.png", Host = "us.v2ray.com", Port = 443 },
            new Server { Country = "Free v2ray", City = "Germany", FlagUrl = "germany_flag.png", Host = "de.v2ray.com", Port = 80 },
            new Server { Country = "Public Server", City = "NL", FlagUrl = "netherlands_flag.png", Host = "nl.server.com", Port = 8080 },
            new Server { Country = "Test Server", City = "JP", FlagUrl = "japan_flag.png", Host = "jp.test.net", Port = 2052 }
        };
        this.BindingContext = this;
    }

    private async void OnConnectButtonClicked(object sender, EventArgs e)
    {
        if (isConnected == false)
        {
            StatusLabel.Text = "در حال تست سرورها...";
            ConnectButton.IsEnabled = false;

            var testTasks = new List<Task>();
            foreach (var server in Servers)
            {
                testTasks.Add(TestServerConnectionAsync(server)); // ۳. از کماندوی جدید استفاده می‌کنیم
            }
            await Task.WhenAll(testTasks);

            var bestServer = Servers.OrderBy(s => s.Ping).FirstOrDefault(s => s.Ping > 0);

            if (bestServer != null)
            {
                StatusLabel.Text = $"آماده برای اتصال به: {bestServer.Host}";
                // منطق اتصال واقعی در آینده اینجا قرار می‌گیره
            }
            else
            {
                StatusLabel.Text = "خطا: هیچ سروری پاسخ نداد.";
            }

            ConnectButton.IsEnabled = true;
        }
        else
        {
            // منطق قطع اتصال
        }
    }

    // ۴. این کماندوی متخصص ما برای تست پورت است
    private async Task TestServerConnectionAsync(Server server)
    {
        var stopwatch = new Stopwatch();
        try
        {
            using (var client = new TcpClient())
            {
                stopwatch.Start();
                // ما فقط سعی می‌کنیم یه اتصال خیلی سریع برقرار کنیم
                var connectTask = client.ConnectAsync(server.Host, server.Port);
                if (await Task.WhenAny(connectTask, Task.Delay(2000)) == connectTask) // ۲ ثانیه برای اتصال صبر می‌کنیم
                {
                    // اگر اتصال موفق بود
                    stopwatch.Stop();
                    server.Ping = (int)stopwatch.ElapsedMilliseconds;
                }
                else
                {
                    // اگر بعد از ۲ ثانیه جوابی نیومد (Timeout)
                    stopwatch.Stop();
                    server.Ping = -1;
                }
            }
        }
        catch (Exception)
        {
            // اگه هر خطای دیگه‌ای رخ بده (مثل آدرس غلط)
            stopwatch.Stop();
            server.Ping = -1;
        }
    }
}