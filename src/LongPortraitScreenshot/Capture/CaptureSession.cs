using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using LongPortraitScreenshot.Automation;
using LongPortraitScreenshot.Imaging;

namespace LongPortraitScreenshot.Capture;

internal static class CaptureSession
{
    private const int EscapeVirtualKey = 0x1B;
    private const int MaximumFrames = 256;
    private const long MaximumRawFramePixels = 100_000_000;
    private const double BottomScrollPercent = 99.95;
    private const double MinimumMovementPercent = 0.0001;
    private const int BoundsTolerancePixels = 2;
    private const int ScrollSettleTimeoutMilliseconds = 2_000;
    private const int ScrollPollMilliseconds = 50;
    private const int RenderDelayMilliseconds = 100;
    private const int MaximumIncrementalScrollAttempts = 256;
    private const int MaximumIncrementalScrollDurationMilliseconds = 20_000;
    private const double RequestedPositionTolerancePercent = 0.25;

    public static CaptureResult Capture(
        ScrollTarget target,
        CaptureOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        CaptureResult? result = null;
        ExceptionDispatchInfo? failure = null;

        Thread worker = new(() =>
        {
            try
            {
                using CancellationTokenSource captureCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using System.Threading.Timer escapeMonitor =
                    StartEscapeCancellationMonitor(captureCancellation);
                result = CaptureCore(target, options, captureCancellation.Token);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Long portrait screenshot capture"
        };

        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();

        failure?.Throw();
        return result ?? throw new InvalidOperationException("The screenshot worker stopped without producing a result.");
    }

    private static CaptureResult CaptureCore(
        ScrollTarget target,
        CaptureOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target.Element);
        ArgumentNullException.ThrowIfNull(target.ScrollPattern);

        Point originalCursor = Cursor.Position;
        ScrollState originalScroll = default;
        bool haveOriginalScroll = false;
        bool scrollMayHaveChanged = false;
        bool useIncrementalScrolling = false;

        try
        {
            ThrowIfCancelled(cancellationToken);
            originalScroll = ReadScrollState(target.ScrollPattern);
            haveOriginalScroll = true;

            if (!originalScroll.VerticallyScrollable
                || originalScroll.VerticalScrollPercent == ScrollPattern.NoScroll
                || originalScroll.VerticalViewSize >= 100.0)
            {
                throw new InvalidOperationException(
                    "The selected element does not currently expose a vertically scrollable viewport. " +
                    "Try dropping the target on the scrollbar or on a child inside the scrolling area.");
            }

            Rectangle resolvedBounds = target.Bounds;
            if (resolvedBounds.Width <= 0 || resolvedBounds.Height <= 0)
            {
                throw new InvalidOperationException(
                    "The selected scrolling control has no visible bounds. Make the window visible and try again.");
            }

            Rectangle captureBounds = ReadVisibleBounds(target.Element);
            EnsureBoundsStable(resolvedBounds, captureBounds);
            EnsureCaptureModeCanStart(captureBounds, originalScroll.VerticalViewSize, options.Mode);

            Cursor.Position = FindParkingPoint(captureBounds);
            scrollMayHaveChanged = true;
            ScrollState currentScroll = MoveToScrollPercent(
                target.ScrollPattern,
                originalScroll,
                originalScroll.HorizontalScrollPercent,
                requestedVerticalPercent: 0.0,
                requireRequestedPosition: true,
                ref useIncrementalScrolling,
                cancellationToken,
                checkEscape: true);

            if (currentScroll.VerticalScrollPercent > 0.25)
            {
                throw new InvalidOperationException(
                    $"The target would not scroll to its top (it stopped at {currentScroll.VerticalScrollPercent:0.##}%). " +
                    "Try a different scrolling container.");
            }

            List<CapturedFrame> frames = [];
            List<int> measuredVerticalShifts = [];
            long rawFramePixels = 0;
            long measuredStitchedHeight = captureBounds.Height;
            bool isPartial = false;
            int? finalFrameRowsToAppend = null;
            Bitmap? stitched = null;

            try
            {
                while (true)
                {
                    ThrowIfCancelled(cancellationToken);

                    Rectangle beforeCaptureBounds = ReadVisibleBounds(target.Element);
                    EnsureBoundsStable(captureBounds, beforeCaptureBounds);

                    if (frames.Count >= MaximumFrames)
                    {
                        if (CaptureSizePolicy.ShouldReturnPartialAtCaptureGuard(
                            options.Mode,
                            frames.Count))
                        {
                            isPartial = true;
                            break;
                        }

                        throw new InvalidOperationException(
                            $"Capture stopped after {MaximumFrames} frames to prevent a runaway scroll. " +
                            "Select a shorter scrolling region and try again.");
                    }

                    if (options.Mode != CaptureMode.Full)
                    {
                        long framePixels = CaptureSizePolicy.CalculatePixelCount(
                            captureBounds.Width,
                            captureBounds.Height);
                        long prospectiveRawPixels = checked(rawFramePixels + framePixels);
                        if (prospectiveRawPixels > MaximumRawFramePixels)
                        {
                            if (CaptureSizePolicy.ShouldReturnPartialAtCaptureGuard(
                                options.Mode,
                                frames.Count))
                            {
                                isPartial = true;
                                break;
                            }

                            throw new InvalidOperationException(
                                $"Capture would exceed the {MaximumRawFramePixels:N0}-pixel working-memory safety limit. " +
                                "Select a narrower or shorter scrolling region.");
                        }

                        rawFramePixels = prospectiveRawPixels;
                    }

                    Bitmap image = ScreenGrabber.Capture(captureBounds);
                    try
                    {
                        Rectangle afterCaptureBounds = ReadVisibleBounds(target.Element);
                        EnsureBoundsStable(captureBounds, afterCaptureBounds);
                        CapturedFrame capturedFrame = new(
                            image,
                            currentScroll.VerticalScrollPercent,
                            currentScroll.VerticalViewSize);

                        bool stopAfterFrame = false;
                        int? retainedShift = null;
                        if (options.Mode != CaptureMode.Full && frames.Count > 0)
                        {
                            int addedRows = VerticalStitcher.MeasureVerticalShift(
                                frames[^1],
                                capturedFrame,
                                frames.Count,
                                cancellationToken);
                            retainedShift = addedRows;

                            if (options.Mode == CaptureMode.Standard)
                            {
                                long candidateHeight = checked(measuredStitchedHeight + addedRows);
                                long candidatePixels = CaptureSizePolicy.CalculatePixelCount(
                                    captureBounds.Width,
                                    candidateHeight);
                                CaptureSizePolicy.EnsureStandardActualFits(candidatePixels);
                                measuredStitchedHeight = candidateHeight;
                            }
                            else
                            {
                                int acceptedRows = CaptureSizePolicy.GetSafeRowsToAppend(
                                    captureBounds.Width,
                                    measuredStitchedHeight,
                                    addedRows,
                                    out long acceptedHeight,
                                    out _);
                                if (acceptedRows == 0)
                                {
                                    image.Dispose();
                                    isPartial = true;
                                    break;
                                }

                                measuredStitchedHeight = acceptedHeight;
                                if (acceptedRows < addedRows)
                                {
                                    finalFrameRowsToAppend = acceptedRows;
                                    isPartial = true;
                                    stopAfterFrame = true;
                                }
                            }
                        }

                        frames.Add(capturedFrame);
                        if (retainedShift is int shift)
                        {
                            measuredVerticalShifts.Add(shift);
                        }

                        if (stopAfterFrame)
                        {
                            break;
                        }
                    }
                    catch
                    {
                        image.Dispose();
                        throw;
                    }

                    if (currentScroll.VerticalScrollPercent >= BottomScrollPercent)
                    {
                        break;
                    }

                    double step = CalculateScrollPercentStep(currentScroll.VerticalViewSize);
                    double requestedPercent = Math.Min(100.0, currentScroll.VerticalScrollPercent + step);
                    ScrollState nextScroll = MoveToScrollPercent(
                        target.ScrollPattern,
                        currentScroll,
                        currentScroll.HorizontalScrollPercent,
                        requestedPercent,
                        requireRequestedPosition: false,
                        ref useIncrementalScrolling,
                        cancellationToken,
                        checkEscape: true);
                    double actualMovement = nextScroll.VerticalScrollPercent - currentScroll.VerticalScrollPercent;
                    if (actualMovement <= MinimumMovementPercent)
                    {
                        if (currentScroll.VerticalScrollPercent >= 99.0)
                        {
                            break;
                        }

                        throw new InvalidOperationException(
                            $"The selected control stopped scrolling at {currentScroll.VerticalScrollPercent:0.##}% before reaching the bottom. " +
                            "The control may use custom scrolling; try selecting a different ancestor.");
                    }

                    currentScroll = nextScroll;
                }

                ThrowIfCancelled(cancellationToken);
                CancellationToken processingToken = cancellationToken;

                stitched = VerticalStitcher.Stitch(
                    frames,
                    options.Mode == CaptureMode.Full
                        ? long.MaxValue
                        : CaptureSizePolicy.SafePixelLimit,
                    processingToken,
                    finalFrameRowsToAppend,
                    options.Mode == CaptureMode.Full ? null : measuredVerticalShifts);
                if (options.CropVerticalScrollIndicator)
                {
                    processingToken.ThrowIfCancellationRequested();
                    int scrollBarWidth = ScrollbarCropper.GetVerticalScrollBarWidth(captureBounds);
                    Bitmap cropped = ScrollbarCropper.CropRight(stitched, scrollBarWidth);
                    stitched.Dispose();
                    stitched = cropped;
                    processingToken.ThrowIfCancellationRequested();
                }

                if (options.TrimEmptyHorizontalSpace)
                {
                    Bitmap cropped = EmptySpaceCropper.Trim(stitched, processingToken);
                    stitched.Dispose();
                    stitched = cropped;
                }

                processingToken.ThrowIfCancellationRequested();

                CaptureResult result = new(
                    stitched,
                    string.IsNullOrWhiteSpace(target.DisplayName) ? "Scrolling control" : target.DisplayName,
                    frames.Count,
                    isPartial);
                stitched = null;
                return result;
            }
            finally
            {
                stitched?.Dispose();
                foreach (CapturedFrame frame in frames)
                {
                    frame.Image.Dispose();
                }
            }
        }
        finally
        {
            if (haveOriginalScroll && scrollMayHaveChanged)
            {
                try
                {
                    ScrollState restoreStart = ReadScrollState(target.ScrollPattern);
                    _ = MoveToScrollPercent(
                        target.ScrollPattern,
                        restoreStart,
                        originalScroll.HorizontalScrollPercent,
                        originalScroll.VerticalScrollPercent,
                        requireRequestedPosition: true,
                        ref useIncrementalScrolling,
                        CancellationToken.None,
                        checkEscape: false);
                }
                catch
                {
                    // The target may have closed during capture. Preserve the primary result or failure.
                }
            }

            try
            {
                Cursor.Position = originalCursor;
            }
            catch
            {
                // Cursor restoration must not hide the capture's primary result or failure.
            }
        }
    }

    private static double CalculateScrollPercentStep(double viewSize)
    {
        if (viewSize <= 0.0 || viewSize >= 100.0 || !double.IsFinite(viewSize))
        {
            throw new InvalidOperationException(
                $"The target reported an invalid vertical view size ({viewSize:0.###}%). Try a different scrolling container.");
        }

        // ScrollPercent spans the scrollable range, not the full content height. This converts
        // 65% of one viewport into that coordinate system and leaves ample image overlap.
        return 65.0 * viewSize / (100.0 - viewSize);
    }

    private static ScrollState WaitForScrollToSettle(
        ScrollPattern scrollPattern,
        double startPercent,
        double requestedPercent,
        CancellationToken cancellationToken,
        bool checkEscape)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ScrollState previous = ReadScrollState(scrollPattern);
        ScrollSettleTracker tracker = new(
            startPercent,
            requestedPercent,
            previous.VerticalScrollPercent,
            MinimumMovementPercent);
        long? movementStartedAtMilliseconds = tracker.HasDepartedStart ? 0 : null;

        while (stopwatch.ElapsedMilliseconds < GetWaitDeadlineMilliseconds(movementStartedAtMilliseconds))
        {
            ThrowIfCancelled(cancellationToken, checkEscape);
            Thread.Sleep(ScrollPollMilliseconds);
            ScrollState current = ReadScrollState(scrollPattern);

            bool wasWaitingForMovement = !tracker.HasDepartedStart;
            bool readyForRenderCheck = tracker.Observe(current.VerticalScrollPercent);
            if (wasWaitingForMovement && tracker.HasDepartedStart)
            {
                movementStartedAtMilliseconds = stopwatch.ElapsedMilliseconds;
            }

            previous = current;
            if (readyForRenderCheck)
            {
                Thread.Sleep(RenderDelayMilliseconds);
                ThrowIfCancelled(cancellationToken, checkEscape);
                ScrollState afterRender = ReadScrollState(scrollPattern);
                previous = afterRender;
                if (tracker.ConfirmAfterRender(afterRender.VerticalScrollPercent))
                {
                    return afterRender;
                }
            }
        }

        if (!tracker.HasDepartedStart
            && Math.Abs(requestedPercent - startPercent) > MinimumMovementPercent)
        {
            return previous;
        }

        throw new InvalidOperationException(
            "The selected control did not finish scrolling within two seconds. Disable smooth scrolling or animations and retry.");
    }

