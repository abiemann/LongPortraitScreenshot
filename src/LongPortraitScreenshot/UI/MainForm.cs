using System.Drawing.Imaging;
using LongPortraitScreenshot.Automation;
using LongPortraitScreenshot.Capture;
using LongPortraitScreenshot.Configuration;

namespace LongPortraitScreenshot.UI;

public sealed class MainForm : Form
{
    private readonly FinderTargetControl _finderTarget = new();
    private readonly Label _targetLabel = new();
    private readonly Label _statusLabel = new();
    private readonly CheckBox _cropVerticalScrollIndicatorCheckBox = new();
    private readonly CheckBox _trimEmptyHorizontalSpaceCheckBox = new();
    private readonly System.Windows.Forms.Timer _resolveTimer = new() { Interval = 60 };
    private readonly TargetOverlay _targetOverlay = new();
    private readonly AppSettings _settings;

    private bool _dragging;
    private bool _resolving;
    private bool _capturing;
    private Point _latestPoint;
    private CancellationTokenSource? _captureCancellation;

    public MainForm()
    {
        _settings = AppSettings.Load();

        Text = "Long Portrait Screenshot";
        ClientSize = new Size(460, 380);
        MinimumSize = new Size(476, 419);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Capture an entire scrolling pane"
        };

        var instructions = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(410, 0),
            Text = "Drag the finder onto a vertical scroll area. Release when the green border surrounds the content you want."
        };

        _finderTarget.Anchor = AnchorStyles.None;
        _finderTarget.Margin = new Padding(0, 12, 0, 8);

        _targetLabel.AutoEllipsis = true;
        _targetLabel.Dock = DockStyle.Fill;
        _targetLabel.TextAlign = ContentAlignment.MiddleCenter;
        _targetLabel.Text = "No target selected";

        _statusLabel.AutoEllipsis = false;
        _statusLabel.AutoSize = true;
        _statusLabel.Anchor = AnchorStyles.Top;
        _statusLabel.MaximumSize = new Size(410, 0);
        _statusLabel.TextAlign = ContentAlignment.TopCenter;
        _statusLabel.ForeColor = SystemColors.GrayText;
        _statusLabel.Text = "The target must remain visible and unobscured during capture.";

        _cropVerticalScrollIndicatorCheckBox.Anchor = AnchorStyles.Left;
        _cropVerticalScrollIndicatorCheckBox.AutoSize = true;
        _cropVerticalScrollIndicatorCheckBox.Checked = _settings.CropVerticalScrollIndicator;
        _cropVerticalScrollIndicatorCheckBox.Margin = new Padding(0, 6, 0, 6);
        _cropVerticalScrollIndicatorCheckBox.Text = "Crop vertical scroll indicator from saved image";

        _trimEmptyHorizontalSpaceCheckBox.Anchor = AnchorStyles.Left;
        _trimEmptyHorizontalSpaceCheckBox.AutoSize = true;
        _trimEmptyHorizontalSpaceCheckBox.Checked = _settings.TrimEmptyHorizontalSpace;
        _trimEmptyHorizontalSpaceCheckBox.Margin = new Padding(0, 0, 0, 6);
        _trimEmptyHorizontalSpaceCheckBox.Text = "Trim empty space on left and right (5 px margin)";

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(24, 20, 24, 16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(instructions, 0, 1);
        layout.Controls.Add(_finderTarget, 0, 2);
        layout.Controls.Add(_targetLabel, 0, 3);
        layout.Controls.Add(_cropVerticalScrollIndicatorCheckBox, 0, 4);
        layout.Controls.Add(_trimEmptyHorizontalSpaceCheckBox, 0, 5);
        layout.Controls.Add(_statusLabel, 0, 6);
        Controls.Add(layout);

        _finderTarget.DragStarted += FinderTarget_DragStarted;
        _finderTarget.FinderMoved += FinderTarget_FinderMoved;
        _finderTarget.DragEnded += FinderTarget_DragEnded;
        _cropVerticalScrollIndicatorCheckBox.CheckedChanged += CropVerticalScrollIndicatorCheckBox_CheckedChanged;
        _trimEmptyHorizontalSpaceCheckBox.CheckedChanged += TrimEmptyHorizontalSpaceCheckBox_CheckedChanged;
        _resolveTimer.Tick += ResolveTimer_Tick;
    }

    private void CropVerticalScrollIndicatorCheckBox_CheckedChanged(object? sender, EventArgs e)
    {
        _settings.CropVerticalScrollIndicator = _cropVerticalScrollIndicatorCheckBox.Checked;
        SaveSettingsAfterOptionChange();
    }

    private void TrimEmptyHorizontalSpaceCheckBox_CheckedChanged(object? sender, EventArgs e)
    {
        _settings.TrimEmptyHorizontalSpace = _trimEmptyHorizontalSpaceCheckBox.Checked;
        SaveSettingsAfterOptionChange();
    }

    private void SaveSettingsAfterOptionChange()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception exception)
        {
            _statusLabel.Text = $"The option changed for this session, but could not be saved: {exception.Message}";
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _captureCancellation?.Cancel();
        _resolveTimer.Stop();
        _targetOverlay.Dispose();
        base.OnFormClosing(e);
    }

    private void FinderTarget_DragStarted(object? sender, EventArgs e)
    {
        if (_capturing)
        {
            return;
        }

        _dragging = true;
        _latestPoint = Cursor.Position;
        _targetLabel.Text = "Looking for a vertical scroll container…";
        _statusLabel.Text = "Release over the highlighted pane.";
        _resolveTimer.Start();
        ResolvePreviewAsync();
    }

    private void FinderTarget_FinderMoved(object? sender, FinderEventArgs e)
    {
        _latestPoint = e.ScreenPoint;
    }

    private async void FinderTarget_DragEnded(object? sender, FinderEventArgs e)
    {
        if (!_dragging || _capturing)
        {
            return;
        }

        _dragging = false;
        _latestPoint = e.ScreenPoint;
        _resolveTimer.Stop();
        _targetOverlay.Hide();

        await CaptureAtPointAsync(_latestPoint);
    }

    private void ResolveTimer_Tick(object? sender, EventArgs e)
    {
        ResolvePreviewAsync();
    }

    private async void ResolvePreviewAsync()
    {
        if (!_dragging || _resolving)
        {
            return;
        }

        _resolving = true;
        Point point = _latestPoint;

        try
        {
            ScrollTarget? target = await Task.Run(
                () => TargetResolver.Resolve(point, Environment.ProcessId));

            if (!_dragging)
            {
                return;
            }

            if (target is null)
            {
                _targetOverlay.Hide();
                _targetLabel.Text = "No compatible vertical scroll container here";
                return;
            }

            _targetOverlay.Show(target.Bounds);
            _targetLabel.Text = target.DisplayName;
        }
        catch
        {
            if (_dragging)
            {
                _targetOverlay.Hide();
                _targetLabel.Text = "Unable to inspect this window";
            }
        }
        finally
        {
            _resolving = false;
        }
    }

    private async Task CaptureAtPointAsync(Point screenPoint)
    {
        CaptureOptions captureOptions = new(
            _cropVerticalScrollIndicatorCheckBox.Checked,
            _trimEmptyHorizontalSpaceCheckBox.Checked);
        _capturing = true;
        _finderTarget.Enabled = false;
        _statusLabel.Text = "Capturing… Press Escape to cancel.";
        _targetLabel.Text = "Resolving target…";
        _captureCancellation = new CancellationTokenSource();

        CaptureResult? result = null;

        try
        {
            Hide();
            await Task.Delay(180, _captureCancellation.Token);

            result = await Task.Run(() =>
            {
                ScrollTarget? target = TargetResolver.Resolve(screenPoint, Environment.ProcessId);
                if (target is null)
                {
                    throw new InvalidOperationException(
                        "The selected area does not expose a vertical UI Automation scroll pattern.");
                }

                return CaptureSession.Capture(target, captureOptions, _captureCancellation.Token);
            }, _captureCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Capture cancelled. The original scroll position was restored.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Capture failed.";
            Show();
            Activate();
            MessageBox.Show(
                this,
                ex.Message,
                "Unable to capture this pane",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            Show();
            Activate();
            _capturing = false;
            _finderTarget.Enabled = true;
            _captureCancellation.Dispose();
            _captureCancellation = null;
        }

        if (result is null)
        {
            return;
        }

        using (result)
        {
            _targetLabel.Text = result.TargetName;

            using var dialog = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = "png",
                Filter = "PNG image (*.png)|*.png",
                FileName = $"PortraitScreenshot_{DateTime.Now:yyyy-MM-dd_HHmmss}.png",
                InitialDirectory = GetInitialSaveDirectory(),
                OverwritePrompt = true,
                Title = "Save the complete scrolling screenshot"
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                _statusLabel.Text = "Capture discarded without saving.";
                return;
            }

            try
            {
                SavePngAtomically(result.Image, dialog.FileName);
                _settings.LastSaveDirectory = Path.GetDirectoryName(Path.GetFullPath(dialog.FileName));

                try
                {
                    _settings.Save();
                    _statusLabel.Text = $"Saved {result.Image.Width} × {result.Image.Height:N0} PNG from {result.FrameCount} captures.";
                }
                catch (Exception settingsException)
                {
                    _statusLabel.Text =
                        $"Screenshot saved, but the folder preference could not be saved: {settingsException.Message}";
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "The screenshot was captured but could not be saved.";
                MessageBox.Show(
                    this,
                    ex.Message,
                    "Unable to save screenshot",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }

    private string GetInitialSaveDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_settings.LastSaveDirectory)
            && Directory.Exists(_settings.LastSaveDirectory))
        {
            return _settings.LastSaveDirectory;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }

    private static void SavePngAtomically(Image image, string destinationPath)
    {
        string fullPath = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("Choose a valid destination folder.");
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            image.Save(temporaryPath, ImageFormat.Png);
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
