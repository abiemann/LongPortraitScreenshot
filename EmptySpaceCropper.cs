using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LongPortraitScreenshot;

internal static class EmptySpaceCropper
{
    private const int BackgroundMarginPixels = 5;

    public static Bitmap Trim(Bitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Bitmap? converted = null;
        Bitmap scanSource = source;

        if (source.PixelFormat != PixelFormat.Format32bppArgb)
        {
            converted = source.Clone(
                new Rectangle(0, 0, source.Width, source.Height),
                PixelFormat.Format32bppArgb);
            PreserveResolution(source, converted);
            scanSource = converted;
        }

        try
        {
            HorizontalContentBounds bounds = FindContentBounds(scanSource);
            if (!bounds.HasContent)
            {
                return CopyRegion(scanSource, left: 0, scanSource.Width);
            }

            int left = Math.Max(0, bounds.FirstDifferentFromLeft - BackgroundMarginPixels);
            int right = Math.Min(
                scanSource.Width - 1,
                bounds.LastDifferentFromRight + BackgroundMarginPixels);

            if (left > right)
            {
                return CopyRegion(scanSource, left: 0, scanSource.Width);
            }

            return CopyRegion(scanSource, left, right - left + 1);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private static HorizontalContentBounds FindContentBounds(Bitmap source)
    {
        Rectangle rectangle = new(0, 0, source.Width, source.Height);
        BitmapData? data = null;

        try
        {
            data = source.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int[] row = new int[source.Width];
            Dictionary<int, ColorFrequency> leftFrequencies = [];
            Dictionary<int, ColorFrequency> rightFrequencies = [];

            for (int y = 0; y < source.Height; y++)
            {
                Marshal.Copy(GetRowAddress(data, y), row, 0, row.Length);
                CountColor(leftFrequencies, row[0], y);
                CountColor(rightFrequencies, row[^1], y);
            }

            int leftBackground = FindDominantColor(leftFrequencies);
            int rightBackground = FindDominantColor(rightFrequencies);
            int firstDifferentFromLeft = source.Width;
            int lastDifferentFromRight = -1;

            for (int y = 0; y < source.Height; y++)
            {
                Marshal.Copy(GetRowAddress(data, y), row, 0, row.Length);

                for (int x = 0; x < firstDifferentFromLeft; x++)
                {
                    if (row[x] != leftBackground)
                    {
                        firstDifferentFromLeft = x;
                        break;
                    }
                }

                for (int x = source.Width - 1; x > lastDifferentFromRight; x--)
                {
                    if (row[x] != rightBackground)
                    {
                        lastDifferentFromRight = x;
                        break;
                    }
                }

                if (firstDifferentFromLeft == 0 && lastDifferentFromRight == source.Width - 1)
                {
                    break;
                }
            }

            bool hasContent = firstDifferentFromLeft < source.Width
                && lastDifferentFromRight >= 0;
            return new HorizontalContentBounds(
                hasContent,
                firstDifferentFromLeft,
                lastDifferentFromRight);
        }
        finally
        {
            if (data is not null)
            {
                source.UnlockBits(data);
            }
        }
    }

    private static Bitmap CopyRegion(Bitmap source, int left, int width)
    {
        Bitmap result = new(width, source.Height, PixelFormat.Format32bppArgb);
        PreserveResolution(source, result);

        BitmapData? sourceData = null;
        BitmapData? resultData = null;
        bool completed = false;

        try
        {
            sourceData = source.LockBits(
                new Rectangle(0, 0, source.Width, source.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            resultData = result.LockBits(
                new Rectangle(0, 0, result.Width, result.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            int[] row = new int[width];
            int byteOffset = checked(left * sizeof(int));

            for (int y = 0; y < source.Height; y++)
            {
                IntPtr sourceRow = IntPtr.Add(GetRowAddress(sourceData, y), byteOffset);
                Marshal.Copy(sourceRow, row, 0, row.Length);
                Marshal.Copy(row, 0, GetRowAddress(resultData, y), row.Length);
            }

            completed = true;
            return result;
        }
        finally
        {
            if (resultData is not null)
            {
                result.UnlockBits(resultData);
            }

            if (sourceData is not null)
            {
                source.UnlockBits(sourceData);
            }

            if (!completed)
            {
                result.Dispose();
            }
        }
    }

    private static IntPtr GetRowAddress(BitmapData data, int y) =>
        IntPtr.Add(data.Scan0, checked(y * data.Stride));

    private static void CountColor(
        Dictionary<int, ColorFrequency> frequencies,
        int argb,
        int row)
    {
        if (frequencies.TryGetValue(argb, out ColorFrequency frequency))
        {
            frequencies[argb] = frequency with { Count = frequency.Count + 1 };
        }
        else
        {
            frequencies.Add(argb, new ColorFrequency(Count: 1, FirstRow: row));
        }
    }

    private static int FindDominantColor(Dictionary<int, ColorFrequency> frequencies)
    {
        int dominantArgb = 0;
        int dominantCount = -1;
        int dominantFirstRow = int.MaxValue;

        foreach ((int argb, ColorFrequency frequency) in frequencies)
        {
            if (frequency.Count > dominantCount
                || (frequency.Count == dominantCount && frequency.FirstRow < dominantFirstRow))
            {
                dominantArgb = argb;
                dominantCount = frequency.Count;
                dominantFirstRow = frequency.FirstRow;
            }
        }

        return dominantArgb;
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

    private readonly record struct ColorFrequency(int Count, int FirstRow);

    private readonly record struct HorizontalContentBounds(
        bool HasContent,
        int FirstDifferentFromLeft,
        int LastDifferentFromRight);
}
