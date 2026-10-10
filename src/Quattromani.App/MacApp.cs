using System.Runtime.InteropServices;

namespace Quattromani.App;

/// <summary>Le azioni standard del menu dell'applicazione su macOS (Nascondi, Nascondi altre, Mostra tutte), richiamate da Objective-C.</summary>
static class MacApp
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

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
}
