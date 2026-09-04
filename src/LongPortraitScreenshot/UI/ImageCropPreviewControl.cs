using System.Drawing.Drawing2D;

namespace LongPortraitScreenshot.UI;

internal sealed class ImageCropPreviewControl : ScrollableControl
{
    private const double MaximumZoom = 4.0;
    private const int CanvasPadding = 36;
    private const int HandleLength = 34;
    private const int HandleThickness = 10;
    private const int HandleHitPadding = 7;
    private const int AutoScrollBoundary = 32;
    private const int AutoScrollStep = 18;

    private readonly Bitmap _image;
    private readonly System.Windows.Forms.Timer _dragScrollTimer = new() { Interval = 30 };
    private CropEdge _dragEdge;
    private Rectangle _dragStartBounds;
    private Point _lastDragPoint;
    private double _dragStartPointerCoordinate;
    private int _dragStartEdgeCoordinate;
    private double _zoom = 1.0;

    public ImageCropPreviewControl(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);

        _image = image;
        Selection = new CropSelection(image.Size);
        Selection.Changed += Selection_Changed;

        AutoScroll = true;
        BackColor = Color.FromArgb(31, 29, 29);
        TabStop = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);

        _dragScrollTimer.Tick += DragScrollTimer_Tick;
        UpdateVirtualSize();
    }

    public event EventHandler? SelectionChanged;

    public event EventHandler? ZoomChanged;

    public CropSelection Selection { get; }

    public double Zoom => _zoom;

    public void FitImage()
    {
        // Clear an existing large-image scroll range first so the fit calculation
        // uses the viewport after both scroll bars have been removed.
        AutoScrollMinSize = Size.Empty;
        AutoScrollPosition = Point.Empty;
        PerformLayout();

        int padding = ScaleLogical(CanvasPadding);
        int availableWidth = Math.Max(1, ClientSize.Width - (padding * 2) - 2);
        int availableHeight = Math.Max(1, ClientSize.Height - (padding * 2) - 2);
        double fitZoom = Math.Min(
            availableWidth / (double)_image.Width,
            availableHeight / (double)_image.Height);

        double requestedZoom = Math.Min(1.0, fitZoom);
        if (Math.Abs(requestedZoom - _zoom) < 0.0001)
        {
            UpdateVirtualSize();
            Invalidate();
        }
        else
        {
            SetZoom(requestedZoom);
        }
    }

    public void SetZoom(double zoom) => SetZoom(zoom, null);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Selection.Changed -= Selection_Changed;
            _dragScrollTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateVirtualSize();
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);

        if (_dragEdge != CropEdge.None && !Capture)
        {
            EndDrag();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        CropEdge edge = HitTestHandle(e.Location);
        if (edge == CropEdge.None)
        {
            return;
        }

        Focus();
        _dragEdge = edge;
        _dragStartBounds = Selection.Bounds;
        _lastDragPoint = e.Location;
        SetDragOrigin(e.Location);
        Capture = true;
        _dragScrollTimer.Start();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragEdge != CropEdge.None)
        {
            _lastDragPoint = e.Location;
            UpdateDrag(e.Location);
            return;
        }

        Cursor = GetCursor(HitTestHandle(e.Location));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button == MouseButtons.Left && _dragEdge != CropEdge.None)
        {
            Capture = false;
            EndDrag();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragEdge == CropEdge.None)
        {
            Cursor = Cursors.Default;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
            SetZoom(_zoom * factor, e.Location);
            return;
        }

        base.OnMouseWheel(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);

        Rectangle imageRectangle = GetImageRectangle();
        Rectangle visibleRectangle = Rectangle.Intersect(imageRectangle, ClientRectangle);
        if (visibleRectangle.Width > 0 && visibleRectangle.Height > 0)
        {
            DrawVisibleImage(e.Graphics, imageRectangle, visibleRectangle);
        }

        Rectangle selectionRectangle = ImageToClient(Selection.Bounds, imageRectangle);
        DrawDiscardedAreas(e.Graphics, imageRectangle, selectionRectangle);
        DrawSelection(e.Graphics, selectionRectangle);
        DrawHandles(e.Graphics, selectionRectangle);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _dragEdge != CropEdge.None)
        {
            Selection.SetBounds(_dragStartBounds);
            Capture = false;
            EndDrag();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void SetZoom(double zoom, Point? anchorClient)
    {
        double minimumUsefulZoom = 1.0 / Math.Max(_image.Width, _image.Height);
        double maximumSafeZoom = GetMaximumSafeZoom();
        double updatedZoom = Math.Clamp(zoom, minimumUsefulZoom, maximumSafeZoom);
        if (Math.Abs(updatedZoom - _zoom) < 0.0001)
        {
            return;
        }

        Point anchor = anchorClient ?? new Point(ClientSize.Width / 2, ClientSize.Height / 2);
        Rectangle oldImageRectangle = GetImageRectangle();
        double sourceX = (anchor.X - oldImageRectangle.Left)
            * _image.Width / (double)Math.Max(1, oldImageRectangle.Width);
        double sourceY = (anchor.Y - oldImageRectangle.Top)
            * _image.Height / (double)Math.Max(1, oldImageRectangle.Height);
        sourceX = Math.Clamp(sourceX, 0, _image.Width);
        sourceY = Math.Clamp(sourceY, 0, _image.Height);

        _zoom = updatedZoom;
        UpdateVirtualSize();

        Size scaledSize = GetScaledImageSize();
        int padding = ScaleLogical(CanvasPadding);
        int horizontalScroll = scaledSize.Width + (padding * 2) > ClientSize.Width
            ? (int)Math.Clamp(
                Math.Round(padding + (sourceX * scaledSize.Width / _image.Width) - anchor.X),
                0,
                Math.Max(0, AutoScrollMinSize.Width - ClientSize.Width))
            : 0;
        int verticalScroll = scaledSize.Height + (padding * 2) > ClientSize.Height
            ? (int)Math.Clamp(
                Math.Round(padding + (sourceY * scaledSize.Height / _image.Height) - anchor.Y),
                0,
                Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height))
            : 0;

        AutoScrollPosition = new Point(horizontalScroll, verticalScroll);
        if (_dragEdge != CropEdge.None)
        {
            // Zooming changes the pointer's source coordinate, but must not edit
            // the crop until the pointer moves again.
            SetDragOrigin(_lastDragPoint);
        }

        ZoomChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private void UpdateVirtualSize()
    {
        Size scaledSize = GetScaledImageSize();
        int padding = ScaleLogical(CanvasPadding);
        AutoScrollMinSize = new Size(
            checked(scaledSize.Width + (padding * 2)),
            checked(scaledSize.Height + (padding * 2)));
    }

    private Size GetScaledImageSize() => new(
        Math.Max(1, checked((int)Math.Round(_image.Width * _zoom))),
        Math.Max(1, checked((int)Math.Round(_image.Height * _zoom))));

    private double GetMaximumSafeZoom()
    {
        int padding = ScaleLogical(CanvasPadding);
        double maximumDimension = int.MaxValue - (padding * 2.0) - 1;
        double minimumUsefulZoom = 1.0 / Math.Max(_image.Width, _image.Height);
        return Math.Max(
            minimumUsefulZoom,
            Math.Min(
                MaximumZoom,
                Math.Min(
                    maximumDimension / _image.Width,
                    maximumDimension / _image.Height)));
    }

    private Rectangle GetImageRectangle()
    {
        Size scaledSize = GetScaledImageSize();
        int padding = ScaleLogical(CanvasPadding);
        int contentWidth = scaledSize.Width + (padding * 2);
        int contentHeight = scaledSize.Height + (padding * 2);
        int left = contentWidth <= ClientSize.Width
            ? (ClientSize.Width - scaledSize.Width) / 2
            : AutoScrollPosition.X + padding;
        int top = contentHeight <= ClientSize.Height
            ? (ClientSize.Height - scaledSize.Height) / 2
            : AutoScrollPosition.Y + padding;

        return new Rectangle(left, top, scaledSize.Width, scaledSize.Height);
    }

    private void DrawVisibleImage(
        Graphics graphics,
        Rectangle imageRectangle,
        Rectangle visibleRectangle)
    {
        double scaleX = imageRectangle.Width / (double)_image.Width;
        double scaleY = imageRectangle.Height / (double)_image.Height;
        int sourceLeft = Math.Clamp(
            (int)Math.Floor((visibleRectangle.Left - imageRectangle.Left) / scaleX),
            0,
            _image.Width - 1);
        int sourceTop = Math.Clamp(
            (int)Math.Floor((visibleRectangle.Top - imageRectangle.Top) / scaleY),
            0,
            _image.Height - 1);
        int sourceRight = Math.Clamp(
            (int)Math.Ceiling((visibleRectangle.Right - imageRectangle.Left) / scaleX),
            sourceLeft + 1,
            _image.Width);
        int sourceBottom = Math.Clamp(
            (int)Math.Ceiling((visibleRectangle.Bottom - imageRectangle.Top) / scaleY),
            sourceTop + 1,
            _image.Height);

        Rectangle sourceRectangle = Rectangle.FromLTRB(
            sourceLeft,
            sourceTop,
            sourceRight,
            sourceBottom);
        RectangleF destinationRectangle = RectangleF.FromLTRB(
            imageRectangle.Left + (float)(sourceLeft * scaleX),
            imageRectangle.Top + (float)(sourceTop * scaleY),
            imageRectangle.Left + (float)(sourceRight * scaleX),
            imageRectangle.Top + (float)(sourceBottom * scaleY));

        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = _zoom >= 1
            ? InterpolationMode.NearestNeighbor
            : _dragEdge == CropEdge.None
                ? InterpolationMode.HighQualityBicubic
                : InterpolationMode.Low;
        graphics.PixelOffsetMode = _zoom < 1 && _dragEdge == CropEdge.None
            ? PixelOffsetMode.HighQuality
            : PixelOffsetMode.Half;
        graphics.DrawImage(
            _image,
            destinationRectangle,
            sourceRectangle,
            GraphicsUnit.Pixel);
    }

    private Rectangle ImageToClient(Rectangle imageBounds, Rectangle imageRectangle)
    {
        double scaleX = imageRectangle.Width / (double)_image.Width;
        double scaleY = imageRectangle.Height / (double)_image.Height;
        int left = imageRectangle.Left + (int)Math.Floor(imageBounds.Left * scaleX);
        int top = imageRectangle.Top + (int)Math.Floor(imageBounds.Top * scaleY);
        int right = imageRectangle.Left + (int)Math.Ceiling(imageBounds.Right * scaleX);
        int bottom = imageRectangle.Top + (int)Math.Ceiling(imageBounds.Bottom * scaleY);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static void DrawDiscardedAreas(
        Graphics graphics,
        Rectangle imageRectangle,
        Rectangle selectionRectangle)
    {
        using SolidBrush overlay = new(Color.FromArgb(174, 0, 0, 0));
        FillIfPositive(graphics, overlay, Rectangle.FromLTRB(
            imageRectangle.Left,
            imageRectangle.Top,
            imageRectangle.Right,
            selectionRectangle.Top));
        FillIfPositive(graphics, overlay, Rectangle.FromLTRB(
            imageRectangle.Left,
            selectionRectangle.Bottom,
            imageRectangle.Right,
            imageRectangle.Bottom));
        FillIfPositive(graphics, overlay, Rectangle.FromLTRB(
            imageRectangle.Left,
            selectionRectangle.Top,
            selectionRectangle.Left,
            selectionRectangle.Bottom));
        FillIfPositive(graphics, overlay, Rectangle.FromLTRB(
            selectionRectangle.Right,
            selectionRectangle.Top,
            imageRectangle.Right,
            selectionRectangle.Bottom));
    }

    private static void FillIfPositive(Graphics graphics, Brush brush, Rectangle rectangle)
    {
        if (rectangle.Width > 0 && rectangle.Height > 0)
        {
            graphics.FillRectangle(brush, rectangle);
        }
    }

    private void DrawSelection(Graphics graphics, Rectangle selectionRectangle)
    {
        Rectangle outline = Rectangle.FromLTRB(
            selectionRectangle.Left,
            selectionRectangle.Top,
            Math.Max(selectionRectangle.Left, selectionRectangle.Right - 1),
            Math.Max(selectionRectangle.Top, selectionRectangle.Bottom - 1));
        using Pen shadow = new(Color.FromArgb(220, 0, 0, 0), ScaleLogical(3));
        using Pen accent = new(Color.FromArgb(0, 190, 255), Math.Max(1, ScaleLogical(1)));
        graphics.DrawRectangle(shadow, outline);
        graphics.DrawRectangle(accent, outline);
    }

    private void DrawHandles(Graphics graphics, Rectangle selectionRectangle)
    {
        foreach (CropEdge edge in new[] { CropEdge.Top, CropEdge.Right, CropEdge.Bottom, CropEdge.Left })
        {
            Rectangle handle = GetHandleRectangle(edge, selectionRectangle);
            using SolidBrush shadow = new(Color.FromArgb(210, 0, 0, 0));
            using SolidBrush accent = new(Color.FromArgb(0, 190, 255));
            using Pen border = new(Color.White, Math.Max(1, ScaleLogical(1)));
            graphics.FillRectangle(shadow, Rectangle.Inflate(handle, ScaleLogical(2), ScaleLogical(2)));
            graphics.FillRectangle(accent, handle);
            graphics.DrawRectangle(border, Rectangle.FromLTRB(
                handle.Left,
                handle.Top,
                handle.Right - 1,
                handle.Bottom - 1));

            using Pen grip = new(Color.FromArgb(20, 75, 100), Math.Max(1, ScaleLogical(1)));
            DrawGrip(graphics, grip, edge, handle);
        }
    }

    private void DrawGrip(Graphics graphics, Pen pen, CropEdge edge, Rectangle handle)
    {
        int spacing = ScaleLogical(4);
        if (edge is CropEdge.Top or CropEdge.Bottom)
        {
            int center = handle.Left + (handle.Width / 2);
            for (int offset = -spacing; offset <= spacing; offset += spacing)
            {
                graphics.DrawLine(
                    pen,
                    center + offset,
                    handle.Top + ScaleLogical(2),
                    center + offset,
                    handle.Bottom - ScaleLogical(3));
            }
        }
        else
        {
            int center = handle.Top + (handle.Height / 2);
            for (int offset = -spacing; offset <= spacing; offset += spacing)
            {
                graphics.DrawLine(
                    pen,
                    handle.Left + ScaleLogical(2),
                    center + offset,
                    handle.Right - ScaleLogical(3),
                    center + offset);
            }
        }
    }

    private Rectangle GetHandleRectangle(CropEdge edge, Rectangle selectionRectangle)
    {
        int length = ScaleLogical(HandleLength);
        int thickness = ScaleLogical(HandleThickness);
        int gap = ScaleLogical(3);

        return edge switch
        {
            CropEdge.Top => new Rectangle(
                selectionRectangle.Left + ((selectionRectangle.Width - length) / 2),
                selectionRectangle.Top - thickness - gap,
                length,
                thickness),
            CropEdge.Right => new Rectangle(
                selectionRectangle.Right + gap,
                selectionRectangle.Top + ((selectionRectangle.Height - length) / 2),
                thickness,
                length),
            CropEdge.Bottom => new Rectangle(
                selectionRectangle.Left + ((selectionRectangle.Width - length) / 2),
                selectionRectangle.Bottom + gap,
                length,
                thickness),
            CropEdge.Left => new Rectangle(
                selectionRectangle.Left - thickness - gap,
                selectionRectangle.Top + ((selectionRectangle.Height - length) / 2),
                thickness,
                length),
            _ => Rectangle.Empty
        };
    }

    private CropEdge HitTestHandle(Point location)
    {
        Rectangle selectionRectangle = ImageToClient(Selection.Bounds, GetImageRectangle());
        int padding = ScaleLogical(HandleHitPadding);

        foreach (CropEdge edge in new[] { CropEdge.Top, CropEdge.Right, CropEdge.Bottom, CropEdge.Left })
        {
            Rectangle hitRectangle = Rectangle.Inflate(
                GetHandleRectangle(edge, selectionRectangle),
                padding,
                padding);
            if (hitRectangle.Contains(location))
            {
                return edge;
            }
        }

        return CropEdge.None;
    }

    private void SetDragOrigin(Point location)
    {
        _dragStartPointerCoordinate = GetPointerImageCoordinate(_dragEdge, location);
        _dragStartEdgeCoordinate = _dragEdge switch
        {
            CropEdge.Left => Selection.Bounds.Left,
            CropEdge.Right => Selection.Bounds.Right,
            CropEdge.Top => Selection.Bounds.Top,
            CropEdge.Bottom => Selection.Bounds.Bottom,
            _ => 0
        };
    }

    private static int GetPointerCoordinate(CropEdge edge, Point point) =>
        edge is CropEdge.Left or CropEdge.Right ? point.X : point.Y;

    private double GetPointerImageCoordinate(CropEdge edge, Point location)
    {
        Rectangle imageRectangle = GetImageRectangle();
        double scale = edge is CropEdge.Left or CropEdge.Right
            ? imageRectangle.Width / (double)_image.Width
            : imageRectangle.Height / (double)_image.Height;
        int imageOrigin = edge is CropEdge.Left or CropEdge.Right
            ? imageRectangle.Left
            : imageRectangle.Top;
        return (GetPointerCoordinate(edge, location) - imageOrigin) / scale;
    }

    private void UpdateDrag(Point location)
    {
        // Keep the original source edge exact. Inverting the rounded handle
        // position can otherwise move it by many pixels in a fitted tall image.
        int imageCoordinate = (int)Math.Round(
            _dragStartEdgeCoordinate
                + (GetPointerImageCoordinate(_dragEdge, location) - _dragStartPointerCoordinate),
            MidpointRounding.AwayFromZero);
        Selection.MoveEdge(_dragEdge, imageCoordinate);
    }

    private void DragScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (_dragEdge == CropEdge.None)
        {
            _dragScrollTimer.Stop();
            return;
        }

        int boundary = ScaleLogical(AutoScrollBoundary);
        int step = ScaleLogical(AutoScrollStep);
        int horizontalDelta = _lastDragPoint.X < boundary
            ? -step
            : _lastDragPoint.X > ClientSize.Width - boundary ? step : 0;
        int verticalDelta = _lastDragPoint.Y < boundary
            ? -step
            : _lastDragPoint.Y > ClientSize.Height - boundary ? step : 0;

        if (horizontalDelta == 0 && verticalDelta == 0)
        {
            return;
        }

        int oldHorizontal = -AutoScrollPosition.X;
        int oldVertical = -AutoScrollPosition.Y;
        int newHorizontal = Math.Clamp(
            oldHorizontal + horizontalDelta,
            0,
            Math.Max(0, AutoScrollMinSize.Width - ClientSize.Width));
        int newVertical = Math.Clamp(
            oldVertical + verticalDelta,
            0,
            Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height));

        if (oldHorizontal == newHorizontal && oldVertical == newVertical)
        {
            return;
        }

        AutoScrollPosition = new Point(newHorizontal, newVertical);
        UpdateDrag(_lastDragPoint);
        Invalidate();
    }

    private void EndDrag()
    {
        _dragScrollTimer.Stop();
        _dragEdge = CropEdge.None;
        _dragStartPointerCoordinate = 0;
        _dragStartEdgeCoordinate = 0;
        Cursor = GetCursor(HitTestHandle(PointToClient(MousePosition)));
        Invalidate();
    }

    private void Selection_Changed(object? sender, EventArgs e)
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private static Cursor GetCursor(CropEdge edge) => edge switch
    {
        CropEdge.Top or CropEdge.Bottom => Cursors.SizeNS,
        CropEdge.Left or CropEdge.Right => Cursors.SizeWE,
        _ => Cursors.Default
    };

    private int ScaleLogical(int pixels) => Math.Max(1, (int)Math.Round(pixels * DeviceDpi / 96.0));
}
