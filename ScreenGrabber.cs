using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LongPortraitScreenshot;

public static class ScreenGrabber
{
    public static Bitmap Capture(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException(
                "The selected scrolling control has no visible capture area. Make it visible and try again.");
        }

        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        if (!virtualScreen.Contains(bounds))
        {
            throw new InvalidOperationException(
                "The selected scrolling control is not fully on-screen. Move it onto a monitor, keep it unobscured, and try again.");
        }

        Bitmap image;
        try
        {
            image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException or OutOfMemoryException)
        {
            throw new InvalidOperationException(
                $"Could not allocate a {bounds.Width} x {bounds.Height} capture image. Select a smaller control and try again.",
                exception);
        }

        try
        {
            using Graphics graphics = Graphics.FromImage(image);
            graphics.CopyFromScreen(
                bounds.Location,
                Point.Empty,
                bounds.Size,
                CopyPixelOperation.SourceCopy);
            return image;
        }
        catch (Exception exception) when (exception is Win32Exception or ExternalException or ArgumentException)
        {
            image.Dispose();
            throw new InvalidOperationException(
                "Windows could not capture the selected scrolling control. Keep it visible and unobscured, then try again." +
                $"{Environment.NewLine}{Environment.NewLine}Details: {exception.Message}",
                exception);
        }
    }
}
