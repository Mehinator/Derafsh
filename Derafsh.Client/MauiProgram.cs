using Microsoft.Maui.LifecycleEvents;

namespace Derafsh.Client
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                    fonts.AddFont("Vazirmatn-Bold.ttf", "VazirmatnBold");   // واژه‌نشان فارسی «درفش»
                });

#if WINDOWS
            // دکمه‌های کمینه/بستن پنجره روی نوار عنوان تیره دیده بشن
            builder.ConfigureLifecycleEvents(events => events.AddWindows(windows => windows.OnWindowCreated(window =>
            {
                var bar = window.AppWindow.TitleBar;
                bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                bar.ButtonForegroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 0xBE, 0xB3, 0xC9);          // TextSecondary
                bar.ButtonInactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 0x9A, 0x8D, 0xA8);  // TextMuted
                bar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 0x2A, 0x20, 0x35);     // SurfaceHover
                bar.ButtonHoverForegroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 0xF3, 0xEE, 0xF7);     // TextPrimary
                bar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 0x23, 0x1A, 0x2D);   // SurfaceRaised
                bar.ButtonPressedForegroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 0xF3, 0xEE, 0xF7);
            })));
#endif

            return builder.Build();
        }
    }
}
