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
        public object? Convert(object? v, Type t, object? p, CultureInfo c) => (v is bool b && b) ? Colors.Gold : Colors.Transparent;
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;
    }

    public class BoolToThicknessConverter : IValueConverter
    {
        public object? Convert(object? v, Type t, object? p, CultureInfo c) => (v is bool b && b) ? 2 : 0;
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;
    }

    public class PingToColorConverter : IValueConverter
    {
        public object? Convert(object? v, Type t, object? p, CultureInfo c)
        {
            if (v is int ping)
            {
                if (ping <= 0) return Colors.Gray;
                if (ping < 500) return Colors.LightGreen;
                if (ping < 1000) return Colors.Orange;
            }
            return Colors.Red;
        }
        public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;
    }

    // ==================== کلاس گروه‌بندی ====================
    // اصلاح شده: ObjectModel با O بزرگ و استفاده از using بالا
    public class ServerGroup : ObservableCollection<Derafsh.Client.Models.Server>
    {
        public string Name { get; private set; }
        public ServerGroup(string name, IEnumerable<Derafsh.Client.Models.Server> servers) : base(servers) { Name = name; }
    }
}