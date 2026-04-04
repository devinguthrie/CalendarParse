using System.Text.Json;
using CalendarParse.Data;
using CalendarParse.Models;
using CalendarParse.ViewModels;
using Microsoft.Maui.Graphics;

namespace CalendarParse.Pages;

// ── Persistence DTO ───────────────────────────────────────────────────────────

/// <summary>
/// Serialized state of one bubble — written to ScheduleRun.ShiftsJson after each
/// confirmation action so the session can be resumed exactly where it was left off.
/// </summary>
file record BubblePersist(
    string Employee,
    string Date,
    string OriginalTimeRange,
    string DisplayTime,
    int    TimeState,       // 0=Pending 1=Editing 2=Confirmed
    int    PositionState,   // 0=Pending 1=Confirmed 2=Skipped 3=Editing
    int?   BoundsX,
    int?   BoundsY,
    int?   BoundsWidth,
    int?   BoundsHeight);

// ── View model ────────────────────────────────────────────────────────────────

public class BubbleViewModel
{
    public BubbleState State        { get; }
    public ShiftData   Shift        => State.Shift;
    public string      DisplayTime  => State.DisplayTime;
    public bool        IsFullyConfirmed => State.IsFullyConfirmed;

    // Screen-space bounding box (updated when image renders / zooms)
    public RectF ScreenBounds { get; set; }

    public Color BorderColor => State.TimeState switch
    {
        TimeState.Confirmed => Colors.Green,
        TimeState.Editing   => Colors.Red,
        _                   => Colors.Gold,
    };

    public BubbleViewModel(ShiftData shift, PositionState positionState = PositionState.Skipped)
        => State = new BubbleState(shift, positionState);
}

// ── Page ──────────────────────────────────────────────────────────────────────

public partial class ConfirmationPage : ContentPage
{
    private readonly ConfirmationPageViewModel _viewModel;
    private readonly ScheduleHistoryDb  _db;

    private byte[]? _imageBytes;
    private int     _naturalImageWidth;
    private int     _naturalImageHeight;
    private string  _employeeName  = string.Empty;
    private string? _watchedPackage;
    private int     _runId         = -1;

    private List<BubbleViewModel> _bubbles   = [];
    private BubbleViewModel?      _selected;
    private Size                  _renderedImageSize;
    private Size                  _baseImageSize;

    // ── Drag rect state ───────────────────────────────────────────────────────

    private bool  _panLocked  = false;
    private bool  _zoomLocked = false;
    private bool  _positionOptIn = false;
    private RectF _rectBounds;
    private RectF _rectAtGestureStart;
    private float _zoomScale = 1f;
    private float _zoomScaleAtGestureStart = 1f;
    private bool  _updatingZoomSlider;
    private float _lastRectWidth = 200f;
    private float _lastRectHeight = 70f;
    private int   _currentBubbleIndex = -1;
    private double _lastScrollX;
    private double _lastScrollY;

