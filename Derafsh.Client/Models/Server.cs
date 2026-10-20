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

        // منبع کانفیگ (مثلاً کانال تلگرام) یا "شخصی"
        public string Source { get; set; } = "";

        // امتیاز شخصی کاربر؛ فقط روی همین دستگاه ذخیره می‌شود
        private bool _isFavorite;
        public bool IsFavorite { get => _isFavorite; set { _isFavorite = value; OnPropertyChanged(); } }

        // نتیجه تست جمینی روی همین کانفیگ
        private string _geminiStatus = "";
        public string GeminiStatus { get => _geminiStatus; set { _geminiStatus = value; OnPropertyChanged(); } }

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

        private int _ping = -1;
        public int Ping { get => _ping; set { _ping = value; OnPropertyChanged(); } }

        private bool _isPinging;
        public bool IsPinging { get => _isPinging; set { _isPinging = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

}