using CalendarParse.Core.Models;
using CalendarParse.Core.Services;
using CalendarParse.Data;
using CalendarParse.Models;
using CalendarParse.Services;
using CalendarParse.ViewModels;
using Microsoft.Maui.Graphics;

namespace CalendarParse.Pages;

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

    private bool  _dateConfirmedForThisRun   = false;
    private bool  _crossTwelveStartIsPm      = false;  // default: cross-12 → start AM, end PM
    private bool  _crossTwelvePreferenceSet  = false;
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
    private double?       _pendingMarkerCY;    // image-px Y under marker center captured on drag-start; held for entire gesture
    private double?       _lockedCX;           // same value, persists after gesture for diagnostic display
    private double?       _lockedCY;           // same value, persists after gesture for diagnostic display
    private bool          _zoomGestureActive;  // true while user is actively dragging the zoom slider
    private readonly List<(int Index, int ImageX)> _confirmedPositions = [];
    private int   _currentBubbleIndex = -1;
    private double _intendedScrollX;  // last scroll X we commanded — used for zoom captures to avoid stale ScrollX reads
    private double _lastScrollY;
    private double _imagePad;         // ExtraPad when zoom is active; 0 at rest (set by ApplyZoomScale/reset)

    private const float  MinZoomScale = 1f;
    private const double ExtraPad     = 100; // blank canvas space around image to allow panning past edge
    private const float MaxZoomScale = 10f;

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

        _dateConfirmedForThisRun = false;
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
        LoadingPanel.IsVisible      = panel == Panel.Loading;
        SharePromptPanel.IsVisible  = panel == Panel.SharePrompt;
        SubmittedPanel.IsVisible    = panel == Panel.Submitted;
        OverlayPanel.IsVisible      = panel == Panel.Overlay;
        ErrorPanel.IsVisible        = panel == Panel.Error;
        BottomBar.IsVisible         = panel == Panel.Overlay;
        ProgressHeader.IsVisible    = panel == Panel.Overlay;
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

            if (string.IsNullOrWhiteSpace(_employeeName))
            {
                ShowError("Enter your name in Settings before processing so the parser can filter to one employee.");
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
        LockPanBtn.Text             = "Lock Pan";
        ZoomHideBtn.Text               = MaterialGlyphs.Visibility;
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
        ImageContainer.WidthRequest  = -1;
        ImageContainer.HeightRequest = -1;
        ScrollContent.Padding        = new Thickness(0);
        ScrollContent.WidthRequest   = -1;
        ScrollContent.HeightRequest  = -1;
        _imagePad                    = 0;
        _currentBubbleIndex         = -1;
        ValidationLabel.IsVisible    = false;
        EditPositionBtn.IsVisible    = false;
        UpdateConfirmProgress();

        ScheduleImage.SizeChanged += OnImageSizeChanged;

        // Show date confirmation in the bottom card so the user can see the schedule.
        if (!_dateConfirmedForThisRun && _bubbles.Count > 0)
        {
            var firstDate = DateTime.TryParse(_bubbles[0].Shift.Date, out var d) ? d : DateTime.Today;
            DateConfirmPicker.Date       = firstDate;
            DateConfirmDateLabel.Text    = firstDate.ToString("dddd, MMMM d, yyyy");
            DatePickerActions.IsVisible  = false;
            DateConfirmPicker.IsVisible  = false;
            DateConfirmOkBtn.IsVisible   = true;
            DateConfirmEditBtn.IsVisible = true;
            DateConfirmView.IsVisible    = true;
            // Hide normal bubble content so only the date confirm UI shows in the card.
            BubbleDateLabel.IsVisible    = false;
            BubbleTimeLabel.IsVisible    = false;
            NormalActions.IsVisible      = false;
            EditActions.IsVisible        = false;
            PositionEditActions.IsVisible = false;
            ZoomControlsPanel.IsVisible  = true;
            ZoomSliderWrapper.IsVisible  = true;
            BubbleDetailPanel.IsVisible  = true;
            SetPanel(Panel.Overlay);
            return;
        }

        SetPanel(Panel.Overlay);
        var first = _bubbles.FirstOrDefault(b => !b.IsFullyConfirmed);
        if (first is not null)
            OnBubbleTapped(first);
    }

    // ── Date confirmation handlers ──────────────────────────────────────────────

    private void OnDateConfirmOkClicked(object? sender, EventArgs e)
    {
        _dateConfirmedForThisRun  = true;
        DateConfirmView.IsVisible = false;
        SetPanel(Panel.Overlay);
        var first = _bubbles.FirstOrDefault(b => !b.IsFullyConfirmed);
        if (first is not null)
            OnBubbleTapped(first);
    }

    private void OnDateConfirmEditClicked(object? sender, EventArgs e)
    {
        DateConfirmPicker.IsVisible  = true;
        DatePickerActions.IsVisible  = true;
        DateConfirmOkBtn.IsVisible   = false;
        DateConfirmEditBtn.IsVisible = false;
    }

    private void OnDateConfirmSaveClicked(object? sender, EventArgs e)
    {
        if (_bubbles.Count == 0) return;
        if (DateTime.TryParse(_bubbles[0].Shift.Date, out var originalFirst))
        {
            var delta = ((DateConfirmPicker.Date ?? DateTime.Today).Date - originalFirst.Date).Days;
            if (delta != 0)
            {
                foreach (var bubble in _bubbles)
                {
                    if (DateTime.TryParse(bubble.Shift.Date, out var old))
                        bubble.Shift.Date = old.AddDays(delta).ToString("yyyy-MM-dd");
                }
                if (_runId >= 0)
                    _ = PersistProgressAsync();
            }
        }
        DateConfirmDateLabel.Text    = (DateConfirmPicker.Date ?? DateTime.Today).ToString("dddd, MMMM d, yyyy");
        DatePickerActions.IsVisible  = false;
        DateConfirmPicker.IsVisible  = false;
        DateConfirmOkBtn.IsVisible   = true;
        DateConfirmEditBtn.IsVisible = true;
    }

    private void OnDateConfirmCancelClicked(object? sender, EventArgs e)
    {
        DatePickerActions.IsVisible  = false;
        DateConfirmPicker.IsVisible  = false;
        DateConfirmOkBtn.IsVisible   = true;
        DateConfirmEditBtn.IsVisible = true;
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

        // Re-draw the position target rect now that _renderedImageSize is valid.
        // ShowPositionTargetRect guards against pre-layout calls (_renderedImageSize.Width <= 0),
        // so this is the call that actually places the rect with correct viewport dimensions.
        UpdateOverlayEditVisualState();

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
                var newScrollX = ZoomScrollMath.ComputeScrollXForMarkerColumn(imgCX, sx, ox, ImageScrollView.Width) + _imagePad;
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
        BubbleDateLabel.IsVisible     = true;
        DateConfirmView.IsVisible     = false;
        PositionEditActions.IsVisible = false;
        NormalActions.IsVisible       = true;

        BubbleDateLabel.Text     = FormatShiftDate(bubble.Shift.Date);

        // Time display: read-only label, editing entry hidden by default
        var timeText = bubble.DisplayTime;
        BubbleTimeLabel.Text      = string.IsNullOrWhiteSpace(timeText) ? "No schedule found" : timeText;
        BubbleTimeLabel.IsVisible  = bubble.State.TimeState != TimeState.Editing;
        BubbleTimeEntry.IsVisible  = false;  // always hidden; revealed by "Other" in EditActions
        TimePickerPanel.IsVisible  = bubble.State.TimeState == TimeState.Editing;
        if (bubble.State.TimeState == TimeState.Editing)
            SeedTimePickers(bubble.State.DisplayTime);
        BubbleTimeEntry.Text       = timeText;

        var needsPositionConfirm = IsLocationReviewOrEditStep(bubble);
        var isFullyConfirmed = bubble.IsFullyConfirmed;
        ThumbsUpBtn.IsVisible    = bubble.State.TimeState != TimeState.Confirmed || needsPositionConfirm;
        ThumbsDownBtn.IsVisible  = bubble.State.TimeState != TimeState.Editing;
        ThumbsDownBtn.Text       = needsPositionConfirm
            ? "Edit Position"
            : (isFullyConfirmed ? "Edit Time" : "Edit");
        EditActions.IsVisible    = bubble.State.TimeState == TimeState.Editing;
        EditPositionBtn.IsVisible = _positionOptIn && isFullyConfirmed;

        ThumbsUpBtn.Text = bubble.State.TimeState != TimeState.Confirmed
            ? "Confirm Time"
            : (needsPositionConfirm ? "Confirm Location" : "Confirm");

        BubbleDetailPanel.IsVisible = true;
        BubbleCanvas.Invalidate();

        // Single overlay state: target rect + zoom slider always visible when position opt-in is on.
        if (_positionOptIn)
        {
            DragOverlay.IsVisible = false;
            LockPanBtn.IsVisible  = false;
            if (bubble.State.PositionState == PositionState.Editing)
            {
                EnterLocationEditMode();
            }
            else
            {
                ZoomControlsPanel.IsVisible = true;
                ZoomSliderWrapper.IsVisible = true;
                ZoomSlider.IsVisible        = true;
                ZoomSlider.IsEnabled        = true;
                UpdateOverlayEditVisualState();
            }
        }
        else
        {
            PositionTargetRect.IsVisible  = false;
            PositionTargetLabel.IsVisible = false;
            PositionDebugLabel.IsVisible  = false;
            DragOverlay.IsVisible         = false;
            LockPanBtn.IsVisible          = false;
            ZoomControlsPanel.IsVisible   = true;
            ZoomSliderWrapper.IsVisible   = true;
            ZoomSlider.IsVisible          = true;
            ZoomSlider.IsEnabled          = true;
        }

        UpdateHeaderProgress();

        // Auto-scroll and zoom to center the selected bubble — same behavior as the location
        // edit step, now applied during the time confirmation step too (Bug 5).
        // EnterLocationEditMode handles this internally, so skip it to avoid a redundant scroll.
        // When position opt-in is active use anchor-based interpolation (not raw server ScreenBounds).
        if (bubble.State.PositionState != PositionState.Editing)
            _ = ScrollToBubbleAsync(bubble, useInterpolation: _positionOptIn);
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
        SeedTimePickers(_selected.State.DisplayTime);
        BubbleTimeLabel.IsVisible  = false;
        BubbleTimeEntry.IsVisible  = false;  // revealed by "Other" button
        TimePickerPanel.IsVisible  = true;
        EditActions.IsVisible      = true;
        ThumbsDownBtn.IsVisible    = false;
        NormalActions.IsVisible    = false;  // hide to prevent accidental EditPositionBtn tap
        BubbleCanvas.Invalidate();
    }

    private void OnBlankTimeClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        _selected.State.SaveTime(string.Empty);
        BubbleTimeLabel.Text      = _selected.DisplayTime;
        BubbleTimeLabel.IsVisible = true;
        BubbleTimeEntry.IsVisible = false;
        TimePickerPanel.IsVisible = false;
        EditActions.IsVisible     = false;
        ThumbsDownBtn.IsVisible   = true;
        NormalActions.IsVisible   = true;
        _ = PersistProgressAsync();
        if (IsLocationReviewOrEditStep(_selected))
        {
            OnBubbleTapped(_selected);
            return;
        }
        AdvanceFocus();
    }

    private void OnXTimeClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        _selected.State.SaveTime("x");
        BubbleTimeLabel.Text      = _selected.DisplayTime;
        BubbleTimeLabel.IsVisible = true;
        BubbleTimeEntry.IsVisible = false;
        TimePickerPanel.IsVisible = false;
        EditActions.IsVisible     = false;
        ThumbsDownBtn.IsVisible   = true;
        NormalActions.IsVisible   = true;
        _ = PersistProgressAsync();
        if (IsLocationReviewOrEditStep(_selected))
        {
            OnBubbleTapped(_selected);
            return;
        }
        AdvanceFocus();
    }

    private void OnOtherTimeClicked(object? sender, EventArgs e)
    {
        TimePickerPanel.IsVisible = false;
        BubbleTimeEntry.IsVisible = true;
        BubbleTimeEntry.Focus();
    }

    private void OnEditSaveClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        var origDisplay = _selected.State.DisplayTime;
        // If the text entry is visible the user typed a free-form value; otherwise use the pickers.
        string timeValue = BubbleTimeEntry.IsVisible
            ? (BubbleTimeEntry.Text ?? string.Empty)
            : TimeFormatHelper.FormatTimeRange(StartTimePicker.Time ?? TimeSpan.FromHours(9),
                                              EndTimePicker.Time   ?? TimeSpan.FromHours(17));
        if (!BubbleTimeEntry.IsVisible)
            TryRecordCrossTwelvePreference(
                origDisplay,
                StartTimePicker.Time ?? TimeSpan.FromHours(9),
                EndTimePicker.Time   ?? TimeSpan.FromHours(17));
        _selected.State.SaveTime(timeValue);
        BubbleTimeLabel.Text      = _selected.DisplayTime;
        BubbleTimeLabel.IsVisible = true;
        BubbleTimeEntry.IsVisible = false;
        TimePickerPanel.IsVisible = false;
        EditActions.IsVisible     = false;
        ThumbsDownBtn.IsVisible   = true;
        NormalActions.IsVisible   = true;
        _ = PersistProgressAsync();
        if (IsLocationReviewOrEditStep(_selected))
        {
            OnBubbleTapped(_selected);
            return;
        }
        AdvanceFocus();
    }

    private void OnEditDismissClicked(object? sender, EventArgs e)
    {
        if (_selected is null) return;
        _selected.State.DismissEdit();
        BubbleTimeLabel.IsVisible = true;
        BubbleTimeEntry.IsVisible = false;
        TimePickerPanel.IsVisible = false;
        EditActions.IsVisible     = false;
        ThumbsDownBtn.IsVisible   = true;
        NormalActions.IsVisible   = true;
        BubbleCanvas.Invalidate();
        UpdateConfirmProgress();
    }

    /// <summary>Parses "H:mm-H:mm" (or "H:mm") into the two TimePicker controls.
    /// Applies the cross-12 AM/PM preference (start AM/end PM by default; inverted if user has corrected).
    /// </summary>
    private void SeedTimePickers(string displayTime)
    {
        var (start, end) = TimeFormatHelper.SeedTimePickers(displayTime, _crossTwelveStartIsPm);
        StartTimePicker.Time = start;
        EndTimePicker.Time   = end;
    }

    /// <summary>
    /// Records the user's AM/PM preference for cross-12 shift times after their first edit.
    /// Only fires once per schedule run (preference is stable across the whole schedule).
    /// </summary>
    private void TryRecordCrossTwelvePreference(string originalDisplayTime, TimeSpan savedStart, TimeSpan savedEnd)
    {
        if (_crossTwelvePreferenceSet) return;
        // Detect cross-12: both tokens were in the 0–11h range and start > end.
        var (rawStart, rawEnd) = TimeFormatHelper.SeedTimePickers(originalDisplayTime);
        bool wasCrossTwelve = rawStart.TotalHours < 12.0
                           && rawEnd.TotalHours   < 12.0
                           && rawStart.TotalHours > rawEnd.TotalHours;
        if (!wasCrossTwelve) return;
        _crossTwelveStartIsPm    = savedStart.TotalHours >= 12.0;
        _crossTwelvePreferenceSet = true;
    }

    private void OnEditPositionClicked(object? sender, EventArgs e)
    {        if (_selected is null) return;
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
        LockPanBtn.Text       = "Lock Pan";
        LockPanBtn.Background = BackgroundFromResource("Gray300");
        ZoomSlider.IsVisible  = true;
        ZoomSlider.IsEnabled  = true;

        // Collapse detail card to give more room to the image.
        BubbleDateLabel.IsVisible     = false;
        BubbleTimeLabel.IsVisible     = false;
        BubbleTimeEntry.IsVisible     = false;
        TimePickerPanel.IsVisible     = false;
        EditActions.IsVisible         = false;
        NormalActions.IsVisible       = false;
        PositionEditActions.IsVisible = true;
        ZoomControlsPanel.IsVisible   = true;
        ZoomSliderWrapper.IsVisible   = true;  // restore if user hid it in the previous bubble

        if (_selected is not null)
        {
            SeedBoundsFromPreviousIfNeeded(_selected);
            PositionTargetLabel.Text      = FormatShiftDate(_selected.Shift.Date) + "\n" + _selected.DisplayTime;
            PositionTargetLabel.IsVisible = true;
        }
        ShowPositionTargetRect();
        _ = PersistProgressAsync();
        UpdateOverlayEditVisualState();
    }

    private void ExitPositionEditMode()
    {
        // Restore the detail card to normal confirm state.
        BubbleDateLabel.IsVisible     = true;
        BubbleTimeLabel.IsVisible     = true;
        NormalActions.IsVisible       = true;
        PositionEditActions.IsVisible = false;
        PositionDebugLabel.IsVisible  = false;
        _lockedCX = null;
        // Keep rect + zoom visible — UpdateOverlayEditVisualState re-syncs to current step.
        UpdateOverlayEditVisualState();
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
        // Re-render the entire card from current state — TimeState may now be Pending,
        // so ExitPositionEditMode() alone would leave the UI in a wrong half-state.
        OnBubbleTapped(_selected);
    }

    private async Task ScrollToBubbleAsync(BubbleViewModel? bubble, bool useInterpolation = false)
    {
        if (bubble is null) return;

        // Auto-zoom to 2x if at 1:1 -- makes individual rows readable without user pinching.
        if (_zoomScale < 1.5f)
        {
            ApplyZoomScale(2f);
            await Task.Delay(120); // allow layout pass to resize ImageContainer
        }

        double scrollX, scrollY;

        // Already confirmed: scroll directly to the exact location the user positioned it,
        // using both the confirmed X and Y from ScreenBounds — no interpolation.
        if (bubble.State.PositionState == PositionState.Confirmed && bubble.ScreenBounds.Width > 0)
        {
            var r = bubble.ScreenBounds;
            scrollX = Math.Max(0, r.X + r.Width  / 2.0 + _imagePad - ImageScrollView.Width  / 2.0);
            scrollY = Math.Max(0, r.Y + r.Height / 2.0 + _imagePad - ImageScrollView.Height / 2.0);
            _intendedScrollX = scrollX;
            await ImageScrollView.ScrollToAsync(scrollX, scrollY, animated: true);
            return;
        }

        if (useInterpolation)
        {
            // Time-confirm step (position opt-in flow): derive X from calibrated anchor positions,
            // not raw server ScreenBounds. Mirrors SeedBoundsFromPreviousIfNeeded's X-logic exactly
            // but without mutating the bubble's bounds or ScreenBounds.
            var (scaleX, _, offsetX, _) = GetImageTransform();
            if (scaleX > 0)
            {
                var idx = _bubbles.IndexOf(bubble);
                int imgX, imgW;
                if (_lastConfirmedImageBounds is { Width: > 0 } anchor)
                {
                    // With 1 confirmed: step ahead from anchor; with 2+: interpolate between confirmed.
                    var xStep = _naturalImageWidth > 0 ? _naturalImageWidth / 8 : anchor.Width;
                    imgX = InterpolateImageX(idx) ?? (anchor.X + xStep);
                    imgW = anchor.Width;
                }
                else if (bubble.Shift.EstimatedBounds is { Width: > 0 } existing)
                {
                    // No confirmed anchor yet -- use server-predicted position for first bubble only.
                    imgX = existing.X;
                    imgW = existing.Width;
                }
                else
                {
                    return; // no position info at all -- leave viewport unchanged
                }
                var screenCX = imgX * scaleX + offsetX + imgW * scaleX / 2.0;
                scrollX = Math.Max(0, screenCX + _imagePad - ImageScrollView.Width / 2.0);
            }
            else
            {
                // Transform not ready -- fall back to screen bounds.
                var r = bubble.ScreenBounds;
                if (r.Width <= 0) return;
                scrollX = Math.Max(0, r.X + r.Width / 2.0 + _imagePad - ImageScrollView.Width / 2.0);
            }
            scrollY = _confirmedScrollY ?? Math.Max(0, bubble.ScreenBounds.Y + bubble.ScreenBounds.Height / 2.0 + _imagePad - ImageScrollView.Height / 2.0);
        }
        else
        {
            // Location-edit step: SeedBoundsFromPreviousIfNeeded has already updated ScreenBounds
            // with the calibrated seed position -- just scroll there directly.
            var r = bubble.ScreenBounds;
            if (r.Width <= 0) return;
            scrollX = Math.Max(0, r.X + r.Width  / 2.0 + _imagePad - ImageScrollView.Width  / 2.0);
            scrollY = _confirmedScrollY ?? Math.Max(0, r.Y + r.Height / 2.0 + _imagePad - ImageScrollView.Height / 2.0);
        }

        _intendedScrollX = scrollX;  // track programmatic scroll for zoom captures
        await ImageScrollView.ScrollToAsync(scrollX, scrollY, animated: true);
    }

    private void AdvanceFocus()
    {
        _lastRectWidth               = _rectBounds.Width;
        _lastRectHeight              = _rectBounds.Height;
        DragOverlay.IsVisible        = false;
        PositionTargetRect.IsVisible  = false;
        PositionTargetLabel.IsVisible = false;   // prevent stale label (e.g. "mmm") persisting when no next bubble
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
        var centerX = (float)(ImageScrollView.ScrollX - _imagePad) + (float)(ImageScrollView.Width  / 2d);
        var centerY = (float)(ImageScrollView.ScrollY - _imagePad) + (float)(ImageScrollView.Height / 2d);

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
        // Add _imagePad because ImageContainer is inset by _imagePad within ScrollContent.
        var sx = (float)ImageScrollView.ScrollX;
        var sy = (float)ImageScrollView.ScrollY;
        var r  = _rectBounds;
        var x  = r.X + (float)_imagePad - sx;
        var y  = r.Y + (float)_imagePad - sy;
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

    // ── Position diagnostics ──────────────────────────────────────────────────

    /// <summary>
    /// Appends one NDJSON line to position-diag.ndjson in AppDataDirectory.
    /// Used to diagnose coordinate-space bugs (Bug 1: rect vs canvas alignment,
    /// Bug 2: first-rect sizing).  Fire-and-forget; never throws.
    /// Pull the file with: adb shell run-as &lt;package&gt; cat files/position-diag.ndjson
    /// </summary>
    private void AppendPositionDiag(string step, object payload)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var path = Path.Combine(FileSystem.Current.AppDataDirectory, "position-diag.ndjson");
                var line = System.Text.Json.JsonSerializer.Serialize(
                    new { step, ts = DateTime.Now.ToString("HH:mm:ss.fff"), data = payload });
                File.AppendAllText(path, line + "\n");
            }
            catch { /* never crash the UI */ }
        });
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

        LockPanBtn.Text      = _panLocked ? "Unlock Pan" : "Lock Pan";
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
        ZoomHideBtn.Text = hiding ? MaterialGlyphs.VisibilityOff : MaterialGlyphs.Visibility;
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
        // Capture the locked column/row NOW — layout is stable and _intendedScrollX is correct.
        // This is the authoritative capture for the entire drag gesture.
        if (PositionTargetRect.IsVisible && ImageScrollView.Width > 0 && ImageScrollView.Height > 0)
        {
            var (sx, sy, ox, oy) = GetImageTransform();
            if (sx > 0 && sy > 0 && _renderedImageSize.Width > 0 && _renderedImageSize.Height > 0)
            {
                _pendingMarkerCX = ZoomScrollMath.CaptureMarkerColumn(_intendedScrollX - _imagePad, ImageScrollView.Width, sx, ox);
                _lockedCX        = _pendingMarkerCX;
                _pendingMarkerCY = ZoomScrollMath.CaptureMarkerColumn(ImageScrollView.ScrollY - _imagePad, ImageScrollView.Height, sy, oy);
                _lockedCY        = _pendingMarkerCY;
                System.Diagnostics.Debug.WriteLine($"[DRAG-START]   scrollX={_intendedScrollX:F1} scrollY={ImageScrollView.ScrollY:F1} sx={sx:F4} sy={sy:F4} ox={ox:F1} oy={oy:F1} => markerCX={_pendingMarkerCX:F1} markerCY={_pendingMarkerCY:F1}");
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
        _imagePad                    = ExtraPad;
        ScrollContent.Padding        = new Thickness(ExtraPad);
        ScrollContent.WidthRequest   = _baseImageSize.Width  * _zoomScale + ExtraPad * 2;
        ScrollContent.HeightRequest  = _baseImageSize.Height * _zoomScale + ExtraPad * 2;

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
                var captureScrollX = _intendedScrollX - _imagePad;
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
        // MarkerPositionMath.ComputeImageCoords corrects for the label-above asymmetry.
        // Pass _imagePad so the math can adjust from ScrollContent coords to ImageContainer coords.
        var (imgX, imgY, imgW, imgH) = MarkerPositionMath.ComputeImageCoords(
            scrollX:   ImageScrollView.ScrollX,
            scrollY:   ImageScrollView.ScrollY,
            viewportW: ImageScrollView.Width,
            viewportH: ImageScrollView.Height,
            markerW:   PositionTargetRect.Width,
            markerH:   PositionTargetRect.Height,
            labelH:    PositionTargetLabel.IsVisible ? PositionTargetLabel.Height : 0.0,
            debugH:    PositionDebugLabel.IsVisible  ? PositionDebugLabel.Height  : 0.0,
            scaleX:    scaleX,
            scaleY:    scaleY,
            offsetX:   offsetX,
            offsetY:   offsetY,
            imagePad:  _imagePad);

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

        // Diagnostic: screenBounds.X/Y should match expectedContainer within ±1 px.
        // If they diverge, the confirmed canvas border will not land on the target rect.
        AppendPositionDiag("ConfirmPosition", new
        {
            scrollX        = ImageScrollView.ScrollX,
            scrollY        = ImageScrollView.ScrollY,
            viewportW      = ImageScrollView.Width,
            viewportH      = ImageScrollView.Height,
            labelH         = PositionTargetLabel.IsVisible ? PositionTargetLabel.Height : 0.0,
            debugH         = PositionDebugLabel.IsVisible  ? PositionDebugLabel.Height  : 0.0,
            markerW        = PositionTargetRect.Width,
            markerH        = PositionTargetRect.Height,
            scaleX, scaleY, offsetX, offsetY,
            imgX, imgY, imgW, imgH,
            screenBoundsX  = bubble.ScreenBounds.X,
            screenBoundsY  = bubble.ScreenBounds.Y,
            screenBoundsW  = bubble.ScreenBounds.Width,
            screenBoundsH  = bubble.ScreenBounds.Height,
            // Container-space top-left of the rect at the moment the user tapped confirm.
            // screenBoundsX/Y must equal these within ±1 px (rounding only).
            // NOTE: scrollX/Y are in ScrollContent space; ImageContainer is inset by _imagePad,
            // so subtract _imagePad to get the rect position in ImageContainer/canvas space.
            imagePad           = _imagePad,
            expectedContainerX = (ImageScrollView.ScrollX - _imagePad) + (ImageScrollView.Width  - PositionTargetRect.Width)  / 2.0,
            expectedContainerY = (ImageScrollView.ScrollY - _imagePad) + (ImageScrollView.Height - PositionTargetLabel.Height - PositionTargetRect.Height - PositionDebugLabel.Height) / 2.0 + PositionTargetLabel.Height,
        });

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
        => PositionInterpolator.Interpolate(_confirmedPositions, bubbleIndex);

    /// <summary>Sizes and shows the fixed PositionTargetRect at ~1/3 of the visible image width.</summary>
    private void ShowPositionTargetRect()
    {
        // Pre-layout guard: _renderedImageSize is zero until the ScheduleImage SizeChanged event
        // fires.  Calling here with _renderedImageSize.Width == 0 makes GetImageTransform return
        // (1,1,0,0) and ImageScrollView.Width is still -1, so PositionTargetSizer falls to the
        // 100dp MinUncalibratedW floor (different from the post-layout 137dp).
        // OnImageSizeChanged → UpdateOverlayEditVisualState will call here again with real values.
        if (_renderedImageSize.Width <= 0) return;
        // GetImageTransform() already incorporates _zoomScale (via _renderedImageSize),
        // so multiply by scaleX only — no extra _zoomScale factor.
        var (scaleX, scaleY, _, _) = GetImageTransform();
        var (markerW, markerH) = PositionTargetSizer.Compute(
            _lastConfirmedImageBounds, scaleX, scaleY, ImageScrollView.Width);
        AppendPositionDiag("ShowTargetRect", new
        {
            hasPrevBounds = _lastConfirmedImageBounds is { Width: > 0 },
            prevBoundsW   = _lastConfirmedImageBounds?.Width,
            prevBoundsH   = _lastConfirmedImageBounds?.Height,
            scaleX, scaleY,
            viewportW     = ImageScrollView.Width,
            imagePad      = _imagePad,
            markerW, markerH,
        });
        PositionTargetRect.WidthRequest   = markerW;
        PositionTargetRect.HeightRequest  = markerH;
        PositionTargetRect.IsVisible      = true;
        PositionTargetLabel.WidthRequest  = markerW;
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
        var dtos = bubbles.Select(b => new BubblePersist(
            b.Shift.Employee,
            b.Shift.Date,
            b.Shift.TimeRange,
            ConfirmedShiftSanitizer.NormalizeTimeRange(b.DisplayTime, b.Shift.Employee),
            (int)b.State.TimeState,
            (int)b.State.PositionState,
            b.Shift.EstimatedBounds?.X,
            b.Shift.EstimatedBounds?.Y,
            b.Shift.EstimatedBounds?.Width,
            b.Shift.EstimatedBounds?.Height));

        return BubblePersistenceService.Serialize(dtos);
    }

    private static List<BubbleViewModel> DeserializeBubbles(string json)
    {
        var list = BubblePersistenceService.Deserialize(json);

        return list.Select(p =>
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
            TimeRange       = ConfirmedShiftSanitizer.NormalizeTimeRange(b.DisplayTime, b.Shift.Employee),
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

        var ok = await _viewModel.ConfirmAsync(corrected, _runId);
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
        DragOverlay.IsVisible        = false;
        DragOverlay.InputTransparent = true;

        if (_positionOptIn && _selected is not null
            && _selected.State.PositionState is PositionState.Pending or PositionState.Editing)
        {
            // PositionTargetRect is the sole visual indicator during time-confirm and location-edit.
            // Hidden for Confirmed/Skipped bubbles — the canvas border is enough once positioned.
            PositionTargetLabel.Text      = FormatShiftDate(_selected.Shift.Date) + "\n" + _selected.DisplayTime;
            PositionTargetLabel.IsVisible = true;
            ShowPositionTargetRect();
        }
        else
        {
            PositionTargetRect.IsVisible  = false;
            PositionTargetLabel.IsVisible = false;
        }
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

    private static string FormatShiftDate(string isoDate)
        => DateTime.TryParse(isoDate, out var d) ? d.ToString("ddd, MMM d") : isoDate;

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
                    var dateText = FormatShiftDate(b.Shift.Date);
                    var timeText = b.DisplayTime;
                    var lineH    = isSelected ? 14f : 12f;
                    var labelH   = lineH * 2;
                    var labelW   = b.ScreenBounds.Width;
                    var labelX   = b.ScreenBounds.X;
                    var labelY   = MathF.Max(0f, b.ScreenBounds.Y - labelH);

                    canvas.FillColor = new Color(0f, 0f, 0f, 0.65f);
                    canvas.FillRectangle(labelX, labelY, labelW, labelH);

                    canvas.FontColor = Colors.White;
                    canvas.FontSize  = isSelected ? 12f : 11f;
                    // Date line (top)
                    canvas.DrawString(dateText, labelX, labelY,       labelW, lineH,
                        HorizontalAlignment.Center, VerticalAlignment.Center);
                    // Time line (bottom)
                    canvas.DrawString(timeText, labelX, labelY + lineH, labelW, lineH,
                        HorizontalAlignment.Center, VerticalAlignment.Center);
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
