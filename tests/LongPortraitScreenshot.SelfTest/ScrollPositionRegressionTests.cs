using LongPortraitScreenshot.Capture;

namespace LongPortraitScreenshot.SelfTest;

internal static class ScrollPositionRegressionTests
{
    public static void Run()
    {
        CaptureIncludesFinalRowsAtAlmostOneHundredPercent();
        CaptureRejectsAStallBeforeTheEndpoint();
        SmallIncrementalRequestsMoveInBothDirections();
        EndpointRequestsReachTheActualTopAndBottom();
        SmallFinalMovementStillCountsAsProgress();
        EdgePageGrowthPreservesMeasuredScrollProgress();
        ExtentChangesWithoutMotionDoNotSettleOrAdvance();
        RebasedComparisonsPreserveOffsetsBeyondAnOldBottom();
    }

    private static void CaptureIncludesFinalRowsAtAlmostOneHundredPercent()
    {
        const int contentHeight = 19_859;
        const int viewportHeight = 1_000;
        List<int> capturedOffsets = SimulateCapture(contentHeight, viewportHeight);

        Require(capturedOffsets[^2] == 18_850,
            "The regression must capture the position that previously stopped nine rows early.");
        Require(capturedOffsets[^1] + viewportHeight == contentHeight,
            "A complete capture must include the final nine rows and reach the actual bottom.");
        Require(capturedOffsets[^1] - capturedOffsets[^2] == 9,
            "The final endpoint request must preserve even a short final movement.");
    }

    private static void CaptureRejectsAStallBeforeTheEndpoint()
    {
        bool rejectedStall = false;
        try
        {
            _ = SimulateCapture(contentHeight: 19_859, viewportHeight: 1_000, stallAtOffset: 18_850);
        }
        catch (InvalidOperationException exception) when (exception.Message == "Capture stopped before the bottom.")
        {
            rejectedStall = true;
        }

        Require(rejectedStall,
            "A provider that stops above 99% must fail instead of returning a falsely complete capture.");
    }

    private static List<int> SimulateCapture(int contentHeight, int viewportHeight, int? stallAtOffset = null)
    {
        int scrollRange = contentHeight - viewportHeight;
        double viewSize = viewportHeight * 100.0 / contentHeight;
        int offset = 0;
        List<int> capturedOffsets = [];

        for (int frame = 0; frame < 256; frame++)
        {
            capturedOffsets.Add(offset);
            double currentPercent = offset * 100.0 / scrollRange;
            if (ScrollPositionPolicy.IsAtBottom(currentPercent))
            {
                return capturedOffsets;
            }

            double requestedPercent = Math.Min(
                100.0,
                currentPercent + ScrollPositionPolicy.CalculateScrollPercentStep(viewSize));
            int nextOffset = offset == stallAtOffset
                ? offset
                : (int)Math.Round(requestedPercent * scrollRange / 100.0);
            double nextPercent = nextOffset * 100.0 / scrollRange;
            if (!ScrollPositionPolicy.HasDirectionalProgress(currentPercent, nextPercent, requestedPercent))
            {
                throw new InvalidOperationException("Capture stopped before the bottom.");
            }

            offset = nextOffset;
        }

        throw new InvalidOperationException("Capture exceeded its frame limit.");
    }

    private static void SmallIncrementalRequestsMoveInBothDirections()
    {
        double requestedMovement = ScrollPositionPolicy.CalculateScrollPercentStep(viewSize: 0.3);
        Require(requestedMovement < 0.25,
            "The regression step must be smaller than the former fixed tolerance.");

        foreach (int direction in new[] { -1, 1 })
        {
            const double startPercent = 50.0;
            double requestedPercent = startPercent + direction * requestedMovement;
            double currentPercent = startPercent;
            int scrollCalls = 0;
            while (!ScrollPositionPolicy.HasReachedRequestedPercent(startPercent, currentPercent, requestedPercent))
            {
                Require(scrollCalls < 4, "Incremental scrolling must finish after reaching its request.");
                scrollCalls++;
                currentPercent = startPercent + direction * requestedMovement * scrollCalls / 4;
            }

            Require(scrollCalls == 4,
                "A small request must scroll far enough to reach its target instead of accepting the initial position.");
            Require(!ScrollPositionPolicy.HasReachedRequestedPercent(
                    startPercent, startPercent - direction * requestedMovement, requestedPercent),
                "Movement away from the request must not satisfy incremental scrolling.");
            Require(ScrollPositionPolicy.HasReachedRequestedPercent(
                    startPercent, requestedPercent + direction * requestedMovement, requestedPercent),
                "Incremental scrolling may cross an interior request when a provider scrolls in discrete units.");
        }
    }

