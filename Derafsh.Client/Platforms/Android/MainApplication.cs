using Android.App;
using Android.Runtime;

namespace Derafsh.Client
{
    [Application]
    public class MainApplication : MauiApplication
    {
        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
            // یه خطای پیش‌بینی‌نشده نباید کل برنامه رو ببنده
            AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
            {
                DebugLog.Write("Unhandled: " + e.Exception);
                e.Handled = true;
            };
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
