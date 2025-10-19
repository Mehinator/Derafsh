using Derafsh.Client.Models;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Derafsh.Client;

public partial class MainPage : ContentPage
{
    public ObservableCollection<Server> Servers { get; set; }
    private Process xrayProcess;
    private bool isConnected = false;

    public MainPage()
    {
        InitializeComponent();
        Servers = new ObservableCollection<Server>
        {
            // لیست سرورهای ما اینجا قرار می‌گیرد
        };
        this.BindingContext = this;
    }

    // این مهندسِ دکمه‌ی اصلی اتصال است که گمشده بود
    private async void OnConnectButtonClicked(object sender, EventArgs e)
    {
        if (isConnected == false)
        {
            try
            {
                // کد مربوط به اتصال را اینجا اضافه خواهیم کرد
                await DisplayAlert("فرمان", "منطق اتصال در اینجا پیاده‌سازی خواهد شد.", "باشه");

                StatusLabel.Text = "وضعیت: متصل";
                ConnectButton.Text = "قطع اتصال";
                isConnected = true;
            }
            catch (Exception ex)
            {
                await DisplayAlert("خطا", ex.Message, "باشه");
            }
        }
        else
        {
            // کد مربوط به قطع اتصال
            StatusLabel.Text = "وضعیت: قطع";
            ConnectButton.Text = "اتصال";
            isConnected = false;
        }
    }

    // این مهندسِ دکمه‌ی آزمایشی ماست
    private void OnRunEngineClicked(object sender, EventArgs e)
    {
        // ۲. قبل از اعزام سرباز جدید، سرباز قدیمی را می‌کشیم
        if (xrayProcess != null && !xrayProcess.HasExited)
        {
            xrayProcess.Kill();
            xrayProcess.Dispose();
        }

        try
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string coreDirectory = Path.Combine(baseDirectory, "Core");
            string enginePath = Path.Combine(coreDirectory, "xray.exe");
            string configPath = Path.Combine(coreDirectory, "config.json");

            if (!File.Exists(enginePath))
            {
                DisplayAlert("خطا", $"فایل موتور پیدا نشد:\n{enginePath}", "باشه");
                return;
            }
            if (!File.Exists(configPath))
            {
                DisplayAlert("خطا", $"فایل نقشه پیدا نشد:\n{configPath}", "باشه");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = enginePath,
                Arguments = $"-c \"{configPath}\"", // حالا نقشه را با آدرس کامل می‌دهیم
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = coreDirectory
            };

            // ۳. سرباز جدید را به خاطر می‌سپاریم
            xrayProcess = new Process { StartInfo = startInfo };
            xrayProcess.Start();

            // ما دیگر منتظر نمی‌مانیم. فقط گزارش می‌دهیم.
            DisplayAlert("فرمان صادر شد", "موتور در پس‌زمینه اجرا شد. برای بررسی وضعیت به Task Manager بروید.", "باشه");
        }
        catch (Exception ex)
        {
            DisplayAlert("خطای بحرانی", ex.Message, "باشه");
        }
    }
}