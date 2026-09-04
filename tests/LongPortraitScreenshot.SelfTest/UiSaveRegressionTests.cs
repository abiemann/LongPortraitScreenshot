using System.Reflection;
using LongPortraitScreenshot.Imaging;
using LongPortraitScreenshot.UI;

namespace LongPortraitScreenshot.SelfTest;

internal static class UiSaveRegressionTests
{
    public static void Run()
    {
        FittedCropDragsPreserveExactEdges();
        CropDragIncludesScrollMovement();
        ZoomDuringCropDragPreservesSelection();
        PngSaveHandlesLongNamesAndDirectories();
        FailedPngSavePreservesDestinationAndCleansTemporaryFile();
    }

    private static void FittedCropDragsPreserveExactEdges()
    {
        using Bitmap image = new(800, 40_000);
        using ImageCropPreviewControl preview = CreatePreview(image);
        preview.FitImage();
        Rectangle original = Rectangle.FromLTRB(123, 123, 677, 39_877);

        foreach (CropEdge edge in new[] { CropEdge.Top, CropEdge.Right, CropEdge.Bottom, CropEdge.Left })
        {
            preview.Selection.SetBounds(original);
            Point start = GetHandleCenter(preview, edge);
            Mouse(preview, "OnMouseDown", start);
            Mouse(preview, "OnMouseMove", start);
            Require(preview.Selection.Bounds == original,
                $"Starting a fitted {edge} drag changed the exact source crop without pointer movement.");

            int delta = edge is CropEdge.Right or CropEdge.Bottom ? -3 : 3;
            Point away = edge is CropEdge.Left or CropEdge.Right
                ? new Point(start.X + delta, start.Y)
                : new Point(start.X, start.Y + delta);
            Mouse(preview, "OnMouseMove", away);
            Require(preview.Selection.Bounds != original,
                $"The fitted {edge} drag did not move in response to pointer movement.");

            Mouse(preview, "OnMouseMove", start);
            Require(preview.Selection.Bounds == original,
                $"Returning a fitted {edge} drag to its start did not restore the exact source crop.");
            Mouse(preview, "OnMouseUp", start);
        }
    }

    private static void CropDragIncludesScrollMovement()
    {
        using Bitmap image = new(4_000, 4_000);
        using ImageCropPreviewControl preview = CreatePreview(image);
        preview.SetZoom(0.5);
        preview.AutoScrollPosition = new Point(120, 120);
        Rectangle original = Rectangle.FromLTRB(503, 503, 1_003, 1_003);

        foreach (CropEdge edge in new[] { CropEdge.Top, CropEdge.Right, CropEdge.Bottom, CropEdge.Left })
        {
            preview.AutoScrollPosition = new Point(120, 120);
            preview.Selection.SetBounds(original);
            Point start = GetHandleCenter(preview, edge);
            Mouse(preview, "OnMouseDown", start);

            preview.AutoScrollPosition = new Point(138, 138);
            Mouse(preview, "OnMouseMove", start);
            Rectangle expected = edge switch
            {
                CropEdge.Top => Rectangle.FromLTRB(503, 539, 1_003, 1_003),
                CropEdge.Right => Rectangle.FromLTRB(503, 503, 1_039, 1_003),
                CropEdge.Bottom => Rectangle.FromLTRB(503, 503, 1_003, 1_039),
                CropEdge.Left => Rectangle.FromLTRB(539, 503, 1_003, 1_003),
                _ => throw new InvalidOperationException()
            };
            Require(preview.Selection.Bounds == expected,
                $"A stationary {edge} drag did not include the 18-client-pixel scroll as 36 source pixels.");

            preview.AutoScrollPosition = new Point(120, 120);
            Mouse(preview, "OnMouseMove", start);
            Require(preview.Selection.Bounds == original,
                $"Returning the viewport during a {edge} drag did not restore the exact crop.");
            Mouse(preview, "OnMouseUp", start);
        }
    }

    private static void ZoomDuringCropDragPreservesSelection()
    {
        using Bitmap image = new(2_000, 2_000);
        using ImageCropPreviewControl preview = CreatePreview(image);
        preview.SetZoom(0.5);
        Rectangle original = Rectangle.FromLTRB(123, 123, 1_000, 1_000);
        preview.Selection.SetBounds(original);
        Point start = GetHandleCenter(preview, CropEdge.Top);
        Mouse(preview, "OnMouseDown", start);

        preview.SetZoom(1.0);
        Mouse(preview, "OnMouseMove", start);
        Require(preview.Selection.Bounds == original,
            "Changing zoom during a stationary drag moved the crop.");

        Mouse(preview, "OnMouseMove", new Point(start.X, start.Y + 7));
        Require(preview.Selection.Bounds.Top == original.Top + 7,
            "Dragging after a zoom did not use the new source-to-client scale.");
        Mouse(preview, "OnMouseMove", start);
        Require(preview.Selection.Bounds == original,
            "Returning the pointer after zooming did not restore the exact crop.");
        Mouse(preview, "OnMouseUp", start);
    }

