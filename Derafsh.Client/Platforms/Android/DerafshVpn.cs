using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;

namespace Derafsh.Client
{
    // سرویس VPN اندروید:
    //   همه‌ی ترافیک گوشی → رابط TUN → تونل hev-socks5-tunnel (داخل همین برنامه) → SOCKS محلی xray (127.0.0.1:10809) → سرور کانفیگ
    // خود برنامه از VPN مستثناست، تا ترافیک xray دوباره وارد تونل نشه.
    // تا وقتی وصله، یه اعلان ثابت داره (سرور، شهر، سرعت، دکمه‌ی «قطع اتصال») و سرویس پیش‌زمینه است تا اندروید نبندتش.
    // تعریفش توی AndroidManifest.xml است (نوع پیش‌زمینه‌ی specialUse/vpn با property، که با attribute نمی‌شه نوشت).
    [Register("com.derafsh.client.DerafshVpnService")]
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
            s_server = intent?.GetStringExtra("name");
            s_location = null;
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
                    throw new Exception("موتور Xray با این کانفیگ روشن نشد.");

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
                _tun = builder.Establish() ?? throw new Exception("اندروید اجازه‌ی VPN نداد. درفش را باز کنید و به درخواست VPN «OK» بگویید.");

                string hevConfig = Path.Combine(CacheDir!.AbsolutePath, "hev.yml");
                File.WriteAllText(hevConfig,
                    "tunnel:\n  mtu: 1500\n  ipv4: 172.19.0.1\n  ipv6: 'fdfe:dcba:9876::1'\n" +
                    $"socks5:\n  port: {SocksPort}\n  address: 127.0.0.1\n  udp: 'udp'\n" +
                    "misc:\n  task-stack-size: 81920\n");
                Hev.Start(this, hevConfig, _tun.Fd);

                IsRunning = true;
                LastError = null;
                ShowForeground();
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
            _statsTimer?.Dispose();
            _statsTimer = null;
            Hev.Stop();
            _xray?.Dispose();
            _xray = null;
            try { _tun?.Close(); } catch { }
            _tun = null;
            bool was = IsRunning;
            IsRunning = false;
            try { StopForeground(StopForegroundFlags.Remove); } catch { }
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

        // ---------- اعلان ثابت: سرور، شهر، سرعت، «قطع اتصال» ----------
        private const int NotificationId = 1;
        private const string ChannelId = "derafsh_vpn";
        private static string? s_server, s_location;
        private Timer? _statsTimer;
        private long _lastDown, _lastUp;
        private DateTime _lastAt;
        private static DerafshVpnService? s_current;

        // صفحه‌ی اصلی بعد از وصل شدن، شهر و کشور سرور رو اینجا می‌ده
        public static void SetLocation(string? location)
        {
            s_location = location;
            s_current?.UpdateNotification();
        }

