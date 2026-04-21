using System.Text.Json;
using CalendarParse.Data;
using CalendarParse.Models;
using CalendarParse.Pages;
using CalendarParse.Services;
using Microsoft.EntityFrameworkCore;
using Sentry;

namespace CalendarParse
{
    /// <summary>
    /// Manual import / fallback tab.
    /// Picks an image, submits it to the API server asynchronously,
    /// and shows an active-job card while the server processes it.
    /// </summary>
    public partial class MainPage : ContentPage
    {
        private readonly IServiceProvider   _services;
        private readonly ScheduleHistoryDb  _db;
        private readonly ApiClient          _api;
        private readonly IJobPollingService _pollingService;
        private FileResult?                 _pendingPhoto;
        // Raw bytes loaded from the notification-monitor flow (no FileResult available)
        private byte[]?                     _pendingImageBytes;

        public MainPage(
            IServiceProvider services,
            ScheduleHistoryDb db,
            ApiClient api,
            IJobPollingService pollingService)
        {
            InitializeComponent();
            _services       = services;
            _db             = db;
            _api            = api;
            _pollingService = pollingService;

#if DEBUG
            OverlayHarnessBtn.IsVisible = true;
            SentryTestBtn.IsVisible = true;
            ClearLocalDbBtn.IsVisible = true;
#endif
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            JobEvents.JobFinished += OnJobFinished;
            await RefreshActiveJobCardAsync();
            await AutoPopulateSearchNameAsync();
        }

