using System.Collections.ObjectModel;
using Derafsh.Client.Models;

namespace Derafsh.Client
{
    // گزینه‌ی «برنامه‌ها و سایت‌های ایرانی بدون VPN» و صفحه‌ی «برنامه‌ها و VPN» (اندروید): کدوم برنامه‌ها از VPN استفاده کنن،
    // به دو حالت (همه جز چندتا / فقط چندتا). منطقش توی AppBypass.
    public partial class MainPage
    {
        private readonly ObservableCollection<AppEntry> _appRows = new();
        private List<AppEntry>? _allApps;
        private bool _appsChanged, _optionsReady;

        private void InitIranOption()
        {
            IranDirectCheck.IsChecked = AppBypass.IranDirect;
#if ANDROID
            ChooseAppsButton.IsVisible = true;
#else
            IranDirectLabel.Text = "سایت‌های ایرانی بدون VPN (بانک‌ها، درگاه پرداخت، سایت‌های ‎.ir‎)";
            SemanticProperties.SetDescription(IranDirectCheck, "سایت‌های ایرانی بدون VPN");
#endif
            FragmentCheck.IsChecked = SessionConfig.FragmentTls;
            AppsList.ItemsSource = _appRows;
            (AppBypass.OnlySelected ? OnlyAppsMode : AllAppsMode).IsChecked = true;
            UpdateAppsHint();
            _optionsReady = true;
        }

        private void OnIranDirectLabelTapped(object sender, TappedEventArgs e) => IranDirectCheck.IsChecked = !IranDirectCheck.IsChecked;
        private void OnFragmentLabelTapped(object sender, TappedEventArgs e) => FragmentCheck.IsChecked = !FragmentCheck.IsChecked;

        // تکه‌تکه کردن TLS: تست‌های بعدی و اتصال با تنظیم تازه؛ اگه وصلیم، همین الان
        private void OnFragmentChanged(object sender, CheckedChangedEventArgs e)
        {
            if (!_optionsReady) return;
            SessionConfig.FragmentTls = e.Value;
            _ = ApplyRoutingChangeAsync();
        }

        private void OnIranDirectChanged(object sender, CheckedChangedEventArgs e)
        {
            if (!_optionsReady) return;
            AppBypass.IranDirect = e.Value;
            // برنامه‌های ایرانی که کاربر خودش انتخابی براشون نکرده با گزینه عوض می‌شن
            foreach (var app in _allApps ?? new()) app.Show(AppBypass.UsesVpn(app.Package, app.Name));
            _ = ApplyRoutingChangeAsync();
        }

        private void OnAppsModeChanged(object sender, CheckedChangedEventArgs e)
        {
            if (!_optionsReady || !e.Value) return;
            AppBypass.OnlySelected = sender == OnlyAppsMode;
            foreach (var app in _allApps ?? new()) app.Show(AppBypass.UsesVpn(app.Package, app.Name));
            SortApps();
            UpdateAppsHint();
            _appsChanged = true;
        }

        private void UpdateAppsHint() => AppsHint.Text = !AppBypass.OnlySelected
            ? "برنامه‌ای که خاموش کنید بدون VPN و با اینترنت خود گوشی کار می‌کند؛ مثلاً بانک‌ها. برنامه‌های ایرانی خودکار خاموش شده‌اند."
            : AppBypass.Selected.Count == 0
                ? "هنوز برنامه‌ای روشن نیست؛ تا یکی را روشن نکنید، همه‌ی برنامه‌ها با VPN می‌مانند."
                : $"فقط {Fa(AppBypass.Selected.Count)} برنامه‌ی روشن از VPN استفاده می‌کنند؛ بقیه با اینترنت خود گوشی.";

        // ---------- صفحه‌ی «برنامه‌ها و VPN» ----------
        private void OnOpenAppsClicked(object sender, EventArgs e)
        {
            ShowView(AppsView);
            if (_allApps == null) _ = LoadAppsAsync();
        }

