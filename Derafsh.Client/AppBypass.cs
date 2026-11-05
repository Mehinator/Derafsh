using System.Text.Json;

namespace Derafsh.Client
{
    // برنامه‌ها و سایت‌های ایرانی بدون VPN (گزینه‌ی صفحه‌ی اصلی، پیش‌فرض روشن).
    //   • سایت‌ها، روی هر دو سیستم: دامنه‌ی .ir، چند سایت ایرانی پرکاربرد و IPهای ایران با اینترنت خود دستگاه می‌رن
    //     (قانون‌هاش توی ConfigService). بانک‌ها و درگاه پرداخت (شاپرک) فقط IP ایران رو قبول می‌کنن.
    //   • برنامه‌ها، فقط اندروید: برنامه‌ای که «بدون VPN» باشه اصلاً وارد VPN نمی‌شه (برنامه‌های بانکی با VPN باز نمی‌شن).
    //     برنامه‌های ایرانی خودشون پیدا می‌شن؛ انتخاب خود کاربر برای هر برنامه همیشه مقدمه. یا برعکس: فقط برنامه‌هایی که
    //     کاربر روشن کرده با VPN باشن (OnlySelected).
    public static class AppBypass
    {
        public static bool IranDirect
        {
            get => Preferences.Default.Get("iran_direct", true);
            set => Preferences.Default.Set("iran_direct", value);
        }

        // انتخاب کاربر، فقط برای برنامه‌هایی که با پیش‌فرض فرق دارن: اسم بسته → بدون VPN (true) یا با VPN (false)
        private static Dictionary<string, bool>? s_choices;
        private static Dictionary<string, bool> Choices
        {
            get
            {
                if (s_choices != null) return s_choices;
                try { s_choices = JsonSerializer.Deserialize<Dictionary<string, bool>>(Preferences.Default.Get("app_choices", "{}")); } catch { }
                return s_choices ??= new();
            }
        }

        // دو حالت، مثل بیشتر VPNها: همه‌ی برنامه‌ها با VPN جز چندتا (پیش‌فرض)، یا فقط برنامه‌هایی که کاربر روشن کرده
        public static bool OnlySelected
        {
            get => Preferences.Default.Get("apps_only_selected", false);
            set => Preferences.Default.Set("apps_only_selected", value);
        }

        // حالت «فقط این‌ها»: برنامه‌هایی که با VPN باشن
        private static HashSet<string>? s_selected;
        public static HashSet<string> Selected
        {
            get
            {
                if (s_selected != null) return s_selected;
                try { s_selected = JsonSerializer.Deserialize<HashSet<string>>(Preferences.Default.Get("apps_selected", "[]")); } catch { }
                return s_selected ??= new();
            }
        }

        public static bool UsesVpn(string package, string label) => OnlySelected ? Selected.Contains(package) : !Bypasses(package, label);

        public static void SetUsesVpn(string package, string label, bool uses)
        {
            if (!OnlySelected) { Choose(package, label, bypass: !uses); return; }
            if (uses) Selected.Add(package); else Selected.Remove(package);
            Preferences.Default.Set("apps_selected", JsonSerializer.Serialize(Selected));
        }

        public static bool Bypasses(string package, string label) =>
            Choices.TryGetValue(package, out var bypass) ? bypass : IranDirect && IsIranian(package, label);

        public static void Choose(string package, string label, bool bypass)
        {
            if (bypass == (IranDirect && IsIranian(package, label))) Choices.Remove(package);
            else Choices[package] = bypass;
            Preferences.Default.Set("app_choices", JsonSerializer.Serialize(Choices));
        }

        // برنامه‌های ایرانی پرکاربردی که اسم بسته‌شون «ir» نداره
        private static readonly HashSet<string> KnownIranian = new()
        {
            "com.farsitel.bazaar", "cab.snapp.passenger", "cab.snapp.driver", "com.zoodfood.android", "taxi.tap30.passenger", "taxi.tap30.driver",
            "com.digikala", "app.rbmain.a", "mobi.mmdt.ottplus", "com.myirancell", "com.aparat", "com.sibche.aspardproject.app",
        };

        // سازنده‌های گوشی و برنامه‌های خارجی: اسم فارسی‌شون (وقتی زبان گوشی فارسیه) دلیل ایرانی بودن نیست
        private static readonly string[] GlobalPrefixes =
        {
            "android", "com.android.", "com.google.", "com.samsung.", "com.sec.", "com.miui.", "com.xiaomi.", "com.mi.", "com.huawei.", "com.hihonor.",
            "com.oppo.", "com.coloros.", "com.heytap.", "com.oplus.", "com.realme.", "com.oneplus.", "com.vivo.", "com.bbk.", "com.motorola.", "com.lge.",
            "com.sony.", "com.sonymobile.", "com.transsion.", "com.nothing.", "com.asus.", "com.lenovo.", "com.zte.", "com.tcl.", "com.facebook.",
            "com.instagram.", "com.whatsapp", "org.telegram.", "com.microsoft.", "org.mozilla.", "com.opera.", "com.brave.", "com.twitter.", "com.spotify.",
        };

        // ایرانی: بسته‌ای با بخش «ir» (مثل ir.divar)، یکی از برنامه‌های شناخته‌شده، یا برنامه‌ای با اسم فارسی.
        // نسخه‌های غیررسمی تلگرام (بیشترشون ایرانی‌ان) و فیلترشکن‌ها ایرانی حساب نمی‌شن: تلگرام بدون VPN باز نمی‌شه.
        public static bool IsIranian(string package, string label)
        {
            string p = package.ToLowerInvariant();
            if (p.Contains("gram") || p.Contains("telegra") || p.Contains("vpn") || label.Contains("تلگرام") || label.Contains("فیلترشکن")) return false;
            if (KnownIranian.Contains(p)) return true;
            if (GlobalPrefixes.Any(x => p == x || p.StartsWith(x))) return false;
            if (p.Split('.').Contains("ir")) return true;
            return label.Any(c => c is >= '؀' and <= 'ۿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿');
        }
    }
}
