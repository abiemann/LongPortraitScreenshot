using System.Runtime.InteropServices;
using System.Windows.Automation;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using UiaPoint = System.Windows.Point;
using UiaRectangle = System.Windows.Rect;

namespace LongPortraitScreenshot;

internal static class TargetResolver
{
    private const int MaximumAncestorDepth = 64;

    public static ScrollTarget? Resolve(DrawingPoint screenPoint, int excludedProcessId)
    {
        AutomationElement? element;

        try
        {
            element = AutomationElement.FromPoint(new UiaPoint(screenPoint.X, screenPoint.Y));
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return null;
        }

        for (int depth = 0; element is not null && depth < MaximumAncestorDepth; depth++)
        {
            try
            {
                AutomationElement.AutomationElementInformation information = element.Current;

                if (information.ProcessId == excludedProcessId)
                {
                    return null;
                }

                if (information.ProcessId > 0 &&
                    TryGetVerticalScrollPattern(element, out ScrollPattern scrollPattern) &&
                    TryConvertBounds(information.BoundingRectangle, out DrawingRectangle bounds))
                {
                    ScrollPattern.ScrollPatternInformation scrollInformation = scrollPattern.Current;

                    if (IsUsableVerticalScroll(scrollInformation))
                    {
                        return new ScrollTarget(
                            element,
                            scrollPattern,
                            information.Name ?? string.Empty,
                            GetControlTypeName(information),
                            bounds,
                            information.ProcessId,
                            scrollInformation.VerticalScrollPercent,
                            scrollInformation.VerticalViewSize);
                    }
                }
            }
            catch (Exception exception) when (IsAutomationFailure(exception))
            {
                // Providers can invalidate an element while the finder is moving.
            }

            try
            {
                element = TreeWalker.RawViewWalker.GetParent(element);
            }
            catch (Exception exception) when (IsAutomationFailure(exception))
            {
                return null;
            }
        }

        return null;
    }

    private static bool TryGetVerticalScrollPattern(
        AutomationElement element,
        out ScrollPattern scrollPattern)
    {
        if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out object pattern) &&
            pattern is ScrollPattern candidate)
        {
            ScrollPattern.ScrollPatternInformation information = candidate.Current;

            if (information.VerticallyScrollable)
            {
                scrollPattern = candidate;
                return true;
            }
        }

        scrollPattern = null!;
        return false;
    }

    private static bool IsUsableVerticalScroll(ScrollPattern.ScrollPatternInformation information)
    {
        return information.VerticallyScrollable &&
               information.VerticalScrollPercent != ScrollPattern.NoScroll &&
               double.IsFinite(information.VerticalScrollPercent) &&
               information.VerticalScrollPercent is >= 0d and <= 100d &&
               double.IsFinite(information.VerticalViewSize) &&
               information.VerticalViewSize is > 0d and <= 100d;
    }

    private static bool TryConvertBounds(UiaRectangle source, out DrawingRectangle bounds)
    {
        bounds = DrawingRectangle.Empty;

        if (source.IsEmpty ||
            !double.IsFinite(source.Left) ||
            !double.IsFinite(source.Top) ||
            !double.IsFinite(source.Right) ||
            !double.IsFinite(source.Bottom))
        {
            return false;
        }

        double leftValue = Math.Floor(source.Left);
        double topValue = Math.Floor(source.Top);
        double rightValue = Math.Ceiling(source.Right);
        double bottomValue = Math.Ceiling(source.Bottom);

        if (leftValue < int.MinValue ||
            topValue < int.MinValue ||
            rightValue > int.MaxValue ||
            bottomValue > int.MaxValue)
        {
            return false;
        }

        int left = (int)leftValue;
        int top = (int)topValue;
        int right = (int)rightValue;
        int bottom = (int)bottomValue;

        if (right - (long)left < 2 || bottom - (long)top < 2)
        {
            return false;
        }

        bounds = DrawingRectangle.FromLTRB(left, top, right, bottom);
        return SystemInformation.VirtualScreen.Contains(bounds);
    }

    private static string GetControlTypeName(
        AutomationElement.AutomationElementInformation information)
    {
        if (!string.IsNullOrWhiteSpace(information.LocalizedControlType))
        {
            return information.LocalizedControlType.Trim();
        }

        const string prefix = "ControlType.";
        string name = information.ControlType?.ProgrammaticName ?? "control";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            ? name[prefix.Length..]
            : name;
    }

    private static bool IsAutomationFailure(Exception exception)
    {
        return exception is ElementNotAvailableException or
            InvalidOperationException or
            COMException;
    }
}
