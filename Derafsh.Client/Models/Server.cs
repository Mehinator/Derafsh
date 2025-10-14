using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Derafsh.Client.Models
{
    public class Server : INotifyPropertyChanged 
    {
        public string Country { get; set; }
        public string City { get; set; }
        public string FlagUrl { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }


        private int _ping;
        public int Ping
        {
            get => _ping;
            set
            {
                _ping = value;
                OnPropertyChanged(); 
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}