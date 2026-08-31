using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LongPortraitScreenshot.Imaging;

internal static class ScrollbarCropper
{
    private const int VerticalScrollBarMetric = 2;

    public static int GetVerticalScrollBarWidth(Rectangle targetBounds)
    {
        Point center = new(
            targetBounds.Left + (targetBounds.Width / 2),
            targetBounds.Top + (targetBounds.Height / 2));

        IntPtr targetWindow = WindowFromPoint(center);
        uint dpi = targetWindow == IntPtr.Zero ? 0 : GetDpiForWindow(targetWindow);
        if (dpi == 0)
        {
            dpi = GetDpiForSystem();
        }

        return GetSystemMetricsForDpi(VerticalScrollBarMetric, dpi);
    }

    public static Bitmap CropRight(Bitmap source, int cropWidth)
    {
        ArgumentNullException.ThrowIfNull(source);

        int safeCropWidth = Math.Clamp(cropWidth, 0, source.Width - 1);
        Bitmap cropped = new(
            source.Width - safeCropWidth,
            source.Height,
            PixelFormat.Format32bppArgb);

        if (source.HorizontalResolution > 0
            && source.VerticalResolution > 0
            && float.IsFinite(source.HorizontalResolution)
            && float.IsFinite(source.VerticalResolution))
        {
            cropped.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        }

        try
        {
            using Graphics graphics = Graphics.FromImage(cropped);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(source, 0, 0);
            return cropped;
        }
        catch
        {
            cropped.Dispose();
            throw;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
