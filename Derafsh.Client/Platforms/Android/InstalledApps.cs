using Android.Content;
using Android.Content.PM;
using Android.Graphics;

namespace Derafsh.Client
{
    // برنامه‌های نصب‌شده‌ای که آیکون دارن (همون‌هایی که کاربر می‌شناسه)، برای صفحه‌ی «برنامه‌ها و VPN» و سرویس VPN.
    // اندروید ۱۱ به بعد فقط برنامه‌هایی رو نشون می‌ده که توی <queries> مانیفست گفته شده (اینجا: همه‌ی برنامه‌های آیکون‌دار).
    public static class InstalledApps
    {
        public sealed record App(string Package, string Label);

        public static List<App> Launchable(Context context)
        {
            var pm = context.PackageManager!;
            var intent = new Intent(Intent.ActionMain).AddCategory(Intent.CategoryLauncher);
            var apps = new Dictionary<string, App>();
#pragma warning disable CA1422 // QueryIntentActivities(int) از اندروید ۱۳ کهنه حساب می‌شه ولی کار می‌کنه
            foreach (var info in pm.QueryIntentActivities(intent, 0) ?? new List<ResolveInfo>())
#pragma warning restore CA1422
            {
                string? package = info.ActivityInfo?.PackageName;
                if (package == null || package == context.PackageName || apps.ContainsKey(package)) continue;
                string label = info.ActivityInfo?.ApplicationInfo?.LoadLabel(pm)?.ToString() ?? info.LoadLabel(pm)?.ToString() ?? package;
                apps[package] = new App(package, label);
            }
            return apps.Values.OrderBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // اسم بسته‌ی برنامه‌هایی که الان باید بدون VPN باشن
        public static List<string> ToBypass(Context context) =>
            Launchable(context).Where(a => AppBypass.Bypasses(a.Package, a.Label)).Select(a => a.Package).ToList();

        // آیکون برنامه به‌صورت PNG (برای نمایش توی لیست)
        public static byte[]? IconPng(Context context, string package, int size = 96)
        {
            try
            {
                var drawable = context.PackageManager!.GetApplicationIcon(package);
                using var bitmap = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!)!;
                using var canvas = new Canvas(bitmap);
                drawable.SetBounds(0, 0, size, size);
                drawable.Draw(canvas);
                using var png = new MemoryStream();
                bitmap.Compress(Bitmap.CompressFormat.Png!, 100, png);
                return png.ToArray();
            }
            catch { return null; }
        }
    }
}