    private static void PngSaveHandlesLongNamesAndDirectories()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            int fileNameLength = 232 - directory.Length - 1;
            Require(fileNameLength is >= 8 and <= 255,
                "Long-name regression needs a temporary directory short enough for a 232-character destination.");
            string longName = Path.Combine(directory, new string('n', fileNameLength - 4) + ".png");
            Require(longName.Length < 260 && longName.Length + 38 >= 260,
                "Long-name regression setup must cross the old GDI+ limit only after extending the filename.");
            SaveAndVerifyOverwrite(longName);

            string longDirectory = directory;
            while (longDirectory.Length < 280)
            {
                longDirectory = Path.Combine(longDirectory, new string('d', 60));
            }

            Directory.CreateDirectory(longDirectory);
            SaveAndVerifyOverwrite(Path.Combine(longDirectory, "capture.png"));
            Require(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories).Length == 0,
                "Saving or overwriting a PNG at a long path left temporary files behind.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void FailedPngSavePreservesDestinationAndCleansTemporaryFile()
    {
        string directory = CreateTemporaryDirectory();
        string destination = Path.Combine(directory, "capture.png");
        byte[] original = [11, 23, 37, 41];
        File.WriteAllBytes(destination, original);

        try
        {
            using Bitmap replacement = new(3, 5);
            using (FileStream lockedDestination = new(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                RequireSaveFails(replacement, destination, "Replacing a locked destination unexpectedly succeeded.");
            }

            Require(File.ReadAllBytes(destination).SequenceEqual(original),
                "A failed destination replacement changed the existing file.");
            Require(Directory.GetFiles(directory).Length == 1,
                "A failed destination replacement left its temporary PNG behind.");

            Bitmap disposedImage = new(3, 5);
            disposedImage.Dispose();
            RequireSaveFails(disposedImage, destination, "Encoding a disposed image unexpectedly succeeded.");
            Require(File.ReadAllBytes(destination).SequenceEqual(original),
                "A failed PNG encoding changed the existing destination.");
            Require(Directory.GetFiles(directory).Length == 1,
                "A failed PNG encoding left its temporary file behind.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void SaveAndVerifyOverwrite(string destination)
    {
        foreach (Color color in new[] { Color.CornflowerBlue, Color.Goldenrod })
        {
            using Bitmap image = new(7, 9);
            using (Graphics graphics = Graphics.FromImage(image))
            {
                graphics.Clear(color);
            }

            PngFileSaver.Save(image, destination);
            using FileStream stream = File.OpenRead(destination);
            using Bitmap decoded = new(stream);
            Require(decoded.Size == image.Size && decoded.GetPixel(3, 4).ToArgb() == color.ToArgb(),
                $"Saving or overwriting a PNG at a {destination.Length}-character path corrupted its pixels.");
        }
    }

    private static void RequireSaveFails(Image image, string destination, string message)
    {
        Exception? failure = null;
        try
        {
            PngFileSaver.Save(image, destination);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Require(failure is not null, message);
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"LpsUiSave.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static ImageCropPreviewControl CreatePreview(Bitmap image)
    {
        ImageCropPreviewControl preview = new(image) { ClientSize = new Size(1_000, 700) };
        preview.CreateControl();
        return preview;
    }

    private static Point GetHandleCenter(ImageCropPreviewControl preview, CropEdge edge)
    {
        Rectangle imageRectangle = Invoke<Rectangle>(preview, "GetImageRectangle");
        Rectangle selectionRectangle = Invoke<Rectangle>(preview, "ImageToClient", preview.Selection.Bounds, imageRectangle);
        Rectangle handle = Invoke<Rectangle>(preview, "GetHandleRectangle", edge, selectionRectangle);
        return new Point(handle.Left + handle.Width / 2, handle.Top + handle.Height / 2);
    }

    private static void Mouse(ImageCropPreviewControl preview, string method, Point point) =>
        Invoke<object?>(preview, method, new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));

    private static T Invoke<T>(ImageCropPreviewControl preview, string method, params object[] arguments) =>
        (T)typeof(ImageCropPreviewControl)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(preview, arguments)!;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
