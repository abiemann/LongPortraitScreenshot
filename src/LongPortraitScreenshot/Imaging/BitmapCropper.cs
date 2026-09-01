using System.Drawing.Imaging;

namespace LongPortraitScreenshot.Imaging;

internal static class BitmapCropper
{
    public static Bitmap Crop(Bitmap source, Rectangle bounds)
    {
        ArgumentNullException.ThrowIfNull(source);

        Rectangle sourceBounds = new(0, 0, source.Width, source.Height);
        if (bounds.Width <= 0
            || bounds.Height <= 0
            || !sourceBounds.Contains(bounds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bounds),
                "The crop must be a non-empty rectangle inside the source image.");
        }

        Bitmap cropped = source.Clone(bounds, PixelFormat.Format32bppArgb);
        PreserveResolution(source, cropped);
        return cropped;
    }

    private static void PreserveResolution(Bitmap source, Bitmap destination)
    {
        if (source.HorizontalResolution > 0
            && source.VerticalResolution > 0
            && float.IsFinite(source.HorizontalResolution)
            && float.IsFinite(source.VerticalResolution))
        {
            destination.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        }
    }
}
