using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;

namespace Derafsh.Client
{
    // سرویس VPN اندروید:
    //   همه‌ی ترافیک گوشی → رابط TUN → تونل hev-socks5-tunnel (داخل همین برنامه) → SOCKS محلی xray (127.0.0.1:10809) → سرور کانفیگ
    // خود برنامه از VPN مستثناست، تا ترافیک xray دوباره وارد تونل نشه.
    [Service(Permission = "android.permission.BIND_VPN_SERVICE", Exported = false)]
    [IntentFilter(new[] { "android.net.VpnService" })]
    public class DerafshVpnService : VpnService
    {
        public const string ActionStart = "com.derafsh.client.START";
        public const string ActionStop = "com.derafsh.client.STOP";
        public const int SocksPort = 10809;

        public static bool IsRunning { get; private set; }
        public static string? LastError { get; private set; }
        public static event Action? StateChanged;

        private ParcelFileDescriptor? _tun;
        private XrayProc? _xray;

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            if (intent?.Action == ActionStop)
            {
                StopVpn();
                StopSelf();
                return StartCommandResult.NotSticky;
            }
            string? config = intent?.GetStringExtra("config");
            if (config == null) return StartCommandResult.NotSticky;
            Task.Run(() => StartVpn(config));
            return StartCommandResult.NotSticky;
        }

        private async Task StartVpn(string configPath)
        {
            try
            {
                // switching servers: stop the old tunnel quietly, the controller is waiting for the new result
                StopVpn(notify: false);
                _xray = XrayProc.Start(configPath);
                if (!await XrayProc.WaitForPortAsync(SocksPort, 6000))
                    throw new Exception("The Xray engine didn't start with this config.");

                var builder = new Builder(this)
                    .SetSession("Derafsh")!
                    .SetMtu(1500)!
                    .AddAddress("172.19.0.1", 30)!
                    .AddRoute("0.0.0.0", 0)!
                    .AddAddress("fdfe:dcba:9876::1", 126)!
                    .AddRoute("::", 0)!
                    .AddDnsServer("1.1.1.1")!
                    .AddDnsServer("8.8.8.8")!;
                builder.AddDisallowedApplication(PackageName!);
                _tun = builder.Establish() ?? throw new Exception("Android didn't allow the VPN. Open Derafsh and allow the VPN request.");

                string hevConfig = Path.Combine(CacheDir!.AbsolutePath, "hev.yml");
                File.WriteAllText(hevConfig,
                    "tunnel:\n  mtu: 1500\n  ipv4: 172.19.0.1\n  ipv6: 'fdfe:dcba:9876::1'\n" +
                    $"socks5:\n  port: {SocksPort}\n  address: 127.0.0.1\n  udp: 'udp'\n" +
                    "misc:\n  task-stack-size: 81920\n");
                Hev.Start(this, hevConfig, _tun.Fd);

                IsRunning = true;
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Android.Util.Log.Error("Derafsh", "VPN start failed: " + ex);
                StopVpn(notify: false);
            }
            StateChanged?.Invoke();
        }

        private void StopVpn(bool notify = true)
        {
            Hev.Stop();
            _xray?.Dispose();
            _xray = null;
            try { _tun?.Close(); } catch { }
            _tun = null;
            bool was = IsRunning;
            IsRunning = false;
            if (was && notify) StateChanged?.Invoke();
        }

        // کاربر VPN رو از تنظیمات اندروید قطع کرده
        public override void OnRevoke()
        {
            StopVpn();
            base.OnRevoke();
        }

        public override void OnDestroy()
        {
            StopVpn();
            base.OnDestroy();
        }
    }

    // صدا زدن hev-socks5-tunnel (کتابخانه‌ی AAR) از C#: کلاس جاوای hev.htproxy.TProxyService با متدهای native
    internal static class Hev
    {
        private static Java.Lang.Reflect.Method? _start, _stop;

        private static void Load(Context context)
        {
            if (_start != null) return;
            var cls = Java.Lang.Class.ForName("hev.htproxy.TProxyService", true, context.ClassLoader);
            _start = cls.GetMethod("TProxyStartService", Java.Lang.Class.FromType(typeof(Java.Lang.String)), Java.Lang.Integer.Type);
            _stop = cls.GetMethod("TProxyStopService");
        }

        public static void Start(Context context, string configPath, int tunFd)
        {
            Load(context);
            var ok = _start!.Invoke(null, new Java.Lang.String(configPath), Java.Lang.Integer.ValueOf(tunFd)) as Java.Lang.Boolean;
            if (ok != null && !ok.BooleanValue())
                throw new Exception("The tunnel didn't start.");
        }

        public static void Stop()
        {
            try { _stop?.Invoke(null); } catch { }
        }
    }

    // از صفحه‌ی اصلی صدا زده می‌شه: اجازه‌ی VPN، روشن و خاموش کردن سرویس
    public static class VpnController
    {
        public static string? LastError { get; private set; }

        public static async Task<bool> StartAsync(string configPath)
        {
            var activity = Platform.CurrentActivity ?? throw new Exception("The app window isn't ready yet.");

            // اولین بار اندروید از کاربر اجازه‌ی VPN می‌گیره
            var consent = VpnService.Prepare(activity);
            if (consent != null)
            {
                MainActivity.VpnPermission = new TaskCompletionSource<bool>();
                activity.StartActivityForResult(consent, MainActivity.VpnRequestCode);
                if (!await MainActivity.VpnPermission.Task)
                {
                    LastError = "VPN permission was not given. Press Connect again and choose OK.";
                    return false;
                }
            }

            var done = new TaskCompletionSource<bool>();
            void OnChanged() => done.TrySetResult(DerafshVpnService.IsRunning);
            DerafshVpnService.StateChanged += OnChanged;
            try
            {
                var intent = new Intent(activity, typeof(DerafshVpnService)).SetAction(DerafshVpnService.ActionStart)!.PutExtra("config", configPath);
                activity.StartService(intent);
                var finished = await Task.WhenAny(done.Task, Task.Delay(15000));
                bool ok = finished == done.Task && done.Task.Result;
                LastError = ok ? null : DerafshVpnService.LastError ?? "The VPN didn't start in time.";
                return ok;
            }
            finally { DerafshVpnService.StateChanged -= OnChanged; }
        }

        public static void Stop()
        {
            var context = Android.App.Application.Context;
            var intent = new Intent(context, typeof(DerafshVpnService)).SetAction(DerafshVpnService.ActionStop);
            context.StartService(intent);
        }
    }
}
