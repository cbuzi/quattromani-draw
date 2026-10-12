using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace Quattromani.App;

/// <summary>Le azioni standard del menu dell'applicazione su macOS (Nascondi, Nascondi altre, Mostra tutte), richiamate da Objective-C.</summary>
static class MacApp
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr bytes, nint length);

    // [[NSApplication sharedApplication] <selector>nil]
    static void Call(string selector)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        Send(app, sel_registerName(selector), IntPtr.Zero);
    }

    public static void Hide() => Call("hide:");
    public static void HideOthers() => Call("hideOtherApplications:");
    public static void ShowAll() => Call("unhideAllApplications:");

    /// <summary>Icona del Dock: serve anche con "dotnet run", dove non c'è un bundle .app con la sua icona.</summary>
    public static void SetDockIcon()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var stream = AssetLoader.Open(new Uri("avares://Quattromani.App/Assets/icon.png"));
        var png = new MemoryStream();
        stream.CopyTo(png);
        var bytes = png.ToArray();
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var data = Send(objc_getClass("NSData"), sel_registerName("dataWithBytes:length:"), handle.AddrOfPinnedObject(), bytes.Length);
            var image = Send(Send(objc_getClass("NSImage"), sel_registerName("alloc")), sel_registerName("initWithData:"), data);
            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            Send(app, sel_registerName("setApplicationIconImage:"), image);
        }
        finally { handle.Free(); }
    }
}
