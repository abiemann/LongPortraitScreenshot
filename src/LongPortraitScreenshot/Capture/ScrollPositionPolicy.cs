namespace LongPortraitScreenshot.Capture;

internal static class ScrollPositionPolicy
{
    private const double EndpointTolerancePercent = 0.000000001;
    private const double RequestedPositionTolerancePercent = 0.25;
    private const double MinimumMovementPercent = 0.0001;

    public static bool IsAtBottom(double currentPercent) =>
        currentPercent >= 100.0 - EndpointTolerancePercent;

    public static bool IsEndpoint(double requestedPercent) =>
        requestedPercent <= EndpointTolerancePercent || IsAtBottom(requestedPercent);

    // UI Automation reports the offset as a fraction of the scrollable range.
    // Keep the same physical offset when a lazy-loaded document changes that range.
    public static double RebasePercent(double percent, double fromViewSize, double toViewSize)
    {
        if (fromViewSize == toViewSize || percent <= 0 || !double.IsFinite(percent)
            || !(fromViewSize > 0 && fromViewSize < 100) || !(toViewSize > 0 && toViewSize < 100))
        {
            return percent;
        }

        double rangeRatio = (toViewSize / fromViewSize) * ((100 - fromViewSize) / (100 - toViewSize));
        return percent * rangeRatio;
    }

    public static double CalculateScrollPercentStep(double viewSize)
    {
        if (viewSize <= 0.0 || viewSize >= 100.0 || !double.IsFinite(viewSize))
        {
            throw new InvalidOperationException(
                $"The target reported an invalid vertical view size ({viewSize:0.###}%). Try a different scrolling container.");
        }

        // ScrollPercent spans the scrollable range, not the full content height.
        // Moving 65% of a viewport leaves enough overlap for image alignment.
        return 65.0 * viewSize / (100.0 - viewSize);
    }

    public static bool IsRequestedPositionReached(double currentPercent, double requestedPercent) =>
        Math.Abs(currentPercent - requestedPercent) <= (IsEndpoint(requestedPercent)
            ? EndpointTolerancePercent
            : RequestedPositionTolerancePercent);

    public static bool HasDirectionalProgress(
        double startPercent,
        double currentPercent,
        double requestedPercent)
    {
        double requestedMovement = requestedPercent - startPercent;
        int direction = Math.Sign(requestedMovement);
        double movementTolerance = Math.Min(MinimumMovementPercent, Math.Abs(requestedMovement) * 0.1);
        return direction == 0 || direction * (currentPercent - startPercent) > movementTolerance;
    }

    public static bool HasReachedRequestedPercent(
        double startPercent,
        double currentPercent,
        double requestedPercent)
    {
        int direction = Math.Sign(requestedPercent - startPercent);
        if (direction == 0)
        {
            return true;
        }

        // A fixed tolerance can exceed the entire step in a long document. Require
        // actual movement, and keep any tolerated shortfall small relative to the step.
        double tolerance = IsEndpoint(requestedPercent)
            ? EndpointTolerancePercent
            : Math.Min(RequestedPositionTolerancePercent, Math.Abs(requestedPercent - startPercent) * 0.1);
        return HasDirectionalProgress(startPercent, currentPercent, requestedPercent)
            && direction * (currentPercent - requestedPercent) >= -tolerance;
    }
}