    private static long GetWaitDeadlineMilliseconds(long? movementStartedAtMilliseconds) =>
        movementStartedAtMilliseconds is long movementStartedAt
            ? movementStartedAt + ScrollSettleTimeoutMilliseconds
            : ScrollSettleTimeoutMilliseconds;

    private static ScrollState MoveToScrollPercent(
        ScrollPattern scrollPattern,
        ScrollState start,
        double requestedHorizontalPercent,
        double requestedVerticalPercent,
        bool requireRequestedPosition,
        ref bool useIncrementalScrolling,
        CancellationToken cancellationToken,
        bool checkEscape)
    {
        ScrollState fallbackStart = start;
        if (!useIncrementalScrolling)
        {
            try
            {
                SetScrollPercent(
                    scrollPattern,
                    requestedHorizontalPercent,
                    requestedVerticalPercent);
            }
            catch (InvalidOperationException)
            {
                // Some providers advertise SetScrollPercent but do not implement it.
                useIncrementalScrolling = true;
                fallbackStart = ReadScrollState(scrollPattern);
            }

            if (!useIncrementalScrolling)
            {
                ScrollState settled = WaitForScrollToSettle(
                    scrollPattern,
                    start.VerticalScrollPercent,
                    requestedVerticalPercent,
                    cancellationToken,
                    checkEscape);

                bool requestSatisfied = requireRequestedPosition
                    ? IsRequestedPositionReached(settled.VerticalScrollPercent, requestedVerticalPercent)
                    : HasDirectionalProgress(
                        start.VerticalScrollPercent,
                        settled.VerticalScrollPercent,
                        requestedVerticalPercent);
                if (requestSatisfied)
                {
                    return settled;
                }

                fallbackStart = settled;
                useIncrementalScrolling = true;
            }
        }

        return ScrollIncrementallyToPercent(
            scrollPattern,
            fallbackStart,
            requestedVerticalPercent,
            requireRequestedPosition,
            cancellationToken,
            checkEscape);
    }

