using CalendarParse.Data;
using CalendarParse.Services;

namespace CalendarParse.Pages;

public partial class LoginPage : ContentPage
{
    private readonly IAuthService _authService;
    private readonly ScheduleHistoryDb _db;

    public LoginPage(IAuthService authService, ScheduleHistoryDb db)
    {
        InitializeComponent();
        _authService = authService;
        _db          = db;
    }

    private async void OnSignInClicked(object sender, EventArgs e) =>
        await DoLoginAsync(signUp: false);

    private async void OnSignUpTapped(object sender, TappedEventArgs e) =>
        await DoLoginAsync(signUp: true);

    private async Task DoLoginAsync(bool signUp)
    {
        SignInBtn.IsEnabled  = false;
        SignUpLink.IsEnabled = false;
        ErrorLabel.IsVisible = false;

        var success = await _authService.LoginAsync(signUp);

        if (success)
        {
            // Auto-populate employee name from Auth0 profile if the user hasn't set one yet
            try
            {
                var prefs = await _db.GetPreferencesAsync();
                if (string.IsNullOrWhiteSpace(prefs.EmployeeName)
                    && !string.IsNullOrWhiteSpace(_authService.UserName))
                {
                    // Use only the first name (e.g. "Devin" from "Devin Guthrie")
                    prefs.EmployeeName = _authService.UserName.Split(' ')[0];
                    await _db.SaveChangesWithRetryAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LoginPage] Failed to populate name: {ex.Message}");
            }

            Application.Current!.MainPage = new AppShell();
        }
        else
        {
            var detail = _authService.LastLoginError;
            ErrorLabel.Text = string.IsNullOrEmpty(detail)
                ? "Sign in failed — please try again."
                : $"Sign in failed: {detail}";
            ErrorLabel.IsVisible = true;
            SignInBtn.IsEnabled  = true;
            SignUpLink.IsEnabled = true;
        }
    }
}
