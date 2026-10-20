using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Derafsh.Client
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        public const int VpnRequestCode = 4711;

        // نتیجه‌ی پنجره‌ی «اجازه‌ی VPN» اندروید
        public static TaskCompletionSource<bool>? VpnPermission;

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == VpnRequestCode) VpnPermission?.TrySetResult(resultCode == Result.Ok);
        }
    }
}