    private static ScrollState ScrollIncrementallyToPercent(
        ScrollPattern scrollPattern,
        ScrollState start,
        double requestedVerticalPercent,
        bool requireRequestedPosition,
        CancellationToken cancellationToken,
        bool checkEscape)
    {
        int direction = Math.Sign(requestedVerticalPercent - start.VerticalScrollPercent);
        if (direction == 0)
        {
            return start;
        }

        ScrollAmount amount = GetIncrementalScrollAmount(
            direction,
            requestedVerticalPercent,
            requireRequestedPosition);
        ScrollState current = start;
        Stopwatch stopwatch = Stopwatch.StartNew();

        for (int attempt = 0;
            attempt < MaximumIncrementalScrollAttempts
                && stopwatch.ElapsedMilliseconds < MaximumIncrementalScrollDurationMilliseconds;
            attempt++)
        {
            if (HasReachedRequestedPercent(
                start.VerticalScrollPercent,
                current.VerticalScrollPercent,
                requestedVerticalPercent))
            {
                return current;
            }

            ThrowIfCancelled(cancellationToken, checkEscape);
            try
            {
                ScrollByAmount(scrollPattern, amount);
            }
            catch (InvalidOperationException)
            {
                // Providers may reject an incremental request at an edge. Re-read the
                // state so the caller can decide whether that edge is an acceptable end.
                return ReadScrollState(scrollPattern);
            }

            ScrollState next = WaitForScrollToSettle(
                scrollPattern,
                current.VerticalScrollPercent,
                requestedVerticalPercent,
                cancellationToken,
                checkEscape);

            if (!HasDirectionalProgress(
                current.VerticalScrollPercent,
                next.VerticalScrollPercent,
                requestedVerticalPercent))
            {
                return current;
            }

            current = next;
        }

        return current;
    }

