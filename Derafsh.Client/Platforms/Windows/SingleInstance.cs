using System.Runtime.InteropServices;

namespace Derafsh.Client.WinUI
{
    // درفش فقط یک‌بار اجرا می‌شه. اجرای دوباره (مثلاً دابل‌کلیک روی exe وقتی برنامه کنار ساعته) به اولی می‌گه پنجره‌ش رو
    // نشون بده و خودش بی‌صدا بسته می‌شه؛ وگرنه هر بار یه درفش تازه با xray و آیکون کنار ساعت خودش باز می‌شد.
    // Local\ یعنی برای همین کاربر ویندوز؛ کاربر دیگه‌ای روی همین کامپیوتر درفش خودش رو داره.
    internal static class SingleInstance
    {
        private const string MutexName = @"Local\Derafsh.Client.SingleInstance";
        private const string ShowEventName = @"Local\Derafsh.Client.ShowWindow";
        private const int ASFW_ANY = -1;

        private static Mutex? s_mutex;             // تا آخر اجرا نگه داشته می‌شه؛ با بسته شدن برنامه آزاد می‌شه
        private static EventWaitHandle? s_show;

        // true: این تنها درفشه. false: یکی دیگه بازه و بهش گفته شد پنجره‌ش رو نشون بده.
        public static bool Claim()
        {
            s_show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            s_mutex = new Mutex(false, MutexName);
            bool first;
            try { first = s_mutex.WaitOne(0); }
            catch (AbandonedMutexException) { first = true; }   // اولی وسط کار بسته شده بود
            if (first) return true;

            // اجازه‌ی اومدن جلو رو به اولی بده (این یکی رو کاربر همین الان باز کرده، پس خودش این اجازه رو داره)
            AllowSetForegroundWindow(ASFW_ANY);
            s_show.Set();
            return false;
        }

        // فقط در اولی: هر بار که یه اجرای دیگه علامت داد، پنجره رو برگردون (از کنار ساعت هم)
        public static void Listen(Action show)
        {
            var signal = s_show;
            if (signal == null) return;
            new Thread(() =>
            {
                while (signal.WaitOne()) show();
            })
            { IsBackground = true, Name = "Derafsh single instance" }.Start();
        }

        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    }
}
