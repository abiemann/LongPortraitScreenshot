using System.Buffers;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using LongPortraitScreenshot.Capture;

namespace LongPortraitScreenshot.Imaging;

public static class VerticalStitcher
{
    private const int MaximumAcceptablePixelError = 22;
    private const int MaximumPixelErrorContribution = MaximumAcceptablePixelError * 4 * 3;
    private const int StationaryPixelTolerance = 3;

    public static Bitmap Stitch(
        IReadOnlyList<CapturedFrame> frames,
        long maxPixels = 40_000_000) =>
        Stitch(
            frames,
            maxPixels,
            CancellationToken.None,
            finalFrameRowsToAppend: null);

    public static Bitmap Stitch(
        IReadOnlyList<CapturedFrame> frames,
        long maxPixels,
        CancellationToken cancellationToken) =>
        Stitch(
            frames,
            maxPixels,
            cancellationToken,
            finalFrameRowsToAppend: null);

    internal static Bitmap Stitch(
        IReadOnlyList<CapturedFrame> frames,
        long maxPixels,
        CancellationToken cancellationToken,
        int? finalFrameRowsToAppend) =>
        Stitch(
            frames,
            maxPixels,
            cancellationToken,
            finalFrameRowsToAppend,
            measuredVerticalShifts: null);

    internal static Bitmap Stitch(
        IReadOnlyList<CapturedFrame> frames,
        long maxPixels,
        CancellationToken cancellationToken,
        int? finalFrameRowsToAppend,
        IReadOnlyList<int>? measuredVerticalShifts,
        IReadOnlyList<int>? measuredCompositionRows = null)
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

        if (measuredCompositionRows is not null
            && (measuredVerticalShifts is null || measuredCompositionRows.Count != measuredVerticalShifts.Count))
        {
            throw new ArgumentException("Retained seam rows must correspond to the retained shifts.",
                nameof(measuredCompositionRows));
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
        int[] compositionSourceRows = new int[frames.Count - 1];
        int[] rowsToAppend = new int[frames.Count - 1];
        long outputHeight = height;

        for (int index = 1; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerticalAlignment alignment = measuredVerticalShifts is null
                ? MeasureVerticalAlignment(
                    frames[index - 1],
                    frames[index],
                    index,
                    cancellationToken)
                : new VerticalAlignment(measuredVerticalShifts[index - 1], measuredCompositionRows?[index - 1] ?? -1);
            int shift = alignment.Shift;
            int maximumMeasuredShift = height - Math.Max(24, height / 8);
            if (shift <= 0 || shift > maximumMeasuredShift)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(measuredVerticalShifts),
                    $"Retained shift {index} is outside the supported overlap range.");
            }

