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
        bool cropVerticalScrollIndicator = _cropVerticalScrollIndicatorCheckBox.Checked;
        bool trimEmptyHorizontalSpace = _trimEmptyHorizontalSpaceCheckBox.Checked;
        CaptureMode captureMode = CaptureMode.Standard;
        _capturing = true;
        _finderTarget.Enabled = false;
        _statusLabel.Text = "Capturing… Press Escape to cancel.";
        _targetLabel.Text = "Resolving target…";
        _captureCancellation = new CancellationTokenSource();

        CaptureResult? result = null;

        try
        {
            while (result is null)
            {
                Hide();
                await Task.Delay(180, _captureCancellation.Token);

                try
                {
                    CaptureOptions captureOptions = new(
                        cropVerticalScrollIndicator,
                        trimEmptyHorizontalSpace,
                        captureMode);

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
                catch (CaptureSizeLimitExceededException exception) when (captureMode == CaptureMode.Standard)
                {
                    Show();
                    Activate();

                    CaptureMode? selectedMode = ShowOversizedCaptureDialog(exception);
                    if (selectedMode is null)
                    {
                        _statusLabel.Text = "Capture cancelled.";
                        return;
                    }

                    captureMode = selectedMode.Value;
                    _statusLabel.Text = captureMode == CaptureMode.Full
                        ? "Attempting the entire pane… Press Escape to cancel."
                        : "Capturing the largest safe top section… Press Escape to cancel.";
                    _targetLabel.Text = "Resolving target…";
                }
            }
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

            using CapturePreviewForm preview = new(
                result.Image,
                result.TargetName,
                result.FrameCount,
                result.IsPartial,
                GetInitialSaveDirectory(),
                $"PortraitScreenshot_{DateTime.Now:yyyy-MM-dd_HHmmss}.png");

            _statusLabel.Text = "Review the capture, crop if needed, then choose Save As.";
            bool restoreTopMost = TopMost;
            DialogResult previewResult;

            try
            {
                // A topmost owner can otherwise remain above an owned modal form.
                // Keep only the preview in the topmost band until it closes.
                TopMost = false;
                preview.TopMost = restoreTopMost;
                previewResult = preview.ShowDialog(this);
            }
            finally
            {
                preview.TopMost = false;
                TopMost = restoreTopMost;
            }

            if (previewResult != DialogResult.OK
                || string.IsNullOrWhiteSpace(preview.SavedPath))
            {
                _statusLabel.Text = "Capture discarded without saving.";
                return;
            }

            _settings.LastSaveDirectory = Path.GetDirectoryName(preview.SavedPath);

            try
            {
                _settings.Save();
                string captureDescription = result.IsPartial ? "safe partial" : "complete";
                _statusLabel.Text =
                    $"Saved {captureDescription} {preview.SavedSize.Width:N0} × " +
                    $"{preview.SavedSize.Height:N0} PNG " +
                    $"from {result.FrameCount} captures.";
            }
            catch (Exception settingsException)
            {
                _statusLabel.Text =
                    $"Screenshot saved, but the folder preference could not be saved: {settingsException.Message}";
            }
        }
    }

    private CaptureMode? ShowOversizedCaptureDialog(CaptureSizeLimitExceededException exception)
    {
        double estimatedBitmapMiB = exception.EstimatedPixels * 4.0 / (1024.0 * 1024.0);
        string memoryAssessment = CaptureMemoryAssessment.GetCurrentText(exception.EstimatedPixels);
        string sizeFinding = exception.IsEstimate
            ? $"The screenshot is estimated to need {exception.EstimatedPixels:N0} pixels"
            : $"The captured content has already reached {exception.EstimatedPixels:N0} pixels";
        var fullCaptureButton = new TaskDialogCommandLinkButton(
            "Try to capture all (ignore app pixel limits)",
            "Attempt the entire pane. The runaway-scroll guard, Windows, or available memory can still stop the capture.",
            true,
            true);
        var safeCaptureButton = new TaskDialogCommandLinkButton(
            "Capture up to the safe limit",
            $"Capture the largest full-width section from the top within {exception.SafePixelLimit:N0} pixels.",
            true,
            true);

        var page = new TaskDialogPage
        {
            AllowCancel = true,
            Caption = "Long Portrait Screenshot",
            DefaultButton = safeCaptureButton,
            Heading = "This pane exceeds the normal safety limit",
            Icon = TaskDialogIcon.Warning,
            SizeToContent = true,
            Text =
                $"{sizeFinding}, above the " +
                $"{exception.SafePixelLimit:N0}-pixel safety limit. A 32-bit bitmap at that size would use about " +
                $"{estimatedBitmapMiB:N0} MiB, and capture processing needs additional memory.\n\n" +
                memoryAssessment
        };
        page.Buttons.Add(fullCaptureButton);
        page.Buttons.Add(safeCaptureButton);
        page.Buttons.Add(TaskDialogButton.Cancel);

        TaskDialogButton selectedButton = TaskDialog.ShowDialog(
            this,
            page,
            TaskDialogStartupLocation.CenterOwner);

        if (ReferenceEquals(selectedButton, fullCaptureButton))
        {
            return CaptureMode.Full;
        }

        return ReferenceEquals(selectedButton, safeCaptureButton)
            ? CaptureMode.SafePortion
            : null;
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

}
