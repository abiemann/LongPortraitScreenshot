using System.Runtime.InteropServices;

namespace LongPortraitScreenshot.Interop;

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

    internal static bool TryGetAvailablePhysicalMemoryBytes(out ulong availableBytes)
    {
        MemoryStatusEx status = new()
        {
            Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>())
        };

        if (GlobalMemoryStatusEx(ref status))
        {
            availableBytes = status.AvailablePhysicalMemory;
            return true;
        }

        availableBytes = 0;
        return false;
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysicalMemory;
        internal ulong AvailablePhysicalMemory;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtualMemory;
        internal ulong AvailableVirtualMemory;
        internal ulong AvailableExtendedVirtualMemory;
    }
}