    private static void EndpointRequestsReachTheActualTopAndBottom()
    {
        foreach (double shortfall in new[] { 0.05, 0.001, 0.000001 })
        {
            Require(!ScrollPositionPolicy.IsAtBottom(100.0 - shortfall),
                "A near-bottom position must not complete capture.");
            Require(!ScrollPositionPolicy.IsRequestedPositionReached(100.0 - shortfall, 100.0),
                "Direct endpoint scrolling must not accept a position short of the bottom.");
            Require(!ScrollPositionPolicy.IsRequestedPositionReached(shortfall, 0.0),
                "Direct endpoint scrolling must not accept a position short of the top.");
            Require(!ScrollPositionPolicy.HasReachedRequestedPercent(99.0, 100.0 - shortfall, 100.0),
                "Incremental endpoint scrolling must continue to the bottom.");
            Require(!ScrollPositionPolicy.HasReachedRequestedPercent(1.0, shortfall, 0.0),
                "Incremental endpoint scrolling must continue to the top.");
        }

        Require(ScrollPositionPolicy.IsAtBottom(100.0)
                && ScrollPositionPolicy.HasReachedRequestedPercent(99.0, 100.0, 100.0)
                && ScrollPositionPolicy.HasReachedRequestedPercent(1.0, 0.0, 0.0),
            "Actual endpoints must satisfy the request.");
        Require(ScrollPositionPolicy.HasReachedRequestedPercent(100.0, 100.0, 100.0),
            "An already satisfied request does not require additional scrolling.");
    }

    private static void SmallFinalMovementStillCountsAsProgress()
    {
        const double startPercent = 99.99999;
        Require(ScrollPositionPolicy.HasDirectionalProgress(startPercent, 100.0, 100.0),
            "The last step must count even when it is below the usual movement threshold.");
        Require(!ScrollPositionPolicy.HasDirectionalProgress(startPercent, startPercent, 100.0),
            "No movement must never satisfy a pending endpoint request.");
    }

    private static void EdgePageGrowthPreservesMeasuredScrollProgress()
    {
        // Edge grew the page during this 1,485-pixel viewport capture.
        const double start = 96.281374, initialView = 5.203588, reported = 92.23205, grownView = 4.82582;
        double requested = start + ScrollPositionPolicy.CalculateScrollPercentStep(initialView);
        double actual = ScrollPositionPolicy.RebasePercent(reported, grownView, initialView);
        double deltaPixels = (actual - start) * 1485 * (100 / initialView - 1) / 100;

        Require(Math.Abs(deltaPixels - 965) < 0.1, "Edge's lower percentage must account for 965px of forward movement.");
        Require(ScrollPositionPolicy.HasDirectionalProgress(start, actual, requested)
                && ScrollPositionPolicy.HasReachedRequestedPercent(start, actual, requested),
            "Growing the scroll range must not trigger another relative scroll after reaching the request.");

        ScrollSettleTracker tracker = new(start, requested, actual, 0.0001);
        Require(!tracker.TryBeginAbsoluteRetry(350) && !tracker.Observe(actual)
                && tracker.Observe(actual) && tracker.ConfirmAfterRender(actual),
            "Edge's move should settle without issuing another absolute scroll.");
        double restore = ScrollPositionPolicy.RebasePercent(start, initialView, grownView);
        Require(Math.Abs(restore - 88.937159) < 0.00001,
            "Restoring the original pixel offset must target its position in the new extent.");
    }

    private static void ExtentChangesWithoutMotionDoNotSettleOrAdvance()
    {
        const double start = 50, request = 60, initialView = 20, grownView = 10;
        double still = ScrollPositionPolicy.RebasePercent(start, initialView, grownView);
        double actual = ScrollPositionPolicy.RebasePercent(still, grownView, initialView);
        ScrollSettleTracker tracker = new(start, request, start, 0.0001);
        Require(!tracker.Observe(actual) && !tracker.HasDepartedStart && tracker.TryBeginAbsoluteRetry(350),
            "A percentage reduced by page growth alone must not look like pixel movement.");
        Require(!ScrollPositionPolicy.HasDirectionalProgress(start, actual, request),
            "An unchanged pixel offset must not count as an accepted capture step.");
        double retry = ScrollPositionPolicy.RebasePercent(request, initialView, grownView);
        Require(Math.Abs(retry - 26.6666666667) < 1e-8,
            "The retry must request the original physical destination in the latest scroll range.");
        double sameReading = ScrollPositionPolicy.RebasePercent(start, grownView, initialView);
        Require(ScrollPositionPolicy.HasDirectionalProgress(start, sameReading, request),
            "The same reported percentage can be forward movement after the document grows.");
    }

    private static void RebasedComparisonsPreserveOffsetsBeyondAnOldBottom()
    {
        double oldPercent = ScrollPositionPolicy.RebasePercent(80, 10, 20);
        Require(oldPercent == 180 && ScrollPositionPolicy.RebasePercent(oldPercent, 20, 10) == 80,
            "A comparison must preserve positions past the original scrollable extent.");
        foreach (double invalidView in new[] { 0, 100, -1, double.NaN, double.PositiveInfinity })
        {
            Require(ScrollPositionPolicy.RebasePercent(40, invalidView, 5) == 40
                    && ScrollPositionPolicy.RebasePercent(40, 5, invalidView) == 40,
                "Unavailable scroll extents must preserve the original reading.");
        }
        Require(ScrollPositionPolicy.RebasePercent(-1, 5, 10) == -1,
            "The UI Automation NoScroll sentinel must be preserved.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
