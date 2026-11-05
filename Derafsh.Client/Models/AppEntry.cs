using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Derafsh.Client.Models
{
    // یه ردیف از صفحه‌ی «برنامه‌ها و VPN» (اندروید): کلید روشن = این برنامه از VPN استفاده می‌کنه
    public class AppEntry : INotifyPropertyChanged
    {
        public string Package { get; init; } = "";
        public string Name { get; init; } = "";
        public bool IsIranian { get; init; }

        // کاربر کلید رو زد: انتخابش ذخیره می‌شه (صفحه گوش می‌ده)
        public event Action<AppEntry>? Toggled;

        private bool _usesVpn;
        public bool UsesVpn
        {
            get => _usesVpn;
            set
            {
                if (_usesVpn == value) return;
                _usesVpn = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Note));
                Toggled?.Invoke(this);
            }
        }

        // از صفحه (عوض شدن حالت یا گزینه‌ی «ایرانی‌ها بدون VPN»)، بدون ذخیره به‌عنوان انتخاب کاربر
        public void Show(bool usesVpn)
        {
            if (_usesVpn == usesVpn) return;
            _usesVpn = usesVpn;
            OnPropertyChanged(nameof(UsesVpn));
            OnPropertyChanged(nameof(Note));
        }

        public string Note => (UsesVpn ? "با VPN" : "بدون VPN") + (IsIranian ? "  ·  ایرانی" : "");

        private ImageSource? _icon;
        public ImageSource? Icon { get => _icon; set { _icon = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
