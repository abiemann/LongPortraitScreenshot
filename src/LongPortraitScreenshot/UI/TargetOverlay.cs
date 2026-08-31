using LongPortraitScreenshot.Interop;

namespace LongPortraitScreenshot.UI;

internal sealed class TargetOverlay : IDisposable
{
    private const int BorderThickness = 4;
    private const int BorderOffset = BorderThickness / 2;
    private readonly BorderWindow[] _windows =
    [
        new BorderWindow(),
        new BorderWindow(),
        new BorderWindow(),
        new BorderWindow()
    ];
    private bool _disposed;

    public void Show(Rectangle bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (bounds.Width < 2 || bounds.Height < 2)
        {
            Hide();
            return;
        }

        Rectangle outline = Rectangle.Inflate(bounds, BorderOffset, BorderOffset);
        int sideHeight = Math.Max(1, outline.Height - (BorderThickness * 2));

        _windows[0].ShowAt(new Rectangle(
            outline.Left,
            outline.Top,
            outline.Width,
            BorderThickness));
        _windows[1].ShowAt(new Rectangle(
            outline.Left,
            outline.Bottom - BorderThickness,
            outline.Width,
            BorderThickness));
        _windows[2].ShowAt(new Rectangle(
            outline.Left,
            outline.Top + BorderThickness,
            BorderThickness,
            sideHeight));
        _windows[3].ShowAt(new Rectangle(
            outline.Right - BorderThickness,
            outline.Top + BorderThickness,
            BorderThickness,
            sideHeight));
    }

    public void Hide()
    {
        if (_disposed)
        {
            return;
        }

        foreach (BorderWindow window in _windows)
        {
            window.Hide();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (BorderWindow window in _windows)
        {
            window.Dispose();
        }

        _disposed = true;
    }

    private sealed class BorderWindow : Form
    {
        public BorderWindow()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.LimeGreen;
            FormBorderStyle = FormBorderStyle.None;
            Opacity = 0.9d;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |=
                    NativeMethods.WsExLayered |
                    NativeMethods.WsExNoActivate |
                    NativeMethods.WsExToolWindow |
                    NativeMethods.WsExTransparent;
                return parameters;
            }
        }

        public void ShowAt(Rectangle bounds)
        {
            if (!Visible)
            {
                Bounds = bounds;
                Show();
            }

            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HwndTopmost,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WmNcHitTest)
            {
                message.Result = NativeMethods.HtTransparent;
                return;
            }

            if (message.Msg == NativeMethods.WmMouseActivate)
            {
                message.Result = NativeMethods.MaNoActivate;
                return;
            }

            base.WndProc(ref message);
        }
    }
}
