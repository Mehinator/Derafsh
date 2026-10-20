using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Derafsh.Client.Models
{
    public class Server : INotifyPropertyChanged
    {
        public int Index { get; set; }
        public string City { get; set; } = "";
        public string Config { get; set; } = "";
        public string Country { get; set; } = "";

        // منبع کانفیگ (اسم کانال تلگرام) یا "Personal"
        private string _source = "";
        public string Source { get => _source; set { _source = value; OnPropertyChanged(); OnPropertyChanged(nameof(Meta)); } }

        // خط دوم هر ردیف: پروتکل · کشور · منبع
        public string Meta => string.Join("  ·  ", new[] { Protocol, CountryCode, Source }.Where(x => !string.IsNullOrEmpty(x)));

        public string Protocol => Config.Split("://")[0] switch
        {
            "vless" => "VLESS",
            "vmess" => "VMess",
            "ss" => "Shadowsocks",
            "trojan" => "Trojan",
            "hysteria2" => "Hysteria2",
            var p => p.ToUpperInvariant(),
        };

        // ویندوز پرچم ایموجی (مثلاً 🇩🇪) رو نشون نمی‌ده؛ کد دوحرفی (DE) جاش
        public string CountryCode
        {
            get
            {
                var letters = Country.EnumerateRunes().Where(r => r.Value >= 0x1F1E6 && r.Value <= 0x1F1FF).Take(2).ToList();
                return letters.Count == 2 ? new string(letters.Select(r => (char)('A' + r.Value - 0x1F1E6)).ToArray()) : "";
            }
        }

        // این فیلد تعیین می‌کنه دکمه حذف یا ذخیره بیاد
        private bool _isRemovable;
        public bool IsRemovable
        {
            get => _isRemovable;
            set { _isRemovable = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowSave)); }
        }

        public bool ShowSave => !IsRemovable;

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

        // امتیاز شخصی کاربر؛ فقط روی همین دستگاه ذخیره می‌شود
        private bool _isFavorite;
        public bool IsFavorite { get => _isFavorite; set { _isFavorite = value; OnPropertyChanged(); } }

        // نتیجه تست جمینی روی همین کانفیگ (کدها در GeminiTextConverter)
        private string _geminiStatus = "";
        public string GeminiStatus { get => _geminiStatus; set { _geminiStatus = value; OnPropertyChanged(); } }

        private int _ping = -1;
        public int Ping { get => _ping; set { _ping = value; OnPropertyChanged(); } }

        private bool _isPinging;
        public bool IsPinging { get => _isPinging; set { _isPinging = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

}
