using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Quattromani.App;

/// <summary>
/// macOS workaround: Avalonia does not set the colorspace of the CAMetalLayer, and sometimes the window is composited with a
/// different color space (tints that lighten/darken when moving the mouse, until it is resized).
/// We pin it to sRGB on the window's layer.
/// </summary>
static class MacColorSpace
{
    const string ObjC = "/usr/lib/libobjc.dylib";
    const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr obj, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr obj, IntPtr sel, IntPtr arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SendBool(IntPtr obj, IntPtr sel, IntPtr arg);
    [DllImport(CF)] static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string s, uint encoding);
    [DllImport(CF)] static extern void CFRelease(IntPtr obj);
    [DllImport(CG)] static extern IntPtr CGColorSpaceCreateWithName(IntPtr name);

    static bool _logged;

    /// <summary>Idempotent: the CAMetalLayer appears only after the first redraw and can be recreated, so it must be called until it finds it and then periodically.</summary>
    /// <returns>true if it found a layer to set it on.</returns>
    public static bool Apply(TopLevel top)
    {
        if (!OperatingSystem.IsMacOS()) return true;
        try
        {
            var h = top.TryGetPlatformHandle();
            var window = h?.Handle ?? IntPtr.Zero;
            if (window == IntPtr.Zero) return false;
            var name = CFStringCreateWithCString(IntPtr.Zero, "kCGColorSpaceSRGB", 0x08000100);
            var srgb = CGColorSpaceCreateWithName(name);
            CFRelease(name);
            if (srgb == IntPtr.Zero) return false;
            var n = SetOnLayers(Send(window, sel_registerName("contentView")), srgb);
            CFRelease(srgb);
            if (n > 0 && !_logged) { _logged = true; Console.Error.WriteLine($"[colorspace] sRGB impostato su {n} layer"); }
            return n > 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return true; }
    }

    /// <summary>Sets the colorspace on the view's layer and on the subviews that support it (the CAMetalLayer lives in a subview).</summary>
    static int SetOnLayers(IntPtr view, IntPtr colorSpace)
    {
        var count = 0;
        var layer = Send(view, sel_registerName("layer"));
        if (layer != IntPtr.Zero && SendBool(layer, sel_registerName("respondsToSelector:"), sel_registerName("setColorspace:")))
        {
            Send(layer, sel_registerName("setColorspace:"), colorSpace);
            count++;
        }
        var subviews = Send(view, sel_registerName("subviews"));
        var n = (long)Send(subviews, sel_registerName("count"));
        for (long i = 0; i < n; i++)
            count += SetOnLayers(Send(subviews, sel_registerName("objectAtIndex:"), (IntPtr)i), colorSpace);
        return count;
    }
}