    private const float MinZoomScale = 1f;
    private const float MaxZoomScale = 4f;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNameCaseInsensitive = true };

    // ── Entry points ──────────────────────────────────────────────────────────

    public ConfirmationPage(ConfirmationPageViewModel viewModel, ScheduleHistoryDb db)
    {
        InitializeComponent();
        _viewModel      = viewModel;
        _db             = db;

        BubbleCanvas.Drawable = new BubbleDrawable(this);
    }

    public async Task StartWithImageAsync(byte[] imageBytes, string? watchedPackage = null)
    {
        _imageBytes     = imageBytes;
        _watchedPackage = watchedPackage;
        _employeeName   = await _viewModel.GetEmployeeNameAsync();
        await RunProcessingFlowAsync();
    }

    public async Task StartResumeAsync(int runId)
    {
        SetPanel(Panel.Loading);
        LoadingLabel.Text       = "Restoring session…";
        ProgressLabel.IsVisible = false;

        var run = await _db.GetRunForResumeAsync(runId);
        if (run is null)
        {
            ShowError("Session not found.");
            return;
        }

        _runId              = run.Id;
        _naturalImageWidth  = run.ImageWidth;
        _naturalImageHeight = run.ImageHeight;

        var prefs       = await _db.GetPreferencesAsync();
        _positionOptIn  = prefs.PositionOptIn == true;

        if (!string.IsNullOrEmpty(run.ImagePath) && File.Exists(run.ImagePath))
        {
            _imageBytes = await File.ReadAllBytesAsync(run.ImagePath);
            await ShowImageAsync(_imageBytes);
        }
        else
        {
            ScheduleImage.Source = null;
        }

        _bubbles = DeserializeBubbles(run.ShiftsJson);

        System.Diagnostics.Debug.WriteLine(
            $"[StartResumeAsync] runId={runId} shiftsJson len={run.ShiftsJson?.Length ?? 0} " +
            $"bubbles={_bubbles.Count} imageW={_naturalImageWidth}");

        RenderOverlay();
    }

    public void ShowSharePrompt(string? watchedPackage)
    {
        _watchedPackage = watchedPackage;
        SetPanel(Panel.SharePrompt);
    }

    // ── Panel switching ───────────────────────────────────────────────────────

    private enum Panel { Loading, SharePrompt, Submitted, Overlay, Error }

    private void SetPanel(Panel panel)
    {
        LoadingPanel.IsVisible     = panel == Panel.Loading;
        SharePromptPanel.IsVisible = panel == Panel.SharePrompt;
        SubmittedPanel.IsVisible   = panel == Panel.Submitted;
        OverlayPanel.IsVisible     = panel == Panel.Overlay;
        ErrorPanel.IsVisible       = panel == Panel.Error;
        BottomBar.IsVisible        = panel == Panel.Overlay;
        ProgressHeader.IsVisible   = panel == Panel.Overlay;
    }

    // ── Processing flow ───────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;

    private async Task RunProcessingFlowAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        SetPanel(Panel.Loading);
        LoadingLabel.Text       = "Checking server…";
        ProgressLabel.IsVisible = false;

        try
        {
            var health = await _viewModel.CheckHealthAsync(ct);
            if (health is null || !health.OllamaAvailable)
            {
                ShowError(health is null
                    ? "Can't reach server.\nCheck IP:PORT in Settings."
                    : "Server reached but Ollama is unavailable.\nCheck your PC.");
                return;
            }

            if (_imageBytes is null)
            {
                SetPanel(Panel.SharePrompt);
                return;
            }

            LoadingLabel.Text       = "Submitting to server…";
            ProgressLabel.IsVisible = false;

            var submitOutcome = await _viewModel.SubmitForProcessingAsync(_imageBytes, _employeeName, ct);
            if (!submitOutcome.IsSuccess)
            {
                ShowError(submitOutcome.ErrorMessage ?? "Could not submit to server.\nCheck connection and try again.");
                return;
            }

            SetPanel(Panel.Submitted);
        }
        catch (OperationCanceledException)
        {
            // User navigated away — ignore
        }
        catch (Exception ex)
        {
            ShowError($"Unexpected error:\n{ex.Message}");
        }
    }

    private async Task ShowImageAsync(byte[] imageBytes)
    {
        var stream = new MemoryStream(imageBytes);
        ScheduleImage.Source = ImageSource.FromStream(() => stream);
        await Task.Yield();
    }

    private void RenderOverlay()
    {
        SetPanel(Panel.Overlay);
        BubbleDetailPanel.IsVisible = false;
        LockPanBtn.IsVisible        = false;
        ZoomControlsPanel.IsVisible = false;
        DragOverlay.IsVisible       = false;
        _panLocked                  = false;
        _zoomLocked                 = false;
        _zoomScale                  = 1f;
        _baseImageSize              = Size.Zero;
        ImageScrollView.Orientation = ScrollOrientation.Both;
        DragOverlay.InputTransparent = true;
        LockPanBtn.Text             = "🔓 Lock Pan";
        ZoomLockBtn.Text            = "🔓";
        LockPanBtn.Background  = BackgroundFromResource("Gray300");
        ZoomLockBtn.Background = BackgroundFromResource("Gray300");
        ZoomSlider.Value            = 1;
        ZoomSlider.IsEnabled        = true;
        ImageContainer.WidthRequest = -1;
        ImageContainer.HeightRequest = -1;
        _currentBubbleIndex         = -1;
        ValidationLabel.IsVisible    = false;
        EditPositionBtn.IsVisible    = false;
        UpdateConfirmProgress();

        ScheduleImage.SizeChanged += OnImageSizeChanged;

        var first = _bubbles.FirstOrDefault(b => !b.IsFullyConfirmed);
        if (first is not null)
            OnBubbleTapped(first);
    }

    private void OnImageSizeChanged(object? sender, EventArgs e)
    {
        _renderedImageSize = new Size(ScheduleImage.Width, ScheduleImage.Height);
        if (_zoomScale <= 1.001f || _baseImageSize.Width <= 0)
            _baseImageSize = _renderedImageSize;

        RecalculateBubbleScreenPositions();
        BubbleCanvas.Invalidate();

        // Keep current drag rect anchored; do not reinitialize on layout changes.
        if (_selected is not null)
            UpdateDragRect();
    }

    private void RecalculateBubbleScreenPositions()
    {
        var (scaleX, scaleY, offsetX, offsetY) = GetImageTransform();

        foreach (var b in _bubbles)
        {
            if (b.Shift.EstimatedBounds is not { } bounds) continue;

            b.ScreenBounds = new RectF(
                x:      (float)(bounds.X * scaleX + offsetX),
                y:      (float)(bounds.Y * scaleY + offsetY),
                width:  (float)(bounds.Width  * scaleX),
                height: (float)(bounds.Height * scaleY));
        }
    }

    private (double scaleX, double scaleY, double offsetX, double offsetY) GetImageTransform()
    {
        if (_naturalImageWidth <= 0 || _renderedImageSize.Width <= 0)
            return (1, 1, 0, 0);

        var natW  = (double)_naturalImageWidth;
        var natH  = (double)_naturalImageHeight;
        var rendW = _renderedImageSize.Width;
        var rendH = _renderedImageSize.Height;

        var scale   = Math.Min(rendW / natW, rendH / natH);
        var offsetX = (rendW - natW * scale) / 2.0;
        var offsetY = (rendH - natH * scale) / 2.0;

        return (scale, scale, offsetX, offsetY);
    }

    // ── Tap hit test (bubble selection) ──────────────────────────────────────

    private void OnImageContainerTapped(object? sender, TappedEventArgs e)
    {
        if (_bubbles.Count == 0) return;

        var withBounds = _bubbles.Where(b => b.ScreenBounds.Width > 0).ToList();
        if (withBounds.Count == 0) return;

        var pos = e.GetPosition(ImageContainer);
        if (pos is not { } p) return;

        var hit = withBounds.FirstOrDefault(b => b.ScreenBounds.Contains((float)p.X, (float)p.Y))
                  ?? withBounds.OrderBy(b =>
                  {
                      var cx = b.ScreenBounds.X + b.ScreenBounds.Width  / 2f;
                      var cy = b.ScreenBounds.Y + b.ScreenBounds.Height / 2f;
                      var dx = p.X - cx;
                      var dy = p.Y - cy;
                      return dx * dx + dy * dy;
                  }).First();

        OnBubbleTapped(hit);
    }

    // ── Bubble interaction ────────────────────────────────────────────────────

    private void OnBubbleTapped(BubbleViewModel bubble)
    {
        var previousSelection = _selected;
        _currentBubbleIndex = _bubbles.IndexOf(bubble);
        _selected = bubble;
        BubbleEmployeeLabel.Text = bubble.Shift.Employee;
        BubbleDateLabel.Text     = bubble.Shift.Date;

        // Time display: read-only label, editing entry hidden by default
        var timeText = bubble.DisplayTime;
        BubbleTimeLabel.Text    = string.IsNullOrWhiteSpace(timeText) ? "No schedule found" : timeText;
        BubbleTimeLabel.IsVisible  = bubble.State.TimeState != TimeState.Editing;
        BubbleTimeEntry.IsVisible  = bubble.State.TimeState == TimeState.Editing;
        BubbleTimeEntry.Text       = timeText;

        var needsPositionConfirm = IsLocationReviewOrEditStep(bubble);
        var isFullyConfirmed = bubble.IsFullyConfirmed;
        ThumbsUpBtn.IsVisible    = bubble.State.TimeState != TimeState.Confirmed || needsPositionConfirm;
        ThumbsDownBtn.IsVisible  = bubble.State.TimeState != TimeState.Editing;
        ThumbsDownBtn.Text       = needsPositionConfirm
            ? "📍 Edit Position"
            : (isFullyConfirmed ? "✏️ Edit Time" : "✏️ Edit");
        EditActions.IsVisible    = bubble.State.TimeState == TimeState.Editing;
        EditPositionBtn.IsVisible = _positionOptIn && isFullyConfirmed;

        ThumbsUpBtn.Text = bubble.State.TimeState != TimeState.Confirmed
            ? "👍 Confirm Time"
            : (needsPositionConfirm ? "📍 Confirm Location" : "👍 Confirm");

        BubbleDetailPanel.IsVisible = true;
        BubbleCanvas.Invalidate();

        // Show position overlay for all position-opted-in bubbles.
        // If OCR bounds are missing, we still allow manual location editing via a centered rect.
        if (_positionOptIn)
        {
            var isNewSelection = !ReferenceEquals(previousSelection, bubble);
            if (isNewSelection)
            {
                if (bubble.State.PositionState == PositionState.Confirmed && bubble.ScreenBounds.Width > 0)
                {
                    // Snap rect to the confirmed location so the user can see where it was placed.
                    _rectBounds = bubble.ScreenBounds;
                    _lastRectWidth  = _rectBounds.Width;
                    _lastRectHeight = _rectBounds.Height;
                    UpdateDragRect();
                }
                else
                {
                    InitDragRect(bubble);
                }
            }

            // Ensure a visible rect exists even when selection is unchanged and bounds were absent.
            if (_rectBounds.Width <= 0 || _rectBounds.Height <= 0)
                InitDragRect(bubble);

            DragOverlay.IsVisible = true;
            LockPanBtn.IsVisible  = true;
            ZoomControlsPanel.IsVisible = true;
            if (bubble.State.PositionState == PositionState.Editing)
                EnterLocationEditMode();
            else
                UpdateOverlayEditVisualState();
        }
        else
        {
            DragOverlay.IsVisible = false;
            LockPanBtn.IsVisible  = false;
            ZoomControlsPanel.IsVisible = false;
        }

        UpdateHeaderProgress();
    }

    private void OnThumbsUpClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;

        // Enforce order: time confirm first, then location confirm for the same overlay.
        if (_selected.State.TimeState != TimeState.Confirmed)
        {
            _selected.State.ConfirmTime();
            _ = PersistProgressAsync();
            BubbleCanvas.Invalidate();
            UpdateConfirmProgress();

            if (IsLocationReviewOrEditStep(_selected))
            {
                OnBubbleTapped(_selected);
                return;
            }

            AdvanceFocus();
            return;
        }

        if (IsLocationReviewOrEditStep(_selected))
        {
            SyncBoundsToImageCoords(_selected);
            _selected.State.ConfirmPosition();
            _ = PersistProgressAsync();
            BubbleCanvas.Invalidate();
            UpdateConfirmProgress();
            AdvanceFocus();
            return;
        }

        AdvanceFocus();
    }

    private void OnThumbsDownClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;

        // During the location-confirm step, Edit should enter position editing mode,
        // not open the time text editor.
        if (IsLocationReviewOrEditStep(_selected))
        {
            EnterLocationEditMode();
            return;
        }

        _selected.State.EditTime();
        BubbleTimeLabel.IsVisible  = false;
        BubbleTimeEntry.IsVisible  = true;
        EditActions.IsVisible      = true;
        ThumbsDownBtn.IsVisible    = false;
        BubbleTimeEntry.Focus();
        BubbleCanvas.Invalidate();
    }

    private void OnEditSaveClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        _selected.State.SaveTime(BubbleTimeEntry.Text ?? string.Empty);
        BubbleTimeLabel.Text      = _selected.DisplayTime;
        BubbleTimeLabel.IsVisible = true;
        BubbleTimeEntry.IsVisible = false;
        EditActions.IsVisible     = false;
        ThumbsDownBtn.IsVisible   = true;
        _ = PersistProgressAsync();
        AdvanceFocus();
    }

    private void OnEditDismissClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        _selected.State.DismissEdit();
        BubbleTimeLabel.IsVisible = true;
        BubbleTimeEntry.IsVisible = false;
        EditActions.IsVisible     = false;
        ThumbsDownBtn.IsVisible   = true;
        BubbleCanvas.Invalidate();
        UpdateConfirmProgress();
    }

    private void OnEditPositionClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        _selected.State.BeginEditPosition();
        _ = PersistProgressAsync();
        OnBubbleTapped(_selected);
        EnterLocationEditMode();
    }

    private void EnterLocationEditMode()
    {
        if (_selected is not null)
            _selected.State.BeginEditPosition();

        // Enter edit-position step without auto-locking pan.
        _panLocked = false;
        ImageScrollView.Orientation = ScrollOrientation.Both;
        LockPanBtn.Text = "🔓 Lock Pan";
        LockPanBtn.Background = BackgroundFromResource("Gray300");

        _ = PersistProgressAsync();
        UpdateOverlayEditVisualState();
    }

    private void AdvanceFocus()
    {
        _lastRectWidth            = _rectBounds.Width;
        _lastRectHeight           = _rectBounds.Height;
        DragOverlay.IsVisible        = false;
        LockPanBtn.IsVisible         = false;
        ZoomControlsPanel.IsVisible  = false;
        EditPositionBtn.IsVisible    = false;
        BubbleDetailPanel.IsVisible  = false;
        BubbleCanvas.Invalidate();
        UpdateConfirmProgress();

        var next = _bubbles
            .SkipWhile(b => b != _selected)
            .Skip(1)
            .FirstOrDefault(b => !b.IsFullyConfirmed)
            ?? _bubbles.FirstOrDefault(b => !b.IsFullyConfirmed);

        if (next is not null)
            OnBubbleTapped(next);
    }

    private void UpdateConfirmProgress()
    {
        var confirmed = _bubbles.Count(b => b.IsFullyConfirmed);
        var total     = _bubbles.Count;
        ConfirmProgressLabel.Text    = string.Empty;
        ProcessScheduleBtn.IsEnabled = total > 0;

        UpdateHeaderProgress();

        // Validation hints are shown when user clicks Process Schedule.
        ValidationLabel.IsVisible = false;
    }

    // ── Drag rect ─────────────────────────────────────────────────────────────

    private void InitDragRect(BubbleViewModel bubble)
    {
        var width  = _lastRectWidth > 0 ? _lastRectWidth : (bubble.ScreenBounds.Width > 0 ? bubble.ScreenBounds.Width : 200f);
        var height = _lastRectHeight > 0 ? _lastRectHeight : (bubble.ScreenBounds.Height > 0 ? bubble.ScreenBounds.Height : 70f);

        // Start in the middle of the currently visible viewport (not image origin).
        var centerX = (float)ImageScrollView.ScrollX + (float)(ImageScrollView.Width / 2d);
        var centerY = (float)ImageScrollView.ScrollY + (float)(ImageScrollView.Height / 2d);

        _rectBounds = new RectF(
            centerX - width / 2f,
            centerY - height / 2f,
            width,
            height);
        UpdateDragRect();
    }

    private void UpdateDragRect()
    {
        // _rectBounds is in ImageContainer (scroll-content) space.
        // DragOverlay is now a sibling of the ScrollView in viewport space, so subtract scroll offset.
        var sx = (float)ImageScrollView.ScrollX;
        var sy = (float)ImageScrollView.ScrollY;
        var r  = _rectBounds;
        var x  = r.X - sx;
        var y  = r.Y - sy;
        AbsoluteLayout.SetLayoutBounds(SelectedRectBorder, new Microsoft.Maui.Graphics.Rect(x, y, r.Width, r.Height));
        AbsoluteLayout.SetLayoutBounds(SelectedRectFill, new Microsoft.Maui.Graphics.Rect(x, y, r.Width, r.Height));
        AbsoluteLayout.SetLayoutBounds(RectMoveHandle,   new Microsoft.Maui.Graphics.Rect(x, y, r.Width, r.Height));
        AbsoluteLayout.SetLayoutBounds(CornerTL, new Microsoft.Maui.Graphics.Rect(x - 12,           y - 12,            24, 24));
        AbsoluteLayout.SetLayoutBounds(CornerTR, new Microsoft.Maui.Graphics.Rect(x + r.Width - 12, y - 12,            24, 24));
        AbsoluteLayout.SetLayoutBounds(CornerBL, new Microsoft.Maui.Graphics.Rect(x - 12,           y + r.Height - 12, 24, 24));
        AbsoluteLayout.SetLayoutBounds(CornerBR, new Microsoft.Maui.Graphics.Rect(x + r.Width - 12, y + r.Height - 12, 24, 24));
    }

    private void OnImageScrollViewScrolled(object? sender, ScrolledEventArgs e)
    {
        _lastScrollX = e.ScrollX;
        _lastScrollY = e.ScrollY;
    }

    // ── Lock pan toggle ───────────────────────────────────────────────────────

    private void OnLockPanClicked(object? sender, EventArgs e)
    {
        var scrollX = _lastScrollX;
        var scrollY = _lastScrollY;

        _panLocked = !_panLocked;
        ImageScrollView.Orientation  = _panLocked ? ScrollOrientation.Neither : ScrollOrientation.Both;
        LockPanBtn.Text              = _panLocked ? "🔒 Unlock Pan" : "🔓 Lock Pan";
        LockPanBtn.Background = _panLocked
            ? BackgroundFromResource("Primary")
            : BackgroundFromResource("Gray300");

        // Preserve current viewport; the delay lets the layout pass triggered by Orientation settle first.
        Dispatcher.Dispatch(async () =>
        {
            await Task.Delay(180);
            await ImageScrollView.ScrollToAsync(scrollX, scrollY, false);
        });

        UpdateOverlayEditVisualState();
    }

    private void OnLockZoomClicked(object? sender, EventArgs e)
    {
        _zoomLocked = !_zoomLocked;
        ZoomLockBtn.Text            = _zoomLocked ? "🔒" : "🔓";
        ZoomLockBtn.Background = _zoomLocked
            ? BackgroundFromResource("Primary")
            : BackgroundFromResource("Gray300");
        ZoomSlider.IsEnabled        = !_zoomLocked;

        UpdateOverlayEditVisualState();
    }

    private void OnZoomSliderValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_zoomLocked || _updatingZoomSlider)
            return;

        ApplyZoomScale((float)e.NewValue);
    }

    private void OnImagePinched(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (_zoomLocked) return;

        switch (e.Status)
        {
            case GestureStatus.Started:
                if (_baseImageSize.Width <= 0 || _baseImageSize.Height <= 0)
                {
                    var baseW = ImageContainer.Width > 0 ? ImageContainer.Width : ScheduleImage.Width;
                    var baseH = ImageContainer.Height > 0 ? ImageContainer.Height : ScheduleImage.Height;
                    if (baseW > 0 && baseH > 0)
                        _baseImageSize = new Size(baseW, baseH);
                }
                _zoomScaleAtGestureStart = _zoomScale;
                break;
            case GestureStatus.Running:
                var nextScale = Math.Clamp(_zoomScaleAtGestureStart * (float)e.Scale, MinZoomScale, MaxZoomScale);
                if (Math.Abs(nextScale - _zoomScale) < 0.001f)
                    return;

                _zoomScale = nextScale;
                ApplyZoomScale(_zoomScale);
                break;
        }
    }

    private void ApplyZoomScale(float scale)
    {
        _zoomScale = Math.Clamp(scale, MinZoomScale, MaxZoomScale);

        if (_baseImageSize.Width <= 0 || _baseImageSize.Height <= 0)
            _baseImageSize = _renderedImageSize;
        if (_baseImageSize.Width <= 0 || _baseImageSize.Height <= 0)
            return;

        ImageContainer.WidthRequest  = _baseImageSize.Width * _zoomScale;
        ImageContainer.HeightRequest = _baseImageSize.Height * _zoomScale;

        _updatingZoomSlider = true;
        ZoomSlider.Value = _zoomScale;
        _updatingZoomSlider = false;

        RecalculateBubbleScreenPositions();

        // If vertical whitespace appears above content after zoom, snap image to top.
        if (ImageContainer.Height > 0 && ImageScrollView.Height > 0)
        {
            var maxScrollY = Math.Max(0d, ImageContainer.Height - ImageScrollView.Height);
            if (ImageScrollView.ScrollY > maxScrollY + 0.5 || maxScrollY <= 0.5)
            {
                Dispatcher.Dispatch(() => _ = ImageScrollView.ScrollToAsync(ImageScrollView.ScrollX, 0, false));
            }
        }

        if (_selected is not null)
            UpdateDragRect();
        BubbleCanvas.Invalidate();
    }

    // ── Drag rect gesture handlers ────────────────────────────────────────────

    private void OnRectMovePanned(object? sender, PanUpdatedEventArgs e)
    {
        if (!CanEditOverlay()) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _rectAtGestureStart = _rectBounds;
                break;
            case GestureStatus.Running:
                _rectBounds = new RectF(
                    _rectAtGestureStart.X + (float)e.TotalX,
                    _rectAtGestureStart.Y + (float)e.TotalY,
                    _rectAtGestureStart.Width,
                    _rectAtGestureStart.Height);
                UpdateDragRect();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _lastRectWidth = _rectBounds.Width;
                _lastRectHeight = _rectBounds.Height;
                if (_selected is not null) SyncBoundsToImageCoords(_selected);
                break;
        }
    }

    private void OnCornerTLPanned(object? sender, PanUpdatedEventArgs e)
    {
        if (!CanEditOverlay()) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _rectAtGestureStart = _rectBounds;
                break;
            case GestureStatus.Running:
                var newX = _rectAtGestureStart.X + (float)e.TotalX;
                var newY = _rectAtGestureStart.Y + (float)e.TotalY;
                var newW = _rectAtGestureStart.Right  - newX;
                var newH = _rectAtGestureStart.Bottom - newY;
                if (newW > 20 && newH > 20)
                    _rectBounds = new RectF(newX, newY, newW, newH);
                UpdateDragRect();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _lastRectWidth = _rectBounds.Width;
                _lastRectHeight = _rectBounds.Height;
                if (_selected is not null) SyncBoundsToImageCoords(_selected);
                break;
        }
    }

    private void OnCornerTRPanned(object? sender, PanUpdatedEventArgs e)
    {
        if (!CanEditOverlay()) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _rectAtGestureStart = _rectBounds;
                break;
            case GestureStatus.Running:
                var newY = _rectAtGestureStart.Y + (float)e.TotalY;
                var newW = _rectAtGestureStart.Width  + (float)e.TotalX;
                var newH = _rectAtGestureStart.Bottom - newY;
                if (newW > 20 && newH > 20)
                    _rectBounds = new RectF(_rectAtGestureStart.X, newY, newW, newH);
                UpdateDragRect();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _lastRectWidth = _rectBounds.Width;
                _lastRectHeight = _rectBounds.Height;
                if (_selected is not null) SyncBoundsToImageCoords(_selected);
                break;
        }
    }

    private void OnCornerBLPanned(object? sender, PanUpdatedEventArgs e)
    {
        if (!CanEditOverlay()) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _rectAtGestureStart = _rectBounds;
                break;
            case GestureStatus.Running:
                var newX = _rectAtGestureStart.X + (float)e.TotalX;
                var newW = _rectAtGestureStart.Right  - newX;
                var newH = _rectAtGestureStart.Height + (float)e.TotalY;
                if (newW > 20 && newH > 20)
                    _rectBounds = new RectF(newX, _rectAtGestureStart.Y, newW, newH);
                UpdateDragRect();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _lastRectWidth = _rectBounds.Width;
                _lastRectHeight = _rectBounds.Height;
                if (_selected is not null) SyncBoundsToImageCoords(_selected);
                break;
        }
    }

    private void OnCornerBRPanned(object? sender, PanUpdatedEventArgs e)
    {
        if (!CanEditOverlay()) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _rectAtGestureStart = _rectBounds;
                break;
            case GestureStatus.Running:
                var newW = _rectAtGestureStart.Width  + (float)e.TotalX;
                var newH = _rectAtGestureStart.Height + (float)e.TotalY;
                if (newW > 20 && newH > 20)
                    _rectBounds = new RectF(_rectAtGestureStart.X, _rectAtGestureStart.Y, newW, newH);
                UpdateDragRect();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _lastRectWidth = _rectBounds.Width;
                _lastRectHeight = _rectBounds.Height;
                if (_selected is not null) SyncBoundsToImageCoords(_selected);
                break;
        }
    }

    /// <summary>Converts screen rect back to image-pixel coords and updates the shift.</summary>
    private void SyncBoundsToImageCoords(BubbleViewModel bubble)
    {
        var (scaleX, scaleY, offsetX, offsetY) = GetImageTransform();
        if (scaleX <= 0) return;

        var r = _rectBounds;
        bubble.Shift.EstimatedBounds = new BoundingBox
        {
            X      = (int)Math.Round((r.X - offsetX) / scaleX),
            Y      = (int)Math.Round((r.Y - offsetY) / scaleY),
            Width  = (int)Math.Round(r.Width  / scaleX),
            Height = (int)Math.Round(r.Height / scaleY),
        };

        // Recalculate screen bounds from the now-updated image bounds so the canvas rect stays in sync
        bubble.ScreenBounds = new RectF(
            (float)(bubble.Shift.EstimatedBounds.X * scaleX + offsetX),
            (float)(bubble.Shift.EstimatedBounds.Y * scaleY + offsetY),
            (float)(bubble.Shift.EstimatedBounds.Width  * scaleX),
            (float)(bubble.Shift.EstimatedBounds.Height * scaleY));

        BubbleCanvas.Invalidate();
    }

    // ── Progress persistence ──────────────────────────────────────────────────

    private async Task PersistProgressAsync(CancellationToken ct = default)
    {
        if (_runId < 0) return;

        var confirmed = _bubbles.Count(b => b.IsFullyConfirmed);
        try
        {
            await _db.UpdateRunProgressAsync(
                _runId, SerializeBubbles(_bubbles), confirmed, _bubbles.Count, ct);
        }
        catch
        {
            // Non-critical
        }
    }

    // ── Bubble state serialization ────────────────────────────────────────────

    private static string SerializeBubbles(List<BubbleViewModel> bubbles)
    {
        var list = bubbles.Select(b => new BubblePersist(
            b.Shift.Employee,
            b.Shift.Date,
            b.Shift.TimeRange,
            b.DisplayTime,
            (int)b.State.TimeState,
            (int)b.State.PositionState,
            b.Shift.EstimatedBounds?.X,
            b.Shift.EstimatedBounds?.Y,
            b.Shift.EstimatedBounds?.Width,
            b.Shift.EstimatedBounds?.Height)).ToList();

        return JsonSerializer.Serialize(list);
    }

    private static List<BubbleViewModel> DeserializeBubbles(string json)
    {
        List<BubblePersist>? list = null;
        try { list = JsonSerializer.Deserialize<List<BubblePersist>>(json, _json); }
        catch { /* malformed JSON — treat as empty */ }

        return (list ?? []).Select(p =>
        {
            BoundingBox? bounds = p.BoundsX is int bx
                ? new BoundingBox
                  {
                      X      = bx,
                      Y      = p.BoundsY      ?? 0,
                      Width  = p.BoundsWidth  ?? 1,
                      Height = p.BoundsHeight ?? 1,
                  }
                : null;

            var shift = new ShiftData
            {
                Employee        = p.Employee,
                Date            = p.Date,
                TimeRange       = p.DisplayTime,
                EstimatedBounds = bounds,
            };

            var vm = new BubbleViewModel(shift, (PositionState)p.PositionState);

            if ((TimeState)p.TimeState == TimeState.Confirmed)
                vm.State.ConfirmTime();

            return vm;
        }).ToList();
    }

    // ── Process Schedule (submit) ─────────────────────────────────────────────

    private async void OnProcessScheduleClicked(object? sender, EventArgs e)
    {
        var unconfirmed = _bubbles.Count(b => !b.IsFullyConfirmed);
        if (_bubbles.Count == 0)
        {
            ValidationLabel.Text = "No shifts loaded yet.";
            ValidationLabel.IsVisible = true;
            return;
        }

        if (unconfirmed > 0)
        {
            ValidationLabel.Text = $"Please confirm all shifts before processing ({unconfirmed} remaining).";
            ValidationLabel.IsVisible = true;
            return;
        }

        ValidationLabel.IsVisible = false;
        ProcessScheduleBtn.IsEnabled = false;

        var corrected = _bubbles.Select(b => new ShiftData
        {
            Employee        = b.Shift.Employee,
            Date            = b.Shift.Date,
            TimeRange       = b.DisplayTime,
            EstimatedBounds = b.Shift.EstimatedBounds,
        }).ToList();

        if (_runId >= 0)
        {
            var weekStart    = GetMondayOfWeek(corrected);
            var totalMinutes = CalculateTotalMinutes(corrected);
            try
            {
                await _db.MarkRunCompleteAsync(
                    _runId, SerializeBubbles(_bubbles), totalMinutes, weekStart);
            }
            catch { /* non-critical */ }
        }

        var ok = await _viewModel.ConfirmAsync(corrected);
        if (!ok)
        {
            await DisplayAlertAsync("Network Error",
                "Corrections saved locally and will retry automatically.", "OK");
        }

        await Navigation.PushAsync(new ScheduleSummaryPage(_db, corrected, _runId));
    }

    private bool CanEditOverlay()
        => DragOverlay.IsVisible && _panLocked && _zoomLocked;

    private bool IsLocationReviewOrEditStep(BubbleViewModel? bubble)
        => bubble is not null
        && _positionOptIn
        && bubble.State.TimeState == TimeState.Confirmed
        && bubble.State.PositionState is PositionState.Pending or PositionState.Editing;

    private void UpdateOverlayEditVisualState()
    {
        var editable     = CanEditOverlay();
        var posReviewStep = IsLocationReviewOrEditStep(_selected);
        var posEditing = _selected?.State.PositionState == PositionState.Editing;
        var posConfirmed = _selected?.State.PositionState == PositionState.Confirmed;

        // Drag overlay should only capture touches when both locks are engaged.
        DragOverlay.InputTransparent = !editable;

        // Show border in location step, edit mode, or when position already confirmed.
        SelectedRectBorder.IsVisible = editable || posReviewStep || posConfirmed || posEditing;
        SelectedRectBorder.Stroke    = editable
            ? new SolidColorBrush(Color.FromArgb("#00BFFF"))
            : posEditing
                ? new SolidColorBrush(Color.FromArgb("#00BFFF"))
            : posReviewStep
                ? new SolidColorBrush(Colors.Gold)
            : new SolidColorBrush(Colors.Green);

        // Show handles/fill only in edit mode.
        CornerTL.IsVisible = editable;
        CornerTR.IsVisible = editable;
        CornerBL.IsVisible = editable;
        CornerBR.IsVisible = editable;
        SelectedRectFill.Color          = editable ? new Color(0f, 0.749f, 1f, 0.25f) : Colors.Transparent;
        RectMoveHandle.InputTransparent = !editable;
    }

    private void UpdateHeaderProgress()
    {
        var total = _bubbles.Count;
        var step = _currentBubbleIndex >= 0 && total > 0 ? _currentBubbleIndex + 1 : 0;
        HeaderProgressLabel.Text = $"{step}/{total}";
        var hasItems = total > 0;
        PrevBubbleBtn.IsEnabled = hasItems;
        NextBubbleBtn.IsEnabled = hasItems;
    }

    private void OnPrevBubbleClicked(object? sender, EventArgs e)
    {
        if (_bubbles.Count == 0) return;
        if (_currentBubbleIndex < 0) _currentBubbleIndex = 0;
        var prev = (_currentBubbleIndex - 1 + _bubbles.Count) % _bubbles.Count;
        OnBubbleTapped(_bubbles[prev]);
    }

    private void OnNextBubbleClicked(object? sender, EventArgs e)
    {
        if (_bubbles.Count == 0) return;
        if (_currentBubbleIndex < 0) _currentBubbleIndex = 0;
        var next = (_currentBubbleIndex + 1) % _bubbles.Count;
        OnBubbleTapped(_bubbles[next]);
    }

    // ── Share prompt actions ──────────────────────────────────────────────────

    private async void OnOpenMessagingAppClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_watchedPackage)) return;
