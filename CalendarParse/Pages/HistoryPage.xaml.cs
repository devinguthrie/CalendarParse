using CalendarParse.Data;
using CalendarParse.Models;
using CalendarParse.Services;

namespace CalendarParse.Pages;

public partial class HistoryPage : ContentPage
{
    private readonly ScheduleHistoryDb  _db;
    private readonly IServiceProvider   _services;
    private readonly IJobPollingService _pollingService;
    private readonly ApiClient          _api;

    public HistoryPage(
        ScheduleHistoryDb db,
        IServiceProvider services,
        IJobPollingService pollingService,
        ApiClient api)
    {
        InitializeComponent();
        _db             = db;
        _services       = services;
        _pollingService = pollingService;
        _api            = api;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadRunsAsync();
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        await LoadRunsAsync();
        RefreshContainer.IsRefreshing = false;
    }

    private async Task LoadRunsAsync()
    {
        try
        {
            var runs = await _db.GetRecentRunsAsync();

            ProcessingList.ItemsSource = runs
                .Where(r => r.Status == RunStatus.Processing || r.Status == RunStatus.Error)
                .ToList();

            CorrectionList.ItemsSource = runs
                .Where(r => r.Status == RunStatus.CorrectionInProgress)
                .ToList();

            CompletedList.ItemsSource = runs
                .Where(r => r.Status == RunStatus.Completed || r.IsComplete)
                .ToList();
        }
        catch
        {
            // Non-critical — leave lists empty on DB error
        }
    }

    private async void OnCorrectionTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not int runId) return;

        var page = _services.GetRequiredService<ConfirmationPage>();
        await Navigation.PushAsync(page);
        await page.StartResumeAsync(runId);
    }

    private async void OnRetryProcessingTapped(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not int runId) return;

        // Find the run to get the image for re-submission
        var run = await _db.GetRunForResumeAsync(runId);
        if (run is null) return;

        if (string.IsNullOrEmpty(run.ImagePath) || !File.Exists(run.ImagePath))
        {
            await DisplayAlertAsync("Cannot Retry",
                "The original image is no longer available. Please import or capture a new photo.",
                "OK");
            return;
        }

        var imageBytes = await File.ReadAllBytesAsync(run.ImagePath);
        var prefs      = await _db.GetPreferencesAsync();

        var jobId = await _api.SubmitAsync(imageBytes, prefs.EmployeeName ?? string.Empty);
        if (jobId is null)
        {
            await DisplayAlertAsync("Error", "Could not reach server. Check Settings.", "OK");
            return;
        }

        // Persist both status and fresh remote job ID in one tracked update.
        await _db.UpdateRunStatusAsync(
            runId,
            RunStatus.Processing,
            errorMessage: null,
            remoteJobId: jobId);

        _pollingService.StartPolling(runId, jobId);
        await LoadRunsAsync();
    }
}
