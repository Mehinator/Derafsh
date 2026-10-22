using System.Text.Json;
using Derafsh.Client.Models;

namespace Derafsh.Client
{
    // تست خودکار لیست آنلاین و لیست‌های ذخیره‌شده برای هر اینترنت (ایرانسل، شاتل، وای‌فای خونه، ...)
    public partial class MainPage
    {
        // پیدا کردن کانفیگ‌هایی که روی همین اینترنت کار می‌کنن: از جدیدترین‌ها (ستاره‌دارها اول) تست می‌شه تا
        // TargetWorking تا کانفیگ سالم پیدا بشه یا MaxTested تا تست بشه. فقط کانفیگ‌های سالم توی لیست آنلاین میان.
        private const int TargetWorking = 20;
#if ANDROID
        private const int MaxTested = 120, DiscoverParallel = 5;
#else
        private const int MaxTested = 200, DiscoverParallel = 8;
#endif
        private bool _discovering, _discoveryDone;   // _discoveryDone: یه بار تست تا آخر رفته (قبلش «هیچی کار نمی‌کنه» نشون داده نمی‌شه)
        private int _discoverGen;
        private ServerGroup? _onlineGroup;
        private Dictionary<string, string> _cloudSources = new();

        private string SavedListsPath => Path.Combine(FileSystem.AppDataDirectory, "saved_lists.json");

        // fresh: نتیجه‌های قبلی کانفیگ‌های آنلاین پاک می‌شن و همه از نو تست می‌شن («Test all»)
        private async Task DiscoverAsync(bool fresh)
        {
            int gen = ++_discoverGen;   // اجرای قبلی (اگه هنوز در جریانه) دیگه ردیف اضافه نمی‌کنه
            var cloud = _configService.ParseFile(CloudPath, false).Select(s => s.Config).Distinct().ToList();
            if (cloud.Count == 0) return;
            if (fresh)
            {
                foreach (var c in cloud) _pingCache.Remove(c);
                LoadConfigs();
            }

            var favs = ReadFavorites();
            var queue = cloud.OrderByDescending(favs.Contains).Where(c => !_pingCache.ContainsKey(c)).ToList();   // لیست از جدید به قدیمه
            int found = cloud.Count(c => _pingCache.TryGetValue(c, out var p) && p is > 0 and < NoReply);
            int planned = Math.Min(queue.Count, MaxTested), done = 0, started = 0;

            _discovering = true;
            UpdateOnlineHeader();
            StatusLabel.Text = $"Finding configs that work on this network · {found} found";
            try
            {
                using var sem = new SemaphoreSlim(DiscoverParallel);
                var running = new List<Task>();
                foreach (var config in queue)
                {
                    if (gen != _discoverGen || found >= TargetWorking || started >= MaxTested) break;
                    await sem.WaitAsync();
                    if (gen != _discoverGen || found >= TargetWorking) { sem.Release(); break; }
                    started++;
                    running.Add(TestOne(config));
                }
                await Task.WhenAll(running);

                async Task TestOne(string config)
                {
                    try { await PingSingle(config); }
                    finally { sem.Release(); }
                    done++;
                    if (gen != _discoverGen) return;
                    if (_pingCache.TryGetValue(config, out var ping) && ping is > 0 and < NoReply)
                    {
                        found++;
                        AddOnlineRow(config);   // پیدا شده‌ها پایین لیست اضافه می‌شن؛ مرتب‌سازی فقط یه بار، آخر کار
                    }
                    StatusLabel.Text = $"Finding configs that work on this network · {found} found · {done} of {planned} tested";
                }
            }
            finally
            {
                if (gen == _discoverGen)
                {
                    _discovering = false;
                    _discoveryDone = true;
                    ResortList();
                    UpdateOnlineHeader();
                    UpdateEmptyState();
                    if (!_isTestingAll) ShowTestSummary();
                }
            }
        }

        private void AddOnlineRow(string config)
        {
            if (_onlineGroup == null || !ServerGroups.Contains(_onlineGroup) || _onlineGroup.Any(x => x.Config == config)) return;
            var s = _configService.CreateServerObj(config, false);
            if (s == null) return;
            Decorate(new List<Server> { s }, _cloudSources, ReadFavorites());
            s.Index = AllServers.Count + 1;
            _onlineGroup.Add(s);
            UpdateOnlineHeader();
            UpdateEmptyState();
        }

