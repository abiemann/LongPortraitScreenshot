using System.Drawing.Imaging;

namespace LongPortraitScreenshot.Imaging;

internal static class PngFileSaver
{
    public static void Save(Image image, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string fullPath = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("Choose a valid destination folder.");
        }

        string temporaryPath = Path.Combine(
            directory,
            Path.ChangeExtension(Path.GetRandomFileName(), ".tmp"));
        bool temporaryFileCreated = false;

        try
        {
            // GDI+ path-based encoding still applies legacy path limits. The
            // stream API also permits long destination directories and names.
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write))
            {
                temporaryFileCreated = true;
                image.Save(stream, ImageFormat.Png);
            }

            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (temporaryFileCreated && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