        private void OnAppsBackClicked(object sender, EventArgs e) => CloseApps();

        private void CloseApps()
        {
            ShowView(SettingsView);
            if (_appsChanged) { _appsChanged = false; _ = ApplyRoutingChangeAsync(); }
        }

        private async Task LoadAppsAsync()
        {
#if ANDROID
            var context = Platform.CurrentActivity ?? Android.App.Application.Context;
            var apps = await Task.Run(() => InstalledApps.Launchable(context));
            _allApps = apps.Select(a => new AppEntry { Package = a.Package, Name = a.Label, IsIranian = AppBypass.IsIranian(a.Package, a.Label) }).ToList();
            foreach (var app in _allApps)
            {
                app.Show(AppBypass.UsesVpn(app.Package, app.Name));
                app.Toggled += a => { AppBypass.SetUsesVpn(a.Package, a.Name, a.UsesVpn); _appsChanged = true; UpdateAppsHint(); };
            }
            SortApps();
            ShowApps(AppSearch.Text);
            if (_allApps.Count == 0) AppsEmptyLabel.Text = "برنامه‌ای پیدا نشد.";
            // آیکون‌ها بعد از لیست، توی پس‌زمینه
            foreach (var app in _allApps.ToList())
            {
                var png = await Task.Run(() => InstalledApps.IconPng(context, app.Package));
                if (png != null) app.Icon = ImageSource.FromStream(() => new MemoryStream(png));
            }
#else
            await Task.CompletedTask;
#endif
        }

        // اون‌هایی که با حالت عادی فرق دارن بالای لیست: «همه جز…»: خاموش‌ها؛ «فقط…»: روشن‌ها. بعد ایرانی‌ها، بعد الفبا.
        private void SortApps()
        {
            if (_allApps == null) return;
            bool only = AppBypass.OnlySelected;
            _allApps = _allApps.OrderByDescending(x => only ? x.UsesVpn : !x.UsesVpn).ThenByDescending(x => x.IsIranian).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            ShowApps(AppSearch.Text);
        }

        private void OnAppSearchChanged(object sender, TextChangedEventArgs e) => ShowApps(e.NewTextValue);

        private void ShowApps(string? filter)
        {
            if (_allApps == null) return;
            _appRows.Clear();
            foreach (var app in _allApps.Where(a => string.IsNullOrWhiteSpace(filter)
                         || a.Name.Contains(filter.Trim(), StringComparison.CurrentCultureIgnoreCase) || a.Package.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)))
                _appRows.Add(app);
            if (_appRows.Count == 0 && _allApps.Count > 0) AppsEmptyLabel.Text = "برنامه‌ای با این اسم نیست.";
        }

        // اگه وصلیم، تغییر همین الان اعمال می‌شه: کانفیگ همین سرور دوباره ساخته می‌شه و موتور (و در اندروید VPN) دوباره روشن می‌شه
        private async Task ApplyRoutingChangeAsync()
        {
            if (!isConnected || _connecting || _currentServer == null) return;
            var server = _currentServer;
#if ANDROID
            string link = DerafshVpnService.CurrentLink ?? server.Config;
#else
            string link = server.Config;
#endif
            StatusLabel.Text = "در حال اعمال تغییر…";
            try
            {
                string configPath = ConnectionGuard.CurrentConfigPath;
                await File.WriteAllTextAsync(configPath, await BuildXrayConfigAsync(link, XrayProc.SessionPort));
#if ANDROID
                if (!await VpnController.StartAsync(configPath, server.City, link)) throw new Exception(VpnController.LastError);
#else
                if (!await SwitchEngineAsync(new ConnectionGuard.Spare(server.City, link, configPath))) throw new Exception();
#endif
                StatusLabel.Text = "تغییر اعمال شد.";   // زاپاس‌ها موقع رفتن با تنظیم تازه ساخته می‌شن
            }
            catch { StatusLabel.Text = "تغییر از اتصال بعدی اعمال می‌شود."; }
        }
    }
}