#if ANDROID
        try
        {
            var intent = global::Android.App.Application.Context.PackageManager?
                .GetLaunchIntentForPackage(_watchedPackage);
            if (intent is not null)
            {
                intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
                global::Android.App.Application.Context.StartActivity(intent);
            }
        }
        catch
        {
            await DisplayAlertAsync("Error", "Could not open the app.", "OK");
        }
#else
        await Task.CompletedTask;
#endif
    }

    private async void OnImportClicked(object? sender, EventArgs e)
    {
        var photos = await MediaPicker.Default.PickPhotosAsync();
        var photo  = photos.FirstOrDefault();
        if (photo is null) return;

        await using var stream = await photo.OpenReadAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        _imageBytes = ms.ToArray();
        await RunProcessingFlowAsync();
    }

    private async void OnRetryClicked(object? sender, EventArgs e)
        => await RunProcessingFlowAsync();

    private async void OnViewHistoryClicked(object? sender, EventArgs e)
    {
        await Navigation.PopAsync();
        if (Shell.Current is { } shell)
            await shell.GoToAsync("//HistoryPage");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        SetPanel(Panel.Error);
    }

    private static string GetMondayOfWeek(List<ShiftData> shifts)
    {
        foreach (var s in shifts)
        {
            if (DateTime.TryParse(s.Date, out var dt))
            {
                var diff = ((int)dt.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
                return dt.AddDays(-diff).ToString("yyyy-MM-dd");
            }
        }
        return DateTime.Today.ToString("yyyy-MM-dd");
    }

    private static int CalculateTotalMinutes(List<ShiftData> shifts)
    {
        var total = 0;
        foreach (var s in shifts)
        {
            if (!DateTime.TryParse(s.Date, out _)) continue;
            var parts = s.TimeRange.Split('-');
            if (parts.Length != 2) continue;
            if (!TimeSpan.TryParse(parts[0].Trim(), out var start)) continue;
            if (!TimeSpan.TryParse(parts[1].Trim(), out var end))   continue;
            if (end < start) end = end.Add(TimeSpan.FromHours(24));
            total += (int)(end - start).TotalMinutes;
        }
        return total;
    }

    private static Brush BackgroundFromResource(string key)
    {
        if (Application.Current?.Resources[key] is Brush brush)
            return brush;
        if (Application.Current?.Resources[key] is Color color)
            return new SolidColorBrush(color);
        return new SolidColorBrush(Colors.Transparent);
    }

    // ── GraphicsView drawable ─────────────────────────────────────────────────

    private class BubbleDrawable(ConfirmationPage page) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            foreach (var b in page._bubbles)
            {
                if (b.ScreenBounds.Width <= 0) continue;

                canvas.StrokeColor = b.BorderColor;
                canvas.StrokeSize  = b == page._selected ? 3f : 2f;
                canvas.DrawRectangle(b.ScreenBounds);

                canvas.FontColor = b.BorderColor;
                canvas.FillColor = new Color(0f, 0f, 0f, 0.65f);
                var labelY = MathF.Max(0f, b.ScreenBounds.Y - 18f);
                canvas.FillRectangle(
                    b.ScreenBounds.X,
                    labelY,
                    MathF.Max(90f, b.ScreenBounds.Width),
                    18f);

                canvas.FontColor = Colors.White;
                canvas.FontSize  = b == page._selected ? 12f : 11f;
                canvas.DrawString(
                    b.DisplayTime,
                    b.ScreenBounds.X + 2,
                    labelY,
                    b.ScreenBounds.Width,
                    18,
                    HorizontalAlignment.Left,
                    VerticalAlignment.Center);
            }
        }
    }
}