    private static ScrollAmount GetIncrementalScrollAmount(
        int direction,
        double requestedVerticalPercent,
        bool requireRequestedPosition)
    {
        bool requestedEndpoint = requestedVerticalPercent <= RequestedPositionTolerancePercent
            || requestedVerticalPercent >= 100.0 - RequestedPositionTolerancePercent;
        if (requireRequestedPosition && requestedEndpoint)
        {
            return direction > 0
                ? ScrollAmount.LargeIncrement
                : ScrollAmount.LargeDecrement;
        }

        return direction > 0
            ? ScrollAmount.SmallIncrement
            : ScrollAmount.SmallDecrement;
    }

    private static bool IsRequestedPositionReached(
        double currentPercent,
        double requestedPercent) =>
        Math.Abs(currentPercent - requestedPercent) <= RequestedPositionTolerancePercent;

    private static bool HasDirectionalProgress(
        double startPercent,
        double currentPercent,
        double requestedPercent)
    {
        int direction = Math.Sign(requestedPercent - startPercent);
        return direction == 0
            || direction * (currentPercent - startPercent) > MinimumMovementPercent;
    }

    private static bool HasReachedRequestedPercent(
        double startPercent,
        double currentPercent,
        double requestedPercent)
    {
        int direction = Math.Sign(requestedPercent - startPercent);
        return direction == 0
            || direction * (currentPercent - requestedPercent) >= -RequestedPositionTolerancePercent;
    }

