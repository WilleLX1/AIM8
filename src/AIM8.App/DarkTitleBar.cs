using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AIM8.App;

/// <summary>
/// Asks the desktop window manager for a dark title bar, so the chrome matches
/// the dashboard instead of framing it in white.
/// </summary>
internal static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;

    /// <summary>The attribute id used before Windows 10 build 18985.</summary>
    private const int UseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = 1;
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
    }
}
