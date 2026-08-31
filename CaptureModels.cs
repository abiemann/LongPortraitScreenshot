using System.Drawing;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("LongPortraitScreenshot.SelfTest")]

namespace LongPortraitScreenshot;

public sealed record CaptureOptions(
    bool CropVerticalScrollIndicator,
    bool TrimEmptyHorizontalSpace);

public sealed record CapturedFrame(Bitmap Image, double ScrollPercent, double ViewSize);

public sealed class CaptureResult : IDisposable
{
    public CaptureResult(Bitmap image, string targetName, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(image);

        Image = image;
        TargetName = string.IsNullOrWhiteSpace(targetName) ? "Scrolling control" : targetName;
        FrameCount = frameCount;
    }

    public Bitmap Image { get; }

    public string TargetName { get; }

    public int FrameCount { get; }

    public void Dispose() => Image.Dispose();
}
