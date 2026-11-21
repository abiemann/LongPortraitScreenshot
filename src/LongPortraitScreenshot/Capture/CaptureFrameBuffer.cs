using LongPortraitScreenshot.Imaging;

namespace LongPortraitScreenshot.Capture;

// Owns the consecutive viewports whose overlap has already been verified.
internal sealed class CaptureFrameBuffer(int maximumFrames) : IDisposable
{
    private readonly List<CapturedFrame> _frames = new(maximumFrames);
    private readonly List<int> _shifts = new(maximumFrames);
    private readonly List<int> _seamRows = new(maximumFrames);

    public int Count => _frames.Count;
    public CapturedFrame this[int index] => _frames[index];

    public void Add(CapturedFrame frame, VerticalStitcher.VerticalAlignment? alignment)
    {
        _frames.Add(frame);
        if (alignment is { } measured)
        {
            _shifts.Add(measured.Shift);
            _seamRows.Add(measured.CompositionSourceY);
        }
    }

    public static bool CanRecover(Exception failure, CancellationToken token) =>
        !token.IsCancellationRequested && failure is not (OperationCanceledException or CaptureSizeLimitExceededException);

    public CaptureResult Finish(CaptureOptions options, Rectangle bounds, string targetName,
        int? finalRows, bool isPartial, string? failureReason, CancellationToken token)
    {
        Bitmap? image = null;
        try
        {
            int frameCount;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                frameCount = Count;
                if (frameCount == 1)
                {
                    // Transfer the original viewport even if no larger bitmap can be allocated.
                    image = _frames[0].Image;
                    _frames.Clear();
                    break;
                }

                try
                {
                    image = VerticalStitcher.Stitch(_frames,
                        options.Mode == CaptureMode.Full ? long.MaxValue : CaptureSizePolicy.SafePixelLimit,
                        token, finalRows, _shifts, _seamRows);
                    break;
                }
                catch (Exception failure) when (frameCount > 1 && CanRecover(failure, token))
                {
                    failureReason ??= failure.Message;
                    // Free the tail before attempting a smaller output. Only the original
                    // last viewport could have had a partially accepted row count.
                    DiscardAfter((frameCount + 1) / 2);
                    finalRows = null;
                }
            }

            DiscardAfter(0);
            try
            {
                token.ThrowIfCancellationRequested();
                if (options.CropVerticalScrollIndicator)
                {
                    int width = ScrollbarCropper.GetVerticalScrollBarWidth(bounds);
                    Bitmap cropped = ScrollbarCropper.CropRight(image, width);
                    image.Dispose();
                    image = cropped;
                }

                if (options.TrimEmptyHorizontalSpace)
                {
                    Bitmap trimmed = EmptySpaceCropper.Trim(image, token);
                    image.Dispose();
                    image = trimmed;
                }
            }
            catch (Exception failure) when (CanRecover(failure, token))
            {
                // The last successfully composed or cropped bitmap is still usable.
                failureReason ??= failure.Message;
            }

            token.ThrowIfCancellationRequested();
            CaptureResult result = new(image, targetName, frameCount, isPartial, failureReason);
            image = null;
            return result;
        }
        finally
        {
            image?.Dispose();
        }
    }

    public void Dispose() => DiscardAfter(0);

    private void DiscardAfter(int frameCount)
    {
        for (int i = frameCount; i < Count; i++)
        {
            _frames[i].Image.Dispose();
        }
        _frames.RemoveRange(frameCount, Count - frameCount);
        int seamCount = Math.Max(0, frameCount - 1);
        _shifts.RemoveRange(seamCount, _shifts.Count - seamCount);
        _seamRows.RemoveRange(seamCount, _seamRows.Count - seamCount);
    }
}
