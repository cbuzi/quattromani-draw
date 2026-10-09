using Avalonia;

namespace AiDraw.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect()
            // Il menu dell'applicazione su macOS lo costruisce App (senza le voci predefinite di Avalonia: About Avalonia, Services...).
            .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true })
            .StartWithClassicDesktopLifetime(args);
}
