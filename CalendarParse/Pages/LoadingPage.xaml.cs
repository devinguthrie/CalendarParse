using CalendarParse.Services;

namespace CalendarParse.Pages;

public partial class LoadingPage : ContentPage
{
    private readonly IAuthService _authService;
    private readonly IServiceProvider _serviceProvider;

    public LoadingPage(IAuthService authService, IServiceProvider serviceProvider)
    {
        InitializeComponent();
        _authService     = authService;
        _serviceProvider = serviceProvider;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // DB init and auth restore run in parallel for fastest possible startup
        var dbTask   = Task.Run(() => MauiProgram.InitializeDatabaseAsync(_serviceProvider));
        var authTask = _authService.RestoreSessionAsync();

        try
        {
            await Task.WhenAll(dbTask, authTask);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LoadingPage] Init failed: {ex.Message}");
        }

        // Resume any in-flight jobs after DB is ready
        try { await MauiProgram.ResumeInFlightJobsAsync(_serviceProvider); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[LoadingPage] ResumeInFlight failed: {ex.Message}"); }

        // Retry pending confirmations — low priority, fire after navigation
        _ = RetryPendingConfirmationsAsync(_serviceProvider);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            Application.Current!.MainPage = _authService.IsAuthenticated
                ? new AppShell()
                : _serviceProvider.GetRequiredService<LoginPage>();
        });
    }

    private static async Task RetryPendingConfirmationsAsync(IServiceProvider services)
    {
        try
        {
            var apiClient = services.GetRequiredService<ApiClient>();
            await apiClient.RetryPendingConfirmationsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LoadingPage] Retry pending confirmations failed: {ex.Message}");
        }
    }
}
