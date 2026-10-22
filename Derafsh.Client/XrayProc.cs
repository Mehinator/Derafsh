using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Derafsh.Client
{
    // یک پروسه‌ی Xray. ویندوز: Core/xray.exe کنار برنامه؛ اندروید: libxray.so از پوشه‌ی کتابخانه‌های native
    // (اندروید فقط اجازه‌ی اجرای فایل‌های همون پوشه رو می‌ده)
    public sealed class XrayProc : IDisposable
    {
#if ANDROID
        private readonly Java.Lang.Process _p;
        private XrayProc(Java.Lang.Process p) { _p = p; }
#else
        private readonly Process _p;
        private XrayProc(Process p) { _p = p; }

        // پروسه‌های xray تست (پینگ و جمینی)؛ وصل شدن نباید این‌ها رو ببنده
        private static readonly HashSet<int> s_testIds = new();
        private bool _isTest;
        public static bool IsTestProcess(int id) { lock (s_testIds) return s_testIds.Contains(id); }
#endif

        // پوشه‌ای که کانفیگ‌ها توش نوشته می‌شن
        public static string WorkDir =>
#if ANDROID
            FileSystem.CacheDirectory;
#else
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core");
#endif

        public static string EnginePath =>
#if ANDROID
            Path.Combine(Android.App.Application.Context.ApplicationInfo!.NativeLibraryDir!, "libxray.so");
#else
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core", "xray.exe");
#endif

        public static bool EngineExists => File.Exists(EnginePath);

        public static XrayProc Start(string configPath, bool isTest = false)
        {
#if ANDROID
            var pb = new Java.Lang.ProcessBuilder(EnginePath, "run", "-c", configPath);
            pb.Directory(new Java.IO.File(WorkDir));
            pb.Environment()!["XRAY_LOCATION_ASSET"] = WorkDir;
            pb.RedirectErrorStream(true);
            // خروجی باید جایی خونده/نوشته بشه وگرنه بافرش پر می‌شه و xray گیر می‌کنه.
            // اندروید ۸ به بعد: مستقیم توی فایل؛ اندروید ۷ (بدون RedirectOutput): خودمون می‌خونیم و دور می‌ریزیم
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                pb.RedirectOutput(new Java.IO.File(Path.Combine(WorkDir, Path.GetFileNameWithoutExtension(configPath) + ".log")));
            var proc = pb.Start()!;
            if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var output = proc.InputStream;
                _ = Task.Run(() => { var buf = new byte[4096]; try { while (output.Read(buf, 0, buf.Length) > 0) { } } catch { } });
            }
            return new XrayProc(proc);
#else
            var p = new Process { StartInfo = new ProcessStartInfo { FileName = EnginePath, Arguments = $"run -c \"{configPath}\"", WorkingDirectory = WorkDir, CreateNoWindow = true, UseShellExecute = false } };
            p.Start();
            if (isTest) lock (s_testIds) s_testIds.Add(p.Id);
            return new XrayProc(p) { _isTest = isTest };
#endif
        }

        public void Kill()
        {
#if ANDROID
            try { _p.Destroy(); } catch { }
#else
            try { if (!_p.HasExited) _p.Kill(); } catch { }
            if (_isTest) try { lock (s_testIds) s_testIds.Remove(_p.Id); } catch { }
#endif
        }

        public void Dispose()
        {
            Kill();
#if ANDROID
            _p.Dispose();
#else
            _p.Dispose();
#endif
        }

        // صبر تا xray پورت SOCKS رو باز کنه
        public static async Task<bool> WaitForPortAsync(int port, int timeoutMs = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, port); return true; }
                catch { await Task.Delay(100); }
            }
            return false;
        }

        // HttpClient از راه پراکسی SOCKS محلی xray؛ SocketsHttpHandler روی ویندوز و اندروید هر دو SOCKS رو پشتیبانی می‌کنه
        public static HttpClient SocksClient(int port, TimeSpan? timeout = null)
        {
            var handler = new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{port}"), UseProxy = true };
            var client = new HttpClient(handler);
            if (timeout != null) client.Timeout = timeout.Value;
            return client;
        }
    }
}
