using System.Globalization;
using System.Text.Json;

namespace Derafsh.Client
{
    // مکان سرورها: کشور و شهری که ترافیک هر کانفیگ ازش بیرون می‌آد (همون جایی که سایت‌ها می‌بینن). collector روی گیت‌هاب
    // از داخل خود هر کانفیگ پیداش می‌کنه و توی derafsh_locations.json می‌نویسه («DE|Berlin»)؛ برنامه همراه لیست دانلودش می‌کنه.
    // مکان انتخابی کاربر: "" (خودکار، هر جا)، "DE" (یه کشور) یا "DE|Berlin" (یه شهر). صفحه و نگهبان اتصال (سرویس VPN) هر دو
    // از همین می‌خونن.
    public static class Locations
    {
        public static string FilePath => Path.Combine(FileSystem.AppDataDirectory, "cloud_locations.json");

        private static readonly object s_lock = new();
        private static Dictionary<string, (string cc, string city)>? s_map;
        private static DateTime s_mapTime;

        public static string Selected
        {
            get => Preferences.Default.Get("location", "");
            set => Preferences.Default.Set("location", value ?? "");
        }

        private static Dictionary<string, (string cc, string city)> Map()
        {
            lock (s_lock)
            {
                DateTime time = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
                if (s_map != null && time == s_mapTime) return s_map;
                var map = new Dictionary<string, (string, string)>();
                try
                {
                    if (File.Exists(FilePath))
                        foreach (var (link, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new())
                        {
                            int bar = value.IndexOf('|');
                            string cc = (bar < 0 ? value : value[..bar]).Trim().ToUpperInvariant();
                            if (cc.Length == 2) map[Key(link)] = (cc, bar < 0 ? "" : value[(bar + 1)..].Trim());
                        }
                }
                catch { }
                (s_map, s_mapTime) = (map, time);
                return map;
            }
        }

        // لینک بدون اسم؛ اسم کانفیگ (بعد از #) فرقی نمی‌سازه
        private static string Key(string link) => link.Split('#')[0].Trim();

        public static (string cc, string city)? Of(string link) => Map().TryGetValue(Key(link), out var l) ? l : null;

        public static bool Matches(string link) => Matches(link, Selected);

        public static bool Matches(string link, string selected)
        {
            if (string.IsNullOrEmpty(selected)) return true;
            if (Of(link) is not { } l) return false;
            var (cc, city) = Split(selected);
            return l.cc == cc && (city.Length == 0 || string.Equals(l.city, city, StringComparison.OrdinalIgnoreCase));
        }

        public static (string cc, string city) Split(string selected)
        {
            int bar = selected.IndexOf('|');
            return bar < 0 ? (selected, "") : (selected[..bar], selected[(bar + 1)..]);
        }

        // «آلمان، Berlin» یا «آلمان»؛ "" برای خودکار
        public static string Describe(string selected)
        {
            if (string.IsNullOrEmpty(selected)) return "";
            var (cc, city) = Split(selected);
            return city.Length == 0 ? CountryName(cc) : $"{CountryName(cc)}، {city}";
        }

        // پرچم فقط روی اندروید؛ ویندوز پرچم ایموجی رو نشون نمی‌ده (دو حرف توی مربع می‌شد)
        public static string Flag(string cc)
        {
#if ANDROID
            return cc.Length == 2 && char.IsAsciiLetterUpper(cc[0]) && char.IsAsciiLetterUpper(cc[1])
                ? char.ConvertFromUtf32(0x1F1E6 + cc[0] - 'A') + char.ConvertFromUtf32(0x1F1E6 + cc[1] - 'A') : "";
#else
            return "";
#endif
        }

        public static string CountryName(string cc)
        {
            if (FaNames.TryGetValue(cc, out var fa)) return fa;
            try { return new RegionInfo(cc).EnglishName; } catch { return cc; }
        }

        private static readonly Dictionary<string, string> FaNames = new()
        {
            ["DE"] = "آلمان", ["NL"] = "هلند", ["FR"] = "فرانسه", ["GB"] = "انگلیس", ["FI"] = "فنلاند", ["SE"] = "سوئد",
            ["NO"] = "نروژ", ["DK"] = "دانمارک", ["PL"] = "لهستان", ["AT"] = "اتریش", ["CH"] = "سوئیس", ["IT"] = "ایتالیا",
            ["ES"] = "اسپانیا", ["PT"] = "پرتغال", ["BE"] = "بلژیک", ["LU"] = "لوکزامبورگ", ["IE"] = "ایرلند", ["CZ"] = "چک",
            ["SK"] = "اسلواکی", ["HU"] = "مجارستان", ["RO"] = "رومانی", ["BG"] = "بلغارستان", ["GR"] = "یونان", ["CY"] = "قبرس",
            ["LT"] = "لیتوانی", ["LV"] = "لتونی", ["EE"] = "استونی", ["UA"] = "اوکراین", ["MD"] = "مولداوی", ["RS"] = "صربستان",
            ["HR"] = "کرواسی", ["SI"] = "اسلوونی", ["AL"] = "آلبانی", ["IS"] = "ایسلند", ["RU"] = "روسیه", ["TR"] = "ترکیه",
            ["AM"] = "ارمنستان", ["GE"] = "گرجستان", ["AZ"] = "آذربایجان", ["KZ"] = "قزاقستان", ["AE"] = "امارات",
            ["SA"] = "عربستان", ["QA"] = "قطر", ["BH"] = "بحرین", ["OM"] = "عمان", ["KW"] = "کویت", ["IQ"] = "عراق",
            ["IL"] = "اسرائیل", ["IR"] = "ایران", ["IN"] = "هند", ["PK"] = "پاکستان", ["US"] = "آمریکا", ["CA"] = "کانادا",
            ["MX"] = "مکزیک", ["BR"] = "برزیل", ["AR"] = "آرژانتین", ["CL"] = "شیلی", ["JP"] = "ژاپن", ["KR"] = "کره‌ی جنوبی",
            ["CN"] = "چین", ["HK"] = "هنگ‌کنگ", ["TW"] = "تایوان", ["SG"] = "سنگاپور", ["MY"] = "مالزی", ["TH"] = "تایلند",
            ["VN"] = "ویتنام", ["ID"] = "اندونزی", ["PH"] = "فیلیپین", ["AU"] = "استرالیا", ["NZ"] = "نیوزیلند",
            ["ZA"] = "آفریقای جنوبی", ["EG"] = "مصر",
        };
    }
}
