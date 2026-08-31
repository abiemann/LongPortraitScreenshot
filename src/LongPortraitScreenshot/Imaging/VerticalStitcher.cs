using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace LongPortraitScreenshot.Imaging;

public static class VerticalStitcher
{
    private const double MaximumAcceptablePixelError = 22.0;

    public static Bitmap Stitch(
        IReadOnlyList<CapturedFrame> frames,
        long maxPixels = 40_000_000,
        bool removeRepeatedFixedOverlays = false) =>
        Stitch(
            frames,
            maxPixels,
            removeRepeatedFixedOverlays,
            CancellationToken.None,
            finalFrameRowsToAppend: null);

    public static Bitmap Stitch(
        IReadOnlyList<CapturedFrame> frames,
        long maxPixels,
        bool removeRepeatedFixedOverlays,
        CancellationToken cancellationToken) =>
        Stitch(
            frames,
            maxPixels,
            removeRepeatedFixedOverlays,
            cancellationToken,
            finalFrameRowsToAppend: null);

    internal static Bitmap Stitch(
        IReadOnlyList<CapturedFrame> frames,
        long maxPixels,
        bool removeRepeatedFixedOverlays,
        CancellationToken cancellationToken,
        int? finalFrameRowsToAppend) =>
        Stitch(
            frames,
            maxPixels,
            removeRepeatedFixedOverlays,
            cancellationToken,
            finalFrameRowsToAppend,
            measuredVerticalShifts: null);

    internal static Bitmap Stitch(
        IReadOnlyList<CapturedFrame> frames,
        long maxPixels,
        bool removeRepeatedFixedOverlays,
        CancellationToken cancellationToken,
        int? finalFrameRowsToAppend,
        IReadOnlyList<int>? measuredVerticalShifts)
    {
        ArgumentNullException.ThrowIfNull(frames);
        cancellationToken.ThrowIfCancellationRequested();

        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one captured frame is required.", nameof(frames));
        }

