using System.Globalization;
using System.IO;
using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic; // برای IEnumerable
using System.Collections.ObjectModel; // برای ObservableCollection

namespace Derafsh.Client
{
    // ==================== لاگ ====================
    public static class DebugLog
    {
        private static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "derafsh_debug.txt");
        public static void Write(string msg)
        {
#if DEBUG
            try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} > {msg}{Environment.NewLine}"); } catch { }
#endif
        }
    }

    // ==================== مبدل‌ها (Converters) ====================
    public class BoolToColorConverter : IValueConverter
    {
        public Color TrueColor { get; set; } = Colors.Gold;
        public Color FalseColor { get; set; } = Colors.Transparent;
        public object? Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? TrueColor : FalseColor;
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;
    }

    public class BoolToTextConverter : IValueConverter
    {
        public string TrueText { get; set; } = "";
        public string FalseText { get; set; } = "";
        public object? Convert(object? v, Type t, object? p, CultureInfo c) => v is true ? TrueText : FalseText;
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;
    }

    // پینگ: -1 = تست نشده، 9999 = جواب نداد
    public class PingToTextConverter : IValueConverter
    {
        public object? Convert(object? v, Type t, object? p, CultureInfo c) =>
            v is int ping ? ping switch { < 0 => "—", >= 9999 => "بی‌پاسخ", _ => $"{ping} ms" } : "—";
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;
    }

    // سرعت دانلود: خالی (اندازه گرفته نشده)، «۱۲۰ KB/s»؛ رنگ: کافی برای ویدیو / متوسط / کند
    public class SpeedToTextConverter : IValueConverter
    {
        public object? Convert(object? v, Type t, object? p, CultureInfo c) =>
            v is int s && s >= 0 ? (s >= 1024 ? $"{s / 1024.0:0.0} MB/s" : $"{s} KB/s") : "";
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;
    }

    public class SpeedToColorConverter : IValueConverter
    {
        public Color Good { get; set; } = Colors.LightGreen;
        public Color Slow { get; set; } = Colors.Orange;
        public Color Bad { get; set; } = Colors.Red;
        public object? Convert(object? v, Type t, object? p, CultureInfo c) =>
            v is int s ? (s >= 150 ? Good : s >= 60 ? Slow : Bad) : Slow;
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;
    }

    public class PingToColorConverter : IValueConverter
    {
        public Color Untested { get; set; } = Colors.Gray;
        public Color Good { get; set; } = Colors.LightGreen;
        public Color Slow { get; set; } = Colors.Orange;
        public Color Bad { get; set; } = Colors.Red;
        public object? Convert(object? v, Type t, object? p, CultureInfo c) =>
            v is int ping ? ping switch { < 0 => Untested, < 500 => Good, < 1000 => Slow, _ => Bad } : Untested;
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;
    }

    // نتیجه تست جمینی: "" (تست نشده)، "testing"، "ok"، "region"، "blocked"، "timeout"، "failed"، "http:کد"
    public class GeminiTextConverter : IValueConverter
    {
        public object? Convert(object? v, Type t, object? p, CultureInfo c) => (v as string) switch
        {
            null or "" => "",
            "testing" => "Gemini: در حال تست…",
            "ok" => "Gemini ✓",
            var s when s.StartsWith("ok:") => $"Gemini ✓ · {s[3..]}",
            "region" => "Gemini: محدودیت منطقه",
            var s when s.StartsWith("region:") => $"Gemini ✗ · {s[7..]}",
            "unknown" => "Gemini: نامشخص",
            "blocked" => "Gemini: مسدود",
            "timeout" => "Gemini: بی‌پاسخ",
            "failed" => "Gemini: باز نشد",
            var s when s.StartsWith("http:") => $"Gemini: خطای {s[5..]}",
            var s => s,
        };
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;
    }

    public class GeminiColorConverter : IValueConverter
    {
        public Color Neutral { get; set; } = Colors.Gray;
        public Color Good { get; set; } = Colors.LightGreen;
        public Color Warn { get; set; } = Colors.Orange;
        public Color Bad { get; set; } = Colors.Red;
        public object? Convert(object? v, Type t, object? p, CultureInfo c) => (v as string) switch
        {
            "ok" => Good,
            var s when s != null && s.StartsWith("ok:") => Good,
            "region" => Warn,
            var s when s != null && s.StartsWith("region:") => Bad,
            "blocked" or "timeout" or "failed" => Bad,
            var s when s != null && s.StartsWith("http:") => Warn,
            _ => Neutral,
        };
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;
    }

    // ==================== کلاس گروه‌بندی ====================
    // اصلاح شده: ObjectModel با O بزرگ و استفاده از using بالا
    public class ServerGroup : ObservableCollection<Derafsh.Client.Models.Server>
    {
        // عنوان گروه وسط تست خودکار عوض می‌شه («… · 7 · TESTING…»)
        private string _name;
        public string Name { get => _name; set { _name = value; OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Name))); } }

        // اسم لیست ذخیره‌شده (تب Saved)؛ برای بقیه‌ی گروه‌ها null. دکمه‌ی «Delete list» فقط برای این‌ها نمایش داده می‌شه
        public string? SavedListName { get; init; }
        public bool IsSavedList => SavedListName != null;

        public ServerGroup(string name, IEnumerable<Derafsh.Client.Models.Server> servers) : base(servers) { _name = name; }
    }
}
