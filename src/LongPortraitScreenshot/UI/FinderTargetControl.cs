using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace LongPortraitScreenshot.UI;

internal sealed class FinderEventArgs(Point screenPoint) : EventArgs
{
    public Point ScreenPoint { get; } = screenPoint;
}

internal sealed class FinderTargetControl : Control
{
    private const int DefaultTargetSize = 46;

    public FinderTargetControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);

        AccessibleName = "Window finder target";
        AccessibleRole = AccessibleRole.Graphic;
        BackColor = Color.Transparent;
        Cursor = Cursors.Cross;
        Size = new Size(DefaultTargetSize, DefaultTargetSize);
    }

    [Browsable(false)]
    public bool IsDragging { get; private set; }

    public event EventHandler? DragStarted;

    public event EventHandler<FinderEventArgs>? FinderMoved;

    public event EventHandler<FinderEventArgs>? DragEnded;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left || IsDragging)
        {
            return;
        }

        IsDragging = true;
        Capture = true;
        Invalidate();

        DragStarted?.Invoke(this, EventArgs.Empty);
        RaiseFinderMoved(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (IsDragging)
        {
            RaiseFinderMoved(e.Location);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button != MouseButtons.Left || !IsDragging)
        {
            return;
        }

        RaiseFinderMoved(e.Location);
        FinishDrag(PointToScreen(e.Location));
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);

        if (IsDragging && !Capture)
        {
            FinishDrag(Cursor.Position);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle ring = Rectangle.Inflate(ClientRectangle, -5, -5);
        Color accent = IsDragging ? Color.LimeGreen : Color.DeepSkyBlue;

        using var outerPen = new Pen(Color.FromArgb(225, accent), 3f);
        using var innerPen = new Pen(Color.FromArgb(210, accent), 2f);
        using var centerBrush = new SolidBrush(Color.FromArgb(235, accent));

        e.Graphics.DrawEllipse(outerPen, ring);
        e.Graphics.DrawEllipse(innerPen, Rectangle.Inflate(ring, -8, -8));

        Point center = new(ClientRectangle.Width / 2, ClientRectangle.Height / 2);
        e.Graphics.DrawLine(outerPen, center.X, ring.Top - 3, center.X, ring.Bottom + 3);
        e.Graphics.DrawLine(outerPen, ring.Left - 3, center.Y, ring.Right + 3, center.Y);
        e.Graphics.FillEllipse(centerBrush, center.X - 3, center.Y - 3, 6, 6);
    }

    private void RaiseFinderMoved(Point clientPoint)
    {
        FinderMoved?.Invoke(this, new FinderEventArgs(PointToScreen(clientPoint)));
    }

    private void FinishDrag(Point screenPoint)
    {
        if (!IsDragging)
        {
            return;
        }

        IsDragging = false;
        Capture = false;
        Invalidate();
        DragEnded?.Invoke(this, new FinderEventArgs(screenPoint));
    }
}