    private static ScrollState ReadScrollState(ScrollPattern scrollPattern)
    {
        try
        {
            ScrollPattern.ScrollPatternInformation current = scrollPattern.Current;
            return new ScrollState(
                current.VerticallyScrollable,
                current.HorizontalScrollPercent,
                current.VerticalScrollPercent,
                current.VerticalViewSize);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            throw new InvalidOperationException(
                "The selected scrolling control is no longer available. Keep its window open and retry.",
                exception);
        }
    }

    private static void SetScrollPercent(
        ScrollPattern scrollPattern,
        double horizontalScrollPercent,
        double verticalScrollPercent)
    {
        try
        {
            scrollPattern.SetScrollPercent(horizontalScrollPercent, verticalScrollPercent);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            throw new InvalidOperationException(
                "Windows found the scrolling control but could not change its scroll position. " +
                "Try selecting the control's scrollbar or a different ancestor.",
                exception);
        }
    }

    private static void ScrollByAmount(ScrollPattern scrollPattern, ScrollAmount verticalAmount)
    {
        try
        {
            scrollPattern.Scroll(ScrollAmount.NoAmount, verticalAmount);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            throw new InvalidOperationException(
                "Windows found the scrolling control but could not scroll it incrementally. " +
                "Try selecting the control's scrollbar or a different ancestor.",
                exception);
        }
    }

    private static Rectangle ReadVisibleBounds(AutomationElement element)
    {
        try
        {
            AutomationElement.AutomationElementInformation current = element.Current;
            if (current.IsOffscreen)
            {
                throw new InvalidOperationException(
                    "The selected scrolling control became hidden or minimized. Keep it visible throughout capture.");
            }

            var bounds = current.BoundingRectangle;
            if (bounds.IsEmpty
                || !double.IsFinite(bounds.Left)
                || !double.IsFinite(bounds.Top)
                || !double.IsFinite(bounds.Right)
                || !double.IsFinite(bounds.Bottom))
            {
                throw new InvalidOperationException(
                    "The selected scrolling control no longer reports a usable on-screen rectangle.");
            }

            int left = checked((int)Math.Floor(bounds.Left));
            int top = checked((int)Math.Floor(bounds.Top));
            int right = checked((int)Math.Ceiling(bounds.Right));
            int bottom = checked((int)Math.Ceiling(bounds.Bottom));
            return Rectangle.FromLTRB(left, top, right, bottom);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or COMException or OverflowException)
        {
            throw new InvalidOperationException(
                "The selected scrolling control is no longer available. Keep its window open and stationary, then retry.",
                exception);
        }
    }

