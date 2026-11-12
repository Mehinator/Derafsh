using System.Globalization;
using Derafsh.Client.Models;

namespace Derafsh.Client
{
    // صفحه‌ی «مکان سرور»: کاربر یه کشور (مثلاً آلمان) یا یه شهر (برلین) انتخاب می‌کنه؛ «اتصال» خودکار فقط سرورهای همون‌جا رو
    // تست و انتخاب می‌کنه، و نگهبان اتصال هم فقط به سرورهای همون‌جا می‌ره. مکان هر سرور از derafsh_locations.json (Locations).
    public partial class MainPage
    {
        private List<LocationRow> _locationRows = new();

        private void OnOpenLocationsClicked(object sender, EventArgs e) => OpenLocations();
        private void OnOpenLocationsTapped(object sender, TappedEventArgs e) => OpenLocations();
        private void OnLocationsBackClicked(object sender, EventArgs e) => ShowView(HomeView);

        private void OpenLocations()
        {
            BuildLocationRows();
            LocationSearch.Text = "";
            ShowLocationRows("");
            ShowView(LocationsView);
        }

        // کشورها (پرسرورترین اول) و زیر هر کشور شهرهاش؛ «سالم»: روی همین اینترنت جواب داده
        private void BuildLocationRows()
        {
            var cloud = _configService.ParseFile(CloudPath, false).Select(s => s.Config).Distinct().ToList();
            var counts = new Dictionary<string, (int all, int ok)>();
            foreach (var c in cloud)
            {
                if (Locations.Of(c) is not { } l) continue;
                bool ok = _pingCache.TryGetValue(c, out var p) && p is > 0 and < NoReply;
                Count(l.cc);
                if (l.city.Length > 0) Count($"{l.cc}|{l.city}");
                void Count(string key) { var n = counts.GetValueOrDefault(key); counts[key] = (n.all + 1, n.ok + (ok ? 1 : 0)); }
            }

            string selected = Locations.Selected;
            int working = cloud.Count(c => _pingCache.TryGetValue(c, out var p) && p is > 0 and < NoReply);
            var rows = new List<LocationRow>
            {
                new() { Key = "", Title = "خودکار، هر کشوری", Detail = Detail(cloud.Count, working), IsSelected = selected.Length == 0 },
            };
            foreach (var country in counts.Where(x => !x.Key.Contains('|')).OrderByDescending(x => x.Value.all))
            {
                string cc = country.Key, english = EnglishName(cc);
                rows.Add(new() { Key = cc, Title = $"{Locations.Flag(cc)} {Locations.CountryName(cc)}".Trim(), Detail = Detail(country.Value.all, country.Value.ok),
                                 IsSelected = selected == cc, Search = $"{english} {cc}" });
                foreach (var city in counts.Where(x => x.Key.StartsWith(cc + "|")).OrderByDescending(x => x.Value.all))
                    rows.Add(new() { Key = city.Key, Title = city.Key[(cc.Length + 1)..], Detail = Detail(city.Value.all, city.Value.ok), IsCity = true,
                                     IsSelected = selected == city.Key, Search = $"{Locations.CountryName(cc)} {english}" });
            }
            // مکانی که انتخاب شده ولی توی لیست تازه سروری نداره هم دیده بشه
            if (selected.Length > 0 && rows.All(r => r.Key != selected))
                rows.Insert(1, new() { Key = selected, Title = Locations.Describe(selected), Detail = "فعلاً سروری در لیست نیست", IsSelected = true });
            _locationRows = rows;

            static string Detail(int all, int ok) => ok > 0 ? $"{Fa(all)} سرور · {Fa(ok)} سالم" : $"{Fa(all)} سرور";
        }

        private static string EnglishName(string cc)
        {
            try { return new RegionInfo(cc).EnglishName; } catch { return cc; }
        }

        private void OnLocationSearchChanged(object sender, TextChangedEventArgs e) => ShowLocationRows(e.NewTextValue);

        // کشوری که پیدا شد با همه‌ی شهرهاش؛ شهری که پیدا شد با کشورش
        private void ShowLocationRows(string? filter)
        {
            filter = filter?.Trim() ?? "";
            if (filter.Length == 0) { LocationsList.ItemsSource = _locationRows; return; }
            bool Has(string text) => text.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
            var keep = new HashSet<string>();
            foreach (var r in _locationRows.Where(r => r.Key.Length > 0))
            {
                string cc = Locations.Split(r.Key).cc;
                if (!r.IsCity && (Has(r.Title) || Has(r.Search))) { keep.Add(cc); foreach (var x in _locationRows.Where(x => x.Key.StartsWith(cc + "|"))) keep.Add(x.Key); }
                if (r.IsCity && Has(r.Title)) { keep.Add(r.Key); keep.Add(cc); }
            }
            LocationsList.ItemsSource = _locationRows.Where(r => keep.Contains(r.Key)).ToList();
        }

        private void OnLocationClicked(object sender, EventArgs e)
        {
            if (sender is Button { CommandParameter: LocationRow row }) ChooseLocation(row.Key);
        }

        private void ChooseLocation(string key)
        {
            string old = Locations.Selected;
            Locations.Selected = key;
            // مکان برای انتخاب خودکاره؛ سروری که کاربر دستی زده بود کنار می‌ره
            bool wasManual = !_autoServer;
            _autoServer = true;
            foreach (var x in AllServers) x.IsSelected = false;
            if (!isConnected) _currentServer = null;
            ShowView(HomeView);
            if (key == old && !wasManual) return;

            LoadConfigs();   // لیست آنلاین فقط همین مکان
            _discoveryDone = false;
            string where = key.Length == 0 ? "هر کشوری" : Locations.Describe(key);
            if (isConnected)
            {
                string? link = CurrentLink();
                if (link != null && Locations.Matches(link, key))
                {
                    UpdatePool();
                    StatusLabel.Text = $"همین الان به سروری در {where} وصل هستید.";
                    return;
                }
                StatusLabel.Text = $"در حال رفتن به سروری در {where}…";
                Disconnect();
                Task.Delay(500).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() => OnConnectButtonClicked(this, EventArgs.Empty)));
                return;
            }
            StatusLabel.Text = key.Length == 0 ? "سریع‌ترین سرور از هر کشوری انتخاب می‌شود." : $"سرورهای {where} روی همین اینترنت تست می‌شوند…";
            if (!_isUpdating) _ = DiscoverAsync(fresh: false);
            UpdateHome();   // «سرورهای آلمان روی همین اینترنت تست می‌شوند…»
        }
    }
}
