namespace Derafsh.Client.Models
{
    // یه ردیف صفحه‌ی «مکان سرور»: خودکار، یه کشور، یا یه شهر (زیر کشورش)
    public sealed class LocationRow
    {
        public string Key { get; init; } = "";      // "" (خودکار)، "DE" یا "DE|Berlin"
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";   // «۸۵ سرور · ۱۲ سالم»
        public bool IsCity { get; init; }
        public bool IsSelected { get; init; }
        public string Search { get; init; } = "";   // اسم انگلیسی کشور و کد، برای جستجو
    }
}
