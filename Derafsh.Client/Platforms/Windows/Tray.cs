using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace Derafsh.Client.WinUI
{
    // آیکون درفش کنار ساعت ویندوز: کمینه کردن پنجره، برنامه رو اونجا می‌بره (از نوار وظیفه برمی‌داره) و اتصال روشن
    // می‌مونه. کلیک روی آیکون پنجره رو برمی‌گردونه؛ راست‌کلیک: «نمایش درفش» و «خروج». با Shell_NotifyIcon خود ویندوز،
    // بدون بسته‌ی اضافه؛ پیام‌های آیکون از راه subclass همون پنجره‌ی برنامه می‌رسن.
    internal static class Tray
    {
        private const int WM_APP_TRAY = 0x8000 + 1, WM_SIZE = 0x0005, SIZE_MINIMIZED = 1;
        private const int WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_NULL = 0;
        private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
        private const int MF_STRING = 0, MF_SEPARATOR = 0x800, TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, TPM_LAYOUTRTL = 0x8000;
        private const int CmdShow = 1, CmdExit = 2;

        private static IntPtr s_hwnd, s_icon;
        private static AppWindow? s_appWindow;
        private static Microsoft.UI.Dispatching.DispatcherQueue? s_dispatcher;
        private static SubclassProc? s_proc;   // نگه داشته می‌شه تا جمع‌آوری نشه
        private static uint s_taskbarCreated;
        private static bool s_added;

        public static void Attach(Microsoft.UI.Xaml.Window window)
        {
            s_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            s_appWindow = window.AppWindow;
            s_dispatcher = window.DispatcherQueue;
            string exe = Environment.ProcessPath ?? "";
            if (ExtractIconEx(exe, 0, out _, out s_icon, 1) == 0 || s_icon == IntPtr.Zero) s_icon = LoadIcon(IntPtr.Zero, (IntPtr)32512);
            s_taskbarCreated = RegisterWindowMessage("TaskbarCreated");   // اکسپلورر دوباره راه افتاد: آیکون دوباره
            s_proc = WndProc;
            SetWindowSubclass(s_hwnd, s_proc, (UIntPtr)1, IntPtr.Zero);
            Add();
        }

        private static void Add()
        {
            var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            s_added = Shell_NotifyIcon(NIM_ADD, ref data);
        }

        public static void Remove()
        {
            if (!s_added) return;
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            s_added = false;
        }

        // متن آیکون (مثلاً «درفش · وصل به 🇩🇪 Berlin»)
        public static void SetTooltip(string text)
        {
            if (!s_added) return;
            var data = Data(NIF_TIP);
            data.szTip = text.Length > 120 ? text[..120] : text;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }

        private static NOTIFYICONDATAW Data(int flags) => new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = s_hwnd,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = s_icon,
            szTip = "درفش",
            szInfo = "",
            szInfoTitle = "",
        };

        private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
        {
            if (msg == WM_SIZE && (int)wParam == SIZE_MINIMIZED)
                s_dispatcher?.TryEnqueue(HideToTray);   // کمینه: کنار ساعت
            else if (msg == WM_APP_TRAY)
            {
                int mouse = (int)lParam & 0xFFFF;
                if (mouse is WM_LBUTTONUP or WM_LBUTTONDBLCLK) s_dispatcher?.TryEnqueue(ShowWindow);
                else if (mouse == WM_RBUTTONUP) ShowMenu();
            }
            else if (msg == s_taskbarCreated && s_taskbarCreated != 0) Add();
            return DefSubclassProc(hwnd, msg, wParam, lParam);
        }

        private static void HideToTray()
        {
            if (s_appWindow == null) return;
            s_appWindow.Hide();
            // بار اول بگه کجا رفت
            if (!Preferences.Default.Get("tray_hint_shown", false) && s_added)
            {
                Preferences.Default.Set("tray_hint_shown", true);
                var data = Data(NIF_INFO);
                data.szInfoTitle = "درفش اینجاست";
                data.szInfo = "اتصال روشن می‌ماند. برای باز کردن روی آیکون بزنید؛ برای خروج راست‌کلیک کنید.";
                Shell_NotifyIcon(NIM_MODIFY, ref data);
            }
        }

        private static void ShowWindow()
        {
            if (s_appWindow == null) return;
            s_appWindow.Show(true);
            if (s_appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) p.Restore();
            SetForegroundWindow(s_hwnd);
        }

        private static void ShowMenu()
        {
            var menu = CreatePopupMenu();
            AppendMenu(menu, MF_STRING, CmdShow, "نمایش درفش");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, CmdExit, "خروج");
            GetCursorPos(out var pt);
            SetForegroundWindow(s_hwnd);   // وگرنه منو با کلیک بیرونش بسته نمی‌شه
            int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LAYOUTRTL, pt.X, pt.Y, 0, s_hwnd, IntPtr.Zero);
            PostMessage(s_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            DestroyMenu(menu);
            if (cmd == CmdShow) s_dispatcher?.TryEnqueue(ShowWindow);
            else if (cmd == CmdExit) s_dispatcher?.TryEnqueue(Exit);
        }

        // خروج کامل: پروکسی ویندوز برمی‌گرده، xray بسته می‌شه، آیکون برداشته می‌شه
        private static void Exit()
        {
            MainPage.CleanupOnExit();
            Remove();
            Microsoft.Maui.Controls.Application.Current?.Quit();
        }

        private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATAW data);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
        [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);
        [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, int flags, int id, string? text);
        [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, int flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
