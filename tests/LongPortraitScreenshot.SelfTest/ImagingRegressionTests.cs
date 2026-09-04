using System.Drawing.Imaging;
using LongPortraitScreenshot.Imaging;

namespace LongPortraitScreenshot.SelfTest;

internal static class ImagingRegressionTests
{
    internal static void Run()
    {
        FixedChromeIsComposedOnce(headerRows: 0, footerRows: 20, [0, 195, 390]);
        FixedChromeIsComposedOnce(headerRows: 72, footerRows: 0, [0, 195]);
        FixedChromeIsComposedOnce(headerRows: 72, footerRows: 20, [0, 195]);
        FixedChromeIsComposedOnce(headerRows: 34, footerRows: 20, [0, 195, 202]);
        FixedChromeIsComposedOnce(headerRows: 34, footerRows: 20, [0, 195], partialRows: 8);
        FixedChromeIsComposedOnce(headerRows: 34, footerRows: 20, [0, 195], partialRows: 187);
        FixedChromeIsComposedOnce(headerRows: 0, footerRows: 0, [0, 195, 202], PixelFormat.Format24bppRgb);
        FixedChromeIsComposedOnce(headerRows: 34, footerRows: 20, [0, 195], PixelFormat.Format24bppRgb);
        SparseContentWithBlankMarginsStillComposes();
        SmallGlobalRenderingDifferenceStillMatches();
        AnimatedFooterPixelCannotEstablishCompositionSeam();
        FixedChromeWithoutSharedContentIsRejected();
        MeasurementCancellationLeavesSourcesUsable();
    }

    private static void FixedChromeIsComposedOnce(
        int headerRows,
        int footerRows,
        int[] offsets,
        PixelFormat format = PixelFormat.Format32bppArgb,
        int? partialRows = null)
    {
        const int width = 97;
        const int viewportHeight = 300;
        int fullHeight = viewportHeight + offsets[^1];
        using Bitmap page = CreatePage(width, fullHeight);
        using Bitmap fullExpected = (Bitmap)page.Clone();
        PaintChrome(fullExpected, headerRows, footerRows);
        int outputHeight = partialRows is int appendedRows
            ? viewportHeight + offsets[^2] + appendedRows
            : fullHeight;
        using Bitmap expected = fullExpected.Clone(
            new Rectangle(0, 0, width, outputHeight), PixelFormat.Format32bppArgb);
        List<Bitmap> images = [];
        try
        {
            List<CapturedFrame> frames = [];
            foreach (int offset in offsets)
            {
                Bitmap frame = new(width, viewportHeight, format);
                using (Graphics graphics = Graphics.FromImage(frame))
                {
                    graphics.DrawImage(page,
                        new Rectangle(0, 0, width, viewportHeight),
                        new Rectangle(0, offset, width, viewportHeight), GraphicsUnit.Pixel);
                }

                images.Add(frame);
                PaintChrome(frame, headerRows, footerRows);
                frames.Add(new CapturedFrame(frame,
                    offset * 100.0 / (fullHeight - viewportHeight),
                    viewportHeight * 100.0 / fullHeight));
            }

            int[] measuredShifts = new int[offsets.Length - 1];
            for (int index = 1; index < frames.Count; index++)
            {
                measuredShifts[index - 1] = VerticalStitcher.MeasureVerticalShift(frames[index - 1], frames[index], index);
                Require(measuredShifts[index - 1] == offsets[index] - offsets[index - 1],
                    "Fixed chrome caused an incorrect measured shift.");
            }

            using Bitmap result = VerticalStitcher.Stitch(frames, 1_000_000, CancellationToken.None, partialRows);
            AssertEqual(expected, result, $"header {headerRows}, footer {footerRows}, partial {partialRows}, format {format}");
            using Bitmap retainedResult = VerticalStitcher.Stitch(
                frames, 1_000_000, CancellationToken.None, partialRows, measuredShifts);
            AssertEqual(expected, retainedResult, "retained-shift composition");
        }
        finally
        {
            foreach (Bitmap bitmap in images)
            {
                bitmap.Dispose();
            }
        }
    }

    private static void SparseContentWithBlankMarginsStillComposes()
    {
        const int width = 97;
        const int viewportHeight = 300;
        const int shift = 195;
        using Bitmap page = new(width, viewportHeight + shift, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(page))
        {
            graphics.Clear(Color.White);
            using SolidBrush ink = new(Color.MidnightBlue);
            graphics.FillRectangle(ink, new Rectangle(20, shift + 40, 51, 5));
        }

        using Bitmap first = page.Clone(new Rectangle(0, 0, width, viewportHeight), PixelFormat.Format32bppArgb);
        using Bitmap second = page.Clone(new Rectangle(0, shift, width, viewportHeight), PixelFormat.Format32bppArgb);
        double viewSize = viewportHeight * 100.0 / page.Height;
        using Bitmap actual = VerticalStitcher.Stitch(
            [new CapturedFrame(first, 0, viewSize), new CapturedFrame(second, 100, viewSize)]);
        AssertEqual(page, actual, "sparse content with stationary white margins");
    }

