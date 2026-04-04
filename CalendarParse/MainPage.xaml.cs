using System.Text.Json;
using CalendarParse.Data;
using CalendarParse.Models;
using CalendarParse.Pages;
using CalendarParse.Services;

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
#endif
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            JobEvents.JobFinished += OnJobFinished;
            await RefreshActiveJobCardAsync();
        }

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
                ActiveJobCard.IsVisible = processing.Count > 0;
            }
            catch
            {
                ActiveJobCard.IsVisible = false;
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
            _pendingPhoto        = photo;
            ProcessBtn.IsEnabled = true;
            FileNameLabel.Text   = photo.FileName;
        }

        private async void OnProcessClicked(object? sender, EventArgs e)
        {
            if (_pendingPhoto is null) return;
            await SubmitPhotoAsync(_pendingPhoto);
        }

        private async Task SubmitPhotoAsync(FileResult photo)
        {
            await using var stream = await photo.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var imageBytes = ms.ToArray();

            // Show the popup before any network work
            await DisplayAlertAsync(
                "Processing your schedule",
                "This takes about 2 minutes. You'll get a notification when it's ready.",
                "OK");

            ProcessBtn.IsEnabled  = false;
            StatusLabel.Text      = "Submitting…";
            StatusLabel.IsVisible = true;
            Spinner.IsRunning     = true;
            Spinner.IsVisible     = true;

            try
            {
                var prefs        = await _db.GetPreferencesAsync();
                var employeeName = prefs.EmployeeName ?? SearchEntry.Text?.Trim() ?? string.Empty;

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

                ActiveJobCard.IsVisible = true;
                StatusLabel.Text        = "Submitted!";

                _pendingPhoto        = null;
                ProcessBtn.IsEnabled = false;
                FileNameLabel.Text   = "No file selected";
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("Error", $"Submission failed: {ex.Message}", "OK");
            }
            finally
            {
                Spinner.IsRunning = false;
                Spinner.IsVisible = false;
            }
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
