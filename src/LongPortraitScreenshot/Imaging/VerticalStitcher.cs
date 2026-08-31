using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LongPortraitScreenshot.Imaging;

public static class VerticalStitcher
{
    private const double MaximumAcceptablePixelError = 22.0;

    public static Bitmap Stitch(IReadOnlyList<CapturedFrame> frames, long maxPixels = 40_000_000)
    {
        ArgumentNullException.ThrowIfNull(frames);

        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one captured frame is required.", nameof(frames));
        }

        if (maxPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPixels), "The output pixel limit must be positive.");
        }

        (int width, int height) = ValidateFrames(frames);
        if (frames.Count == 1)
        {
            EnsureOutputFits(width, height, maxPixels);
            return CloneAsArgb(frames[0].Image);
        }

        int[] shifts = new int[frames.Count - 1];
        long outputHeight = height;

        for (int index = 1; index < frames.Count; index++)
        {
            shifts[index - 1] = FindVerticalShift(frames[index - 1], frames[index], index, width, height);
            outputHeight = checked(outputHeight + shifts[index - 1]);
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
            using Graphics graphics = Graphics.FromImage(output);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.None;
            graphics.DrawImageUnscaled(frames[0].Image, 0, 0);

            int destinationY = height;
            for (int index = 1; index < frames.Count; index++)
            {
                int newRows = shifts[index - 1];
                int sourceY = height - newRows;
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

            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static (int Width, int Height) ValidateFrames(IReadOnlyList<CapturedFrame> frames)
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

    private static int FindVerticalShift(
        CapturedFrame previous,
        CapturedFrame current,
        int currentFrameIndex,
        int width,
        int height)
    {
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

        PixelBuffer previousPixels = PixelBuffer.Create(previous.Image);
        PixelBuffer currentPixels = PixelBuffer.Create(current.Image);

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
            double score = CalculateScore(
                previousPixels,
                currentPixels,
                shift,
                firstX,
                exclusiveLastX);
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
        int exclusiveLastX)
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

    private static Bitmap CloneAsArgb(Bitmap source)
    {
        Bitmap clone = new(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(clone);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(source, 0, 0);
        return clone;
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

        public static PixelBuffer Create(Bitmap source)
        {
            using Bitmap normalized = CloneAsArgb(source);
            Rectangle bounds = new(0, 0, normalized.Width, normalized.Height);
            BitmapData data = normalized.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                int rowBytes = checked(normalized.Width * 4);
                byte[] pixels = new byte[checked(rowBytes * normalized.Height)];
                for (int y = 0; y < normalized.Height; y++)
                {
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
