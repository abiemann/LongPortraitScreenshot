namespace LongPortraitScreenshot.UI;

internal enum CropEdge
{
    None,
    Top,
    Right,
    Bottom,
    Left
}

internal sealed class CropSelection
{
    private readonly Size _imageSize;

    public CropSelection(Size imageSize)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageSize),
                "The image dimensions must both be positive.");
        }

        _imageSize = imageSize;
        Bounds = FullBounds;
    }

    public event EventHandler? Changed;

    public Rectangle Bounds { get; private set; }

    public bool IsCropped => Bounds != FullBounds;

    public ulong Estimated32BitMemoryBytes =>
        (ulong)Bounds.Width * (ulong)Bounds.Height * sizeof(int);

    public void MoveEdge(CropEdge edge, int coordinate)
    {
        Rectangle updated = edge switch
        {
            CropEdge.Top => Rectangle.FromLTRB(
                Bounds.Left,
                Math.Clamp(coordinate, 0, Bounds.Bottom - 1),
                Bounds.Right,
                Bounds.Bottom),
            CropEdge.Right => Rectangle.FromLTRB(
                Bounds.Left,
                Bounds.Top,
                Math.Clamp(coordinate, Bounds.Left + 1, _imageSize.Width),
                Bounds.Bottom),
            CropEdge.Bottom => Rectangle.FromLTRB(
                Bounds.Left,
                Bounds.Top,
                Bounds.Right,
                Math.Clamp(coordinate, Bounds.Top + 1, _imageSize.Height)),
            CropEdge.Left => Rectangle.FromLTRB(
                Math.Clamp(coordinate, 0, Bounds.Right - 1),
                Bounds.Top,
                Bounds.Right,
                Bounds.Bottom),
            CropEdge.None => Bounds,
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };

        SetBounds(updated);
    }

    public void Reset() => SetBounds(FullBounds);

    internal void SetBounds(Rectangle bounds)
    {
        if (bounds.Width <= 0
            || bounds.Height <= 0
            || bounds.Left < 0
            || bounds.Top < 0
            || bounds.Right > _imageSize.Width
            || bounds.Bottom > _imageSize.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bounds),
                "The crop must be a non-empty rectangle inside the image.");
        }

        if (Bounds == bounds)
        {
            return;
        }

        Bounds = bounds;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Rectangle FullBounds => new(Point.Empty, _imageSize);
}
