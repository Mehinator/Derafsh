using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Derafsh.Client.Models // فضای نام باید Models باشه
{
    public class Server : INotifyPropertyChanged
    {
        public int Index { get; set; }
        public string City { get; set; } = "";
        public string Config { get; set; } = "";
        public bool IsRemovable { get; set; }
        public string Country { get; set; } = "";

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        private int _ping = -1;
        public int Ping
        {
            get => _ping;
            set { _ping = value; OnPropertyChanged(); }
        }

        private bool _isPinging;
        public bool IsPinging
        {
            get => _isPinging;
            set { _isPinging = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}