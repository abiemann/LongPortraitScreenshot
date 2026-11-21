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
        BrowserScrollMetadataCanDisagreeWithOnlyOneFrame();
        GrowingPageCanAdvanceWhileItsScrollPercentFalls();
        RepaintedRowsCanHideAnAccuratelyPredictedShift();
        ActualScrollCanExceedThePredictedSearchWindow();
        TwoEquallyGoodDistantOffsetsAreRejected();
        MatchingOnlyTheTopOfThePageIsRejected();
        MatchingOnlyHalfThePredictedOverlapIsRejected();
        MovingSidebarOnStationaryContentIsRejected();
    }

    private static void BrowserScrollMetadataCanDisagreeWithOnlyOneFrame()
    {
        // Edge reported equal percentage steps on this viewport. The page moved
        // 965 pixels on the first step and only 60 on the next one.
        const int width = 151;
        const int height = 1485;
        const double viewSize = 12.052593;
        using Bitmap page = CreateTexturedPage(width, height + 1025);
        using Bitmap first = page.Clone(new Rectangle(0, 0, width, height), page.PixelFormat);
        using Bitmap second = page.Clone(new Rectangle(0, 965, width, height), page.PixelFormat);
        using Bitmap third = page.Clone(new Rectangle(0, 1025, width, height), page.PixelFormat);
        foreach (Bitmap image in new[] { page, first, second, third })
        {
            PaintChrome(image, headerRows: 105, footerRows: 30);
        }

        using Bitmap result = VerticalStitcher.Stitch([
            new CapturedFrame(first, 0, viewSize),
            new CapturedFrame(second, 8.9055, viewSize),
            new CapturedFrame(third, 17.811, viewSize)]);
        AssertEqual(page, result, "unequal pixel shifts for equal reported scroll steps");
    }

    private static void GrowingPageCanAdvanceWhileItsScrollPercentFalls()
    {
        const int width = 97, height = 1485, shift = 965;
        using Bitmap page = CreateTexturedPage(width, height + shift);
        using Bitmap first = page.Clone(new Rectangle(0, 0, width, height), page.PixelFormat);
        using Bitmap second = page.Clone(new Rectangle(0, shift, width, height), page.PixelFormat);
        // Edge loaded more content as the viewport advanced near the old end of the page.
        using Bitmap result = VerticalStitcher.Stitch([
            new CapturedFrame(first, 96.281374, 5.203588),
            new CapturedFrame(second, 92.23205, 4.82582)]);
        AssertEqual(page, result, "forward scroll across a growing page boundary");
    }

    private static void RepaintedRowsCanHideAnAccuratelyPredictedShift()
    {
        const int width = 113, height = 600, shift = 390;
        using Bitmap page = CreateTexturedPage(width, height + shift);
        using Bitmap first = page.Clone(new Rectangle(0, 0, width, height), page.PixelFormat);
        using Bitmap second = page.Clone(new Rectangle(0, shift, width, height), page.PixelFormat);
        using Bitmap repaint = CreateTexturedPage(width, 58, seed: 835);
        using (Graphics graphics = Graphics.FromImage(second))
        {
            graphics.DrawImageUnscaled(repaint, 0, 0);
        }

        // The changed strip dominates the narrow scoring window, but 152 rows still match.
        double viewSize = height * 100.0 / page.Height;
        using Bitmap result = VerticalStitcher.Stitch([
            new CapturedFrame(first, 0, viewSize), new CapturedFrame(second, 100, viewSize)]);
        AssertEqual(page, result, "accurate scroll offset obscured by repainted rows");
    }

    private static void ActualScrollCanExceedThePredictedSearchWindow()
    {
        const int width = 89;
        const int height = 500;
        using Bitmap page = CreateTexturedPage(width, height + 380);
        using Bitmap first = page.Clone(new Rectangle(0, 0, width, height), page.PixelFormat);
        using Bitmap second = page.Clone(new Rectangle(0, 380, width, height), page.PixelFormat);
        // A 20% step across a 500-pixel scroll range predicts only 100 pixels.
        using Bitmap result = VerticalStitcher.Stitch([
            new CapturedFrame(first, 0, 50), new CapturedFrame(second, 20, 50)]);
        AssertEqual(page, result, "actual movement above predicted search window");
    }

    private static void TwoEquallyGoodDistantOffsetsAreRejected()
    {
        const int width = 103;
        const int height = 600;
        using Bitmap tile = CreateTexturedPage(width, 256);
        using Bitmap page = new(width, height + 40, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(page))
        {
            for (int y = 0; y < page.Height; y += tile.Height)
            {
                graphics.DrawImageUnscaled(tile, 0, y);
            }
        }

        using Bitmap first = page.Clone(new Rectangle(0, 0, width, height), page.PixelFormat);
        using Bitmap second = page.Clone(new Rectangle(0, 40, width, height), page.PixelFormat);
        // The metadata predicts 168. Both 40 and 296 match exactly outside the search window.
        AssertSeamRejected(first, second, 28, "Two indistinguishable offsets outside the prediction must not be stitched.");
    }

    private static void MatchingOnlyTheTopOfThePageIsRejected()
    {
        const int height = 700;
        const int width = 117;
        using Bitmap first = CreateTexturedPage(width, height);
        using Bitmap second = CreateTexturedPage(width, height, seed: 835);
        using (Graphics graphics = Graphics.FromImage(second))
        {
            // Only the top panel shares content. It moves by 35px while the rest
            // of the viewport has been replaced entirely.
            graphics.DrawImage(first, new Rectangle(0, 0, width, 180),
                new Rectangle(0, 35, width, 180), GraphicsUnit.Pixel);
        }

        AssertSeamRejected(first, second, 65, "Matching a top panel alone must not certify a whole-page shift.");
    }

    private static void MatchingOnlyHalfThePredictedOverlapIsRejected()
    {
        const int width = 113, height = 600, shift = 390;
        using Bitmap first = CreateTexturedPage(width, height, channelRange: 128);
        using Bitmap second = CreateTexturedPage(width, height, seed: 835, channelRange: 128);
        using (Graphics graphics = Graphics.FromImage(second))
        {
            // Only the lower 110 of the 210 overlapping rows match. The unrelated rows
            // have low enough contrast for the whole-overlap average to pass.
            graphics.DrawImage(first, new Rectangle(0, 100, width, 110),
                new Rectangle(0, shift + 100, width, 110), GraphicsUnit.Pixel);
        }

        AssertSeamRejected(first, second, 65, "A matching panel cannot verify the predicted shift on its own.");
    }

    private static void MovingSidebarOnStationaryContentIsRejected()
    {
        const int width = 171, height = 600, shift = 35, sidebar = 30;
        using Bitmap first = CreateTexturedPage(width, height);
        // Even one coincidentally suitable composition row cannot prove that the page moved.
        for (int x = sidebar; x < width; x++)
        {
            first.SetPixel(x, height - 1, first.GetPixel(x, height - shift - 1));
        }

        using Bitmap second = (Bitmap)first.Clone();
        using (Graphics graphics = Graphics.FromImage(second))
        {
            graphics.DrawImage(first, new Rectangle(0, 0, sidebar, height - shift),
                new Rectangle(0, shift, sidebar, height - shift), GraphicsUnit.Pixel);
        }

        AssertSeamRejected(first, second, 65, "A moving sidebar beside a stationary page must not establish a shift.");
    }

    private static void AssertSeamRejected(Bitmap first, Bitmap second, double reportedPercent, string message)
    {
        try
        {
            using Bitmap result = VerticalStitcher.Stitch([
                new CapturedFrame(first, 0, 50), new CapturedFrame(second, reportedPercent, 50)]);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("confidently stitch", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static Bitmap CreateTexturedPage(int width, int height, int seed = 596, int channelRange = 256)
    {
        Random random = new(seed);
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(
                    random.Next(channelRange), random.Next(channelRange), random.Next(channelRange)));
            }
        }

        return bitmap;
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
