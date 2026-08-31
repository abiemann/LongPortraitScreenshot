using System.Drawing.Imaging;
using LongPortraitScreenshot.Capture;
using LongPortraitScreenshot.Imaging;

namespace LongPortraitScreenshot.SelfTest;

internal static class Program
{
    private const long MaximumOutputPixels = 64_000_000;

    private static int Main()
    {
        try
        {
            StitchOverlappingFramesReconstructsOriginal();
            StitchSingleFrameReturnsIndependentClone();
            CropRightRemovesExpectedPixelsAndPreservesDpi();
            CropRightKeepsOnePixelForNarrowImages();
            TrimEmptySpaceFromBothSidesKeepsFivePixelMargins();
            TrimEmptySpaceLeavesSideWithEdgeContentUncropped();
            TrimEmptySpaceLeavesUniformImageUnchanged();
            TrimEmptySpacePreservesPixelsAndDpi();
            ScrollSettleWaitsForDelayedMovement();
            ScrollSettleRestartsWhenRenderCheckMoves();
            ScrollSettleRejectsNeverMovingRequest();
            ScrollSettleAllowsAlreadySatisfiedRequest();

            Console.WriteLine("All LongPortraitScreenshot self-tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("LongPortraitScreenshot self-test failed:");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void StitchOverlappingFramesReconstructsOriginal()
    {
        const int width = 97;
        const int fullHeight = 677;
        const int viewportHeight = 181;
        int[] scrollOffsets = [0, 137, 274, 411, 496];

        using Bitmap expected = CreateDeterministicBitmap(width, fullHeight);
        List<Bitmap> frameBitmaps = [];

        try
        {
            double maximumScrollOffset = fullHeight - viewportHeight;
            double verticalViewSize = viewportHeight * 100.0 / fullHeight;
            List<CapturedFrame> frames = [];

            foreach (int scrollOffset in scrollOffsets)
            {
                Bitmap bitmap = expected.Clone(
                    new Rectangle(0, scrollOffset, width, viewportHeight),
                    PixelFormat.Format32bppArgb);

                frameBitmaps.Add(bitmap);
                double scrollPercent = scrollOffset * 100.0 / maximumScrollOffset;
                frames.Add(new CapturedFrame(bitmap, scrollPercent, verticalViewSize));
            }

            int regularMovement = scrollOffsets[1] - scrollOffsets[0];
            int finalMovement = scrollOffsets[^1] - scrollOffsets[^2];
            Require(finalMovement < regularMovement,
                "Test setup must include a shorter final scroll movement.");

            using Bitmap actual = VerticalStitcher.Stitch(frames, MaximumOutputPixels);
            AssertBitmapsEqual(expected, actual, "overlapping multi-frame stitch");
        }
        finally
        {
            foreach (Bitmap bitmap in frameBitmaps)
            {
                bitmap.Dispose();
            }
        }
    }

    private static void StitchSingleFrameReturnsIndependentClone()
    {
        using Bitmap source = CreateDeterministicBitmap(width: 43, height: 59);
        Color originalFirstPixel = source.GetPixel(0, 0);
        CapturedFrame frame = new(source, 0, 100);

        using Bitmap actual = VerticalStitcher.Stitch([frame], MaximumOutputPixels);

        Require(!ReferenceEquals(source, actual),
            "A single-frame stitch must return a clone, not the input bitmap.");
        AssertBitmapsEqual(source, actual, "single-frame stitch");

        source.SetPixel(0, 0, Color.Magenta);
        Require(actual.GetPixel(0, 0).ToArgb() == originalFirstPixel.ToArgb(),
            "The stitched bitmap changed after the source bitmap was modified.");
    }

    private static void CropRightRemovesExpectedPixelsAndPreservesDpi()
    {
        const int cropWidth = 26;
        using Bitmap source = CreateDeterministicBitmap(width: 80, height: 37);
        source.SetResolution(144, 144);

        using Bitmap actual = ScrollbarCropper.CropRight(source, cropWidth);

        Require(actual.Width == source.Width - cropWidth && actual.Height == source.Height,
            $"Right crop produced {actual.Width}x{actual.Height}; expected " +
            $"{source.Width - cropWidth}x{source.Height}.");
        Require(Math.Abs(actual.HorizontalResolution - source.HorizontalResolution) < 0.01f
            && Math.Abs(actual.VerticalResolution - source.VerticalResolution) < 0.01f,
            "Right crop did not preserve the source bitmap DPI.");
        AssertLeadingPixelsEqual(source, actual, "26-pixel right crop");
    }

    private static void CropRightKeepsOnePixelForNarrowImages()
    {
        using Bitmap source = CreateDeterministicBitmap(width: 8, height: 19);
        using Bitmap actual = ScrollbarCropper.CropRight(source, cropWidth: 26);

        Require(actual.Width == 1 && actual.Height == source.Height,
            $"Guarded right crop produced {actual.Width}x{actual.Height}; expected 1x{source.Height}.");
        AssertLeadingPixelsEqual(source, actual, "guarded right crop");
    }

    private static void TrimEmptySpaceFromBothSidesKeepsFivePixelMargins()
    {
        Color background = Color.FromArgb(255, 25, 25, 25);
        using Bitmap source = CreateSolidBitmap(width: 1_747, height: 20, background);
        FillRectangle(source, new Rectangle(27, 4, 1_013, 12), Color.CornflowerBlue);

        using Bitmap actual = EmptySpaceCropper.Trim(source);

        Require(actual.Width == 1_023 && actual.Height == source.Height,
            $"Two-sided empty-space crop produced {actual.Width}x{actual.Height}; " +
            $"expected 1023x{source.Height}.");
        AssertRegionPixelsEqual(
            source,
            actual,
            sourceLeft: 22,
            "two-sided crop with five-pixel margins");
        Require(actual.GetPixel(4, 4).ToArgb() == background.ToArgb()
            && actual.GetPixel(5, 4).ToArgb() == Color.CornflowerBlue.ToArgb()
            && actual.GetPixel(1_017, 4).ToArgb() == Color.CornflowerBlue.ToArgb()
            && actual.GetPixel(1_018, 4).ToArgb() == background.ToArgb(),
            "The crop did not retain exactly five background pixels beside the content.");
    }

    private static void TrimEmptySpaceLeavesSideWithEdgeContentUncropped()
    {
        Color background = Color.FromArgb(255, 24, 24, 24);
        using Bitmap source = CreateSolidBitmap(width: 40, height: 12, background);
        FillRectangle(source, new Rectangle(8, 2, 12, 8), Color.Goldenrod);
        source.SetPixel(0, 0, Color.Magenta);

        using Bitmap actual = EmptySpaceCropper.Trim(source);

        Require(actual.Width == 25 && actual.Height == source.Height,
            $"One-sided empty-space crop produced {actual.Width}x{actual.Height}; expected 25x{source.Height}.");
        AssertRegionPixelsEqual(source, actual, sourceLeft: 0, "edge content blocks the left crop");
    }

    private static void TrimEmptySpaceLeavesUniformImageUnchanged()
    {
        using Bitmap source = CreateSolidBitmap(
            width: 31,
            height: 17,
            Color.FromArgb(123, 10, 20, 30));

        using Bitmap actual = EmptySpaceCropper.Trim(source);

        Require(!ReferenceEquals(source, actual),
            "A uniform image must still produce an independently owned bitmap.");
        AssertBitmapsEqual(source, actual, "uniform image remains full width");
    }

    private static void TrimEmptySpacePreservesPixelsAndDpi()
    {
        Color background = Color.FromArgb(77, 7, 9, 11);
        using Bitmap source = CreateSolidBitmap(width: 29, height: 13, background);
        source.SetResolution(144, 120);

        for (int y = 2; y <= 10; y++)
        {
            for (int x = 9; x <= 18; x++)
            {
                int alpha = 32 + ((x * 19 + y * 13) % 224);
                source.SetPixel(
                    x,
                    y,
                    Color.FromArgb(alpha, (x * 17) & 0xff, (y * 23) & 0xff, (x * y * 7) & 0xff));
            }
        }

        using Bitmap actual = EmptySpaceCropper.Trim(source);

        Require(actual.Width == 20 && actual.Height == source.Height,
            $"Pixel-preservation crop produced {actual.Width}x{actual.Height}; expected 20x{source.Height}.");
        Require(Math.Abs(actual.HorizontalResolution - source.HorizontalResolution) < 0.01f
            && Math.Abs(actual.VerticalResolution - source.VerticalResolution) < 0.01f,
            "Empty-space crop did not preserve the source bitmap DPI.");
        AssertRegionPixelsEqual(source, actual, sourceLeft: 4, "empty-space crop pixel preservation");
    }

    private static void ScrollSettleWaitsForDelayedMovement()
    {
        ScrollSettleTracker tracker = new(0, 20, 0, minimumMovementPercent: 0.0001);

        Require(!tracker.Observe(0), "An unchanged first poll must not settle a pending scroll.");
        Require(!tracker.Observe(0), "An unchanged second poll must not settle a pending scroll.");
        Require(!tracker.Observe(0), "A delayed scroll must retain its full startup allowance.");
        Require(!tracker.HasDepartedStart, "The tracker incorrectly reported movement before it occurred.");
        Require(!tracker.Observe(20), "The first moving poll must restart stability tracking.");
        Require(!tracker.Observe(20), "One stable poll is insufficient after movement.");
        Require(tracker.Observe(20), "Two stable polls should request a render confirmation.");
        Require(tracker.ConfirmAfterRender(20), "An unchanged render confirmation should settle.");
    }

    private static void ScrollSettleRestartsWhenRenderCheckMoves()
    {
        ScrollSettleTracker tracker = new(0, 20, 20, minimumMovementPercent: 0.0001);

        Require(!tracker.Observe(20), "One stable poll is insufficient.");
        Require(tracker.Observe(20), "Two stable polls should request a render confirmation.");
        Require(!tracker.ConfirmAfterRender(23),
            "Movement during the render delay must restart stability tracking.");
        Require(!tracker.Observe(23), "The first poll after render movement must not settle.");
        Require(tracker.Observe(23), "The second stable poll after render movement should be confirmable.");
        Require(tracker.ConfirmAfterRender(23), "The second unchanged render check should settle.");
    }

    private static void ScrollSettleRejectsNeverMovingRequest()
    {
        ScrollSettleTracker tracker = new(0, 20, 0, minimumMovementPercent: 0.0001);

        for (int poll = 0; poll < 50; poll++)
        {
            Require(!tracker.Observe(0), "A nontrivial request settled without any movement.");
        }

        Require(!tracker.HasDepartedStart, "A stationary request incorrectly reported movement.");
    }

    private static void ScrollSettleAllowsAlreadySatisfiedRequest()
    {
        ScrollSettleTracker tracker = new(0, 0, 0, minimumMovementPercent: 0.0001);

        Require(!tracker.Observe(0), "One stable poll is insufficient for an already satisfied request.");
        Require(tracker.Observe(0), "An already satisfied request should settle after two stable polls.");
        Require(tracker.ConfirmAfterRender(0),
            "An already satisfied request should pass an unchanged render confirmation.");
    }

    private static Bitmap CreateSolidBitmap(int width, int height, Color color)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        return bitmap;
    }

    private static void FillRectangle(Bitmap bitmap, Rectangle rectangle, Color color)
    {
        using Graphics graphics = Graphics.FromImage(bitmap);
        using SolidBrush brush = new(color);
        graphics.FillRectangle(brush, rectangle);
    }

    private static Bitmap CreateDeterministicBitmap(int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int red = (x * 31 + y * 17 + (y >> 8) * 23) & 0xff;
                int green = (x * 7 + y * 47 + (y >> 4) * 13) & 0xff;
                int blue = (x * 19 + y * 71 + (y >> 7) * 29) & 0xff;
                bitmap.SetPixel(x, y, Color.FromArgb(255, red, green, blue));
            }
        }

        return bitmap;
    }

