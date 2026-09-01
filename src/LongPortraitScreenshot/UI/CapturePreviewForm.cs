using LongPortraitScreenshot.Imaging;
using System.Runtime.InteropServices;

namespace LongPortraitScreenshot.UI;

internal sealed class CapturePreviewForm : Form
{
    private const int StandardZoomTrackBarWidth = 150;
    private const int CompactZoomTrackBarWidth = 110;

    private static readonly double[] ZoomSteps =
    [
        0.001, 0.002, 0.003, 0.005, 0.0075, 0.01, 0.02, 0.03, 0.05,
        0.075, 0.10, 0.15, 0.20, 0.25,
        0.33, 0.50, 0.67, 0.75, 1.00, 1.25, 1.50, 2.00, 3.00, 4.00
    ];

    private readonly Bitmap _image;
    private readonly string _initialDirectory;
    private readonly string _suggestedFileName;
    private readonly ImageCropPreviewControl _preview;
    private readonly Label _outputLabel = new();
    private readonly Label _zoomLabel = new();
    private readonly TrackBar _zoomTrackBar = new();
    private readonly Button _clipboardCopyButton = new();
    private readonly Button _resetButton = new();
    private readonly Button _saveButton = new();
    private readonly ToolTip _toolTip = new();
    private bool _syncingZoom;

    public CapturePreviewForm(
        Bitmap image,
        string targetName,
        int frameCount,
        bool isPartial,
        string initialDirectory,
        string suggestedFileName)
    {
        ArgumentNullException.ThrowIfNull(image);

        _image = image;
        _initialDirectory = initialDirectory;
        _suggestedFileName = suggestedFileName;
        _preview = new ImageCropPreviewControl(image) { Dock = DockStyle.Fill };

        Text = isPartial
            ? "Preview safe partial screenshot"
            : "Preview scrolling screenshot";
        ClientSize = new Size(1160, 760);
        MinimumSize = new Size(740, 480);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowIcon = false;

        Label titleLabel = new()
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            Text = isPartial
                ? $"Safe partial capture · {targetName}"
                : targetName,
            TextAlign = ContentAlignment.BottomLeft
        };

