using System.Text.Json;
using CalendarParse.Core.Services;
using CalendarParse.Data;
using CalendarParse.Models;
using CalendarParse.Services;
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
    private BoundingBox? _lastConfirmedImageBounds;
    private int?          _confirmedRowY;      // Y locked to first position confirmation; only X advances between bubbles
    private double?       _confirmedScrollY;   // viewport scrollY at first confirmation — reused so Y never drifts
    private double?       _pendingMarkerCX;    // image-px X under marker center captured on drag-start; held for entire gesture
    private double?       _lockedCX;           // same value, persists after gesture for diagnostic display
    private bool          _zoomGestureActive;  // true while user is actively dragging the zoom slider
    private readonly List<(int Index, int ImageX)> _confirmedPositions = [];
    private int   _currentBubbleIndex = -1;
    private double _intendedScrollX;  // last scroll X we commanded — used for zoom captures to avoid stale ScrollX reads
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

        // Keep rotated ZoomSlider track length equal to the wrapper height as layout changes.
        ZoomSliderWrapper.SizeChanged += (_, _) =>
        {
            if (ZoomSliderWrapper.Height > 0)
                ZoomSlider.WidthRequest = ZoomSliderWrapper.Height;
        };
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
        ZoomHideBtn.Text            = "🙈";
        LockPanBtn.Background  = BackgroundFromResource("Gray300");
        ZoomHideBtn.Background = BackgroundFromResource("Gray300");
        ZoomSlider.Value            = 1;
        ZoomSlider.IsVisible        = true;
        ZoomSlider.IsEnabled        = true;
        ZoomSliderWrapper.IsVisible = true;
        _lastConfirmedImageBounds   = null;
        _confirmedRowY              = null;
        _confirmedScrollY           = null;
        _pendingMarkerCX            = null;
        _lockedCX                   = null;
        _zoomGestureActive          = false;
        _confirmedPositions.Clear();
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

        // Apply zoom X-lock scroll AFTER layout has committed the new content size.
        // _pendingMarkerCX is an image-pixel column captured pre-zoom; we recompute the
        // absolute scrollX here using the NEW transform so there is no compounding error.
        System.Diagnostics.Debug.WriteLine($"[SIZE-CHANGED] rendW={_renderedImageSize.Width:F1} rendH={_renderedImageSize.Height:F1} zoom={_zoomScale:F3} pendingCX={_pendingMarkerCX?.ToString("F1") ?? "null"} viewW={ImageScrollView.Width:F1}");
        if (_pendingMarkerCX is { } imgCX)
        {
            // During an active drag gesture keep _pendingMarkerCX alive so every
            // intermediate layout commit restores the same column. Only null-reset
            // once the gesture has ended so the slot is available for the next gesture.
            if (!_zoomGestureActive)
                _pendingMarkerCX = null;
            // Capture viewW now (constant during zoom). Re-read the transform INSIDE the dispatch
            // lambda so we use the fully-settled layout values, not an intermediate SizeChanged pass.
            // Compute scrollX synchronously while _renderedImageSize is current (just updated above).
            // Do NOT defer GetImageTransform into Dispatcher.Dispatch — _renderedImageSize may be
            // updated again by the next zoom step before the dispatch runs.
            var (sx, _, ox, _) = GetImageTransform();
            System.Diagnostics.Debug.WriteLine($"[SIZE-APPLY]   imgCX={imgCX:F1} sx={sx:F4} ox={ox:F1} viewW={ImageScrollView.Width:F1} => scrollX={Math.Max(0, imgCX * sx + ox - ImageScrollView.Width / 2.0):F1}");
            if (sx > 0 && ImageScrollView.Width > 0)
            {
                var newScrollX = ZoomScrollMath.ComputeScrollXForMarkerColumn(imgCX, sx, ox, ImageScrollView.Width);
                var capturedScrollY = ImageScrollView.ScrollY;
                _intendedScrollX = newScrollX;  // update intention BEFORE dispatch so next capture sees it
                Dispatcher.Dispatch(() => _ = ImageScrollView.ScrollToAsync(newScrollX, capturedScrollY, false));
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[SIZE-SKIP]    sx={sx} or viewW={ImageScrollView.Width} is zero — cannot scroll");
            }
        }
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

        var (scale, offsetX, offsetY) = ZoomScrollMath.GetImageTransform(
            _naturalImageWidth, _naturalImageHeight,
            _renderedImageSize.Width, _renderedImageSize.Height);

        return (scale, scale, offsetX, offsetY);
    }

    // ── Tap hit test (bubble selection) ──────────────────────────────────────

    private void OnImageContainerTapped(object? sender, TappedEventArgs e)
    {
        if (_bubbles.Count == 0) return;

        var withBounds = _bubbles
            .Where(b => b.ScreenBounds.Width > 0 &&
                        OverlayVisibilityLogic.ShouldDrawCanvasBorder(_positionOptIn, b.State, b == _selected))
            .ToList();
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

        // Reset card to normal state whenever a new bubble is selected.
        BubbleEmployeeLabel.IsVisible = true;
        BubbleDateLabel.IsVisible     = true;
        PositionEditActions.IsVisible = false;
        NormalActions.IsVisible       = true;

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

        // Drag rect removed — user scrolls the image under the fixed PositionTargetRect.
        if (_positionOptIn)
        {
            DragOverlay.IsVisible       = false;
            LockPanBtn.IsVisible        = false;
            ZoomControlsPanel.IsVisible = false;
            if (bubble.State.PositionState == PositionState.Editing)
                EnterLocationEditMode();
            else
                UpdateOverlayEditVisualState();
        }
        else
        {
            PositionTargetRect.IsVisible = false;
            PositionDebugLabel.IsVisible  = false;
            DragOverlay.IsVisible        = false;
            LockPanBtn.IsVisible         = false;
            ZoomControlsPanel.IsVisible  = false;
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
            ConfirmPositionFromMarker(_selected);
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

        // Image stays freely pannable/zoomable — user aligns it under the fixed PositionTargetRect.
        _panLocked  = false;
        _zoomLocked = false;
        _lockedCX   = null;  // fresh lock for this bubble session
        LockPanBtn.Text       = "🔓 Lock Pan";
        LockPanBtn.Background = BackgroundFromResource("Gray300");
        ZoomSlider.IsVisible  = true;
        ZoomSlider.IsEnabled  = true;

        // Collapse detail card to give more room to the image.
        BubbleEmployeeLabel.IsVisible = false;
        BubbleDateLabel.IsVisible     = false;
        BubbleTimeLabel.IsVisible     = false;
        BubbleTimeEntry.IsVisible     = false;
        EditActions.IsVisible         = false;
        NormalActions.IsVisible       = false;
        PositionEditActions.IsVisible = true;
        ZoomControlsPanel.IsVisible   = true;
        ZoomSliderWrapper.IsVisible   = true;  // restore if user hid it in the previous bubble

        if (_selected is not null)
        {
            SeedBoundsFromPreviousIfNeeded(_selected);
            PositionTargetLabel.Text      = _selected.DisplayTime;
            PositionTargetLabel.IsVisible = true;
        }
        ShowPositionTargetRect();
        _ = ScrollToBubbleAsync(_selected);
        _ = PersistProgressAsync();
        UpdateOverlayEditVisualState();
    }

    private void ExitPositionEditMode()
    {
        // Restore card to normal time-confirm state.
        BubbleEmployeeLabel.IsVisible = true;
        BubbleDateLabel.IsVisible     = true;
        BubbleTimeLabel.IsVisible     = true;
        NormalActions.IsVisible       = true;
        PositionEditActions.IsVisible  = false;
        PositionTargetRect.IsVisible  = false;
        PositionTargetLabel.IsVisible  = false;
        PositionDebugLabel.IsVisible   = false;
        ZoomControlsPanel.IsVisible    = false;
        _lockedCX = null;
    }

    private void OnPositionSaveClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        ConfirmPositionFromMarker(_selected);
        _selected.State.ConfirmPosition();
        _ = PersistProgressAsync();
        BubbleCanvas.Invalidate();
        UpdateConfirmProgress();
        ExitPositionEditMode();
        AdvanceFocus();
    }

    private void OnPositionCancelClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        _selected.State.CancelEditPosition();
        _ = PersistProgressAsync();
        ExitPositionEditMode();
        UpdateOverlayEditVisualState();
        BubbleCanvas.Invalidate();
    }

    private async Task ScrollToBubbleAsync(BubbleViewModel? bubble)
    {
        if (bubble is null) return;

        // Auto-zoom to 2× if we’re at 1:1 — makes individual rows readable without user pinching.
        if (_zoomScale < 1.5f)
        {
            ApplyZoomScale(2f);
            await Task.Delay(120); // allow layout pass to resize ImageContainer
        }

        // Re-read ScreenBounds AFTER the potential zoom recalculation — they are now in current
        // zoomed container space, so no extra _zoomScale multiply is needed.
        var r = bubble.ScreenBounds;
        if (r.Width <= 0) return;

        var cx      = r.X + r.Width  / 2.0;
        var cy      = r.Y + r.Height / 2.0;
        var scrollX = Math.Max(0, cx - ImageScrollView.Width  / 2.0);
        // Reuse the most-recently confirmed scroll-Y so the row axis follows the latest anchor.
        // If the schedule is split across two rows the user will confirm on the new row and
        // _confirmedScrollY will update, then subsequent bubbles track to the new row.
        var scrollY = _confirmedScrollY ?? Math.Max(0, cy - ImageScrollView.Height / 2.0);

        _intendedScrollX = scrollX;  // track programmatic scroll for zoom captures
        await ImageScrollView.ScrollToAsync(scrollX, scrollY, animated: true);
    }

    private void AdvanceFocus()
    {
        _lastRectWidth               = _rectBounds.Width;
        _lastRectHeight              = _rectBounds.Height;
        DragOverlay.IsVisible        = false;
        PositionTargetRect.IsVisible = false;
        PositionDebugLabel.IsVisible  = false;
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
        _intendedScrollX = e.ScrollX;  // user scrolled: update intention
        _lastScrollY = e.ScrollY;
        UpdatePositionDebugLabel();
    }

    private void UpdatePositionDebugLabel()
    {
        if (!PositionTargetRect.IsVisible) return;
        var (scaleX, scaleY, offsetX, offsetY) = GetImageTransform();
        if (scaleX <= 0) return;
        var markerW  = PositionTargetRect.Width;
        var markerH  = PositionTargetRect.Height;
        var viewX    = (ImageScrollView.Width  - markerW) / 2.0;
        var viewY    = (ImageScrollView.Height - markerH) / 2.0;
        var containerCX = viewX + markerW / 2.0 + ImageScrollView.ScrollX;
        var containerCY = viewY + markerH / 2.0 + ImageScrollView.ScrollY;
        var imgCX = (int)Math.Round((containerCX - offsetX) / scaleX);
        var imgCY = (int)Math.Round((containerCY - offsetY) / scaleY);
        var pendingStr = _pendingMarkerCX.HasValue ? ((int)_pendingMarkerCX.Value).ToString() : "none";
        var lockedStr  = _lockedCX.HasValue ? $"{(int)_lockedCX.Value}(err={(int)(imgCX - _lockedCX.Value):+#;-#;0})" : "none";
        PositionDebugLabel.Text      = $"cx={imgCX} cy={imgCY}\nzoom={_zoomScale:F2} scrollX={ImageScrollView.ScrollX:F0}\nlocked={lockedStr} p={pendingStr}";
        PositionDebugLabel.IsVisible = true;
#if ANDROID
        // Make the native TextView text-selectable so Maestro's copyTextFrom can read it.
        if (PositionDebugLabel.Handler?.PlatformView is Android.Widget.TextView tv)
        {
            tv.SetTextIsSelectable(true);
            tv.LongClickable = true;
        }
#endif
    }

    // ── Lock pan toggle ───────────────────────────────────────────────────────

    private void OnLockPanClicked(object? sender, EventArgs e)
    {
        // Pan and zoom always lock together — one button controls both.
        // Scroll blocking is achieved by setting DragOverlay.InputTransparent = false
        // (DragOverlay sits above the ScrollView and absorbs all touch events).
        // We never change ScrollOrientation because that triggers a MAUI layout pass
        // which resizes the image to fit the viewport.
        _panLocked  = !_panLocked;
        _zoomLocked = _panLocked;

        LockPanBtn.Text      = _panLocked ? "🔒 Unlock Pan" : "🔓 Lock Pan";
        LockPanBtn.Background = _panLocked
            ? BackgroundFromResource("Primary")
            : BackgroundFromResource("Gray300");
        ZoomSlider.IsEnabled = !_zoomLocked;

        UpdateOverlayEditVisualState();
    }

    private void OnZoomHideClicked(object? sender, EventArgs e)
    {
        var hiding                  = ZoomSliderWrapper.IsVisible; // true = currently visible → we're hiding
        ZoomSliderWrapper.IsVisible = !hiding;
        ZoomSlider.IsEnabled        = !hiding;
        ZoomHideBtn.Text            = hiding ? "👁" : "🙈";
    }

    private void OnZoomSliderValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_zoomLocked || _updatingZoomSlider)
            return;

        ApplyZoomScale((float)e.NewValue);
    }

    private void OnZoomDragStarted(object? sender, EventArgs e)
    {
        if (_zoomLocked) return;
        _zoomGestureActive = true;
        // Capture the locked column NOW — layout is stable and _intendedScrollX is correct.
        // This is the authoritative capture for the entire drag gesture.
        if (PositionTargetRect.IsVisible && ImageScrollView.Width > 0)
        {
            var (sx, _, ox, _) = GetImageTransform();
            if (sx > 0 && _renderedImageSize.Width > 0)
            {
                _pendingMarkerCX = ZoomScrollMath.CaptureMarkerColumn(_intendedScrollX, ImageScrollView.Width, sx, ox);
                _lockedCX        = _pendingMarkerCX;
                System.Diagnostics.Debug.WriteLine($"[DRAG-START]   scrollX={_intendedScrollX:F1} sx={sx:F4} ox={ox:F1} => markerCX={_pendingMarkerCX:F1}");
            }
        }
    }

    private void OnZoomDragCompleted(object? sender, EventArgs e)
    {
        _zoomGestureActive = false;
        // Allow OnImageSizeChanged to null-reset on the next (final) layout commit.
        // No extra action needed — the last _pendingMarkerCX value will be applied normally.
        System.Diagnostics.Debug.WriteLine($"[DRAG-DONE]    markerCX={_pendingMarkerCX:F1}");
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
        var oldZoom = _zoomScale;
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

        // When the position marker is visible, lock the image-pixel column under the marker center.
        // At this point _renderedImageSize still reflects the OLD zoom (layout hasn't committed yet),
        // so GetImageTransform() gives the correct pre-zoom transform.
        // Use ??= so rapid slider events don't overwrite the original capture mid-gesture.
        // The actual scroll target is computed in OnImageSizeChanged after layout commits.
        if (PositionTargetRect.IsVisible && ImageScrollView.Width > 0)
        {
            var (sx, _, ox, _) = GetImageTransform();
            if (sx > 0)
            {
                // Use _intendedScrollX (last commanded position) not ImageScrollView.ScrollX
                // which lags behind because async ScrollToAsync hasn't settled yet.
                var captureScrollX = _intendedScrollX;
                // Only overwrite the lock if we are NOT mid-gesture.
                // The authoritative capture happens in OnZoomDragStarted; subsequent
                // ValueChanged events during the drag should NOT re-capture.
                if (!_zoomGestureActive)
                {
                    _pendingMarkerCX ??= ZoomScrollMath.CaptureMarkerColumn(captureScrollX, ImageScrollView.Width, sx, ox);
                    _lockedCX ??= _pendingMarkerCX;
                }
                System.Diagnostics.Debug.WriteLine($"[ZOOM-CAPTURE] zoom={_zoomScale:F3} intendedScrollX={captureScrollX:F1} liveScrollX={ImageScrollView.ScrollX:F1} viewW={ImageScrollView.Width:F1} sx={sx:F4} ox={ox:F1} => markerCX={_pendingMarkerCX:F1}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ZOOM-NOGOOD]  sx={sx} — GetImageTransform returned zero scale");
            }
        }
        else
        {
            // If vertical whitespace appears above content after zoom, snap image to top.
            if (ImageContainer.Height > 0 && ImageScrollView.Height > 0)
            {
                var maxScrollY = Math.Max(0d, ImageContainer.Height - ImageScrollView.Height);
                if (ImageScrollView.ScrollY > maxScrollY + 0.5 || maxScrollY <= 0.5)
                {
                    Dispatcher.Dispatch(() => _ = ImageScrollView.ScrollToAsync(ImageScrollView.ScrollX, 0, false));
                }
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

    /// <summary>
    /// Computes BoundingBox from the fixed PositionTargetRect's current viewport center plus the
    /// active scroll offset, then writes it back to the bubble's Shift and ScreenBounds.
    /// Call this instead of SyncBoundsToImageCoords when the drag-rect UX is not in use.
    /// </summary>
    private void ConfirmPositionFromMarker(BubbleViewModel bubble)
    {
        var (scaleX, scaleY, offsetX, offsetY) = GetImageTransform();
        if (scaleX <= 0) return;

        // PositionTargetRect sits inside a centered VerticalStackLayout (Spacing=0) with
        // PositionTargetLabel above it and PositionDebugLabel below it.
        // The naive center-of-viewport formula ignores the label above the rect, causing a
        // systematic downward offset equal to half the label's height. Correct for it explicitly.
        var markerW     = PositionTargetRect.Width;
        var markerH     = PositionTargetRect.Height;
        var labelOffset = PositionTargetLabel.IsVisible ? PositionTargetLabel.Height : 0.0;
        var debugOffset = PositionDebugLabel.IsVisible  ? PositionDebugLabel.Height  : 0.0;
        var totalStackH = labelOffset + markerH + debugOffset;   // Spacing=0
        var viewX       = (ImageScrollView.Width  - markerW)    / 2.0;
        var viewY       = (ImageScrollView.Height - totalStackH) / 2.0 + labelOffset;

        // Shift to image-container space (viewport origin + scroll offset).
        var containerX = viewX + ImageScrollView.ScrollX;
        var containerY = viewY + ImageScrollView.ScrollY;

        // Convert to image-pixel space via inverse of GetImageTransform.
        var imgX = (int)Math.Round((containerX - offsetX) / scaleX);
        var imgY = (int)Math.Round((containerY - offsetY) / scaleY);
        var imgW = (int)Math.Round(markerW / scaleX);
        var imgH = (int)Math.Round(markerH / scaleY);

        bubble.Shift.EstimatedBounds = new BoundingBox
        {
            X      = Math.Max(0, imgX),
            Y      = Math.Max(0, imgY),
            Width  = Math.Max(1, imgW),
            Height = Math.Max(1, imgH),
        };
        _lastConfirmedImageBounds = bubble.Shift.EstimatedBounds;
        // Always update to the most-recently confirmed anchor so that a schedule split
        // across two rows will correctly track to the new row after the user confirms it.
        _confirmedRowY    = bubble.Shift.EstimatedBounds?.Y;
        _confirmedScrollY = ImageScrollView.ScrollY;

        // Track this confirmation for X interpolation.
        var bubbleListIndex = _bubbles.IndexOf(bubble);
        _confirmedPositions.RemoveAll(cp => cp.Index == bubbleListIndex);
        _confirmedPositions.Add((bubbleListIndex, imgX));
        _confirmedPositions.Sort((a, b) => a.Index.CompareTo(b.Index));

        // Normalize all other bubble sizes to the confirmed W/H so canvas borders are uniform.
        foreach (var b in _bubbles.Where(b => b != bubble && b.Shift.EstimatedBounds is { Width: > 0 }))
            b.Shift.EstimatedBounds = b.Shift.EstimatedBounds! with { Width = imgW, Height = imgH };
        RecalculateBubbleScreenPositions();

        // Sync screen bounds so the canvas confirmation border renders at the right position.
        bubble.ScreenBounds = new RectF(
            (float)(bubble.Shift.EstimatedBounds.X * scaleX + offsetX),
            (float)(bubble.Shift.EstimatedBounds.Y * scaleY + offsetY),
            (float)(bubble.Shift.EstimatedBounds.Width  * scaleX),
            (float)(bubble.Shift.EstimatedBounds.Height * scaleY));

        BubbleCanvas.Invalidate();
    }

    /// <summary>
    /// Applies calibrated row-Y, correct dimensions, and (when 2+ positions are confirmed)
    /// an interpolated X to the bubble before entering position-edit mode.
    /// Works for both server-provided bounds (<em>overrides Y/W/H/X</em>) and
    /// bubbles with no estimated bounds at all (seeds everything from the last confirmed).
    /// </summary>
    private void SeedBoundsFromPreviousIfNeeded(BubbleViewModel bubble)
    {
        var currentIndex  = _bubbles.IndexOf(bubble);
        var interpolatedX = InterpolateImageX(currentIndex);
        var (scaleX, scaleY, offsetX, offsetY) = GetImageTransform();

        // Case A: bubble already has bounds (server-provided or previously seeded).
        // Still override Y and dimensions with the locked calibrated values if we have them.
        if (bubble.Shift.EstimatedBounds is { Width: > 0 } existing)
        {
            if (!_confirmedRowY.HasValue && _lastConfirmedImageBounds is not { Width: > 0 })
                return; // nothing calibrated yet — leave server bounds untouched

            var seedY = _confirmedRowY ?? existing.Y;
            var seedW = _lastConfirmedImageBounds?.Width  ?? existing.Width;
            var seedH = _lastConfirmedImageBounds?.Height ?? existing.Height;
            // With 0 or 1 confirmed anchors InterpolateImageX returns null.
            // Never use the server's existing.X in that case — it may be to the LEFT of the
            // confirmed position. Always advance right from the most-recently confirmed anchor.
            var xStep = _naturalImageWidth > 0 ? _naturalImageWidth / 8 : seedW;
            var seedX = interpolatedX
                        ?? (_lastConfirmedImageBounds is { Width: > 0 } lc ? lc.X + xStep : existing.X);

            bubble.Shift.EstimatedBounds = existing with { X = seedX, Y = seedY, Width = seedW, Height = seedH };
            if (scaleX > 0)
                bubble.ScreenBounds = new RectF(
                    (float)(seedX * scaleX + offsetX),
                    (float)(seedY * scaleY + offsetY),
                    (float)(seedW * scaleX),
                    (float)(seedH * scaleY));
            return;
        }

        // Case B: no existing bounds — seed everything from the last confirmed position.
        if (_lastConfirmedImageBounds is not { Width: > 0 } prev) return;

        var xOffset = _naturalImageWidth > 0 ? _naturalImageWidth / 8 : prev.Width;
        var seedXB  = interpolatedX ?? (prev.X + xOffset);
        var seedYB  = _confirmedRowY ?? prev.Y;
        bubble.Shift.EstimatedBounds = new BoundingBox
        {
            X      = seedXB,
            Y      = seedYB,
            Width  = prev.Width,
            Height = prev.Height,
        };

        if (scaleX > 0)
            bubble.ScreenBounds = new RectF(
                (float)(bubble.Shift.EstimatedBounds.X * scaleX + offsetX),
                (float)(bubble.Shift.EstimatedBounds.Y * scaleY + offsetY),
                (float)(bubble.Shift.EstimatedBounds.Width  * scaleX),
                (float)(bubble.Shift.EstimatedBounds.Height * scaleY));
    }

    /// <summary>
    /// Linearly interpolates (or extrapolates) an image-space X coordinate for
    /// <paramref name="bubbleIndex"/> based on the two outermost confirmed positions.
    /// Returns null when fewer than two positions have been confirmed.
    /// </summary>
    private int? InterpolateImageX(int bubbleIndex)
    {
        if (_confirmedPositions.Count < 2) return null;
        var first = _confirmedPositions[0];
        var last  = _confirmedPositions[^1];
        if (first.Index == last.Index) return null;
        var t = (double)(bubbleIndex - first.Index) / (last.Index - first.Index);
        return (int)Math.Round(first.ImageX + (last.ImageX - first.ImageX) * t);
    }

    /// <summary>Sizes and shows the fixed PositionTargetRect at ~1/3 of the visible image width.</summary>
    private void ShowPositionTargetRect()
    {
        double markerW, markerH;
        if (_lastConfirmedImageBounds is { Width: > 0 } prev)
        {
            // GetImageTransform() already incorporates _zoomScale (via _renderedImageSize),
            // so multiply by scaleX only — no extra _zoomScale factor.
            var (scaleX, scaleY, _, _) = GetImageTransform();
            markerW = Math.Max(60d,  prev.Width  * scaleX);
            markerH = Math.Max(20d,  prev.Height * scaleY);
        }
        else
        {
            markerW = Math.Max(100d, ImageScrollView.Width / 3.0);
            markerH = Math.Max(40d,  markerW / 4.5);   // typical schedule-row aspect ratio
        }
        PositionTargetRect.WidthRequest  = markerW;
        PositionTargetRect.HeightRequest = markerH;
        PositionTargetRect.IsVisible     = true;
        UpdatePositionDebugLabel();
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
        // Drag rect removed — PositionTargetRect is the fixed centered marker the user aligns the image under.
        DragOverlay.IsVisible        = false;
        DragOverlay.InputTransparent = true;
        PositionTargetRect.IsVisible = IsLocationReviewOrEditStep(_selected);
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

                var isSelected = b == page._selected;
                var drawBorder = OverlayVisibilityLogic.ShouldDrawCanvasBorder(page._positionOptIn, b.State, isSelected);
                // Label is drawn on canvas only when the border is also drawn.
                // During position-editing the time label is handled by the fixed PositionTargetLabel XAML element.
                var drawLabel = drawBorder;

                if (drawBorder)
                {
                    canvas.StrokeColor = b.BorderColor;
                    canvas.StrokeSize  = isSelected ? 3f : 2f;
                    canvas.DrawRectangle(b.ScreenBounds);
                }

                if (drawLabel)
                {
                    canvas.FillColor = new Color(0f, 0f, 0f, 0.65f);
                    var labelY = MathF.Max(0f, b.ScreenBounds.Y - 18f);
                    canvas.FillRectangle(
                        b.ScreenBounds.X,
                        labelY,
                        MathF.Max(90f, b.ScreenBounds.Width),
                        18f);

                    canvas.FontColor = Colors.White;
                    canvas.FontSize  = isSelected ? 12f : 11f;
                    canvas.DrawString(
                        b.DisplayTime,
                        b.ScreenBounds.X + 2,
                        labelY,
                        b.ScreenBounds.Width,
                        18,
                        HorizontalAlignment.Left,
                        VerticalAlignment.Center);
                }

                // Below confirmed-position bubbles, show the image-pixel centre so
                // you can visually verify the x-lock landed on the right column.
                if (drawBorder && b.State.PositionState == PositionState.Confirmed)
                {
                    var (sx, sy, ox, oy) = page.GetImageTransform();
                    if (sx > 0 && sy > 0)
                    {
                        var scrCX    = b.ScreenBounds.X + b.ScreenBounds.Width  / 2f;
                        var scrCY    = b.ScreenBounds.Y + b.ScreenBounds.Height / 2f;
                        var imgX     = (int)Math.Round((scrCX - ox) / sx);
                        var imgY     = (int)Math.Round((scrCY - oy) / sy);
                        var debugText = $"cx={imgX} cy={imgY}";
                        var labelW   = MathF.Max(90f, b.ScreenBounds.Width);
                        canvas.FillColor = new Color(0f, 0f, 0f, 0.65f);
                        canvas.FillRectangle(b.ScreenBounds.X, b.ScreenBounds.Bottom, labelW, 16f);
                        canvas.FontColor = Colors.Yellow;
                        canvas.FontSize  = 10f;
                        canvas.DrawString(
                            debugText,
                            b.ScreenBounds.X + 2,
                            b.ScreenBounds.Bottom,
                            labelW, 16f,
                            HorizontalAlignment.Left,
                            VerticalAlignment.Center);
                    }
                }
            }
        }
    }
}