    private static void EnsureBoundsStable(Rectangle expected, Rectangle actual)
    {
        bool stable = Math.Abs(expected.Left - actual.Left) <= BoundsTolerancePixels
            && Math.Abs(expected.Top - actual.Top) <= BoundsTolerancePixels
            && Math.Abs(expected.Right - actual.Right) <= BoundsTolerancePixels
            && Math.Abs(expected.Bottom - actual.Bottom) <= BoundsTolerancePixels;

        if (!stable)
        {
            throw new InvalidOperationException(
                $"The selected control moved or resized during capture ({expected.Width} x {expected.Height} became " +
                $"{actual.Width} x {actual.Height}). Keep the target window stationary and retry.");
        }
    }

    internal static void EnsureCaptureModeCanStart(
        Rectangle bounds,
        double viewSize,
        CaptureMode mode)
    {
        switch (mode)
        {
            case CaptureMode.Standard:
                long firstFramePixels = CaptureSizePolicy.CalculatePixelCount(
                    bounds.Width,
                    bounds.Height);
                CaptureSizePolicy.EnsureStandardEstimateFits(firstFramePixels);

                long estimatedPixels = EstimateOutputPixels(bounds, viewSize);
                if (estimatedPixels >= 0)
                {
                    CaptureSizePolicy.EnsureStandardEstimateFits(estimatedPixels);
                }

                break;

            case CaptureMode.SafePortion:
                long safeFirstFramePixels = CaptureSizePolicy.CalculatePixelCount(
                    bounds.Width,
                    bounds.Height);
                if (!CaptureSizePolicy.FitsWithinSafeLimit(safeFirstFramePixels))
                {
                    throw new CaptureSizeLimitExceededException(
                        safeFirstFramePixels,
                        CaptureSizePolicy.SafePixelLimit);
                }

                break;

            case CaptureMode.Full:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown capture mode.");
        }
    }

    internal static long EstimateOutputPixels(Rectangle bounds, double viewSize)
    {
        if (viewSize <= 0.0 || viewSize >= 100.0 || !double.IsFinite(viewSize))
        {
            return -1;
        }

        double estimatedHeightValue = Math.Ceiling(bounds.Height * 100.0 / viewSize);
        if (!double.IsFinite(estimatedHeightValue)
            || estimatedHeightValue > long.MaxValue
            || estimatedHeightValue > long.MaxValue / (double)bounds.Width)
        {
            return long.MaxValue;
        }

        long estimatedHeight = checked((long)estimatedHeightValue);
        return CaptureSizePolicy.CalculatePixelCount(bounds.Width, estimatedHeight);
    }

    private static Point FindParkingPoint(Rectangle captureBounds)
    {
        Rectangle virtualScreen = SystemInformation.VirtualScreen;
        Point[] candidates =
        [
            new Point(virtualScreen.Left + 1, virtualScreen.Top + 1),
            new Point(virtualScreen.Right - 2, virtualScreen.Top + 1),
            new Point(virtualScreen.Left + 1, virtualScreen.Bottom - 2),
            new Point(virtualScreen.Right - 2, virtualScreen.Bottom - 2)
        ];

        foreach (Point candidate in candidates)
        {
            if (!captureBounds.Contains(candidate))
            {
                return candidate;
            }
        }

        return Cursor.Position;
    }

    private static System.Threading.Timer StartEscapeCancellationMonitor(
        CancellationTokenSource cancellation)
    {
        return new System.Threading.Timer(
            _ =>
            {
                if ((GetAsyncKeyState(EscapeVirtualKey) & 0x8000) == 0)
                {
                    return;
                }

                try
                {
                    cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // A queued timer callback may finish after the processing scope exits.
                }
            },
            state: null,
            dueTime: 0,
            period: ScrollPollMilliseconds);
    }

    private static void ThrowIfCancelled(
        CancellationToken cancellationToken,
        bool checkEscape = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (checkEscape && (GetAsyncKeyState(EscapeVirtualKey) & 0x8000) != 0)
        {
            throw new OperationCanceledException("Screenshot capture was cancelled with Escape.");
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private readonly record struct ScrollState(
        bool VerticallyScrollable,
        double HorizontalScrollPercent,
        double VerticalScrollPercent,
        double VerticalViewSize);
}
