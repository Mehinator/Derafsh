namespace Derafsh.Client
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            UserAppTheme = AppTheme.Dark;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell())
            {
                Title = "Derafsh",
                Width = 1000,
                Height = 780,
                MinimumWidth = 380,
                MinimumHeight = 560,
            };
#if WINDOWS
            // خود پنجره همیشه چپ‌به‌راست: MAUI جهت پنجره رو از «فرمت منطقه‌ای» ویندوز می‌گیره و با فرمت فارسی کل پنجره رو
            // آینه می‌کنه (WS_EX_LAYOUTRTL)، دکمه‌ی بستن می‌ره سمت چپ. متن برنامه خودش راست‌به‌چپه (FlowDirection صفحه‌ها).
            window.FlowDirection = FlowDirection.LeftToRight;
#endif

            // اگه برنامه وقتی وصله بسته بشه، پروکسی ویندوز رو خاموش کن (وگرنه اینترنت قطع می‌مونه)
            window.Destroying += (_, _) =>
            {
                global::Derafsh.Client.MainPage.CleanupOnExit();
#if WINDOWS
                global::Derafsh.Client.WinUI.Tray.Remove();   // وگرنه آیکون کنار ساعت تا رفتن موس روش می‌مونه
#endif
            };
            return window;
        }
    }
}
