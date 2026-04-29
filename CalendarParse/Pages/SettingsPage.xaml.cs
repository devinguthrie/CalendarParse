using CalendarParse.Data;
using CalendarParse.Services;

namespace CalendarParse.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly ScheduleHistoryDb _db;
    private readonly IAuthService      _authService;
    private AppPreferences? _prefs;
    private bool _loading;

    public SettingsPage(ScheduleHistoryDb db, IAuthService authService)
    {
        InitializeComponent();
        _db          = db;
        _authService = authService;

        // TimePicker doesn't expose a TimeChanged event — wire via BindableProperty change
        QuietStartPicker.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(TimePicker.Time)) UpdateSaveButton(); };
        QuietEndPicker.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(TimePicker.Time)) UpdateSaveButton(); };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Refresh account state every time the page appears (e.g. after returning from login)
        AccountEmailLabel.Text = _authService.IsAuthenticated
            ? (_authService.UserEmail ?? _authService.UserName ?? "Signed in")
            : "Not signed in";
        SignOutBtn.IsVisible = _authService.IsAuthenticated;

        _loading = true;
        try
        {
            _prefs = await _db.GetPreferencesAsync();
            NameEntry.Text             = _prefs.EmployeeName;
            ServerUrlEntry.Text        = _prefs.ServerUrl;
            ServerKeyEntry.Text        = _prefs.ServerKey;
            PositionOptInSwitch.IsToggled = _prefs.PositionOptIn == true;
            QuietHoursSwitch.IsToggled = _prefs.QuietHoursEnabled;
            QuietStartPicker.Time      = _prefs.QuietHoursStart.ToTimeSpan();
            QuietEndPicker.Time        = _prefs.QuietHoursEnd.ToTimeSpan();
        }
        finally
        {
            _loading = false;
            UpdateSaveButton();
        }
    }

    private async void OnSignOutClicked(object? sender, EventArgs e)
    {
        await _authService.LogoutAsync();
        Application.Current!.MainPage = new NavigationPage(
            IPlatformApplication.Current!.Services.GetRequiredService<LoginPage>());
    }

    private void OnNameChanged(object? sender, TextChangedEventArgs e)        => UpdateSaveButton();
    private void OnServerChanged(object? sender, TextChangedEventArgs e)       => UpdateSaveButton();
    private void OnPositionOptInToggled(object? sender, ToggledEventArgs e)    => UpdateSaveButton();
    private void OnQuietHoursToggled(object? sender, ToggledEventArgs e)       => UpdateSaveButton();

    private void UpdateSaveButton()
    {
        if (_loading) return;
        // Save is enabled only when name is non-empty (trimmed)
        SaveBtn.IsEnabled = !string.IsNullOrWhiteSpace(NameEntry.Text);
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_prefs is null) return;

        SaveBtn.IsEnabled = false;
        StatusLabel.IsVisible = false;

        _prefs.EmployeeName     = NameEntry.Text.Trim();
        _prefs.ServerUrl        = NormalizeServerUrl(ServerUrlEntry.Text?.Trim() ?? string.Empty);
        _prefs.ServerKey        = ServerKeyEntry.Text?.Trim() ?? string.Empty;
        _prefs.PositionOptIn     = PositionOptInSwitch.IsToggled;
        _prefs.QuietHoursEnabled = QuietHoursSwitch.IsToggled;
        _prefs.QuietHoursStart = TimeOnly.FromTimeSpan(QuietStartPicker.Time.GetValueOrDefault());
        _prefs.QuietHoursEnd   = TimeOnly.FromTimeSpan(QuietEndPicker.Time.GetValueOrDefault());

        try
        {
            await _db.SaveChangesWithRetryAsync();
            StatusLabel.Text      = "Saved.";
            StatusLabel.TextColor = Colors.Green;
        }
        catch (ScheduleDbCorruptException)
        {
            StatusLabel.Text      = "History reset due to database error.";
            StatusLabel.TextColor = Colors.Orange;
        }
        catch
        {
            StatusLabel.Text      = "Save failed — try again.";
            StatusLabel.TextColor = Colors.Red;
        }
        finally
        {
            StatusLabel.IsVisible = true;
            UpdateSaveButton();
        }
    }

    /// <summary>Ensures URL has http:// scheme. Strips trailing slash.</summary>
    private static string NormalizeServerUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            input = "http://" + input;
        return input.TrimEnd('/');
    }
}
