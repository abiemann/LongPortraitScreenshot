using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace LongPortraitScreenshot.Imaging;

internal static class RepeatedOverlayRemover
{
    // Candidates must repeat at stitch offsets, have a safe local background, and remain
    // fixed in viewport coordinates while the underlying document moves between raw frames.
    private const int MinimumOccurrences = 3;
    private const int MinimumCandidateDimension = 8;
    private const int MaximumCandidateDimension = 256;
    private const int MinimumCandidatePixels = 48;
    private const int CandidateContrastThreshold = 24;
    private const int DescriptorContrastThreshold = 36;
    private const int FillPadding = 2;
    private const int RingThickness = 6;
    private const int MaximumBackgroundChannelRange = 12;
    private const int MaximumMatchingPixelDifference = 12;
    private const double MinimumMaskOverlap = 0.88;
    private const double MinimumMatchingPixelFraction = 0.90;
    private const double MaximumMeanPixelDifference = 5.0;

    public static void Remove(
        Bitmap image,
        int viewportHeight,
        IReadOnlyList<int> verticalShifts,
        IReadOnlyList<CapturedFrame> frames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(verticalShifts);
        ArgumentNullException.ThrowIfNull(frames);
        cancellationToken.ThrowIfCancellationRequested();

        if (verticalShifts.Count < MinimumOccurrences - 1
            || frames.Count != verticalShifts.Count + 1
            || viewportHeight <= 0
            || viewportHeight > image.Height
            || image.Width < 32)
        {
            return;
        }

        using LockedBitmap pixels = new(image);
        List<Candidate> candidates = FindCandidates(pixels, viewportHeight, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        candidates.Sort((left, right) => right.PixelCount.CompareTo(left.PixelCount));

        List<RemovalGroup> groups = [];
        foreach (Candidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rectangle paddedBounds = candidate.Bounds;
            paddedBounds.Inflate(FillPadding, FillPadding);

            if (groups.Any(group => group.FirstBounds.IntersectsWith(paddedBounds)))
            {
                continue;
            }

            RemovalGroup? group = TryCreateRemovalGroup(
                pixels,
                candidate,
                viewportHeight,
                verticalShifts,
                frames,
                cancellationToken);
            if (group is not null)
            {
                groups.Add(group);
            }
        }

        foreach (RemovalGroup group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (Occurrence occurrence in group.Occurrences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pixels.Fill(occurrence.Bounds, occurrence.BackgroundRows, cancellationToken);
            }
        }
    }

    private static List<Candidate> FindCandidates(
        LockedBitmap pixels,
        int viewportHeight,
        CancellationToken cancellationToken)
    {
        int bandWidth = Math.Min(
            pixels.Width / 2,
            Math.Clamp(pixels.Width / 8, 48, 256));
        if (bandWidth < MinimumCandidateDimension + (FillPadding + RingThickness) * 2)
        {
            return [];
        }

        Rectangle leftBand = new(0, 0, bandWidth, viewportHeight);
        Rectangle rightBand = new(pixels.Width - bandWidth, 0, bandWidth, viewportHeight);
        List<Candidate> candidates = FindCandidatesInBand(
            pixels.Read(leftBand, cancellationToken),
            leftBand,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        candidates.AddRange(FindCandidatesInBand(
            pixels.Read(rightBand, cancellationToken),
            rightBand,
            cancellationToken));
        return candidates;
    }

    private static List<Candidate> FindCandidatesInBand(
        byte[] bandPixels,
        Rectangle band,
        CancellationToken cancellationToken)
    {
        int width = band.Width;
        int height = band.Height;
        byte[] mask = new byte[checked(width * height)];
        int[] blueHistogram = new int[256];
        int[] greenHistogram = new int[256];
        int[] redHistogram = new int[256];

        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(blueHistogram);
            Array.Clear(greenHistogram);
            Array.Clear(redHistogram);

            int rowOffset = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int offset = rowOffset + x * 4;
                blueHistogram[bandPixels[offset]]++;
                greenHistogram[bandPixels[offset + 1]]++;
                redHistogram[bandPixels[offset + 2]]++;
            }

            int backgroundBlue = FindMedian(blueHistogram, width);
            int backgroundGreen = FindMedian(greenHistogram, width);
            int backgroundRed = FindMedian(redHistogram, width);

            for (int x = 0; x < width; x++)
            {
                int offset = rowOffset + x * 4;
                int contrast = Math.Max(
                    Math.Abs(bandPixels[offset] - backgroundBlue),
                    Math.Max(
                        Math.Abs(bandPixels[offset + 1] - backgroundGreen),
                        Math.Abs(bandPixels[offset + 2] - backgroundRed)));
                if (contrast >= CandidateContrastThreshold)
                {
                    mask[y * width + x] = 1;
                }
            }
        }

        int[] work = new int[mask.Length];
        List<Candidate> candidates = [];

        for (int start = 0; start < mask.Length; start++)
        {
            if ((start & 0xfff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (mask[start] == 0)
            {
                continue;
            }

            int workCount = 0;
            int workIndex = 0;
            work[workCount++] = start;
            mask[start] = 0;

            int minimumX = start % width;
            int maximumX = minimumX;
            int minimumY = start / width;
            int maximumY = minimumY;
            int pixelCount = 0;

            while (workIndex < workCount)
            {
                if ((workIndex & 0x3ff) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                int current = work[workIndex++];
                int x = current % width;
                int y = current / width;
                pixelCount++;
                minimumX = Math.Min(minimumX, x);
                maximumX = Math.Max(maximumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumY = Math.Max(maximumY, y);

                int firstY = Math.Max(0, y - 1);
                int lastY = Math.Min(height - 1, y + 1);
                int firstX = Math.Max(0, x - 1);
                int lastX = Math.Min(width - 1, x + 1);

                for (int neighborY = firstY; neighborY <= lastY; neighborY++)
                {
                    for (int neighborX = firstX; neighborX <= lastX; neighborX++)
                    {
                        int neighbor = neighborY * width + neighborX;
                        if (mask[neighbor] != 0)
                        {
                            mask[neighbor] = 0;
                            work[workCount++] = neighbor;
                        }
                    }
                }
            }

            int candidateWidth = maximumX - minimumX + 1;
            int candidateHeight = maximumY - minimumY + 1;
            double aspectRatio = (double)candidateWidth / candidateHeight;
            double density = (double)pixelCount / (candidateWidth * candidateHeight);

            if (pixelCount < MinimumCandidatePixels
                || candidateWidth < MinimumCandidateDimension
                || candidateHeight < MinimumCandidateDimension
                || candidateWidth > MaximumCandidateDimension
                || candidateHeight > MaximumCandidateDimension
                || aspectRatio < 0.25
                || aspectRatio > 4.0
                || density < 0.15)
            {
                continue;
            }

            candidates.Add(new Candidate(
                new Rectangle(
                    band.Left + minimumX,
                    band.Top + minimumY,
                    candidateWidth,
                    candidateHeight),
                pixelCount));
        }

        return candidates;
    }

    private static RemovalGroup? TryCreateRemovalGroup(
        LockedBitmap pixels,
        Candidate candidate,
        int viewportHeight,
        IReadOnlyList<int> verticalShifts,
        IReadOnlyList<CapturedFrame> frames,
        CancellationToken cancellationToken)
    {
        Rectangle firstBounds = candidate.Bounds;
        firstBounds.Inflate(FillPadding, FillPadding);

        List<Rectangle> occurrenceBounds = [firstBounds];
        int cumulativeShift = 0;

        foreach (int shift in verticalShifts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shift <= 0 || shift >= viewportHeight)
            {
                return null;
            }

            int copiedSourceTop = viewportHeight - shift;
            bool partiallyCopied = firstBounds.Bottom > copiedSourceTop
                && firstBounds.Top < copiedSourceTop;
            if (partiallyCopied)
            {
                return null;
            }

            cumulativeShift = checked(cumulativeShift + shift);
            if (firstBounds.Top >= copiedSourceTop)
            {
                occurrenceBounds.Add(new Rectangle(
                    firstBounds.X,
                    checked(firstBounds.Y + cumulativeShift),
                    firstBounds.Width,
                    firstBounds.Height));
            }
        }

        if (occurrenceBounds.Count < MinimumOccurrences)
        {
            return null;
        }

        List<Occurrence> occurrences = [];
        foreach (Rectangle bounds in occurrenceBounds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Occurrence? occurrence = TryAnalyzeOccurrence(pixels, bounds, cancellationToken);
            if (occurrence is null)
            {
                return null;
            }

            occurrences.Add(occurrence);
        }

        Occurrence reference = occurrences[0];
        if (!HasEnoughForeground(reference, cancellationToken))
        {
            return null;
        }

        for (int index = 1; index < occurrences.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Matches(reference, occurrences[index], cancellationToken))
            {
                return null;
            }
        }

        if (!HasConclusiveFixedMotion(
                candidate,
                firstBounds,
                viewportHeight,
                verticalShifts,
                frames,
                cancellationToken))
        {
            return null;
        }

        return new RemovalGroup(firstBounds, occurrences);
    }

    private static bool HasConclusiveFixedMotion(
        Candidate candidate,
        Rectangle paddedBounds,
        int viewportHeight,
        IReadOnlyList<int> verticalShifts,
        IReadOnlyList<CapturedFrame> frames,
        CancellationToken cancellationToken)
    {
        int conclusiveNonmatches = 0;

        // Real document content from the previous frame reappears `shift` pixels higher in
        // the next frame. A match there proves that the candidate scrolls with the page.
        for (int index = 0; index < verticalShifts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int shift = verticalShifts[index];
            int copiedSourceTop = viewportHeight - shift;
            if (paddedBounds.Top < copiedSourceTop)
            {
                continue;
            }

            Rectangle movedBounds = new(
                candidate.Bounds.X,
                candidate.Bounds.Y - shift,
                candidate.Bounds.Width,
                candidate.Bounds.Height);
            if (movedBounds.Top < 0
                || movedBounds.Left < 0
                || movedBounds.Right > frames[index + 1].Image.Width
                || movedBounds.Bottom > frames[index + 1].Image.Height)
            {
                return false;
            }

            byte[]? previous = TryReadFrameRegion(
                frames[index].Image,
                candidate.Bounds,
                cancellationToken);
            byte[]? moved = TryReadFrameRegion(
                frames[index + 1].Image,
                movedBounds,
                cancellationToken);
            if (previous is null || moved is null)
            {
                return false;
            }

            MotionComparison comparison = CompareMotion(previous, moved, cancellationToken);
            if (comparison is MotionComparison.Match or MotionComparison.Ambiguous)
            {
                return false;
            }

            conclusiveNonmatches++;
        }

        return conclusiveNonmatches >= 2;
    }

    private static MotionComparison CompareMotion(
        byte[] previous,
        byte[] moved,
        CancellationToken cancellationToken)
    {
        if (previous.Length != moved.Length || previous.Length == 0)
        {
            return MotionComparison.Ambiguous;
        }

        int pixelCount = previous.Length / 4;
        int matchingPixels = 0;
        long totalDifference = 0;

        for (int offset = 0; offset < previous.Length; offset += 4)
        {
            if ((offset & 0xfff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int blueDifference = Math.Abs(previous[offset] - moved[offset]);
            int greenDifference = Math.Abs(previous[offset + 1] - moved[offset + 1]);
            int redDifference = Math.Abs(previous[offset + 2] - moved[offset + 2]);
            totalDifference += blueDifference + greenDifference + redDifference;

            if (Math.Max(blueDifference, Math.Max(greenDifference, redDifference))
                <= MaximumMatchingPixelDifference)
            {
                matchingPixels++;
            }
        }

        double matchingFraction = (double)matchingPixels / pixelCount;
        double meanDifference = (double)totalDifference / (pixelCount * 3);
        if (matchingFraction >= MinimumMatchingPixelFraction
            && meanDifference <= MaximumMeanPixelDifference)
        {
            return MotionComparison.Match;
        }

        return matchingFraction <= 0.65 && meanDifference >= 20.0
            ? MotionComparison.ConclusiveNonmatch
            : MotionComparison.Ambiguous;
    }

    private static byte[]? TryReadFrameRegion(
        Bitmap image,
        Rectangle bounds,
        CancellationToken cancellationToken)
    {
        if (image.PixelFormat != PixelFormat.Format32bppArgb
            || bounds.Left < 0
            || bounds.Top < 0
            || bounds.Right > image.Width
            || bounds.Bottom > image.Height)
        {
            return null;
        }

        BitmapData? data = null;
        try
        {
            data = image.LockBits(
                new Rectangle(0, 0, image.Width, image.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            int rowBytes = checked(bounds.Width * 4);
            byte[] pixels = new byte[checked(rowBytes * bounds.Height)];

            for (int y = 0; y < bounds.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr source = IntPtr.Add(
                    data.Scan0,
                    checked((bounds.Y + y) * data.Stride + bounds.X * 4));
                Marshal.Copy(source, pixels, y * rowBytes, rowBytes);
            }

            return pixels;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or ExternalException
                                           or OverflowException)
        {
            return null;
        }
        finally
        {
            if (data is not null)
            {
                image.UnlockBits(data);
            }
        }
    }

    private static Occurrence? TryAnalyzeOccurrence(
        LockedBitmap pixels,
        Rectangle bounds,
        CancellationToken cancellationToken)
    {
        Rectangle outerBounds = bounds;
        outerBounds.Inflate(RingThickness, RingThickness);
        if (outerBounds.Left < 0
            || outerBounds.Top < 0
            || outerBounds.Right > pixels.Width
            || outerBounds.Bottom > pixels.Height)
        {
            return null;
        }

        byte[] patch = pixels.Read(outerBounds, cancellationToken);
        int minimumBlue = 255;
        int minimumGreen = 255;
        int minimumRed = 255;
        int maximumBlue = 0;
        int maximumGreen = 0;
        int maximumRed = 0;

        for (int y = 0; y < outerBounds.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < outerBounds.Width; x++)
            {
                bool isRing = x < RingThickness
                    || x >= outerBounds.Width - RingThickness
                    || y < RingThickness
                    || y >= outerBounds.Height - RingThickness;
                if (!isRing)
                {
                    continue;
                }

                int offset = (y * outerBounds.Width + x) * 4;
                minimumBlue = Math.Min(minimumBlue, patch[offset]);
                minimumGreen = Math.Min(minimumGreen, patch[offset + 1]);
                minimumRed = Math.Min(minimumRed, patch[offset + 2]);
                maximumBlue = Math.Max(maximumBlue, patch[offset]);
                maximumGreen = Math.Max(maximumGreen, patch[offset + 1]);
                maximumRed = Math.Max(maximumRed, patch[offset + 2]);
            }
        }

        if (maximumBlue - minimumBlue > MaximumBackgroundChannelRange
            || maximumGreen - minimumGreen > MaximumBackgroundChannelRange
            || maximumRed - minimumRed > MaximumBackgroundChannelRange)
        {
            return null;
        }

        BackgroundRow[] backgroundRows = new BackgroundRow[bounds.Height];
        for (int y = 0; y < bounds.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int patchY = RingThickness + y;
            Bgra left = FindMedianPixel(patch, outerBounds.Width, patchY, 0, RingThickness);
            Bgra right = FindMedianPixel(
                patch,
                outerBounds.Width,
                patchY,
                outerBounds.Width - RingThickness,
                outerBounds.Width);
            backgroundRows[y] = new BackgroundRow(left, right);
        }

        return new Occurrence(bounds, patch, outerBounds.Width, backgroundRows);
    }

    private static bool HasEnoughForeground(
        Occurrence occurrence,
        CancellationToken cancellationToken)
    {
        int count = 0;
        for (int y = FillPadding; y < occurrence.Bounds.Height - FillPadding; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = FillPadding; x < occurrence.Bounds.Width - FillPadding; x++)
            {
                Bgra pixel = occurrence.GetPixel(x, y);
                Bgra background = occurrence.GetBackground(x, y);
                if (GetContrast(pixel, background) >= DescriptorContrastThreshold)
                {
                    count++;
                }
            }
        }

        return count >= MinimumCandidatePixels;
    }

    private static bool Matches(
        Occurrence reference,
        Occurrence candidate,
        CancellationToken cancellationToken)
    {
        int referenceForeground = 0;
        int intersection = 0;
        int union = 0;
        int matchingPixels = 0;
        long totalDifference = 0;

        for (int y = FillPadding; y < reference.Bounds.Height - FillPadding; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = FillPadding; x < reference.Bounds.Width - FillPadding; x++)
            {
                Bgra referencePixel = reference.GetPixel(x, y);
                Bgra candidatePixel = candidate.GetPixel(x, y);
                bool referenceIsForeground = GetContrast(
                    referencePixel,
                    reference.GetBackground(x, y)) >= DescriptorContrastThreshold;
                bool candidateIsForeground = GetContrast(
                    candidatePixel,
                    candidate.GetBackground(x, y)) >= DescriptorContrastThreshold;

                if (referenceIsForeground || candidateIsForeground)
                {
                    union++;
                }

                if (referenceIsForeground && candidateIsForeground)
                {
                    intersection++;
                }

                if (!referenceIsForeground)
                {
                    continue;
                }

                referenceForeground++;
                int blueDifference = Math.Abs(referencePixel.Blue - candidatePixel.Blue);
                int greenDifference = Math.Abs(referencePixel.Green - candidatePixel.Green);
                int redDifference = Math.Abs(referencePixel.Red - candidatePixel.Red);
                totalDifference += blueDifference + greenDifference + redDifference;
                if (candidateIsForeground
                    && Math.Max(blueDifference, Math.Max(greenDifference, redDifference))
                        <= MaximumMatchingPixelDifference)
                {
                    matchingPixels++;
                }
            }
        }

        if (referenceForeground < MinimumCandidatePixels || union == 0)
        {
            return false;
        }

        double maskOverlap = (double)intersection / union;
        double matchingFraction = (double)matchingPixels / referenceForeground;
        double meanDifference = (double)totalDifference / (referenceForeground * 3);
        return maskOverlap >= MinimumMaskOverlap
            && matchingFraction >= MinimumMatchingPixelFraction
            && meanDifference <= MaximumMeanPixelDifference;
    }

    private static int GetContrast(Bgra pixel, Bgra background) => Math.Max(
        Math.Abs(pixel.Blue - background.Blue),
        Math.Max(
            Math.Abs(pixel.Green - background.Green),
            Math.Abs(pixel.Red - background.Red)));

    private static Bgra FindMedianPixel(
        byte[] pixels,
        int width,
        int y,
        int firstX,
        int exclusiveLastX)
    {
        int count = exclusiveLastX - firstX;
        byte[] blue = new byte[count];
        byte[] green = new byte[count];
        byte[] red = new byte[count];
        byte[] alpha = new byte[count];

        for (int index = 0; index < count; index++)
        {
            int offset = (y * width + firstX + index) * 4;
            blue[index] = pixels[offset];
            green[index] = pixels[offset + 1];
            red[index] = pixels[offset + 2];
            alpha[index] = pixels[offset + 3];
        }

        Array.Sort(blue);
        Array.Sort(green);
        Array.Sort(red);
        Array.Sort(alpha);
        int median = count / 2;
        return new Bgra(blue[median], green[median], red[median], alpha[median]);
    }

    private static int FindMedian(int[] histogram, int count)
    {
        int target = count / 2;
        int seen = 0;
        for (int value = 0; value < histogram.Length; value++)
        {
            seen += histogram[value];
            if (seen > target)
            {
                return value;
            }
        }

        return histogram.Length - 1;
    }

    private sealed record Candidate(Rectangle Bounds, int PixelCount);

    private sealed record RemovalGroup(Rectangle FirstBounds, IReadOnlyList<Occurrence> Occurrences);

    private enum MotionComparison
    {
        Match,
        ConclusiveNonmatch,
        Ambiguous
    }

    private sealed class Occurrence
    {
        public Occurrence(
            Rectangle bounds,
            byte[] patch,
            int patchWidth,
            BackgroundRow[] backgroundRows)
        {
            Bounds = bounds;
            Patch = patch;
            PatchWidth = patchWidth;
            BackgroundRows = backgroundRows;
        }

        public Rectangle Bounds { get; }

        public byte[] Patch { get; }

        public int PatchWidth { get; }

        public BackgroundRow[] BackgroundRows { get; }

        public Bgra GetPixel(int x, int y)
        {
            int offset = ((y + RingThickness) * PatchWidth + x + RingThickness) * 4;
            return new Bgra(Patch[offset], Patch[offset + 1], Patch[offset + 2], Patch[offset + 3]);
        }

        public Bgra GetBackground(int x, int y) => BackgroundRows[y].Interpolate(x, Bounds.Width);
    }

    private readonly record struct BackgroundRow(Bgra Left, Bgra Right)
    {
        public Bgra Interpolate(int x, int width)
        {
            if (width <= 1)
            {
                return Left;
            }

            int denominator = width - 1;
            return new Bgra(
                Interpolate(Left.Blue, Right.Blue, x, denominator),
                Interpolate(Left.Green, Right.Green, x, denominator),
                Interpolate(Left.Red, Right.Red, x, denominator),
                Interpolate(Left.Alpha, Right.Alpha, x, denominator));
        }

        private static byte Interpolate(byte left, byte right, int numerator, int denominator) =>
            (byte)((left * (denominator - numerator) + right * numerator + denominator / 2) / denominator);
    }

    private readonly record struct Bgra(byte Blue, byte Green, byte Red, byte Alpha);

    private sealed class LockedBitmap : IDisposable
    {
        private readonly Bitmap _image;
        private readonly BitmapData _data;

        public LockedBitmap(Bitmap image)
        {
            if (image.PixelFormat != PixelFormat.Format32bppArgb)
            {
                throw new ArgumentException(
                    "Repeated overlay removal requires a 32-bit ARGB bitmap.",
                    nameof(image));
            }

            _image = image;
            Width = image.Width;
            Height = image.Height;
            _data = image.LockBits(
                new Rectangle(0, 0, Width, Height),
                ImageLockMode.ReadWrite,
                PixelFormat.Format32bppArgb);
        }

        public int Width { get; }

        public int Height { get; }

        public byte[] Read(Rectangle bounds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rowBytes = checked(bounds.Width * 4);
            byte[] pixels = new byte[checked(rowBytes * bounds.Height)];

            for (int y = 0; y < bounds.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IntPtr source = IntPtr.Add(
                    _data.Scan0,
                    checked((bounds.Y + y) * _data.Stride + bounds.X * 4));
                Marshal.Copy(source, pixels, y * rowBytes, rowBytes);
            }

            return pixels;
        }

        public void Fill(
            Rectangle bounds,
            IReadOnlyList<BackgroundRow> backgroundRows,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rowBytes = checked(bounds.Width * 4);
            byte[] row = new byte[rowBytes];

            for (int y = 0; y < bounds.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < bounds.Width; x++)
                {
                    Bgra pixel = backgroundRows[y].Interpolate(x, bounds.Width);
                    int offset = x * 4;
                    row[offset] = pixel.Blue;
                    row[offset + 1] = pixel.Green;
                    row[offset + 2] = pixel.Red;
                    row[offset + 3] = pixel.Alpha;
                }

                IntPtr destination = IntPtr.Add(
                    _data.Scan0,
                    checked((bounds.Y + y) * _data.Stride + bounds.X * 4));
                Marshal.Copy(row, 0, destination, rowBytes);
            }
        }

        public void Dispose() => _image.UnlockBits(_data);
    }
}
