namespace Derafsh.Client.Models
{
    // یه دکمه‌ی کوچک توی صفحه‌ی «سرورها»: یه لیست (آنلاین، شخصی، یه لیست ذخیره‌شده) یا یه مکان (همه، یه کشور، یه شهر، «کشورهای دیگر»)
    public sealed class ChipItem
    {
        public string Key { get; init; } = "";      // لیست: online / personal / saved:اسم؛ مکان: "" (همه)، "DE"، "DE|Berlin"، "*"
        public string Text { get; init; } = "";     // مثلاً «آنلاین» یا «🇩🇪 آلمان · ۱۲»
        public bool IsSelected { get; init; }
    }
}
