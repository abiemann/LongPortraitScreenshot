using System.Drawing;

namespace LongPortraitScreenshot.Capture;

public sealed record CaptureOptions(
    bool CropVerticalScrollIndicator,
    bool TrimEmptyHorizontalSpace)
{
    internal CaptureOptions(
        bool cropVerticalScrollIndicator,
        bool trimEmptyHorizontalSpace,
        CaptureMode mode)
        : this(
            cropVerticalScrollIndicator,
            trimEmptyHorizontalSpace)
    {
        Mode = mode;
    }

    internal CaptureMode Mode { get; init; } = CaptureMode.Standard;
}

internal enum CaptureMode
{
    Standard,
    Full,
    SafePortion
}

internal sealed class CaptureSizeLimitExceededException : InvalidOperationException
{
    public CaptureSizeLimitExceededException(long estimatedPixels, long safePixelLimit)
        : this(estimatedPixels, safePixelLimit, isEstimate: true)
    {
    }

    public CaptureSizeLimitExceededException(
        long estimatedPixels,
        long safePixelLimit,
        bool isEstimate)
        : base(
            $"The complete screenshot {(isEstimate ? "is estimated at" : "has reached")} " +
            $"{estimatedPixels:N0} pixels, exceeding the " +
            $"{safePixelLimit:N0}-pixel safety limit.")
    {
        EstimatedPixels = estimatedPixels;
        SafePixelLimit = safePixelLimit;
        IsEstimate = isEstimate;
    }

    public long EstimatedPixels { get; }

    public long SafePixelLimit { get; }

    public bool IsEstimate { get; }
}

internal static class CaptureSizePolicy
{
    internal const long SafePixelLimit = 40_000_000;

    internal static void EnsureStandardEstimateFits(long estimatedPixels)
    {
        if (estimatedPixels > SafePixelLimit)
        {
            throw new CaptureSizeLimitExceededException(estimatedPixels, SafePixelLimit);
        }
    }

    internal static void EnsureStandardActualFits(long actualPixels)
    {
        if (actualPixels > SafePixelLimit)
        {
            throw new CaptureSizeLimitExceededException(
                actualPixels,
                SafePixelLimit,
                isEstimate: false);
        }
    }

    internal static bool FitsWithinSafeLimit(long pixels) => pixels <= SafePixelLimit;

    internal static int GetSafeRowsToAppend(
        int width,
        long currentHeight,
        int availableRows,
        out long acceptedHeight,
        out long acceptedPixels)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (currentHeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentHeight));
        }

        if (availableRows < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableRows));
        }

        long maximumFullWidthHeight = SafePixelLimit / width;
        long remainingRows = Math.Max(0, maximumFullWidthHeight - currentHeight);
        int acceptedRows = (int)Math.Min(availableRows, remainingRows);
        acceptedHeight = checked(currentHeight + acceptedRows);
        acceptedPixels = CalculatePixelCount(width, acceptedHeight);
        return acceptedRows;
    }

    internal static bool ShouldReturnPartialAtCaptureGuard(
        CaptureMode mode,
        int validFrameCount) =>
        mode == CaptureMode.SafePortion && validFrameCount > 0;

    internal static long CalculatePixelCount(int width, long height)
    {
        try
        {
            return checked((long)width * height);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                "The screenshot dimensions exceed Windows bitmap limits.",
                exception);
        }
    }
}

public sealed class CaptureResult : IDisposable
{
    public CaptureResult(Bitmap image, string targetName, int frameCount)
        : this(image, targetName, frameCount, isPartial: false)
    {
    }

    public CaptureResult(Bitmap image, string targetName, int frameCount, bool isPartial)
    {
        ArgumentNullException.ThrowIfNull(image);

        Image = image;
        TargetName = string.IsNullOrWhiteSpace(targetName) ? "Scrolling control" : targetName;
        FrameCount = frameCount;
        IsPartial = isPartial;
    }

    public Bitmap Image { get; }

    public string TargetName { get; }

    public int FrameCount { get; }

    public bool IsPartial { get; }

    public void Dispose() => Image.Dispose();
}