    private static void AssertBitmapsEqual(Bitmap expected, Bitmap actual, string scenario)
    {
        Require(actual.Width == expected.Width && actual.Height == expected.Height,
            $"{scenario}: expected {expected.Width}x{expected.Height}, " +
            $"but got {actual.Width}x{actual.Height}.");

        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                int expectedArgb = expected.GetPixel(x, y).ToArgb();
                int actualArgb = actual.GetPixel(x, y).ToArgb();

                if (actualArgb != expectedArgb)
                {
                    throw new InvalidOperationException(
                        $"{scenario}: pixel ({x}, {y}) was 0x{actualArgb:X8}; " +
                        $"expected 0x{expectedArgb:X8}.");
                }
            }
        }
    }

    private static void AssertLeadingPixelsEqual(Bitmap expected, Bitmap actual, string scenario)
    {
        Require(actual.Width <= expected.Width && actual.Height == expected.Height,
            $"{scenario}: source and result dimensions are incompatible.");

        for (int y = 0; y < actual.Height; y++)
        {
            for (int x = 0; x < actual.Width; x++)
            {
                int expectedArgb = expected.GetPixel(x, y).ToArgb();
                int actualArgb = actual.GetPixel(x, y).ToArgb();

                if (actualArgb != expectedArgb)
                {
                    throw new InvalidOperationException(
                        $"{scenario}: pixel ({x}, {y}) was 0x{actualArgb:X8}; " +
                        $"expected 0x{expectedArgb:X8}.");
                }
            }
        }
    }

    private static void AssertRegionPixelsEqual(
        Bitmap expected,
        Bitmap actual,
        int sourceLeft,
        string scenario)
    {
        Require(sourceLeft >= 0
            && sourceLeft + actual.Width <= expected.Width
            && actual.Height == expected.Height,
            $"{scenario}: source and result dimensions are incompatible.");

        for (int y = 0; y < actual.Height; y++)
        {
            for (int x = 0; x < actual.Width; x++)
            {
                int expectedArgb = expected.GetPixel(sourceLeft + x, y).ToArgb();
                int actualArgb = actual.GetPixel(x, y).ToArgb();

                if (actualArgb != expectedArgb)
                {
                    throw new InvalidOperationException(
                        $"{scenario}: pixel ({x}, {y}) was 0x{actualArgb:X8}; " +
                        $"expected source pixel ({sourceLeft + x}, {y}) to be 0x{expectedArgb:X8}.");
                }
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
