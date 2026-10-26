using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Derafsh.Client
{
    // یک پروسه‌ی هسته‌ی Aether (کلاینت WARP). مثل XrayProc:
    //   ویندوز: Core/aether.exe کنار برنامه (پوشه‌ی pt/ کنارش، تور و سایفون رو خودش پیدا می‌کنه)
    //   اندروید: libaether.so از پوشه‌ی کتابخانه‌های native (اندروید فقط اجرای فایل‌های همون پوشه رو اجازه می‌ده)
    // شناسه‌ی WARP در AppData/aether ذخیره می‌شه تا هر بار ثبت‌نام تکراری نشه (محدودیت ثبت‌نام کلادفلر).
    public sealed class AetherProc : IDisposable
    {
        public const int SessionPort = 18190;   // پورت ثابت اتصال (مثل 10809 برای xray)

#if ANDROID
        private readonly Java.Lang.Process _p;
        private AetherProc(Java.Lang.Process p) { _p = p; }
#else
        private readonly Process _p;
        private AetherProc(Process p) { _p = p; }

        // پروسه‌های تست؛ وصل شدن نباید این‌ها رو ببنده
        private static readonly HashSet<int> s_testIds = new();
        private bool _isTest;
        public static bool IsTestProcess(int id) { lock (s_testIds) return s_testIds.Contains(id); }
#endif

        // پوشه‌ی شناسه‌ها (پایدار بین اجراها)
        public static string IdentityDir
        {
            get
            {
                string dir = Path.Combine(FileSystem.AppDataDirectory, "aether");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        public static string EnginePath =>
#if ANDROID
            Path.Combine(Android.App.Application.Context.ApplicationInfo!.NativeLibraryDir!, "libaether.so");
#else
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Core", "aether.exe");
#endif

        public static bool EngineExists => File.Exists(EnginePath);

        // مسیر ابزارهای کناری (سایفون و لایربرد). ویندوز: خود هسته اونا رو در ./pt کنارش پیدا می‌کنه؛
        // اندروید: از پوشه‌ی native با اسم lib*.so باید مسیر صریح بدیم.
        public static string? PsiphonBinPath =>
#if ANDROID
            Path.Combine(Android.App.Application.Context.ApplicationInfo!.NativeLibraryDir!, "libpsiphon-tunnel-core.so");
#else
            null;   // هسته خودش pt/psiphon-tunnel-core.exe را کنارش پیدا می‌کند
#endif

        public static string? LyrebirdPath =>
#if ANDROID
            Path.Combine(Android.App.Application.Context.ApplicationInfo!.NativeLibraryDir!, "liblyrebird.so");
#else
            null;
#endif

        /// <summary>آرگومان کامل اجرا (شامل --config شناسه) برای یک پروفایل؛ هم برای اتصال، هم برای فایل sidecar اندروید.</summary>
        public static List<string> BuildSessionArgs(AetherLink link, int port, int? upstreamPort)
        {
            var launch = link.BuildArgs(port, scan: false, upstreamPort: upstreamPort,
                psiphonBin: PsiphonBinPath, lyrebirdPath: LyrebirdPath);
            string identity = Path.Combine(IdentityDir, $"{link.IdentityKey}.toml");
            var args = new List<string>(launch.Args) { "--config", identity };
            return args;
        }

        /// <summary>اجرا با آرگومان‌های آماده (مثل Start ولی سطح پایین‌تر؛ سرویس VPN اندروید هم از همین استفاده می‌کنه).</summary>
        public static AetherProc StartArgs(List<string> args, bool isTest = false)
        {
#if ANDROID
            var pb = new Java.Lang.ProcessBuilder(new[] { EnginePath }.Concat(args).ToArray());   // اسم برنامه و بعد آرگومان‌ها، هر کدوم جدا (بدون shell)
            pb.Directory(new Java.IO.File(XrayProc.WorkDir));
            pb.RedirectErrorStream(true);
            // خروجی باید جایی بره وگرنه بافر پر می‌شه و هسته گیر می‌کنه (مثل xray)
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                pb.RedirectOutput(new Java.IO.File(Path.Combine(XrayProc.WorkDir, $"aether_{(isTest ? "test" : "session")}.log")));
            var proc = pb.Start()!;
            if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var output = proc.InputStream;
                _ = Task.Run(() => { var buf = new byte[4096]; try { while (output.Read(buf, 0, buf.Length) > 0) { } } catch { } });
            }
            return new AetherProc(proc);
#else
            var psi = new ProcessStartInfo
            {
                FileName = EnginePath,
                WorkingDirectory = Path.GetDirectoryName(EnginePath)!,   // pt/ کنار هسته پیدا بشه
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var p = Process.Start(psi)!;
            if (isTest) lock (s_testIds) s_testIds.Add(p.Id);
            return new AetherProc(p) { _isTest = isTest };
#endif
        }

        /// <summary>اجرا از روی پروفایل لینک. port: پورت گوش‌دهنده‌ی اصلی؛ scan: حالت اسکن (تست‌ها).</summary>
        public static AetherProc Start(AetherLink link, int port, bool isTest = false, int? upstreamPort = null)
        {
            if (isTest)
            {
                var launch = link.BuildArgs(port, scan: true, upstreamPort: upstreamPort,
                    psiphonBin: PsiphonBinPath, lyrebirdPath: LyrebirdPath);
                string identity = Path.Combine(IdentityDir, $"{link.IdentityKey}.toml");
                var args = new List<string>(launch.Args) { "--config", identity };
                return StartArgs(args, isTest: true);
            }
            return StartArgs(BuildSessionArgs(link, port, upstreamPort), isTest: false);
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

        // پورت‌های آزمون تست‌ها (مثل ReserveTestPort برای xray)
        private static readonly HashSet<int> s_ports = new();
        public static int ReservePort()
        {
            lock (s_ports)
            {
                for (int i = 0; i < 20; i++)
                {
                    var l = new TcpListener(IPAddress.Loopback, 0);
                    l.Start();
                    int port = ((IPEndPoint)l.LocalEndpoint).Port;
                    l.Stop();
                    if (port != XrayProc.SessionPortConst && port != SessionPort && s_ports.Add(port)) return port;
                }
                throw new Exception("No free local port for the Aether test.");
            }
        }

        public static void ReleasePort(int port) { lock (s_ports) s_ports.Remove(port); }
    }

    // فایل هم‌نام کانفیگ (config.json.aether): سرویس VPN اندروید از روش می‌فهمه هسته‌ی ایتر رو با چه آرگومان‌هایی بالا بیاره
    public sealed class AetherSidecar
    {
        public List<string> Args { get; set; } = new();
    }
}
