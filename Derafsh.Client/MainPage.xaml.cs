using Derafsh.Client.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;

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
            new Server { Country = "Germany", City = "Frankfurt", FlagUrl = "germany_flag.png" },
            new Server { Country = "USA", City = "New York", FlagUrl = "usa_flag.png" },
            new Server { Country = "Netherlands", City = "Amsterdam", FlagUrl = "netherlands_flag.png" },
            new Server { Country = "Japan", City = "Tokyo", FlagUrl = "japan_flag.png" }
        };
        this.BindingContext = this;
    }

    private async void OnConnectButtonClicked(object sender, EventArgs e)
    {
        if (isConnected == false)
        {
            StatusLabel.Text = "در حال تست پینگ سرورها...";
            ConnectButton.IsEnabled = false;

            var random = new Random();
            foreach (var server in Servers)
            {
                await Task.Delay(250);
                server.Ping = random.Next(50, 500);
            }

            var bestServer = Servers.OrderBy(s => s.Ping).FirstOrDefault();

            if (bestServer != null)
            {
                StatusLabel.Text = $"متصل به سریع‌ترین سرور: {bestServer.Country}";
                ConnectButton.Text = "قطع اتصال";
                isConnected = true;
            }

            ConnectButton.IsEnabled = true;
        }
        else
        {
            StatusLabel.Text = "وضعیت: قطع";
            ConnectButton.Text = "یافتن سریع‌ترین سرور و اتصال";
            isConnected = false;
            foreach (var server in Servers)
            {
                server.Ping = 0;
            }
        }
    }

    private void OnRunEngineClicked(object sender, EventArgs e)
    {
        try
        {
            // آدرس دقیق فایل موتور را اینجا قرار بده
            string enginePath = @"C:\Users\BiBiLi\source\repos\Derafsh\Derafsh.Client\Core\xray.exe";

            if (!System.IO.File.Exists(enginePath))
            {
                DisplayAlert("خطا", $"فایل موتور در مسیر زیر پیدا نشد:\n{enginePath}", "باشه");
                return;
            }

            System.Diagnostics.Process process = new System.Diagnostics.Process();
            System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo();
            startInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            startInfo.FileName = enginePath;
            process.StartInfo = startInfo;

            process.Start();

            DisplayAlert("موفقیت", "فرمان اجرای موتور با موفقیت صادر شد!", "باشه");
        }
        catch (Exception ex)
        {
            DisplayAlert("خطای بحرانی", $"اجرای موتور با شکست مواجه شد: {ex.Message}", "باشه");
        }
    }
}