        Label instructionLabel = new()
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Text =
                $"Drag the cyan handles outside the image to crop any edge. " +
                $"Captured from {frameCount:N0} frame{(frameCount == 1 ? string.Empty : "s")}.",
            TextAlign = ContentAlignment.TopLeft
        };

        _resetButton.Anchor = AnchorStyles.None;
        _resetButton.AutoSize = true;
        _resetButton.Enabled = false;
        _resetButton.Margin = new Padding(8, 3, 8, 3);
        _resetButton.Text = "Reset crop";
        _resetButton.Click += (_, _) => _preview.Selection.Reset();

        TableLayoutPanel header = new()
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 11, 16, 9),
            RowCount = 2
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.Controls.Add(titleLabel, 0, 0);
        header.Controls.Add(instructionLabel, 0, 1);

        _outputLabel.AutoEllipsis = true;
        _outputLabel.AutoSize = false;
        _outputLabel.Dock = DockStyle.Fill;
        _outputLabel.Height = (Font.Height * 2) + 4;
        _outputLabel.TextAlign = ContentAlignment.MiddleLeft;

        _zoomTrackBar.AutoSize = false;
        _zoomTrackBar.Height = 28;
        _zoomTrackBar.LargeChange = 250;
        _zoomTrackBar.Maximum = 4000;
        _zoomTrackBar.Minimum = 1;
        _zoomTrackBar.SmallChange = 10;
        _zoomTrackBar.TickStyle = TickStyle.None;
        _zoomTrackBar.Value = 1000;
        _zoomTrackBar.Width = StandardZoomTrackBarWidth;
        _zoomTrackBar.Scroll += ZoomTrackBar_Scroll;

        Button zoomOutButton = CreateCompactButton("−", "Zoom out");
        zoomOutButton.Click += (_, _) => StepZoom(increase: false);
        Button zoomInButton = CreateCompactButton("+", "Zoom in");
        zoomInButton.Click += (_, _) => StepZoom(increase: true);
        Button fitButton = CreateTextButton("Fit", "Fit the whole image in the window");
        fitButton.Click += (_, _) => _preview.FitImage();
        Button actualSizeButton = CreateTextButton("100%", "Show one image pixel per screen pixel");
        actualSizeButton.Click += (_, _) => _preview.SetZoom(1.0);

        _zoomLabel.AutoSize = false;
        _zoomLabel.TextAlign = ContentAlignment.MiddleCenter;
        _zoomLabel.Width = 52;

        FlowLayoutPanel zoomPanel = new()
        {
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            WrapContents = false
        };
        zoomPanel.Controls.Add(zoomOutButton);
        zoomPanel.Controls.Add(_zoomTrackBar);
        zoomPanel.Controls.Add(zoomInButton);
        zoomPanel.Controls.Add(_zoomLabel);
        zoomPanel.Controls.Add(fitButton);
        zoomPanel.Controls.Add(actualSizeButton);

        Button discardButton = new()
        {
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(8, 3, 0, 3),
            Padding = new Padding(10, 3, 10, 3),
            Text = "Discard"
        };

        _clipboardCopyButton.AutoSize = true;
        _clipboardCopyButton.Margin = new Padding(8, 3, 0, 3);
        _clipboardCopyButton.Padding = new Padding(10, 3, 10, 3);
        _clipboardCopyButton.Text = "Clipboard Copy";
        _clipboardCopyButton.Click += ClipboardCopyButton_Click;

        _saveButton.AutoSize = true;
        _saveButton.Margin = new Padding(8, 3, 0, 3);
        _saveButton.Padding = new Padding(12, 3, 12, 3);
        _saveButton.Text = "Save As…";
        _saveButton.Click += SaveButton_Click;

        FlowLayoutPanel actionPanel = new()
        {
            Anchor = AnchorStyles.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            WrapContents = false
        };
        actionPanel.Controls.Add(discardButton);
        actionPanel.Controls.Add(_clipboardCopyButton);
        actionPanel.Controls.Add(_saveButton);

        ColumnStyle footerLeftColumn = new(SizeType.Percent, 50);
        ColumnStyle footerMiddleColumn = new(SizeType.AutoSize);
        ColumnStyle footerRightColumn = new(SizeType.Percent, 50);
        TableLayoutPanel footer = new()
        {
            AutoSize = true,
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 8, 16, 11),
            RowCount = 4
        };
        footer.ColumnStyles.Add(footerLeftColumn);
        footer.ColumnStyles.Add(footerMiddleColumn);
        footer.ColumnStyles.Add(footerRightColumn);
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.Controls.Add(_outputLabel, 0, 0);
        footer.SetColumnSpan(_outputLabel, 3);
        footer.Controls.Add(zoomPanel, 0, 1);
        footer.Controls.Add(_resetButton, 1, 1);
        footer.Controls.Add(actionPanel, 2, 1);

        bool? compactFooter = null;
        void UpdateFooterLayout()
        {
            int normalZoomPanelWidth = zoomPanel.PreferredSize.Width
                + StandardZoomTrackBarWidth
                - _zoomTrackBar.Width;
            int normalOuterColumnWidth = Math.Max(
                normalZoomPanelWidth,
                actionPanel.PreferredSize.Width);
            int centeredLayoutWidth = footer.Padding.Horizontal
                + (normalOuterColumnWidth * 2)
                + _resetButton.PreferredSize.Width;
            bool useCompactLayout = ClientSize.Width < centeredLayoutWidth;
            if (compactFooter == useCompactLayout)
            {
                return;
            }

            compactFooter = useCompactLayout;
            footer.SuspendLayout();

            if (useCompactLayout)
            {
                _zoomTrackBar.Width = CompactZoomTrackBarWidth;
                footerLeftColumn.SizeType = SizeType.AutoSize;
                footerMiddleColumn.SizeType = SizeType.Percent;
                footerMiddleColumn.Width = 100;
                footerRightColumn.SizeType = SizeType.AutoSize;
                footer.SetColumnSpan(zoomPanel, 3);
                footer.SetCellPosition(zoomPanel, new TableLayoutPanelCellPosition(0, 1));
                footer.SetColumnSpan(actionPanel, 3);
                footer.SetCellPosition(actionPanel, new TableLayoutPanelCellPosition(0, 2));
                footer.SetColumnSpan(_resetButton, 3);
                footer.SetCellPosition(_resetButton, new TableLayoutPanelCellPosition(0, 3));
            }
            else
            {
                _zoomTrackBar.Width = StandardZoomTrackBarWidth;
                footerLeftColumn.SizeType = SizeType.Percent;
                footerLeftColumn.Width = 50;
                footerMiddleColumn.SizeType = SizeType.AutoSize;
                footerRightColumn.SizeType = SizeType.Percent;
                footerRightColumn.Width = 50;
                footer.SetColumnSpan(zoomPanel, 1);
                footer.SetCellPosition(zoomPanel, new TableLayoutPanelCellPosition(0, 1));
                footer.SetColumnSpan(actionPanel, 1);
                footer.SetCellPosition(actionPanel, new TableLayoutPanelCellPosition(2, 1));
                footer.SetColumnSpan(_resetButton, 1);
                footer.SetCellPosition(_resetButton, new TableLayoutPanelCellPosition(1, 1));
            }

            footer.ResumeLayout(performLayout: true);
        }

        footer.SizeChanged += (_, _) => UpdateFooterLayout();
        SizeChanged += (_, _) => UpdateFooterLayout();

        TableLayoutPanel layout = new()
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_preview, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);

        AcceptButton = _saveButton;
        CancelButton = discardButton;
        _preview.SelectionChanged += Preview_SelectionChanged;
        _preview.ZoomChanged += Preview_ZoomChanged;

        UpdateOutputInformation();
        UpdateZoomControls();
    }

    public string? SavedPath { get; private set; }

    public Size SavedSize { get; private set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        BeginInvoke((Action)(() =>
        {
            _preview.FitImage();
            _preview.Focus();
        }));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Rectangle workingArea = Screen.FromControl(Owner ?? this).WorkingArea;
        int margin = 32;
        Size = new Size(
            Math.Min(Width, Math.Max(MinimumSize.Width, workingArea.Width - margin)),
            Math.Min(Height, Math.Max(MinimumSize.Height, workingArea.Height - margin)));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S))
        {
            SaveScreenshot();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private Button CreateCompactButton(string text, string accessibleDescription)
    {
        Button button = new()
        {
            AccessibleDescription = accessibleDescription,
            AutoSize = false,
            Height = 29,
            Margin = new Padding(0, 2, 0, 2),
            Text = text,
            Width = 32
        };
        _toolTip.SetToolTip(button, accessibleDescription);
        return button;
    }

    private Button CreateTextButton(string text, string tooltip)
    {
        Button button = new()
        {
            AutoSize = true,
            Margin = new Padding(8, 2, 0, 2),
            Padding = new Padding(5, 1, 5, 1),
            Text = text
        };
        _toolTip.SetToolTip(button, tooltip);
        return button;
    }

    private void Preview_SelectionChanged(object? sender, EventArgs e)
    {
        _resetButton.Enabled = _preview.Selection.IsCropped;
        _clipboardCopyButton.Text = "Clipboard Copy";
        UpdateOutputInformation();
    }

    private void Preview_ZoomChanged(object? sender, EventArgs e) => UpdateZoomControls();

    private void ZoomTrackBar_Scroll(object? sender, EventArgs e)
    {
        if (!_syncingZoom)
        {
            _preview.SetZoom(_zoomTrackBar.Value / 1000.0);
        }
    }

    private void StepZoom(bool increase)
    {
        double selectedZoom = increase
            ? ZoomSteps.FirstOrDefault(step => step > _preview.Zoom + 0.0001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(
                step => step < _preview.Zoom - 0.0001,
                _preview.Zoom / 2.0);
        _preview.SetZoom(selectedZoom);
    }

    private void UpdateZoomControls()
    {
        int tenthsOfAPercent = Math.Clamp((int)Math.Round(_preview.Zoom * 1000), 1, 4000);
        _syncingZoom = true;
        _zoomTrackBar.Value = tenthsOfAPercent;
        _syncingZoom = false;
        double percentage = _preview.Zoom * 100;
        _zoomLabel.Text = percentage < 0.1
            ? $"{percentage:0.###}%"
            : percentage < 10
                ? $"{percentage:0.0}%"
                : $"{percentage:0}%";
    }

    private void UpdateOutputInformation()
    {
        Rectangle crop = _preview.Selection.Bounds;
        ulong originalMemoryBytes = (ulong)_image.Width * (ulong)_image.Height * sizeof(int);
        _outputLabel.Text =
            $"Original: {_image.Width:N0} × {_image.Height:N0} px · " +
            $"approximately {FormatByteCount(originalMemoryBytes)} bitmap memory at 32 bpp\r\n" +
            $"Output: {crop.Width:N0} × {crop.Height:N0} px · " +
            $"approximately {FormatByteCount(_preview.Selection.Estimated32BitMemoryBytes)} bitmap memory at 32 bpp";
    }

    private static string FormatByteCount(ulong bytes)
    {
        const double kibibyte = 1024;
        const double mebibyte = 1024 * kibibyte;
        const double gibibyte = 1024 * mebibyte;

        return bytes >= gibibyte
            ? $"{bytes / gibibyte:0.0} GiB"
            : bytes >= mebibyte
                ? $"{bytes / mebibyte:0.0} MiB"
                : bytes >= kibibyte
                    ? $"{bytes / kibibyte:0.0} KiB"
                    : $"{bytes:N0} bytes";
    }

    private void SaveButton_Click(object? sender, EventArgs e) => SaveScreenshot();

    private void ClipboardCopyButton_Click(object? sender, EventArgs e)
    {
        try
        {
            UseWaitCursor = true;
            _clipboardCopyButton.Enabled = false;
            Update();

            Rectangle crop = _preview.Selection.Bounds;
            if (_preview.Selection.IsCropped)
            {
                using Bitmap cropped = BitmapCropper.Crop(_image, crop);
                CopyImageToClipboard(cropped);
            }
            else
            {
                CopyImageToClipboard(_image);
            }

            _clipboardCopyButton.Text = "Copied!";
        }
        catch (ExternalException)
        {
            MessageBox.Show(
                this,
                "The Clipboard is busy. Please try again.",
                "Unable to copy screenshot",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Unable to copy screenshot",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _clipboardCopyButton.Enabled = true;
        }
    }

    private static void CopyImageToClipboard(Image image)
    {
        DataObject data = new();
        data.SetImage(image);
        Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 100);
    }

    private void SaveScreenshot()
    {
        using SaveFileDialog dialog = new()
        {
            AddExtension = true,
            DefaultExt = "png",
            Filter = "PNG image (*.png)|*.png",
            FileName = _suggestedFileName,
            InitialDirectory = _initialDirectory,
            OverwritePrompt = true,
            Title = Text.Replace("Preview", "Save", StringComparison.Ordinal)
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            UseWaitCursor = true;
            _saveButton.Enabled = false;
            Update();

            Rectangle crop = _preview.Selection.Bounds;
            if (_preview.Selection.IsCropped)
            {
                using Bitmap cropped = BitmapCropper.Crop(_image, crop);
                PngFileSaver.Save(cropped, dialog.FileName);
            }
            else
            {
                PngFileSaver.Save(_image, dialog.FileName);
            }

            SavedPath = Path.GetFullPath(dialog.FileName);
            SavedSize = crop.Size;
            DialogResult = DialogResult.OK;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Unable to save screenshot",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _saveButton.Enabled = true;
        }
    }
}
