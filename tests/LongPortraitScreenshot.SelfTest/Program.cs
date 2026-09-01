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
            ExactShiftMeasurementOverridesBiasedScrollEstimate();
            FixedHeaderDoesNotCreateFalseOverlapAmbiguity();
            LocalizedFrameChangeDoesNotRejectCorrectSeam();
            LargeFrameChangeStillFailsVisualValidation();
            GloballyChangedFrameStillFailsVisualValidation();
            FourPixelSamplingShoulderDoesNotCreateSecondBasin();
            OnePixelSparseScrollRetainsEnoughEvidence();
            TrulyPeriodicContentRemainsAmbiguous();
            MemoryAssessmentUsesAvailableRamBoundary();
            StandardSizeLimitAcceptsBoundaryAndThrowsTypedAboveIt();
            StandardActualOverflowThrowsMeasuredTypedException();
            SafePortionUsesEveryRemainingWholeRow();
            PartialFinalAppendPreservesTopRowOrderWithoutGaps();
            CaptureGuardPolicyReturnsOnlySafePrefixes();
            FullModeBypassesApplicationPixelLimits();
            StitchHonorsCancellationToken();
            CaptureResultReportsPartialState();
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

    private static void ExactShiftMeasurementOverridesBiasedScrollEstimate()
    {
        const int width = 97;
        const int fullHeight = 677;
        const int viewportHeight = 181;
        const int actualShift = 137;
        const int deliberatelyBiasedPredictedShift = 149;

        using Bitmap page = CreateDeterministicBitmap(width, fullHeight);
        using Bitmap firstImage = page.Clone(
            new Rectangle(0, 0, width, viewportHeight),
            PixelFormat.Format32bppArgb);
        using Bitmap secondImage = page.Clone(
            new Rectangle(0, actualShift, width, viewportHeight),
            PixelFormat.Format32bppArgb);

        double scrollRange = fullHeight - viewportHeight;
        double viewSize = viewportHeight * 100.0 / fullHeight;
        CapturedFrame first = new(firstImage, 0, viewSize);
        CapturedFrame second = new(
            secondImage,
            deliberatelyBiasedPredictedShift * 100.0 / scrollRange,
            viewSize);

        int measuredShift = VerticalStitcher.MeasureVerticalShift(first, second, currentFrameIndex: 1);

        Require(measuredShift == actualShift,
            $"Exact seam measurement returned {measuredShift} rows; expected {actualShift} despite biased scroll metadata.");
    }

    private static void FixedHeaderDoesNotCreateFalseOverlapAmbiguity()
    {
        const int width = 300;
        const int pageHeight = 800;
        const int viewportHeight = 300;
        const int actualShift = 195;

        using Bitmap page = CreateSolidBitmap(
            width,
            pageHeight,
            Color.FromArgb(8, 12, 35));

        for (int sectionTop = 0; sectionTop < pageHeight; sectionTop += 170)
        {
            FillRectangle(
                page,
                new Rectangle(36, sectionTop + 36, 214, 79),
                Color.FromArgb(18, 25, 52));
            FillRectangle(
                page,
                new Rectangle(51, sectionTop + 49, 174, 5),
                Color.FromArgb(180, 185, 205));
            FillRectangle(
                page,
                new Rectangle(51, sectionTop + 67, 139, 3),
                Color.FromArgb(100, 110, 145));
        }

        using Bitmap firstImage = page.Clone(
            new Rectangle(0, 0, width, viewportHeight),
            PixelFormat.Format32bppArgb);
        using Bitmap secondImage = page.Clone(
            new Rectangle(0, actualShift, width, viewportHeight),
            PixelFormat.Format32bppArgb);

        Color headerColor = Color.FromArgb(2, 4, 24);
        FillRectangle(firstImage, new Rectangle(0, 0, width, 34), headerColor);
        FillRectangle(secondImage, new Rectangle(0, 0, width, 34), headerColor);
        FillRectangle(firstImage, new Rectangle(31, 14, 89, 6), Color.FromArgb(220, 225, 240));
        FillRectangle(secondImage, new Rectangle(31, 14, 89, 6), Color.FromArgb(220, 225, 240));

        double scrollRange = pageHeight - viewportHeight;
        double viewSize = viewportHeight * 100.0 / pageHeight;
        CapturedFrame first = new(firstImage, 0, viewSize);
        CapturedFrame second = new(
            secondImage,
            actualShift * 100.0 / scrollRange,
            viewSize);

        int measuredShift = VerticalStitcher.MeasureVerticalShift(first, second, currentFrameIndex: 1);

        Require(measuredShift == actualShift,
            $"Fixed browser chrome produced a {measuredShift}-row shift; expected {actualShift}.");
    }

    private static void LocalizedFrameChangeDoesNotRejectCorrectSeam()
    {
        const int width = 300;
        const int pageHeight = 800;
        const int viewportHeight = 300;
        const int actualShift = 195;
        Rectangle changedRegion = new(35, 18, 220, 8);

        using Bitmap page = CreateDeterministicBitmap(width, pageHeight);
        FillRectangle(
            page,
            new Rectangle(
                changedRegion.X,
                actualShift + changedRegion.Y,
                changedRegion.Width,
                changedRegion.Height),
            Color.Black);

        using Bitmap firstImage = page.Clone(
            new Rectangle(0, 0, width, viewportHeight),
            PixelFormat.Format32bppArgb);
        using Bitmap secondImage = page.Clone(
            new Rectangle(0, actualShift, width, viewportHeight),
            PixelFormat.Format32bppArgb);
        FillRectangle(secondImage, changedRegion, Color.White);

        double scrollRange = pageHeight - viewportHeight;
        double viewSize = viewportHeight * 100.0 / pageHeight;
        CapturedFrame first = new(firstImage, 0, viewSize);
        CapturedFrame second = new(
            secondImage,
            actualShift * 100.0 / scrollRange,
            viewSize);

        int measuredShift = VerticalStitcher.MeasureVerticalShift(first, second, currentFrameIndex: 1);

        Require(measuredShift == actualShift,
            $"A localized frame change produced a {measuredShift}-row shift; expected {actualShift}.");
    }

    private static void LargeFrameChangeStillFailsVisualValidation()
    {
        const int width = 300;
        const int pageHeight = 800;
        const int viewportHeight = 300;
        const int actualShift = 195;
        Rectangle changedRegion = new(20, 12, 250, 20);

        using Bitmap page = CreateDeterministicBitmap(width, pageHeight);
        FillRectangle(
            page,
            new Rectangle(
                changedRegion.X,
                actualShift + changedRegion.Y,
                changedRegion.Width,
                changedRegion.Height),
            Color.Black);

        using Bitmap firstImage = page.Clone(
            new Rectangle(0, 0, width, viewportHeight),
            PixelFormat.Format32bppArgb);
        using Bitmap secondImage = page.Clone(
            new Rectangle(0, actualShift, width, viewportHeight),
            PixelFormat.Format32bppArgb);
        FillRectangle(secondImage, changedRegion, Color.White);

        double scrollRange = pageHeight - viewportHeight;
        double viewSize = viewportHeight * 100.0 / pageHeight;
        CapturedFrame first = new(firstImage, 0, viewSize);
        CapturedFrame second = new(
            secondImage,
            actualShift * 100.0 / scrollRange,
            viewSize);

        InvalidOperationException? failure = null;
        try
        {
            _ = VerticalStitcher.MeasureVerticalShift(first, second, currentFrameIndex: 1);
        }
        catch (InvalidOperationException exception)
        {
            failure = exception;
        }

        Require(failure is not null
            && failure.Message.Contains("too much visual difference", StringComparison.Ordinal),
            "A large changed region must still fail visual seam validation.");
    }

    private static void GloballyChangedFrameStillFailsVisualValidation()
    {
        const int width = 97;
        const int pageHeight = 677;
        const int viewportHeight = 181;
        const int actualShift = 137;

        using Bitmap page = new(width, pageHeight, PixelFormat.Format32bppArgb);
        for (int y = 0; y < pageHeight; y++)
        {
            for (int x = 0; x < width; x++)
            {
                page.SetPixel(
                    x,
                    y,
                    Color.FromArgb(
                        (x * 31 + y * 17) % 200,
                        (x * 7 + y * 47) % 200,
                        (x * 19 + y * 71) % 200));
            }
        }

        using Bitmap firstImage = page.Clone(
            new Rectangle(0, 0, width, viewportHeight),
            PixelFormat.Format32bppArgb);
        using Bitmap secondImage = page.Clone(
            new Rectangle(0, actualShift, width, viewportHeight),
            PixelFormat.Format32bppArgb);
        for (int y = 0; y < viewportHeight; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Color source = secondImage.GetPixel(x, y);
                secondImage.SetPixel(
                    x,
                    y,
                    Color.FromArgb(source.R + 23, source.G + 23, source.B + 23));
            }
        }

        double scrollRange = pageHeight - viewportHeight;
        double viewSize = viewportHeight * 100.0 / pageHeight;
        CapturedFrame first = new(firstImage, 0, viewSize);
        CapturedFrame second = new(
            secondImage,
            actualShift * 100.0 / scrollRange,
            viewSize);

        InvalidOperationException? failure = null;
        try
        {
            _ = VerticalStitcher.MeasureVerticalShift(first, second, currentFrameIndex: 1);
        }
        catch (InvalidOperationException exception)
        {
            failure = exception;
        }

        Require(failure is not null
            && failure.Message.Contains("too much visual difference", StringComparison.Ordinal),
            "A globally changed frame must still fail visual seam validation.");
    }

    private static void FourPixelSamplingShoulderDoesNotCreateSecondBasin()
    {
        const int width = 97;
        const int pageHeight = 2_475;
        const int viewportHeight = 1_500;
        const int actualShift = 975;

        using Bitmap page = new(width, pageHeight, PixelFormat.Format32bppArgb);
        for (int y = 0; y < pageHeight; y++)
        {
            int gray = y / 10;
            Color rowColor = Color.FromArgb(gray, gray, gray);
            for (int x = 0; x < width; x++)
            {
                page.SetPixel(x, y, rowColor);
            }
        }

        using Bitmap firstImage = page.Clone(
            new Rectangle(0, 0, width, viewportHeight),
            PixelFormat.Format32bppArgb);
        using Bitmap secondImage = page.Clone(
            new Rectangle(0, actualShift, width, viewportHeight),
            PixelFormat.Format32bppArgb);

        double scrollRange = pageHeight - viewportHeight;
        double viewSize = viewportHeight * 100.0 / pageHeight;
        CapturedFrame first = new(firstImage, 0, viewSize);
        CapturedFrame second = new(
            secondImage,
            actualShift * 100.0 / scrollRange,
            viewSize);

        int measuredShift = VerticalStitcher.MeasureVerticalShift(first, second, currentFrameIndex: 1);

        Require(measuredShift == actualShift,
            $"A four-pixel sampling shoulder produced a {measuredShift}-row shift; expected {actualShift}.");
    }

    private static void OnePixelSparseScrollRetainsEnoughEvidence()
    {
        const int width = 97;
        const int pageHeight = 300;
        const int viewportHeight = 181;
        const int actualShift = 1;

        using Bitmap page = CreateSolidBitmap(
            width,
            pageHeight,
            Color.FromArgb(12, 16, 32));
        FillRectangle(
            page,
            new Rectangle(20, 51, 51, 1),
            Color.FromArgb(210, 220, 240));

        using Bitmap firstImage = page.Clone(
            new Rectangle(0, 0, width, viewportHeight),
            PixelFormat.Format32bppArgb);
        using Bitmap secondImage = page.Clone(
            new Rectangle(0, actualShift, width, viewportHeight),
            PixelFormat.Format32bppArgb);

        double scrollRange = pageHeight - viewportHeight;
        double viewSize = viewportHeight * 100.0 / pageHeight;
        CapturedFrame first = new(firstImage, 0, viewSize);
        CapturedFrame second = new(
            secondImage,
            actualShift * 100.0 / scrollRange,
            viewSize);

        int measuredShift = VerticalStitcher.MeasureVerticalShift(first, second, currentFrameIndex: 1);

        Require(measuredShift == actualShift,
            $"A sparse one-pixel final scroll produced a {measuredShift}-row shift; expected {actualShift}.");
    }

    private static void TrulyPeriodicContentRemainsAmbiguous()
    {
        const int width = 97;
        const int pageHeight = 800;
        const int viewportHeight = 300;
        const int actualShift = 195;

        using Bitmap page = new(width, pageHeight, PixelFormat.Format32bppArgb);
        for (int y = 0; y < pageHeight; y++)
        {
            Color stripeColor = y % 20 < 10
                ? Color.FromArgb(20, 30, 70)
                : Color.FromArgb(190, 200, 230);
            FillRectangle(page, new Rectangle(0, y, width, 1), stripeColor);
        }

        using Bitmap firstImage = page.Clone(
            new Rectangle(0, 0, width, viewportHeight),
            PixelFormat.Format32bppArgb);
        using Bitmap secondImage = page.Clone(
            new Rectangle(0, actualShift, width, viewportHeight),
            PixelFormat.Format32bppArgb);

        double scrollRange = pageHeight - viewportHeight;
        double viewSize = viewportHeight * 100.0 / pageHeight;
        CapturedFrame first = new(firstImage, 0, viewSize);
        CapturedFrame second = new(
            secondImage,
            actualShift * 100.0 / scrollRange,
            viewSize);

        InvalidOperationException? failure = null;
        try
        {
            _ = VerticalStitcher.MeasureVerticalShift(first, second, currentFrameIndex: 1);
        }
        catch (InvalidOperationException exception)
        {
            failure = exception;
        }

        Require(failure is not null
            && failure.Message.Contains("more than one overlap looked equally likely", StringComparison.Ordinal),
            "Exactly periodic content must remain rejected when several shifts are visually identical.");
    }

    private static void MemoryAssessmentUsesAvailableRamBoundary()
    {
        const ulong availableBytes = 8UL * 1024 * 1024 * 1024;
        long boundaryPixels = checked((long)(availableBytes / 32));
        const string prefix = "Given the available amount of RAM, this operation is: ";

        Require(
            CaptureMemoryAssessment.GetText(boundaryPixels, availableBytes)
                == prefix + "likely to succeed.",
            "A capture exactly at the available-RAM boundary should be described as likely to succeed.");
        Require(
            CaptureMemoryAssessment.GetText(boundaryPixels + 1, availableBytes)
                == prefix + "likely to be slow.",
            "A capture above the available-RAM boundary should be described as likely to be slow.");
        Require(
            CaptureMemoryAssessment.GetText(estimatedPixels: 1, availablePhysicalBytes: 0)
                == prefix + "likely to be slow.",
            "An unavailable RAM reading should use the conservative slow assessment.");
        Require(
            CaptureMemoryAssessment.GetText(long.MaxValue, ulong.MaxValue)
                == prefix + "likely to be slow.",
            "The RAM assessment must handle maximum input values without overflow.");
    }

    private static void StandardSizeLimitAcceptsBoundaryAndThrowsTypedAboveIt()
    {
        CaptureSizePolicy.EnsureStandardEstimateFits(CaptureSizePolicy.SafePixelLimit);

        CaptureSizeLimitExceededException? failure = null;
        try
        {
            CaptureSizePolicy.EnsureStandardEstimateFits(CaptureSizePolicy.SafePixelLimit + 1);
        }
        catch (CaptureSizeLimitExceededException exception)
        {
            failure = exception;
        }

        CaptureSizeLimitExceededException typedFailure = failure
            ?? throw new InvalidOperationException(
                "Standard mode did not throw its typed exception one pixel above the safety limit.");
        Require(typedFailure.EstimatedPixels == CaptureSizePolicy.SafePixelLimit + 1,
            "The typed size exception did not preserve the estimated pixel count.");
        Require(typedFailure.SafePixelLimit == CaptureSizePolicy.SafePixelLimit,
            "The typed size exception did not preserve the safe pixel limit.");
        Require(typedFailure.IsEstimate,
            "A Standard preflight failure must be identified as an estimate.");

        Rectangle exactLimitBounds = new(0, 0, 1_000, 1_000);
        long exactEstimate = CaptureSession.EstimateOutputPixels(exactLimitBounds, viewSize: 2.5);
        Require(exactEstimate == CaptureSizePolicy.SafePixelLimit,
            $"Expected a {CaptureSizePolicy.SafePixelLimit:N0}-pixel estimate; got {exactEstimate:N0}.");
        CaptureSession.EnsureCaptureModeCanStart(
            exactLimitBounds,
            viewSize: 2.5,
            CaptureMode.Standard);

        bool startupRejected = false;
        try
        {
            CaptureSession.EnsureCaptureModeCanStart(
                new Rectangle(0, 0, 1_000, 1_001),
                viewSize: 2.5,
                CaptureMode.Standard);
        }
        catch (CaptureSizeLimitExceededException)
        {
            startupRejected = true;
        }

        Require(startupRejected,
            "Standard mode did not reject an over-limit estimate during pre-scroll validation.");
    }

    private static void StandardActualOverflowThrowsMeasuredTypedException()
    {
        CaptureSizePolicy.EnsureStandardActualFits(CaptureSizePolicy.SafePixelLimit);

        CaptureSizeLimitExceededException? failure = null;
        try
        {
            CaptureSizePolicy.EnsureStandardActualFits(CaptureSizePolicy.SafePixelLimit + 1);
        }
        catch (CaptureSizeLimitExceededException exception)
        {
            failure = exception;
        }

        CaptureSizeLimitExceededException typedFailure = failure
            ?? throw new InvalidOperationException(
                "Standard mode did not throw its typed exception for measured overflow.");
        Require(!typedFailure.IsEstimate,
            "A measured Standard overflow was incorrectly identified as a preflight estimate.");
        Require(typedFailure.EstimatedPixels == CaptureSizePolicy.SafePixelLimit + 1,
            "The measured overflow exception did not retain its exact pixel count.");
    }

    private static void SafePortionUsesEveryRemainingWholeRow()
    {
        int exactBoundaryRows = CaptureSizePolicy.GetSafeRowsToAppend(
            width: 1_000,
            currentHeight: 39_999,
            availableRows: 2,
            out long exactBoundaryHeight,
            out long exactBoundaryPixels);
        Require(exactBoundaryRows == 1
            && exactBoundaryHeight == 40_000
            && exactBoundaryPixels == CaptureSizePolicy.SafePixelLimit,
            "SafePortion did not truncate a crossing frame at the exact 40,000,000-pixel boundary.");

        int acceptedRows = CaptureSizePolicy.GetSafeRowsToAppend(
            width: 3,
            currentHeight: 13_333_330,
            availableRows: 10,
            out long acceptedHeight,
            out long acceptedPixels);

        Require(acceptedRows == 3 && acceptedHeight == 13_333_333,
            "SafePortion did not consume every remaining complete full-width row.");
        Require(acceptedPixels == 39_999_999,
            "SafePortion should leave only the one unusable remainder pixel when width is three.");

        int zeroRows = CaptureSizePolicy.GetSafeRowsToAppend(
            width: 3,
            currentHeight: acceptedHeight,
            availableRows: 1,
            out long unchangedHeight,
            out long unchangedPixels);
        Require(zeroRows == 0
            && unchangedHeight == acceptedHeight
            && unchangedPixels == acceptedPixels,
            "SafePortion accepted a frame row after exhausting the full-width row budget.");
    }

    private static void PartialFinalAppendPreservesTopRowOrderWithoutGaps()
    {
        const int width = 40;
        const int pageHeight = 60;
        const int viewportHeight = 40;
        const int fullShift = 10;
        const int partialRows = 3;

        using Bitmap page = CreateDeterministicBitmap(width, pageHeight);
        using Bitmap firstImage = page.Clone(
            new Rectangle(0, 0, width, viewportHeight),
            PixelFormat.Format32bppArgb);
        using Bitmap secondImage = page.Clone(
            new Rectangle(0, fullShift, width, viewportHeight),
            PixelFormat.Format32bppArgb);
        using Bitmap expected = page.Clone(
            new Rectangle(0, 0, width, viewportHeight + partialRows),
            PixelFormat.Format32bppArgb);

        double viewSize = viewportHeight * 100.0 / pageHeight;
        CapturedFrame[] frames =
        [
            new(firstImage, 0, viewSize),
            // Deliberately unusable scroll metadata proves the retained measured shift is reused.
            new(secondImage, 0, viewSize)
        ];

        using Bitmap actual = VerticalStitcher.Stitch(
            frames,
            maxPixels: width * (viewportHeight + partialRows),
            cancellationToken: CancellationToken.None,
            finalFrameRowsToAppend: partialRows,
            measuredVerticalShifts: [fullShift]);

        AssertBitmapsEqual(expected, actual, "partial final append without skipped rows");
    }

    private static void CaptureGuardPolicyReturnsOnlySafePrefixes()
    {
        Require(CaptureSizePolicy.ShouldReturnPartialAtCaptureGuard(CaptureMode.SafePortion, 1),
            "SafePortion must return its valid prefix when a capture guard is reached.");
        Require(!CaptureSizePolicy.ShouldReturnPartialAtCaptureGuard(CaptureMode.SafePortion, 0),
            "SafePortion cannot return a partial result before capturing one valid frame.");
        Require(!CaptureSizePolicy.ShouldReturnPartialAtCaptureGuard(CaptureMode.Standard, 1),
            "Standard mode must preserve capture-guard failures.");
        Require(!CaptureSizePolicy.ShouldReturnPartialAtCaptureGuard(CaptureMode.Full, 256),
            "Full mode must preserve the 256-frame runaway guard.");
    }

    private static void FullModeBypassesApplicationPixelLimits()
    {
        Rectangle overLimitBounds = new(0, 0, 1_000, 1_001);
        CaptureSession.EnsureCaptureModeCanStart(
            overLimitBounds,
            viewSize: 2.5,
            CaptureMode.Full);

        using Bitmap source = CreateDeterministicBitmap(width: 10, height: 10);
        CapturedFrame frame = new(source, 0, 100);

        bool limitedStitchRejected = false;
        try
        {
            using Bitmap _ = VerticalStitcher.Stitch([frame], maxPixels: 99);
        }
        catch (InvalidOperationException)
        {
            limitedStitchRejected = true;
        }

        Require(limitedStitchRejected,
            "The stitcher's ordinary pixel limit did not reject an oversized result.");

        using Bitmap noLimitResult = VerticalStitcher.Stitch([frame], maxPixels: long.MaxValue);
        AssertBitmapsEqual(source, noLimitResult, "Full mode no-limit stitch");
    }

    private static void StitchHonorsCancellationToken()
    {
        using Bitmap source = CreateDeterministicBitmap(width: 40, height: 40);
        CapturedFrame frame = new(source, 0, 100);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        bool cancelled = false;
        try
        {
            using Bitmap _ = VerticalStitcher.Stitch(
                [frame],
                MaximumOutputPixels,
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Require(cancelled, "The token-aware stitch overload ignored a pre-cancelled token.");
    }

    private static void CaptureResultReportsPartialState()
    {
        using CaptureResult complete = new(
            CreateDeterministicBitmap(width: 5, height: 7),
            "Complete",
            frameCount: 1);
        using CaptureResult partial = new(
            CreateDeterministicBitmap(width: 5, height: 7),
            "Partial",
            frameCount: 3,
            isPartial: true);

        Require(!complete.IsPartial, "The compatible three-argument result constructor must default to complete.");
        Require(partial.IsPartial, "A SafePortion result did not retain its partial marker.");
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