        private void ShowForeground()
        {
            s_current = this;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var channel = new NotificationChannel(ChannelId, "اتصال VPN", NotificationImportance.Low) { Description = "وضعیت اتصال درفش، سرعت و دکمه‌ی قطع" };
                channel.SetShowBadge(false);
                ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(channel);
            }
            var stats = Hev.Stats();
            (_lastDown, _lastUp, _lastAt) = (stats.down, stats.up, DateTime.UtcNow);
            var n = BuildNotification("↓ 0 KB/s   ↑ 0 KB/s");
            if (OperatingSystem.IsAndroidVersionAtLeast(34)) StartForeground(NotificationId, n, ForegroundService.TypeSpecialUse);
            else StartForeground(NotificationId, n);
            _statsTimer = new Timer(_ => UpdateNotification(), null, 1000, 1000);
        }

        private void UpdateNotification()
        {
            if (!IsRunning) return;
            try
            {
                var (down, up) = Hev.Stats();
                var now = DateTime.UtcNow;
                double secs = Math.Max(0.2, (now - _lastAt).TotalSeconds);
                string speed = $"↓ {Speed((down - _lastDown) / secs)}   ↑ {Speed((up - _lastUp) / secs)}";
                (_lastDown, _lastUp, _lastAt) = (down, up, now);
                NotificationManagerCompat.From(this).Notify(NotificationId, BuildNotification(speed));
            }
            catch { }
        }

        private Notification BuildNotification(string speed)
        {
            var flags = PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent;
            var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop), flags);
            var stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(DerafshVpnService)).SetAction(ActionStop), flags);
            string text = string.IsNullOrEmpty(s_location) ? speed : $"{s_location}   ·   {speed}";
            return new NotificationCompat.Builder(this, ChannelId)
                .SetSmallIcon(Resource.Drawable.ic_notify)!
                .SetContentTitle(string.IsNullOrEmpty(s_server) ? "درفش وصل است" : $"درفش وصل است · {s_server}")!
                .SetContentText(text)!
                .SetOngoing(true)!
                .SetOnlyAlertOnce(true)!
                .SetShowWhen(false)!
                .SetCategory(NotificationCompat.CategoryService)!
                .SetColor(unchecked((int)0xFFF2C14E))!   // طلایی درفش
                .SetContentIntent(open)!
                .AddAction(0, "قطع اتصال", stop)!
                .Build()!;
        }

        private static string Speed(double bytesPerSecond) => bytesPerSecond switch
        {
            >= 1024 * 1024 => $"{bytesPerSecond / (1024 * 1024):0.0} MB/s",
            >= 1024 => $"{bytesPerSecond / 1024:0} KB/s",
            _ => $"{Math.Max(0, bytesPerSecond):0} B/s",
        };
    }

    // صدا زدن hev-socks5-tunnel (کتابخانه‌ی AAR) از C#: کلاس جاوای hev.htproxy.TProxyService با متدهای native
    internal static class Hev
    {
        private static Java.Lang.Reflect.Method? _start, _stop, _stats;

        private static void Load(Context context)
        {
            if (_start != null) return;
            var cls = Java.Lang.Class.ForName("hev.htproxy.TProxyService", true, context.ClassLoader);
            _start = cls.GetMethod("TProxyStartService", Java.Lang.Class.FromType(typeof(Java.Lang.String)), Java.Lang.Integer.Type);
            _stop = cls.GetMethod("TProxyStopService");
            _stats = cls.GetMethod("TProxyGetStats");
        }

        public static void Start(Context context, string configPath, int tunFd)
        {
            Load(context);
            var ok = _start!.Invoke(null, new Java.Lang.String(configPath), Java.Lang.Integer.ValueOf(tunFd)) as Java.Lang.Boolean;
            if (ok != null && !ok.BooleanValue())
                throw new Exception("تونل VPN روشن نشد.");
        }

        public static void Stop()
        {
            try { _stop?.Invoke(null); } catch { }
        }

        // بایت‌های رد شده از تونل: [tx_packets, tx_bytes, rx_packets, rx_bytes]؛ hev وقتی از TUN می‌خونه tx رو می‌شمره
        // (یعنی فرستادن گوشی = آپلود) و وقتی توی TUN می‌نویسه rx رو (دانلود)
        public static (long down, long up) Stats()
        {
            try
            {
                if (_stats?.Invoke(null) is Java.Lang.Object arr)
                {
                    var v = JNIEnv.GetArray<long>(arr.Handle);
                    if (v is { Length: 4 }) return (v[3], v[1]);
                }
            }
            catch { }
            return (0, 0);
        }
    }

    // از صفحه‌ی اصلی صدا زده می‌شه: اجازه‌ی VPN، روشن و خاموش کردن سرویس
    public static class VpnController
    {
        public static string? LastError { get; private set; }

        public static async Task<bool> StartAsync(string configPath, string serverName)
        {
            var activity = Platform.CurrentActivity ?? throw new Exception("پنجره‌ی برنامه هنوز آماده نیست.");

            // اندروید ۱۳ به بعد: اجازه‌ی اعلان، برای اعلان ثابت VPN (سرعت و دکمه‌ی قطع). اگه کاربر نده، VPN باز هم کار می‌کنه
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
                try { await Permissions.RequestAsync<Permissions.PostNotifications>(); } catch { }

            // اولین بار اندروید از کاربر اجازه‌ی VPN می‌گیره
            var consent = VpnService.Prepare(activity);
            if (consent != null)
            {
                MainActivity.VpnPermission = new TaskCompletionSource<bool>();
                activity.StartActivityForResult(consent, MainActivity.VpnRequestCode);
                if (!await MainActivity.VpnPermission.Task)
                {
                    LastError = "اجازه‌ی VPN داده نشد. دوباره «اتصال» را بزنید و «OK» را انتخاب کنید.";
                    return false;
                }
            }

            var done = new TaskCompletionSource<bool>();
            void OnChanged() => done.TrySetResult(DerafshVpnService.IsRunning);
            DerafshVpnService.StateChanged += OnChanged;
            try
            {
                var intent = new Intent(activity, typeof(DerafshVpnService)).SetAction(DerafshVpnService.ActionStart)!
                    .PutExtra("config", configPath)!.PutExtra("name", serverName);
                activity.StartService(intent);
                var finished = await Task.WhenAny(done.Task, Task.Delay(15000));
                bool ok = finished == done.Task && done.Task.Result;
                LastError = ok ? null : DerafshVpnService.LastError ?? "VPN به‌موقع روشن نشد.";
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
