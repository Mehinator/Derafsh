using System.Globalization;
using System.Text.Json;
using Derafsh.Client.Models;

namespace Derafsh.Client
{
    // لیست‌های ذخیره‌شده: کاربر سرورهایی رو که الان جواب می‌دن با اسم دلخواه ذخیره می‌کنه (مثلاً «ایرانسل»، «وای‌فای خونه»)
    // و توی صفحه‌ی سرورها از منوی «لیست» انتخابشون می‌کنه
    public partial class MainPage
    {
        private string SavedListsPath => Path.Combine(FileSystem.AppDataDirectory, "saved_lists.json");

        public sealed class SavedList
        {
            public string Name { get; set; } = "";
            public DateTime Saved { get; set; }
            public List<string> Configs { get; set; } = new();
        }

        private List<SavedList> ReadSavedLists()
        {
            try { if (File.Exists(SavedListsPath)) return JsonSerializer.Deserialize<List<SavedList>>(File.ReadAllText(SavedListsPath)) ?? new(); } catch { }
            return new();
        }

        private async Task WriteSavedLists(List<SavedList> lists) =>
            await File.WriteAllTextAsync(SavedListsPath, JsonSerializer.Serialize(lists));

        private async void OnSaveListClicked(object sender, EventArgs e)
        {
            var working = AllServers.Where(s => s.Ping is > 0 and < NoReply).OrderBy(s => s.Ping).Select(s => s.Config).Distinct().ToList();
            if (!working.Any())
            {
                await DisplayAlertAsync("هنوز چیزی برای ذخیره نیست", "فقط سرورهایی ذخیره می‌شوند که در تست جواب داده‌اند. صبر کنید تست تمام شود، یا «تست دوباره» را بزنید.", "باشه");
                return;
            }

            // هر اسمی که کاربر بخواد؛ هر اسم یه لیست جدا
            var existing = ReadSavedLists().Select(l => l.Name).ToList();
            string? name = await DisplayPromptAsync("ذخیره به‌عنوان لیست",
                $"{Fa(working.Count)} سرور روی همین اینترنت جواب می‌دهند. برای این لیست یک اسم بگذارید:" +
                (existing.Any() ? $"\nلیست‌های شما: {string.Join("، ", existing)}" : ""),
                "ذخیره", "لغو", placeholder: "مثلاً: ایرانسل، وای‌فای خانه", maxLength: 40);
            name = name?.Trim();
            if (string.IsNullOrEmpty(name)) return;

            var lists = ReadSavedLists();
            var old = lists.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (old != null && !await DisplayAlertAsync($"لیست {Q(old.Name)} جایگزین شود؟",
                    $"این لیست الان {Fa(old.Configs.Count)} سرور دارد و با این {Fa(working.Count)} سرور جایگزین می‌شود.", "جایگزین کن", "لغو"))
                return;
            if (old != null) lists.Remove(old);
            lists.Add(new SavedList { Name = name, Saved = DateTime.Now, Configs = working });
            try
            {
                await WriteSavedLists(lists);
                RefreshListPicker();
                StatusLabel.Text = $"{Fa(working.Count)} سرور با اسم {Q(name)} ذخیره شد · از منوی «لیست» پیدایش کنید";
            }
            catch { StatusLabel.Text = "لیست ذخیره نشد."; }
        }

        private async void OnDeleteSavedListClicked(object sender, EventArgs e)
        {
            if (!_currentList.StartsWith(SavedPrefix)) return;
            string name = _currentList[SavedPrefix.Length..];
            if (!await DisplayAlertAsync($"لیست {Q(name)} حذف شود؟", "فقط همین لیست ذخیره‌شده پاک می‌شود؛ سرورها در لیست آنلاین می‌مانند.", "حذف لیست", "لغو")) return;
            var lists = ReadSavedLists();
            lists.RemoveAll(l => l.Name == name);
            try
            {
                await WriteSavedLists(lists);
                _currentList = ListOnline;
                RefreshListPicker();
                LoadConfigs();
                StatusLabel.Text = $"لیست {Q(name)} حذف شد";
            }
            catch { StatusLabel.Text = "لیست حذف نشد."; }
        }

        private void AddSavedGroup(string name, HashSet<string> favs)
        {
            var list = ReadSavedLists().FirstOrDefault(l => l.Name == name);
            if (list == null) return;
            var rows = list.Configs.Select(c => _configService.CreateServerObj(c, false)).OfType<Server>().ToList();
            Decorate(rows, _cloudSources, favs);
            ServerGroups.Add(new ServerGroup($"{Q(list.Name)}  ·  {Fa(rows.Count)} سرور  ·  ذخیره‌شده {PersianDate(list.Saved)}", Ordered(rows)) { SavedListName = list.Name });
        }

        // تاریخ شمسی کوتاه، مثلاً «۱ آبان»
        private static readonly string[] PersianMonths = { "فروردین", "اردیبهشت", "خرداد", "تیر", "مرداد", "شهریور", "مهر", "آبان", "آذر", "دی", "بهمن", "اسفند" };
        private static string PersianDate(DateTime d)
        {
            var pc = new PersianCalendar();
            return $"{Fa(pc.GetDayOfMonth(d))} {PersianMonths[pc.GetMonth(d) - 1]}";
        }
    }
}
