using CalendarParse.Services;

namespace CalendarParse.Pages;

public partial class MonitorSetupPage : ContentPage
{
    private readonly ISmsMonitorService? _monitor;

    public MonitorSetupPage(ISmsMonitorService? monitor = null)
    {
        InitializeComponent();
        _monitor = monitor;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Populate from live service config
        if (_monitor is not null)
        {
            PackageEntry.Text = _monitor.WatchedPackage  ?? string.Empty;
            SenderEntry.Text  = _monitor.WatchedSenderName ?? string.Empty;
        }

        RefreshPermissionBanner();
        UpdateSaveButton();
    }

    private void RefreshPermissionBanner()
    {
        var hasAccess = _monitor?.IsListening ?? false;
        PermissionBanner.IsVisible = !hasAccess;
    }

    private void OnFieldChanged(object? sender, TextChangedEventArgs e) => UpdateSaveButton();

    private void UpdateSaveButton()
    {
        // Require at least a package name
        SaveBtn.IsEnabled = !string.IsNullOrWhiteSpace(PackageEntry.Text);
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_monitor is null)
        {
            ShowStatus("Notification monitor not available on this platform.", Colors.Orange);
            return;
        }

        _monitor.WatchedPackage    = PackageEntry.Text.Trim();
        _monitor.WatchedSenderName = string.IsNullOrWhiteSpace(SenderEntry.Text)
            ? null
            : SenderEntry.Text.Trim();

        ShowStatus("Saved. Listening for notifications.", Colors.Green);
    }

    private async void OnOpenPermissionSettingsClicked(object? sender, EventArgs e)
    {
#if ANDROID
        try
        {
            var intent = new global::Android.Content.Intent(
                "android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS");
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            global::Android.App.Application.Context.StartActivity(intent);
        }
        catch
        {
            await DisplayAlertAsync("Error", "Could not open notification settings. Please open them manually.", "OK");
        }
#else
        await DisplayAlertAsync("Not Supported", "Notification monitoring is only available on Android.", "OK");
#endif
    }

    private void ShowStatus(string message, Color color)
    {
        StatusLabel.Text      = message;
        StatusLabel.TextColor = color;
        StatusLabel.IsVisible = true;
    }
}
