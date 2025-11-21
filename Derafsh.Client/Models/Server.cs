using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Derafsh.Client
{
    public class Server : INotifyPropertyChanged
    {
        public string Country { get; set; } = "";
        public string City { get; set; } = "";
        public string FlagUrl { get; set; } = "";
        public string Config { get; set; } = "";

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
        private void OnPropertyChanged([CallerMemberName] string name = "") =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}