        if (maxPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPixels), "The output pixel limit must be positive.");
        }

        (int width, int height) = ValidateFrames(frames, cancellationToken);
        if (measuredVerticalShifts is not null
            && measuredVerticalShifts.Count != frames.Count - 1)
        {
            throw new ArgumentException(
                "The retained vertical-shift count must be one less than the captured frame count.",
                nameof(measuredVerticalShifts));
        }

        if (frames.Count == 1)
        {
            if (finalFrameRowsToAppend is not null)
            {
                throw new ArgumentException(
                    "A partial final append requires at least two captured frames.",
                    nameof(finalFrameRowsToAppend));
            }

            EnsureOutputFits(width, height, maxPixels);
            return CloneAsArgb(frames[0].Image, cancellationToken);
        }

        int[] shifts = new int[frames.Count - 1];
        int[] rowsToAppend = new int[frames.Count - 1];
        long outputHeight = height;

        for (int index = 1; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int shift = measuredVerticalShifts is null
                ? MeasureVerticalShift(
                    frames[index - 1],
                    frames[index],
                    index,
                    cancellationToken)
                : measuredVerticalShifts[index - 1];
            int maximumMeasuredShift = height - Math.Max(24, height / 8);
            if (shift <= 0 || shift > maximumMeasuredShift)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(measuredVerticalShifts),
                    $"Retained shift {index} is outside the supported overlap range.");
            }

            int appendRows = index == frames.Count - 1 && finalFrameRowsToAppend is int requestedRows
                ? requestedRows
                : shift;
            if (appendRows <= 0 || appendRows > shift)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(finalFrameRowsToAppend),
                    "The partial append must contain at least one row and cannot exceed the measured shift.");
            }

            shifts[index - 1] = shift;
            rowsToAppend[index - 1] = appendRows;
            outputHeight = checked(outputHeight + appendRows);
            EnsureOutputFits(width, outputHeight, maxPixels);
        }

        if (outputHeight > int.MaxValue)
        {
            throw new InvalidOperationException(
                "The stitched screenshot is taller than Windows bitmap APIs can represent. Capture a smaller section.");
        }

        Bitmap output;
        try
        {
            output = new Bitmap(width, (int)outputHeight, PixelFormat.Format32bppArgb);
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException or OutOfMemoryException)
        {
            throw new InvalidOperationException(
                $"Windows could not allocate the {width} x {outputHeight} stitched image. Capture a smaller section.",
                exception);
        }

        try
        {
            using (Graphics graphics = Graphics.FromImage(output))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.None;
                graphics.DrawImageUnscaled(frames[0].Image, 0, 0);
                cancellationToken.ThrowIfCancellationRequested();

                int destinationY = height;
                for (int index = 1; index < frames.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int newRows = rowsToAppend[index - 1];
                    int sourceY = height - shifts[index - 1];
                    graphics.DrawImage(
                        frames[index].Image,
                        new Rectangle(0, destinationY, width, newRows),
                        0,
                        sourceY,
                        width,
                        newRows,
                        GraphicsUnit.Pixel);
                    destinationY += newRows;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (removeRepeatedFixedOverlays)
            {
                IReadOnlyList<int> overlayShifts = shifts;
                IReadOnlyList<CapturedFrame> overlayFrames = frames;
                if (finalFrameRowsToAppend is not null)
                {
                    int fullTransitionCount = shifts.Length - 1;
                    int[] fullShifts = new int[fullTransitionCount];
                    CapturedFrame[] fullFrames = new CapturedFrame[fullTransitionCount + 1];
                    Array.Copy(shifts, fullShifts, fullTransitionCount);
                    for (int index = 0; index < fullFrames.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        fullFrames[index] = frames[index];
                    }

                    overlayShifts = fullShifts;
                    overlayFrames = fullFrames;
                }

                RepeatedOverlayRemover.Remove(
                    output,
                    height,
                    overlayShifts,
                    overlayFrames,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static (int Width, int Height) ValidateFrames(
        IReadOnlyList<CapturedFrame> frames,
        CancellationToken cancellationToken)
    {
        CapturedFrame first = frames[0]
            ?? throw new ArgumentException("Captured frames cannot contain null entries.", nameof(frames));

        ArgumentNullException.ThrowIfNull(first.Image);
        int width = first.Image.Width;
        int height = first.Image.Height;

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Captured frames must have a positive width and height.", nameof(frames));
        }

        for (int index = 0; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedFrame frame = frames[index]
                ?? throw new ArgumentException($"Captured frame {index + 1} is null.", nameof(frames));

            ArgumentNullException.ThrowIfNull(frame.Image);
            if (frame.Image.Width != width || frame.Image.Height != height)
            {
                throw new InvalidOperationException(
                    $"The selected control changed size between frames 1 and {index + 1}. Keep the window stationary and retry.");
            }

            if (!double.IsFinite(frame.ScrollPercent) || !double.IsFinite(frame.ViewSize))
            {
                throw new InvalidOperationException(
                    $"The target reported invalid scroll information for frame {index + 1}. Try a different scrolling container.");
            }
        }

        return (width, height);
    }

    internal static int MeasureVerticalShift(
        CapturedFrame previous,
        CapturedFrame current,
        int currentFrameIndex) =>
        MeasureVerticalShift(
            previous,
            current,
            currentFrameIndex,
            CancellationToken.None);

    internal static int MeasureVerticalShift(
        CapturedFrame previous,
        CapturedFrame current,
        int currentFrameIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (currentFrameIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(currentFrameIndex));
        }

        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous.Image);
        ArgumentNullException.ThrowIfNull(current.Image);

        int width = previous.Image.Width;
        int height = previous.Image.Height;
        if (current.Image.Width != width || current.Image.Height != height)
        {
            throw new InvalidOperationException(
                $"The selected control changed size between frames {currentFrameIndex} and " +
                $"{currentFrameIndex + 1}. Keep the window stationary and retry.");
        }

        if (!double.IsFinite(previous.ScrollPercent)
            || !double.IsFinite(current.ScrollPercent)
            || !double.IsFinite(previous.ViewSize)
            || !double.IsFinite(current.ViewSize))
        {
            throw SeamFailure(
                currentFrameIndex,
                "the target reported invalid scroll information");
        }

        double percentDelta = current.ScrollPercent - previous.ScrollPercent;
        double viewSize = (previous.ViewSize + current.ViewSize) / 2.0;

        if (percentDelta <= 0.0001)
        {
            throw SeamFailure(
                currentFrameIndex,
                "the target did not report forward scroll movement");
        }

        if (viewSize <= 0.0 || viewSize >= 100.0)
        {
            throw SeamFailure(
                currentFrameIndex,
                $"the target reported an invalid vertical view size ({viewSize:0.###}%)");
        }

        double scrollRangePixels = height * ((100.0 / viewSize) - 1.0);
        int predictedShift = (int)Math.Round((percentDelta / 100.0) * scrollRangePixels);

        int minimumOverlap = Math.Max(24, height / 8);
        int maximumShift = height - minimumOverlap;
        if (maximumShift < 1)
        {
            throw SeamFailure(currentFrameIndex, "the captured viewport is too short to establish an overlap");
        }

        predictedShift = Math.Clamp(predictedShift, 1, maximumShift);
        int searchRadius = Math.Max(18, (int)Math.Ceiling(height * 0.12));
        int minimumShift = Math.Max(1, predictedShift - searchRadius);
        int maximumSearchShift = Math.Min(maximumShift, predictedShift + searchRadius);

        PixelBuffer previousPixels = PixelBuffer.Create(previous.Image, cancellationToken);
        PixelBuffer currentPixels = PixelBuffer.Create(current.Image, cancellationToken);

        int ignoredRightPixels = Math.Min(Math.Max(18, width / 40), Math.Max(0, width / 4));
        int firstX = Math.Min(3, Math.Max(0, width - 1));
        int exclusiveLastX = width - ignoredRightPixels;
        if (exclusiveLastX - firstX < 12)
        {
            throw SeamFailure(currentFrameIndex, "the selected control is too narrow to compare its content reliably");
        }

        int candidateCount = maximumSearchShift - minimumShift + 1;
        double[] scores = new double[candidateCount];
        int bestShift = -1;
        double bestScore = double.PositiveInfinity;

        for (int shift = minimumShift; shift <= maximumSearchShift; shift++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double score = CalculateScore(
                previousPixels,
                currentPixels,
                shift,
                firstX,
                exclusiveLastX,
                cancellationToken);
            scores[shift - minimumShift] = score;

            bool isBetter = score < bestScore - 0.0001;
            bool isEquivalentButCloser = Math.Abs(score - bestScore) <= 0.0001
                && Math.Abs(shift - predictedShift) < Math.Abs(bestShift - predictedShift);
            if (isBetter || isEquivalentButCloser)
            {
                bestScore = score;
                bestShift = shift;
            }
        }

        if (bestShift < 1 || bestScore > MaximumAcceptablePixelError)
        {
            throw SeamFailure(
                currentFrameIndex,
                $"the best overlap had too much visual difference (error {bestScore:0.##})");
        }

        double secondBasinScore = double.PositiveInfinity;
        for (int shift = minimumShift; shift <= maximumSearchShift; shift++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Math.Abs(shift - bestShift) <= 3)
            {
                continue;
            }

            secondBasinScore = Math.Min(secondBasinScore, scores[shift - minimumShift]);
        }

        double requiredConfidenceGap = Math.Max(0.45, bestScore * 0.08);
        if (double.IsFinite(secondBasinScore) && secondBasinScore - bestScore < requiredConfidenceGap)
        {
            throw SeamFailure(
                currentFrameIndex,
                $"more than one overlap looked equally likely (best error {bestScore:0.##}, alternate {secondBasinScore:0.##})");
        }

        return bestShift;
    }

    private static double CalculateScore(
        PixelBuffer previous,
        PixelBuffer current,
        int shift,
        int firstX,
        int exclusiveLastX,
        CancellationToken cancellationToken)
    {
        int overlap = previous.Height - shift;
        int verticalMargin = Math.Min(6, Math.Max(0, overlap / 10));
        int availableRows = overlap - (verticalMargin * 2);
        if (availableRows <= 0)
        {
            return double.PositiveInfinity;
        }

        int availableColumns = exclusiveLastX - firstX;
        int rowSamples = Math.Min(96, availableRows);
        int columnSamples = Math.Min(80, availableColumns);
        long totalDifference = 0;
        int comparedChannels = 0;

        for (int rowIndex = 0; rowIndex < rowSamples; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rowOffset = rowSamples == 1
                ? 0
                : (int)((long)rowIndex * (availableRows - 1) / (rowSamples - 1));
            int currentY = verticalMargin + rowOffset;
            int previousY = currentY + shift;

            for (int columnIndex = 0; columnIndex < columnSamples; columnIndex++)
            {
                int columnOffset = columnSamples == 1
                    ? 0
                    : (int)((long)columnIndex * (availableColumns - 1) / (columnSamples - 1));
                int x = firstX + columnOffset;

                int previousOffset = previous.GetOffset(x, previousY);
                int currentOffset = current.GetOffset(x, currentY);
                totalDifference += Math.Abs(previous.Pixels[previousOffset] - current.Pixels[currentOffset]);
                totalDifference += Math.Abs(previous.Pixels[previousOffset + 1] - current.Pixels[currentOffset + 1]);
                totalDifference += Math.Abs(previous.Pixels[previousOffset + 2] - current.Pixels[currentOffset + 2]);
                comparedChannels += 3;
            }
        }

        return comparedChannels == 0
            ? double.PositiveInfinity
            : (double)totalDifference / comparedChannels;
    }

    private static InvalidOperationException SeamFailure(int currentFrameIndex, string reason)
    {
        return new InvalidOperationException(
            $"Could not confidently stitch frames {currentFrameIndex} and {currentFrameIndex + 1}: {reason}. " +
            "Keep the target unobscured and stationary, disable animations if possible, and retry.");
    }

    private static void EnsureOutputFits(int width, long height, long maxPixels)
    {
        long pixelCount;
        try
        {
            pixelCount = checked((long)width * height);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException("The stitched screenshot dimensions are too large.", exception);
        }

        if (pixelCount > maxPixels)
        {
            throw new InvalidOperationException(
                $"The stitched screenshot would contain {pixelCount:N0} pixels, exceeding the {maxPixels:N0}-pixel safety limit. " +
                "Select a narrower or shorter scrolling control.");
        }
    }

    private static Bitmap CloneAsArgb(Bitmap source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Bitmap clone = new(source.Width, source.Height, PixelFormat.Format32bppArgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(clone);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImageUnscaled(source, 0, 0);
            cancellationToken.ThrowIfCancellationRequested();
            return clone;
        }
        catch
        {
            clone.Dispose();
            throw;
        }
    }

    private sealed class PixelBuffer
    {
        private PixelBuffer(byte[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
        }

        public byte[] Pixels { get; }

        public int Width { get; }

        public int Height { get; }

        public int GetOffset(int x, int y) => ((y * Width) + x) * 4;

        public static PixelBuffer Create(Bitmap source, CancellationToken cancellationToken)
        {
            using Bitmap normalized = CloneAsArgb(source, cancellationToken);
            Rectangle bounds = new(0, 0, normalized.Width, normalized.Height);
            BitmapData data = normalized.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                int rowBytes = checked(normalized.Width * 4);
                byte[] pixels = new byte[checked(rowBytes * normalized.Height)];
                for (int y = 0; y < normalized.Height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IntPtr sourceRow = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(sourceRow, pixels, y * rowBytes, rowBytes);
                }

                return new PixelBuffer(pixels, normalized.Width, normalized.Height);
            }
            finally
            {
                normalized.UnlockBits(data);
            }
        }
    }
}