    private static void SmallGlobalRenderingDifferenceStillMatches()
    {
        using Bitmap page = CreatePage(97, 495);
        using Bitmap first = page.Clone(new Rectangle(0, 0, 97, 300), PixelFormat.Format32bppArgb);
        using Bitmap second = page.Clone(new Rectangle(0, 195, 97, 300), PixelFormat.Format32bppArgb);
        for (int y = 0; y < second.Height; y++)
        {
            for (int x = 0; x < second.Width; x++)
            {
                Color pixel = second.GetPixel(x, y);
                second.SetPixel(x, y, Color.FromArgb(
                    Math.Min(255, pixel.R + 5), Math.Min(255, pixel.G + 5), Math.Min(255, pixel.B + 5)));
            }
        }

        double viewSize = 300 * 100.0 / 495;
        CapturedFrame previous = new(first, 0, viewSize);
        CapturedFrame current = new(second, 100, viewSize);
        Require(VerticalStitcher.MeasureVerticalShift(previous, current, 1) == 195,
            "A small global rendering difference must retain the existing matching tolerance.");
        using Bitmap actual = VerticalStitcher.Stitch([previous, current]);
        Require(actual.Size == page.Size, "Tolerated rendering differences changed the stitched dimensions.");
    }

    private static void AnimatedFooterPixelCannotEstablishCompositionSeam()
    {
        using Bitmap page = CreatePage(97, 495);
        for (int x = 0; x < page.Width; x++)
        {
            page.SetPixel(x, 299, page.GetPixel(x, 104));
        }

        page.SetPixel(3, 299, Color.White);
        using Bitmap first = page.Clone(new Rectangle(0, 0, 97, 300), PixelFormat.Format32bppArgb);
        using Bitmap second = page.Clone(new Rectangle(0, 195, 97, 300), PixelFormat.Format32bppArgb);
        PaintChrome(first, 0, 20);
        PaintChrome(second, 0, 20);
        first.SetPixel(3, 299, Color.White);
        second.SetPixel(3, 299, Color.Black);
        PaintChrome(page, 0, 20);
        page.SetPixel(3, 494, Color.Black);
        double viewSize = 300 * 100.0 / 495;
        using Bitmap actual = VerticalStitcher.Stitch(
            [new CapturedFrame(first, 0, viewSize), new CapturedFrame(second, 100, viewSize)]);
        AssertEqual(page, actual, "an animated footer pixel cannot hide preceding rows");
    }

    private static void FixedChromeWithoutSharedContentIsRejected()
    {
        using Bitmap page = CreatePage(97, 495);
        using Bitmap first = page.Clone(new Rectangle(0, 0, 97, 300), PixelFormat.Format32bppArgb);
        using Bitmap second = page.Clone(new Rectangle(0, 195, 97, 300), PixelFormat.Format32bppArgb);
        PaintChrome(first, 72, 40);
        PaintChrome(second, 72, 40);
        double viewSize = 300 * 100.0 / 495;
        bool rejected = false;
        try
        {
            using Bitmap unexpected = VerticalStitcher.Stitch(
                [new CapturedFrame(first, 0, viewSize), new CapturedFrame(second, 100, viewSize)]);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("confidently stitch", StringComparison.Ordinal))
        {
            rejected = true;
        }

        Require(rejected, "Frames with no shared unobscured content must be rejected.");
    }

    private static void MeasurementCancellationLeavesSourcesUsable()
    {
        using Bitmap page = CreatePage(97, 495);
        using Bitmap first = page.Clone(new Rectangle(0, 0, 97, 300), PixelFormat.Format32bppArgb);
        using Bitmap second = page.Clone(new Rectangle(0, 195, 97, 300), PixelFormat.Format32bppArgb);
        double viewSize = 300 * 100.0 / 495;
        CapturedFrame previous = new(first, 0, viewSize);
        CapturedFrame current = new(second, 100, viewSize);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        bool canceled = false;
        try
        {
            _ = VerticalStitcher.MeasureVerticalShift(previous, current, 1, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Require(canceled, "Seam measurement ignored cancellation.");
        Require(VerticalStitcher.MeasureVerticalShift(previous, current, 1) == 195,
            "Canceled measurement left the source bitmaps locked or disposed.");
        using Graphics graphics = Graphics.FromImage(first);
        graphics.FillRectangle(Brushes.Magenta, 0, 0, 1, 1);
    }

    private static Bitmap CreatePage(int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(
                    (x * 31 + y * 17 + (y >> 8) * 23) & 255,
                    (x * 7 + y * 47 + (y >> 4) * 13) & 255,
                    (x * 19 + y * 71 + (y >> 7) * 29) & 255));
            }
        }

        return bitmap;
    }

    private static void PaintChrome(Bitmap image, int headerRows, int footerRows)
    {
        using Graphics graphics = Graphics.FromImage(image);
        graphics.FillRectangle(Brushes.Navy, 0, 0, image.Width, headerRows);
        graphics.FillRectangle(Brushes.Gold, 0, image.Height - footerRows, image.Width, footerRows);
    }

    private static void AssertEqual(Bitmap expected, Bitmap actual, string scenario)
    {
        Require(actual.Size == expected.Size, $"{scenario}: unexpected dimensions.");
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Require(actual.GetPixel(x, y).ToArgb() == expected.GetPixel(x, y).ToArgb(),
                    $"{scenario}: incorrect pixel at ({x}, {y}).");
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
