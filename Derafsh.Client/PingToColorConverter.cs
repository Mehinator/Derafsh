using System.Globalization;

namespace Derafsh.Client
{
    public class PingToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int ping)
            {
                if (ping < 100) return Colors.Green;
                if (ping < 300) return Colors.Orange;
                return Colors.Red;
            }
            return Colors.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}