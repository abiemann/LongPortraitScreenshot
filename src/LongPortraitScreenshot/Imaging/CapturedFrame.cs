using System.Drawing;

namespace LongPortraitScreenshot.Imaging;

public sealed record CapturedFrame(Bitmap Image, double ScrollPercent, double ViewSize);
