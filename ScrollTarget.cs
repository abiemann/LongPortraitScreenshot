using System.Windows.Automation;

namespace LongPortraitScreenshot;

internal sealed class ScrollTarget
{
    public ScrollTarget(
        AutomationElement element,
        ScrollPattern scrollPattern,
        string name,
        string controlType,
        Rectangle bounds,
        int processId,
        double verticalScrollPercent,
        double verticalViewSize)
    {
        Element = element;
        ScrollPattern = scrollPattern;
        Name = name.Trim();
        ControlType = controlType;
        Bounds = bounds;
        ProcessId = processId;
        VerticalScrollPercent = verticalScrollPercent;
        VerticalViewSize = verticalViewSize;
    }

    public AutomationElement Element { get; }

    public ScrollPattern ScrollPattern { get; }

    public string Name { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? $"Unnamed {ControlType}"
        : Name;

    public string ControlType { get; }

    public string ControlTypeName => ControlType;

    public Rectangle Bounds { get; }

    public int ProcessId { get; }

    public double VerticalScrollPercent { get; }

    public double VerticalViewSize { get; }
}
