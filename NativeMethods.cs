using System.Runtime.InteropServices;

namespace LongPortraitScreenshot;

internal static class NativeMethods
{
    internal const int WsExTransparent = 0x00000020;
    internal const int WsExToolWindow = 0x00000080;
    internal const int WsExLayered = 0x00080000;
    internal const int WsExNoActivate = 0x08000000;

    internal const int WmNcHitTest = 0x0084;
    internal const int WmMouseActivate = 0x0021;
    internal const int HtTransparent = -1;
    internal const int MaNoActivate = 3;

    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;

    internal static readonly nint HwndTopmost = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
