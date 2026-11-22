using Derafsh.Client.Models;

namespace Derafsh.Client
{
    // مکان‌ها داخل صفحه‌ی «سرورها»: بالای لیست آنلاین یه ردیف کشور و، وقتی کشوری انتخاب شده، یه ردیف شهرهاش. قبلاً «مکان سرور»
    // یه صفحه‌ی جدا بود و «سرورها» یه صفحه‌ی دیگه؛ کاربرها دو جای جدا برای «کجا وصل بشم» رو بی‌دلیل می‌دیدن (۲۲ نوامبر ۲۰۲۶).
    // فقط جاهایی که روی همین اینترنت سرور سالم دارن، پرسالم‌ترها اول (بقیه پشت «کشورهای دیگر»). انتخاب یه مکان هم لیست رو فیلتر
    // می‌کنه هم «اتصال» خودکار (و نگهبان) رو به همون‌جا می‌بره؛ سروری که توی لیست زده بشه هم یعنی همون سرور.
    // مکان هر سرور از derafsh_locations.json (Locations).
    public partial class MainPage
    {
        private const string MoreKey = "*";   // دکمه‌ی «کشورهای دیگر» / «فقط مکان‌های سالم»
        private bool _showAllLocations;
        private bool _scanAllWhenDone;        // صفحه وسط یه تست معمولی باز شد: تست همه‌ی مکان‌ها بعد از تموم شدنش
        private string _shownPlaces = "";     // کلیدهای دکمه‌هایی که الان دیده می‌شن؛ تا وسط تست فقط با پیدا شدن جای تازه از نو ساخته بشن


        private void OpenServers(bool online)
        {
            if (online && _currentList != ListOnline)
            {
                _currentList = ListOnline;
                RefreshListPicker();
                LoadConfigs();
            }
            ShowView(ServersView);
            RefreshLocationChips();
            StartLocationScan();
        }

        // تست معمولی بعد از پیدا شدن ۲۰ سرور سالم می‌ایسته؛ با باز شدن لیست آنلاین بقیه‌ی لیست (همه‌ی مکان‌ها) هم تست می‌شه تا ردیف
        // مکان‌ها همه‌ی جاهای سالم رو داشته باشه. فقط تست‌نشده‌ها؛ بار دوم توی همین اجرا دیگه کاری نداره.
        private void StartLocationScan()
        {
            if (_currentList != ListOnline || _connecting) return;
            var cloud = _configService.ParseFile(CloudPath, false).Select(s => s.Config).Distinct();
            if (!cloud.Any(c => !_pingCache.ContainsKey(c))) return;
            if (_discovering || _isUpdating) _scanAllWhenDone = true;
            else _ = DiscoverAsync(fresh: false, allLocations: true);
        }

        // onlyIfNewPlaces: وسط تست فقط وقتی جای تازه‌ای سالم شد (ساختن دوباره، ردیف رو به اولش برمی‌گردونه)
        private void RefreshLocationChips(bool onlyIfNewPlaces = false)
        {
            bool online = _currentList == ListOnline;
            LocationBar.IsVisible = online;
            if (!online) return;
            var (countries, cities) = BuildLocationChips();
            string places = string.Join(",", countries.Concat(cities).Select(r => r.Key));
            if (onlyIfNewPlaces && places == _shownPlaces) return;
            _shownPlaces = places;
            CountryChips.ItemsSource = countries;
            CityChips.ItemsSource = cities;
            CityChips.IsVisible = cities.Count > 0;
        }

        private (List<ChipItem> countries, List<ChipItem> cities) BuildLocationChips()
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
            string selectedCc = selected.Length > 0 ? Locations.Split(selected).cc : "";
            int working = cloud.Count(c => _pingCache.TryGetValue(c, out var p) && p is > 0 and < NoReply);
            // هنوز هیچ سالمی پیدا نشده (تست اول در جریانه): همه دیده بشن، وگرنه ردیف خالی بود
            bool all = _showAllLocations || working == 0;
            static string Name(string text, int ok) => ok > 0 ? $"{text} · {Fa(ok)}" : text;

            var countries = new List<ChipItem>
            {
                new() { Key = "", Text = working > 0 ? $"همه‌ی کشورها · {Fa(working)}" : "همه‌ی کشورها", IsSelected = selected.Length == 0 },
            };
            int hidden = 0;
            foreach (var country in counts.Where(x => !x.Key.Contains('|')).OrderByDescending(x => x.Value.ok).ThenByDescending(x => x.Value.all))
            {
                string cc = country.Key;
                if (!all && country.Value.ok == 0 && cc != selectedCc) { hidden++; continue; }
                countries.Add(new() { Key = cc, Text = Name($"{Locations.Flag(cc)} {Locations.CountryName(cc)}".Trim(), country.Value.ok), IsSelected = cc == selectedCc });
            }
            // مکانی که انتخاب شده ولی توی لیست فعلی سروری نداره هم دیده بشه
            if (selectedCc.Length > 0 && countries.All(r => r.Key != selectedCc))
                countries.Insert(1, new() { Key = selectedCc, Text = $"{Locations.Flag(selectedCc)} {Locations.CountryName(selectedCc)}".Trim(), IsSelected = true });
            if (hidden > 0 || _showAllLocations)
                countries.Add(new() { Key = MoreKey, Text = _showAllLocations ? "فقط مکان‌های سالم" : $"+ {Fa(hidden)} کشور دیگر" });

            var cities = new List<ChipItem>();
            if (selectedCc.Length > 0)
            {
                cities.Add(new() { Key = selectedCc, Text = $"همه‌ی {Locations.CountryName(selectedCc)}", IsSelected = selected == selectedCc });
                foreach (var city in counts.Where(x => x.Key.StartsWith(selectedCc + "|")).OrderByDescending(x => x.Value.ok).ThenByDescending(x => x.Value.all))
                {
                    if (!all && city.Value.ok == 0 && city.Key != selected) continue;
                    cities.Add(new() { Key = city.Key, Text = Name(city.Key[(selectedCc.Length + 1)..], city.Value.ok), IsSelected = city.Key == selected });
                }
                if (selected.Contains('|') && cities.All(r => r.Key != selected))
                    cities.Add(new() { Key = selected, Text = Locations.Split(selected).city, IsSelected = true });
                if (cities.Count == 2 && !selected.Contains('|')) cities.Clear();   // فقط یه شهر: ردیف شهرها لازم نیست
            }
            return (countries, cities);
        }

        private void OnLocationChipClicked(object sender, EventArgs e)
        {
            if (sender is not Button { CommandParameter: ChipItem row }) return;
            if (row.Key == MoreKey)
            {
                _showAllLocations = !_showAllLocations;
                RefreshLocationChips();
                return;
            }
            ChooseLocation(row.Key);
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
            if (key == old && !wasManual) { RefreshLocationChips(); return; }

            LoadConfigs();   // لیست آنلاین فقط همین مکان
            RefreshLocationChips();
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
            _scanAllWhenDone = true;   // بعدش بقیه‌ی مکان‌ها هم، تا ردیف مکان‌ها کامل بمونه
            UpdateHome();
        }
    }
}
