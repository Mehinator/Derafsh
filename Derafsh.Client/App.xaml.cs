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

            // اگه برنامه وقتی وصله بسته بشه، پروکسی ویندوز رو خاموش کن (وگرنه اینترنت قطع می‌مونه)
            window.Destroying += (_, _) => global::Derafsh.Client.MainPage.CleanupOnExit();
            return window;
        }
    }
}