        private async Task AutoPopulateSearchNameAsync()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(SearchEntry.Text)) return;
                var prefs = await _db.GetPreferencesAsync();
                if (!string.IsNullOrWhiteSpace(prefs.EmployeeName))
                    SearchEntry.Text = prefs.EmployeeName;
                UpdateProcessButtonState();
            }
            catch { /* non-critical */ }
        }

        private void OnSearchEntryTextChanged(object? sender, TextChangedEventArgs e)
            => UpdateProcessButtonState();

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            JobEvents.JobFinished -= OnJobFinished;
        }

        private void OnJobFinished()
        {
            // Called from background thread — marshal to UI thread
            MainThread.BeginInvokeOnMainThread(async () => await RefreshActiveJobCardAsync());
        }

        private async Task RefreshActiveJobCardAsync()
        {
            try
            {
                var processing = await _db.GetProcessingRunsAsync();
                var hasProcessing = processing.Count > 0;
                ActiveJobCard.IsVisible        = hasProcessing;
                ProcessingTitleLabel.IsVisible = hasProcessing;
            }
            catch
            {
                ActiveJobCard.IsVisible        = false;
                ProcessingTitleLabel.IsVisible = false;
            }
        }

        private async void OnCameraClicked(object? sender, EventArgs e)
        {
            if (!MediaPicker.Default.IsCaptureSupported)
            {
                await DisplayAlertAsync("Unavailable", "Camera capture is not supported on this device.", "OK");
                return;
            }
            var photo = await MediaPicker.Default.CapturePhotoAsync();
            if (photo is not null) SetPendingPhoto(photo);
        }

        private async void OnImportClicked(object? sender, EventArgs e)
        {
            var photos = await MediaPicker.Default.PickPhotosAsync();
            var photo  = photos.FirstOrDefault();
            if (photo is not null) SetPendingPhoto(photo);
        }

        private void SetPendingPhoto(FileResult photo)
        {
            _pendingPhoto      = photo;
            FileNameLabel.Text = photo.FileName;
            UpdateProcessButtonState();
        }

        private async void OnProcessClicked(object? sender, EventArgs e)
        {
            if (_pendingImageBytes is { } bytes)
            {
                await SubmitBytesAsync(bytes);
                return;
            }
            if (_pendingPhoto is null) return;
            await SubmitPhotoAsync(_pendingPhoto);
        }

        /// <summary>
        /// Called by MainActivity when the user taps the notification body (load + banner,
        /// user presses Process) or the Yes action button (auto-process).
        /// </summary>
        public async Task LoadMonitorImageAsync(byte[] bytes, bool autoProcess)
        {
            _pendingImageBytes            = bytes;
            _pendingPhoto                 = null;
            FileNameLabel.Text            = "Image from monitored conversation";
            MonitorBannerBorder.IsVisible = true;
            UpdateProcessButtonState();

            System.Diagnostics.Debug.WriteLine(
                $"[MainPage] LoadMonitorImageAsync autoProcess={autoProcess} bytes={bytes.Length:N0}");

            if (autoProcess)
                await SubmitBytesAsync(bytes);
        }

        private async Task SubmitPhotoAsync(FileResult photo)
        {
            await using var stream = await photo.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            await SubmitBytesAsync(ms.ToArray());
        }

        private async Task SubmitBytesAsync(byte[] imageBytes)
        {
            var employeeName = SearchEntry.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(employeeName))
            {
                await DisplayAlertAsync(
                    "Search Name Required",
                    "Enter the employee name in Search Name before processing so the parser only returns that person's shifts.",
                    "OK");
                UpdateProcessButtonState();
                return;
            }

            ProcessBtn.IsEnabled  = false;
            StatusLabel.Text      = "Submitting…";
            StatusLabel.IsVisible = true;
            Spinner.IsRunning     = true;
            Spinner.IsVisible     = true;

            try
            {
                // Save image locally so the overlay can display it later
                var imagePath = await SaveImageLocallyAsync(imageBytes);

                var jobId = await _api.SubmitAsync(imageBytes, employeeName);
                if (jobId is null)
                {
                    await DisplayAlertAsync("Error", "Could not reach server. Check Settings.", "OK");
                    return;
                }

                var runId = await _db.CreateProcessingRunAsync(jobId, imagePath);
                _pollingService.StartPolling(runId, jobId);

                ActiveJobCard.IsVisible        = true;
                ProcessingTitleLabel.IsVisible = true;
                StatusLabel.Text               = "Submitted! Processing takes ~2 min.";

                _pendingPhoto                 = null;
                _pendingImageBytes            = null;
                FileNameLabel.Text            = "No file selected";
                MonitorBannerBorder.IsVisible = false;
                UpdateProcessButtonState();
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("Error", $"Submission failed: {ex.Message}", "OK");
                UpdateProcessButtonState();
            }
            finally
            {
                Spinner.IsRunning = false;
                Spinner.IsVisible = false;
            }
        }

        private void UpdateProcessButtonState()
        {
            bool hasImage = _pendingPhoto is not null || _pendingImageBytes is not null;
            bool hasName = !string.IsNullOrWhiteSpace(SearchEntry.Text);
            ProcessBtn.IsEnabled = hasImage && hasName;
        }

        private static async Task<string?> SaveImageLocallyAsync(byte[] imageBytes)
        {
            try
            {
                var dir = Path.Combine(FileSystem.AppDataDirectory, "schedules");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{Guid.NewGuid():N}.jpg");
                await File.WriteAllBytesAsync(path, imageBytes);
                return path;
            }
            catch
            {
                return null;
            }
        }

        private async void OnActiveJobCardTapped(object? sender, TappedEventArgs e)
        {
            if (Shell.Current is { } shell)
                await shell.GoToAsync("//HistoryPage");
        }

        private void OnSentryTestClicked(object? sender, EventArgs e)
        {
            SentrySdk.CaptureMessage("Hello from CalendarParse MAUI!", SentryLevel.Info);
            DisplayAlert("Sentry", "Test event sent — check your Sentry dashboard.", "OK");
        }

        private async void OnClearLocalDbClicked(object? sender, EventArgs e)
        {
            bool confirmed = await DisplayAlertAsync(
                "Clear Local DB",
                "Delete all schedule runs and pending confirmations from the local database? Settings are kept.",
                "Clear", "Cancel");
            if (!confirmed) return;
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM ScheduleRuns; DELETE FROM PendingConfirmations;");
            await RefreshActiveJobCardAsync();
            await DisplayAlertAsync("Done", "Local DB cleared.", "OK");
        }

        private async void OnOpenOverlayHarnessClicked(object? sender, EventArgs e)
        {
            try
            {
                var runId = await SeedOverlayHarnessRunAsync();
                var page = _services.GetRequiredService<ConfirmationPage>();
                await Navigation.PushAsync(page);
                await page.StartResumeAsync(runId);
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("Harness Error", ex.Message, "OK");
            }
        }

        private async Task<int> SeedOverlayHarnessRunAsync()
        {
            var prefs = await _db.GetPreferencesAsync();
            prefs.PositionOptIn = true;
            await _db.SaveChangesWithRetryAsync();

            // Deterministic first-step state: 7 days, all pending time + pending position.
            var baseDate = new DateTime(2026, 4, 1);
            var shifts = Enumerable.Range(0, 7)
                .Select(i => new
                {
                    Employee = "Harness Employee",
                    Date = baseDate.AddDays(i).ToString("yyyy-MM-dd"),
                    OriginalTimeRange = "9:00-5:00",
                    DisplayTime = "9:00-5:00",
                    TimeState = 0,
                    PositionState = 0,
                    BoundsX = 160 + (i * 24),
                    BoundsY = 260 + (i * 8),
                    BoundsWidth = 220,
                    BoundsHeight = 80,
                })
                .ToArray();

            var shiftsJson = JsonSerializer.Serialize(shifts);
            var imagePath = await SaveHarnessImageFromAssetsAsync();

            var runId = await _db.CreateRunAsync(
                imagePath: imagePath,
                imageWidth: 1600,
                imageHeight: 1200);

            await _db.UpdateRunProgressAsync(
                runId,
                shiftsJson,
                confirmedCount: 0,
                totalCount: 7);

            await _db.UpdateRunStatusAsync(runId, RunStatus.CorrectionInProgress);
            return runId;
        }

        private static async Task<string?> SaveHarnessImageFromAssetsAsync()
        {
            try
            {
                await using var src = await FileSystem.OpenAppPackageFileAsync("harness-im1.jpg");
                var dir = Path.Combine(FileSystem.AppDataDirectory, "schedules");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "harness-im1.jpg");
                await using var dst = File.Create(path);
                await src.CopyToAsync(dst);
                return path;
            }
            catch
            {
                return null;
            }
        }
    }
}
