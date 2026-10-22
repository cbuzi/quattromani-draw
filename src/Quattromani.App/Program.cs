using Avalonia;

namespace Quattromani.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Errors outside the UI: the app may close, but first the error is logged and a copy of the work is saved.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) CrashLog.Write("Fatal error", ex);
            Task.Run(App.Recovery.Write).Wait(3000);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("Background task", e.Exception);
            e.SetObserved();
        };
        BuildApp().StartWithClassicDesktopLifetime(args);
    }

    static AppBuilder BuildApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect()
            // The application menu on macOS is built by App (without Avalonia's default entries: About Avalonia, Services...).
            .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true });
}
