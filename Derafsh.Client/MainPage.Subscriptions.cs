using System.Text.Json;
using Derafsh.Client.Services;

namespace Derafsh.Client
{
    // لینک‌های اشتراک شخصی (فروشنده‌ها کانفیگ‌ها رو مرتب عوض می‌کنن): هر اشتراک با کانفیگ‌هایی که آخرین بار داد نگه داشته می‌شه و
    // هر SubscriptionRefreshHours ساعت دوباره گرفته می‌شه؛ کانفیگ‌های تازه‌اش به لیست شخصی اضافه و کهنه‌هاش برداشته می‌شن.
    // کانفیگ‌هایی که کاربر دستی اضافه کرده (یا از یه اشتراک دیگه هم اومدن) دست نمی‌خورن. قبلاً لینک اشتراک فقط یه بار دانلود می‌شد.
    public partial class MainPage
    {
        private const int SubscriptionRefreshHours = 6;
        private string SubscriptionsPath => Path.Combine(FileSystem.AppDataDirectory, "user_subs.json");

        private sealed class Subscription
        {
            public string Url { get; set; } = "";
            public List<string> Links { get; set; } = new();
            public long Updated { get; set; }   // unix seconds
        }

        private List<Subscription> ReadSubscriptions()
        {
            try { if (File.Exists(SubscriptionsPath)) return JsonSerializer.Deserialize<List<Subscription>>(File.ReadAllText(SubscriptionsPath)) ?? new(); }
            catch { }
            return new();
        }

        private void WriteSubscriptions(List<Subscription> subs)
        {
            try { File.WriteAllText(SubscriptionsPath, JsonSerializer.Serialize(subs)); } catch { }
        }

        // بعد از «افزودن کانفیگ» با لینک اشتراک
        private void RememberSubscription(string url, List<string> links)
        {
            var subs = ReadSubscriptions();
            subs.RemoveAll(s => s.Url == url);
            subs.Add(new Subscription { Url = url, Links = links.Distinct().ToList(), Updated = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            WriteSubscriptions(subs);
        }

        // اشتراک‌هایی که بیشتر از SubscriptionRefreshHours ساعت پیش گرفته شدن (یا همه، force)؛ برمی‌گردونه چند کانفیگ عوض شد
        private async Task<int> RefreshSubscriptionsAsync(bool force = false)
        {
            var subs = ReadSubscriptions();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var due = subs.Where(s => force || now - s.Updated >= SubscriptionRefreshHours * 3600).ToList();
            if (due.Count == 0) return 0;

            var lines = File.Exists(UserPath) ? (await File.ReadAllLinesAsync(UserPath)).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : new List<string>();
            int changed = 0;
            foreach (var sub in due)
            {
                string? text = await DownloadSubscriptionAsync(sub.Url);
                if (text == null) continue;   // این بار باز نشد؛ کانفیگ‌های قبلی می‌مونن و دفعه‌ی بعد دوباره
                var fresh = ConfigLinkRegex.Matches(ConfigService.ExpandSubscription(text)).Select(m => m.Value.Trim()).Distinct().ToList();
                if (fresh.Count == 0) continue;   // لیست خالی (پنل موقتاً خراب): کانفیگ‌های قبلی رو پاک نمی‌کنیم
                var keepElsewhere = subs.Where(s => s != sub).SelectMany(s => s.Links).ToHashSet();
                var gone = sub.Links.Except(fresh).Where(l => !keepElsewhere.Contains(l)).ToHashSet();
                changed += lines.RemoveAll(gone.Contains);
                foreach (var link in fresh.Where(l => !lines.Contains(l))) { lines.Add(link); changed++; }
                sub.Links = fresh;
                sub.Updated = now;
            }
            if (changed > 0) await File.WriteAllLinesAsync(UserPath, lines);
            WriteSubscriptions(subs);
            if (changed > 0 && _currentList == ListPersonal)
            {
                LoadConfigs();
                _ = TestRowsAsync(onlyUntested: true);
            }
            return changed;
        }

        // «بیشتر…» ← «لینک‌های اشتراک»: تازه کردن همه، یا حذف یکی (با کانفیگ‌هایی که فقط از همون اومده بودن)
        private async Task ManageSubscriptionsAsync()
        {
            var subs = ReadSubscriptions();
            if (subs.Count == 0)
            {
                await DisplayAlertAsync("لینک اشتراکی ندارید", "با «افزودن کانفیگ» لینک اشتراک خودتان (https://…) را اضافه کنید؛ هر ۶ ساعت خودکار تازه می‌شود.", "باشه");
                return;
            }
            const string RefreshAll = "همین الان تازه کن";
            string Label(Subscription s) => $"حذف: {Host(s.Url)} ({Fa(s.Links.Count)} کانفیگ)";
            var choices = new List<string> { RefreshAll };
            choices.AddRange(subs.Select(Label));
            string? choice = await DisplayActionSheetAsync("لینک‌های اشتراک (هر ۶ ساعت خودکار تازه می‌شوند)", "لغو", null, choices.ToArray());
            if (choice == RefreshAll)
            {
                StatusLabel.Text = "در حال تازه کردن لینک‌های اشتراک…";
                int n = await RefreshSubscriptionsAsync(force: true);
                StatusLabel.Text = n > 0 ? $"لینک‌های اشتراک تازه شد؛ {Fa(n)} کانفیگ عوض شد." : "لینک‌های اشتراک تازه شد؛ کانفیگ تازه‌ای نبود.";
                return;
            }
            var sub = subs.FirstOrDefault(s => Label(s) == choice);
            if (sub == null) return;
            if (!await DisplayAlertAsync("این لینک اشتراک حذف شود؟", $"{Host(sub.Url)} و کانفیگ‌هایی که فقط از آن آمده بودند از لیست شخصی حذف می‌شوند.", "حذف", "لغو")) return;
            var keepElsewhere = subs.Where(s => s != sub).SelectMany(s => s.Links).ToHashSet();
            var remove = sub.Links.Where(l => !keepElsewhere.Contains(l)).ToHashSet();
            var lines = File.Exists(UserPath) ? (await File.ReadAllLinesAsync(UserPath)).Where(l => !remove.Contains(l.Trim())).ToList() : new List<string>();
            await File.WriteAllLinesAsync(UserPath, lines);
            subs.Remove(sub);
            WriteSubscriptions(subs);
            LoadConfigs();
            StatusLabel.Text = $"لینک اشتراک {Host(sub.Url)} حذف شد.";
        }

        private static string Host(string url)
        {
            try { return new Uri(url).Host; } catch { return url; }
        }
    }
}