        private void UpdateOnlineHeader()
        {
            if (_onlineGroup == null) return;
            int n = _onlineGroup.Count(x => x.Ping is > 0 and < NoReply);
            _onlineGroup.Name = _discovering ? $"ONLINE  ·  WORKING ON THIS NETWORK  ·  {n}  ·  TESTING…" : $"ONLINE  ·  WORKING ON THIS NETWORK  ·  {n}";
        }

        // نتیجه‌ی آخر تست، و انتخاب سریع‌ترین کانفیگ (اگه وصل نیستیم و کانفیگ انتخاب‌شده جواب نمی‌ده)
        private void ShowTestSummary()
        {
            var working = AllServers.Where(s => s.Ping is > 0 and < NoReply).OrderBy(s => s.Ping).ToList();
            if (!working.Any())
            {
                StatusLabel.Text = CurrentTab is "Personal" or "Saved"
                    ? "No config in this list answered on this network."
                    : "No tested config works on this network right now. Press Refresh list later for new ones.";
                return;
            }
            var best = working.First();
            if (!isConnected && (_currentServer == null || _currentServer.Ping is <= 0 or >= NoReply)) SelectServer(best);
            ServerListView.ScrollTo(best, ScrollToPosition.MakeVisible, true);
            StatusLabel.Text = $"{working.Count} config{Plural(working.Count)} work on this network · fastest: {best.City} ({best.Ping} ms) · Save list keeps them";
        }

        // --- لیست‌های ذخیره‌شده ---
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
                await DisplayAlertAsync("Nothing to save yet", "Only configs that answered a test are saved. Wait for the test to finish, or press Test all.", "OK");
                return;
            }

            // هر اسمی که کاربر بخواد؛ هر اسم یه لیست جدا (مثلاً یکی برای ایرانسل، یکی برای وای‌فای خونه)
            var existing = ReadSavedLists().Select(l => l.Name).ToList();
            string? name = await DisplayPromptAsync("Save working configs",
                $"{working.Count} config{Plural(working.Count)} work on this network. Give this list a name:" +
                (existing.Any() ? $"\nYour lists: {string.Join(", ", existing)}" : ""),
                "Save", "Cancel", placeholder: "For example: Irancell, Home Wi-Fi", maxLength: 40);
            name = name?.Trim();
            if (string.IsNullOrEmpty(name)) return;

            var lists = ReadSavedLists();
            var old = lists.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (old != null && !await DisplayAlertAsync($"Replace “{old.Name}”?", $"A list with this name already has {old.Configs.Count} config{Plural(old.Configs.Count)}. It will be replaced by these {working.Count}.", "Replace", "Cancel"))
                return;
            lists.Remove(old!);
            lists.Add(new SavedList { Name = name, Saved = DateTime.Now, Configs = working });
            try
            {
                await WriteSavedLists(lists);
                StatusLabel.Text = $"Saved {working.Count} config{Plural(working.Count)} as “{name}” · open the Saved tab to use them";
            }
            catch { StatusLabel.Text = "Couldn't save the list."; }
        }

        private async void OnDeleteSavedListClicked(object sender, EventArgs e)
        {
            if (sender is not Button { CommandParameter: ServerGroup { SavedListName: string name } }) return;
            if (!await DisplayAlertAsync($"Delete the list “{name}”?", "Only the saved list is deleted; the configs stay in the online list.", "Delete", "Cancel")) return;
            var lists = ReadSavedLists();
            lists.RemoveAll(l => l.Name == name);
            try { await WriteSavedLists(lists); LoadConfigs(); StatusLabel.Text = $"Deleted the list “{name}”"; }
            catch { StatusLabel.Text = "Couldn't delete the list."; }
        }

        private void AddSavedGroups(Dictionary<string, string> sources, HashSet<string> favs, ref int idx)
        {
            foreach (var list in ReadSavedLists().OrderByDescending(l => l.Saved))
            {
                var rows = list.Configs.Select(c => _configService.CreateServerObj(c, false)).OfType<Server>().ToList();
                Decorate(rows, sources, favs);
                rows = Ordered(rows);
                foreach (var s in rows) s.Index = idx++;
                ServerGroups.Add(new ServerGroup($"{list.Name}  ·  {rows.Count}  ·  SAVED {list.Saved:d MMM}".ToUpperInvariant(), rows) { SavedListName = list.Name });
            }
        }
    }
}
