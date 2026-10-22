using Avalonia;

namespace Quattromani.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Errori fuori dall'interfaccia: l'app può chiudersi, ma prima si registra l'errore e si salva una copia del lavoro.
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
            // Il menu dell'applicazione su macOS lo costruisce App (senza le voci predefinite di Avalonia: About Avalonia, Services...).
            .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true });
}
