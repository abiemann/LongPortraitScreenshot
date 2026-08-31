using LongPortraitScreenshot.Interop;

namespace LongPortraitScreenshot.Capture;

internal static class CaptureMemoryAssessment
{
    // Four bytes for the final bitmap, with an 8x allowance for retained frames and processing copies.
    private const ulong EstimatedWorkingBytesPerOutputPixel = 32;
    private const string Prefix = "Given the available amount of RAM, this operation is: ";

    internal static string GetCurrentText(long estimatedPixels)
    {
        ulong availablePhysicalBytes = NativeMethods.TryGetAvailablePhysicalMemoryBytes(out ulong availableBytes)
            ? availableBytes
            : 0;
        return GetText(estimatedPixels, availablePhysicalBytes);
    }

    internal static string GetText(long estimatedPixels, ulong availablePhysicalBytes)
    {
        if (estimatedPixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedPixels));
        }

        bool likelyToSucceed = availablePhysicalBytes > 0
            && (ulong)estimatedPixels <= availablePhysicalBytes / EstimatedWorkingBytesPerOutputPixel;
        return Prefix + (likelyToSucceed ? "likely to succeed." : "likely to be slow.");
    }
}