            if (measuredCompositionRows is not null && (uint)alignment.CompositionSourceY >= (uint)(height - shift))
            {
                throw new ArgumentOutOfRangeException(nameof(measuredCompositionRows),
                    "Retained seam row is outside the overlap.");
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
            compositionSourceRows[index - 1] = alignment.CompositionSourceY >= 0
                ? alignment.CompositionSourceY
                : FindCompositionSourceY(
                    frames[index - 1].Image,
                    frames[index].Image,
                    shift,
                    index,
                    cancellationToken);
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
                    int sourceY = compositionSourceRows[index - 1];
                    int replacedRows = height - shifts[index - 1] - sourceY;
                    int copiedRows = replacedRows + newRows;
                    // Replace the old overlap below a verified content row. In particular,
                    // a fixed footer in the previous frame must give way to revealed content.
                    graphics.DrawImage(
                        frames[index].Image,
                        new Rectangle(0, destinationY - replacedRows, width, copiedRows),
                        0,
                        sourceY,
                        width,
                        copiedRows,
                        GraphicsUnit.Pixel);
                    destinationY += newRows;
                }
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
        CancellationToken cancellationToken) =>
        MeasureVerticalAlignment(previous, current, currentFrameIndex, cancellationToken).Shift;

    internal readonly record struct VerticalAlignment(int Shift, int CompositionSourceY);

    internal static VerticalAlignment MeasureVerticalAlignment(
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

        if (previous.ViewSize is <= 0.0 or >= 100.0 || current.ViewSize is <= 0.0 or >= 100.0)
        {
            throw SeamFailure(
                currentFrameIndex,
                $"the target reported an invalid vertical view size ({previous.ViewSize:0.###}% to {current.ViewSize:0.###}%)");
        }

        // Lazy-loaded content can lower the reported percentage during a forward scroll.
        double currentPercentOnPreviousRange = ScrollPositionPolicy.RebasePercent(
            current.ScrollPercent, current.ViewSize, previous.ViewSize);
        double percentDelta = currentPercentOnPreviousRange - previous.ScrollPercent;
        if (!(percentDelta > 0.0001))
        {
            throw SeamFailure(
                currentFrameIndex,
                "the target did not report forward scroll movement");
        }

        int minimumOverlap = Math.Max(24, height / 8);
        int maximumShift = height - minimumOverlap;
        if (maximumShift < 1)
        {
            throw SeamFailure(currentFrameIndex, "the captured viewport is too short to establish an overlap");
        }

        double previousRangePixels = height * ((100.0 / previous.ViewSize) - 1.0);
        int predictedShift = (int)Math.Clamp(Math.Round(percentDelta / 100.0 * previousRangePixels), 1, maximumShift);
        int searchRadius = Math.Max(18, (int)Math.Ceiling(height * 0.12));
        int minimumShift = Math.Max(1, predictedShift - searchRadius);
        int maximumSearchShift = Math.Min(maximumShift, predictedShift + searchRadius);

        using PixelBuffer previousPixels = PixelBuffer.Create(previous.Image, cancellationToken);
        using PixelBuffer currentPixels = PixelBuffer.Create(current.Image, cancellationToken);

        int ignoredRightPixels = Math.Min(Math.Max(18, width / 40), Math.Max(0, width / 4));
        int firstX = Math.Min(3, Math.Max(0, width - 1));
        int exclusiveLastX = width - ignoredRightPixels;
        if (exclusiveLastX - firstX < 12)
        {
            throw SeamFailure(currentFrameIndex, "the selected control is too narrow to compare its content reliably");
        }

        int shift = FindExpectedShift(previousPixels, currentPixels, predictedShift, minimumShift, maximumSearchShift,
            firstX, exclusiveLastX, cancellationToken, out string? failure);
        if (failure is not null)
        {
            shift = FindShiftAcrossFullOverlap(previousPixels, currentPixels, maximumShift,
                firstX, exclusiveLastX, cancellationToken);
            if (shift < 1)
            {
                throw new FrameAlignmentException(currentFrameIndex, failure);
            }
        }

        return new VerticalAlignment(shift,
            FindCompositionSourceY(previousPixels, currentPixels, shift, currentFrameIndex, cancellationToken));
    }

    private static int FindExpectedShift(
        PixelBuffer previousPixels, PixelBuffer currentPixels, int predictedShift, int minimumShift,
        int maximumSearchShift, int firstX, int exclusiveLastX, CancellationToken cancellationToken,
        out string? failure)
    {
        failure = null;
        int height = currentPixels.Height;
        int commonOverlap = height - maximumSearchShift;
        List<(int X, int Y)> movingSamplePoints = FindMovingSamplePoints(
            previousPixels,
            currentPixels,
            commonOverlap,
            firstX,
            exclusiveLastX,
            cancellationToken,
            out int sampledPointCount,
            out int verticalSampleStride);
        double minimumMovingSampleFraction = Math.Min(
            0.02,
            (double)predictedShift / height);
        int minimumMovingSampleCount = Math.Max(
            12,
            (int)Math.Ceiling(sampledPointCount * minimumMovingSampleFraction));
        bool expandedOverlap = movingSamplePoints.Count < minimumMovingSampleCount;
        if (expandedOverlap)
        {
            // The overlap shared by every candidate may contain only a fixed header.
            // Candidates with smaller shifts can still have useful content below it.
            // Keep the ordinary scoring window unchanged when it already has evidence.
            movingSamplePoints = FindMovingSamplePoints(
                previousPixels,
                currentPixels,
                height - minimumShift,
                firstX,
                exclusiveLastX,
                cancellationToken,
                out sampledPointCount,
                out verticalSampleStride);
            minimumMovingSampleCount = Math.Max(
                12,
                (int)Math.Ceiling(sampledPointCount * minimumMovingSampleFraction));
        }

        if (movingSamplePoints.Count < minimumMovingSampleCount)
        {
            failure = "the overlap did not contain enough moving visual detail to distinguish it from fixed content";
            return -1;
        }

        int candidateCount = maximumSearchShift - minimumShift + 1;
        // Wider candidate overlaps can reach the old footer. Only mask a contiguous
        // bottom band here; individually stationary background pixels must remain
        // negative evidence against incorrect shifts in the ordinary scoring window.
        int exclusiveLastPreviousRow = expandedOverlap
            ? height - CountStationaryBottomRows(previousPixels, currentPixels, cancellationToken)
            : height;
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
                movingSamplePoints,
                minimumMovingSampleCount,
                exclusiveLastPreviousRow,
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
            failure = $"the best overlap had too much visual difference (error {bestScore:0.##})";
            return -1;
        }

        double requiredConfidenceGap = Math.Max(0.45, bestScore * 0.08);
        double sameBasinThreshold = bestScore + requiredConfidenceGap;
        int sameBasinRadius = Math.Max(3, verticalSampleStride);
        int bestScoreIndex = bestShift - minimumShift;
        int firstSameBasinIndex = bestScoreIndex;
        while (firstSameBasinIndex > 0
            && bestScoreIndex - firstSameBasinIndex < sameBasinRadius
            && scores[firstSameBasinIndex - 1] < sameBasinThreshold)
        {
            firstSameBasinIndex--;
        }

        int lastSameBasinIndex = bestScoreIndex;
        while (lastSameBasinIndex + 1 < scores.Length
            && lastSameBasinIndex - bestScoreIndex < sameBasinRadius
            && scores[lastSameBasinIndex + 1] < sameBasinThreshold)
        {
            lastSameBasinIndex++;
        }

        int secondBasinShift = -1;
        double secondBasinScore = double.PositiveInfinity;
        for (int scoreIndex = 0; scoreIndex < scores.Length; scoreIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scoreIndex >= firstSameBasinIndex && scoreIndex <= lastSameBasinIndex)
            {
                continue;
            }

            double score = scores[scoreIndex];
            if (score < secondBasinScore)
            {
                secondBasinScore = score;
                secondBasinShift = minimumShift + scoreIndex;
            }
        }

        if (double.IsFinite(secondBasinScore) && secondBasinScore - bestScore < requiredConfidenceGap)
        {
            failure = $"more than one overlap looked equally likely " +
                $"(best shift {bestShift}px, error {bestScore:0.##}; " +
                $"alternate shift {secondBasinShift}px, error {secondBasinScore:0.##})";
            return -1;
        }

        return bestShift;
    }

    private static int FindShiftAcrossFullOverlap(
        PixelBuffer previous, PixelBuffer current, int maximumShift,
        int firstX, int exclusiveLastX, CancellationToken cancellationToken)
    {
        // Recheck each shift over its full overlap. A repainted area can dominate the
        // shorter window shared by all predicted shifts even when the prediction was right.
        int height = current.Height;
        List<(int X, int Y)> moving = FindMovingSamplePoints(previous, current, height, firstX,
            exclusiveLastX, cancellationToken, out int gridSize, out _);
        const int minimumPoints = 36;
        if (moving.Count < minimumPoints)
        {
            return -1;
        }

        int lastRow = height - CountStationaryBottomRows(previous, current, cancellationToken);
        int bestShift = -1;
        double best = double.PositiveInfinity;
        double nextBest = double.PositiveInfinity;
        for (int shift = 1; shift <= maximumShift && shift < lastRow; shift++)
        {
            int required = Math.Max(minimumPoints, (int)Math.Ceiling(gridSize * 0.02 * (lastRow - shift) / height));
            double score = CalculateScore(previous, current, shift, moving, required, lastRow, cancellationToken);
            if (score < best)
            {
                nextBest = best;
                best = score;
                bestShift = shift;
            }
            else if (score < nextBest)
            {
                nextBest = score;
            }
        }

        if (bestShift < 1 || best > MaximumAcceptablePixelError
            || nextBest - best < Math.Max(1.0, best * 0.08))
        {
            return -1;
        }

        int overlap = height - bestShift;
        int columns = Math.Min(48, exclusiveLastX - firstX);
        int supportedBands = 0;
        for (int band = 0; band < 4; band++)
        {
            int startY = (int)((long)overlap * band / 4);
            int endY = Math.Min((int)((long)overlap * (band + 1) / 4), lastRow - bestShift);
            int rows = Math.Min(24, endY - startY);
            int movingCount = 0, matchingCount = 0, matchingRows = 0;
            long totalDifference = 0;

            // Verify with a separate grid over the actual overlap. A scrolling sidebar can
            // match at every height while the stationary, textured body refutes that shift.
            for (int row = 0; row < rows; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int y = startY + (int)((2L * row + 1) * (endY - startY) / (2 * rows));
                int matchesBeforeRow = matchingCount;
                for (int column = 0; column < columns; column++)
                {
                    int x = firstX + (int)((2L * column + 1) * (exclusiveLastX - firstX) / (2 * columns));
                    int aligned = previous.GetOffset(x, y + bestShift);
                    int currentOffset = current.GetOffset(x, y);
                    totalDifference += PixelDifference(previous, aligned, current, currentOffset);
                    if (LargestChannelDifference(previous, previous.GetOffset(x, y), current, currentOffset)
                        > StationaryPixelTolerance)
                    {
                        movingCount++;
                        if (LargestChannelDifference(previous, aligned, current, currentOffset) <= StationaryPixelTolerance * 2)
                        {
                            matchingCount++;
                        }
                    }
                }

                if (matchingCount > matchesBeforeRow) matchingRows++;
            }

            if (movingCount >= 12 && matchingRows >= 2 && matchingCount >= movingCount * 0.7
                && totalDifference <= (long)MaximumAcceptablePixelError * rows * columns * 3)
            {
                supportedBands++;
            }
        }

        return supportedBands >= 3 ? bestShift : -1;
    }

    private static List<(int X, int Y)> FindMovingSamplePoints(
        PixelBuffer previous,
        PixelBuffer current,
        int overlap,
        int firstX,
        int exclusiveLastX,
        CancellationToken cancellationToken,
        out int sampledPointCount,
        out int verticalSampleStride)
    {
        int verticalMargin = Math.Min(6, Math.Max(0, overlap / 10));
        int availableRows = overlap - (verticalMargin * 2);
        int availableColumns = exclusiveLastX - firstX;
        if (availableRows <= 0 || availableColumns <= 0)
        {
            sampledPointCount = 0;
            verticalSampleStride = 1;
            return [];
        }

        int rowSamples = Math.Min(96, availableRows);
        int columnSamples = Math.Min(80, availableColumns);
        verticalSampleStride = rowSamples <= 1
            ? 1
            : (int)Math.Ceiling((double)(availableRows - 1) / (rowSamples - 1));
        sampledPointCount = checked(rowSamples * columnSamples);
        List<(int X, int Y)> movingPoints = new(sampledPointCount);

        for (int rowIndex = 0; rowIndex < rowSamples; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rowOffset = rowSamples == 1
                ? 0
                : (int)((long)rowIndex * (availableRows - 1) / (rowSamples - 1));
            int y = verticalMargin + rowOffset;

            for (int columnIndex = 0; columnIndex < columnSamples; columnIndex++)
            {
                int columnOffset = columnSamples == 1
                    ? 0
                    : (int)((long)columnIndex * (availableColumns - 1) / (columnSamples - 1));
                int x = firstX + columnOffset;
                int previousOffset = previous.GetOffset(x, y);
                int currentOffset = current.GetOffset(x, y);
                int largestChannelDifference = Math.Max(
                    Math.Abs(previous.Pixels[previousOffset] - current.Pixels[currentOffset]),
                    Math.Max(
                        Math.Abs(previous.Pixels[previousOffset + 1] - current.Pixels[currentOffset + 1]),
                        Math.Abs(previous.Pixels[previousOffset + 2] - current.Pixels[currentOffset + 2])));

                if (largestChannelDifference > StationaryPixelTolerance)
                {
                    movingPoints.Add((x, y));
                }
            }
        }

        return movingPoints;
    }

    private static double CalculateScore(
        PixelBuffer previous,
        PixelBuffer current,
        int shift,
        IReadOnlyList<(int X, int Y)> samplePoints,
        int minimumMovingSampleCount,
        int exclusiveLastPreviousRow,
        CancellationToken cancellationToken)
    {
        long totalDifference = 0;
        int comparedChannels = 0;

        for (int index = 0; index < samplePoints.Count; index++)
        {
            if ((index & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            (int x, int currentY) = samplePoints[index];
            if (currentY + shift >= exclusiveLastPreviousRow)
            {
                // FindMovingSamplePoints emits points in row order.
                break;
            }

            int previousOffset = previous.GetOffset(x, currentY + shift);
            int currentOffset = current.GetOffset(x, currentY);
            int pixelDifference = PixelDifference(previous, previousOffset, current, currentOffset);

            // Keep a small animated or lazy-loaded region from dominating an otherwise exact seam.
            totalDifference += Math.Min(pixelDifference, MaximumPixelErrorContribution);
            comparedChannels += 3;
        }

        return comparedChannels < minimumMovingSampleCount * 3
            ? double.PositiveInfinity
            : (double)totalDifference / comparedChannels;
    }

    private static int FindCompositionSourceY(
        Bitmap previous,
        Bitmap current,
        int shift,
        int currentFrameIndex,
        CancellationToken cancellationToken)
    {
        using PixelBuffer previousPixels = PixelBuffer.Create(previous, cancellationToken);
        using PixelBuffer currentPixels = PixelBuffer.Create(current, cancellationToken);
        return FindCompositionSourceY(
            previousPixels, currentPixels, shift, currentFrameIndex, cancellationToken);
    }

    private static int FindCompositionSourceY(
        PixelBuffer previous,
        PixelBuffer current,
        int shift,
        int currentFrameIndex,
        CancellationToken cancellationToken)
    {
        int ignoredRightPixels = Math.Min(Math.Max(18, current.Width / 40), Math.Max(0, current.Width / 4));
        int firstX = Math.Min(3, Math.Max(0, current.Width - 1));
        int availableColumns = current.Width - ignoredRightPixels - firstX;
        int columnSamples = Math.Min(80, availableColumns);

        for (int y = current.Height - shift - 1; y >= 0; y--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int movingPoints = 0;
            long totalDifference = 0;
            long wholeRowDifference = 0;
            for (int column = 0; column < columnSamples; column++)
            {
                int x = firstX + (columnSamples == 1
                    ? 0
                    : (int)((long)column * (availableColumns - 1) / (columnSamples - 1)));
                int currentOffset = current.GetOffset(x, y);
                int previousOffset = previous.GetOffset(x, y + shift);
                int pixelDifference =
                    Math.Abs(previous.Pixels[previousOffset] - current.Pixels[currentOffset]) +
                    Math.Abs(previous.Pixels[previousOffset + 1] - current.Pixels[currentOffset + 1]) +
                    Math.Abs(previous.Pixels[previousOffset + 2] - current.Pixels[currentOffset + 2]);
                int contribution = Math.Min(pixelDifference, MaximumPixelErrorContribution);
                wholeRowDifference += contribution;
                if (LargestChannelDifference(previous, previous.GetOffset(x, y), current, currentOffset)
                    <= StationaryPixelTolerance)
                {
                    continue;
                }

                movingPoints++;
                totalDifference += contribution;
            }

            // A stationary header/footer or a blank margin alone cannot establish a seam.
            // Select the last row whose moving detail actually matches after alignment.
            // Also check the whole row: a single animated footer pixel must not
            // validate a join when the remaining aligned row is still occluded.
            if (movingPoints > 0
                && (double)totalDifference / (movingPoints * 3) <= MaximumAcceptablePixelError
                && (double)wholeRowDifference / (columnSamples * 3) <= MaximumAcceptablePixelError)
            {
                return y;
            }
        }

        throw new FrameAlignmentException(
            currentFrameIndex,
            "there was no verified moving content at which to join the frames without repeating fixed content");
    }

    private static int CountStationaryBottomRows(
        PixelBuffer previous,
        PixelBuffer current,
        CancellationToken cancellationToken)
    {
        int ignoredRightPixels = Math.Min(Math.Max(18, current.Width / 40), Math.Max(0, current.Width / 4));
        int firstX = Math.Min(3, Math.Max(0, current.Width - 1));
        int availableColumns = current.Width - ignoredRightPixels - firstX;
        int columnSamples = Math.Min(80, availableColumns);
        int stationaryRows = 0;
        for (int y = current.Height - 1; y >= 0; y--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int matchingColumns = 0;
            for (int column = 0; column < columnSamples; column++)
            {
                int x = firstX + (columnSamples == 1
                    ? 0
                    : (int)((long)column * (availableColumns - 1) / (columnSamples - 1)));
                if (LargestChannelDifference(previous, previous.GetOffset(x, y), current, current.GetOffset(x, y))
                    <= StationaryPixelTolerance)
                {
                    matchingColumns++;
                }
            }

            if (matchingColumns < Math.Ceiling(columnSamples * 0.95))
            {
                break;
            }

            stationaryRows++;
        }

        return stationaryRows;
    }

    private static int PixelDifference(PixelBuffer first, int firstOffset, PixelBuffer second, int secondOffset) =>
        Math.Abs(first.Pixels[firstOffset] - second.Pixels[secondOffset]) +
        Math.Abs(first.Pixels[firstOffset + 1] - second.Pixels[secondOffset + 1]) +
        Math.Abs(first.Pixels[firstOffset + 2] - second.Pixels[secondOffset + 2]);

    private static int LargestChannelDifference(
        PixelBuffer first,
        int firstOffset,
        PixelBuffer second,
        int secondOffset) =>
        Math.Max(
            Math.Abs(first.Pixels[firstOffset] - second.Pixels[secondOffset]),
            Math.Max(
                Math.Abs(first.Pixels[firstOffset + 1] - second.Pixels[secondOffset + 1]),
                Math.Abs(first.Pixels[firstOffset + 2] - second.Pixels[secondOffset + 2])));

    internal sealed class FrameAlignmentException(int frameIndex, string reason)
        : InvalidOperationException(FormatSeamFailure(frameIndex, reason));

    private static InvalidOperationException SeamFailure(int currentFrameIndex, string reason) =>
        new(FormatSeamFailure(currentFrameIndex, reason));

    private static string FormatSeamFailure(int frameIndex, string reason) =>
        $"Could not confidently stitch frames {frameIndex} and {frameIndex + 1}: {reason}. " +
        "Keep the target unobscured and stationary, disable animations if possible, and retry.";

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

    private sealed class PixelBuffer : IDisposable
    {
        private bool _disposed;

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

        public void Dispose()
        {
            if (!_disposed)
            {
                ArrayPool<byte>.Shared.Return(Pixels);
                _disposed = true;
            }
        }

        public static PixelBuffer Create(Bitmap source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Bitmap? normalized = source.PixelFormat == PixelFormat.Format32bppArgb
                ? null
                : CloneAsArgb(source, cancellationToken);
            Bitmap readable = normalized ?? source;
            Rectangle bounds = new(0, 0, readable.Width, readable.Height);
            BitmapData data = readable.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[]? pixels = null;

            try
            {
                int rowBytes = checked(readable.Width * 4);
                pixels = ArrayPool<byte>.Shared.Rent(checked(rowBytes * readable.Height));
                for (int y = 0; y < readable.Height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IntPtr sourceRow = IntPtr.Add(data.Scan0, y * data.Stride);
                    Marshal.Copy(sourceRow, pixels, y * rowBytes, rowBytes);
                }

                PixelBuffer result = new(pixels, readable.Width, readable.Height);
                pixels = null;
                return result;
            }
            finally
            {
                readable.UnlockBits(data);
                if (pixels is not null)
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                }
            }
        }
    }
}
