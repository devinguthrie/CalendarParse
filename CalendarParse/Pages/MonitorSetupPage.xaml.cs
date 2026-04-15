using CalendarParse.Services;

namespace CalendarParse.Pages;

public partial class MonitorSetupPage : ContentPage
{
    private readonly ISmsMonitorService?         _monitor;
    private readonly IMessagingAppPickerService? _appPicker;

    // State for the selected app / thread / contact
    private MessagingAppInfo? _selectedApp;
    private SmsThreadInfo?    _selectedThread;

    // Debounce token for contact search
    private CancellationTokenSource? _contactSearchCts;

    public MonitorSetupPage(ISmsMonitorService? monitor = null, IMessagingAppPickerService? appPicker = null)
    {
        InitializeComponent();
        _monitor   = monitor;
        _appPicker = appPicker;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RestoreSelectionState();
        RefreshPermissionBanner();
        UpdateSaveButton();
        _ = RestoreAppIconAsync();
    }

    /// <summary>
    /// Re-fetches the launcher icon for the saved app from PackageManager.
    /// Called fire-and-forget from OnAppearing so the icon is restored after
    /// the app is backgrounded and relaunched (icon bytes are not persisted).
    /// </summary>
    private async Task RestoreAppIconAsync()
    {
        if (_appPicker is null || _selectedApp is null) return;
        if (SelectedAppIcon.IsVisible) return;  // already showing — nothing to do

        try
        {
            var apps  = await _appPicker.GetMessagingAppsAsync();
            var match = apps.FirstOrDefault(a => a.PackageName == _selectedApp.PackageName);
            if (match?.IconPng?.Length > 0)
            {
                SelectedAppIcon.Source    = ImageSource.FromStream(() => new MemoryStream(match.IconPng));
                SelectedAppIcon.IsVisible = true;
            }
        }
        catch { /* non-fatal — icon simply stays hidden */ }
    }

    // ── State restore ─────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the picker UI from the values stored in the monitor service + Preferences.
    /// Called on every OnAppearing so the display stays in sync after returning from pickers.
    /// </summary>
    private void RestoreSelectionState()
    {
        var pkg    = _monitor?.WatchedPackage;
        var sender = _monitor?.WatchedSenderName;

        if (string.IsNullOrEmpty(pkg))
        {
            // Nothing configured yet — leave everything in default collapsed state
            return;
        }

        // Restore selected app display
        var label = Preferences.Get("monitor_app_label", pkg);
        var isSms = Preferences.Get("monitor_app_is_sms", false);

        // Preserve in-memory _selectedApp if already set (e.g. user just picked a new app
        // moments ago and OnAppearing fires again due to modal pop completing asynchronously).
        _selectedApp ??= new MessagingAppInfo(pkg, label, null, isSms);
        SetAppDisplay(_selectedApp, iconSource: null);

        // Restore contact/thread display — give priority to existing in-memory state so
        // that a selection made just before this OnAppearing (modal pop is async) is not
        // overwritten with the last-saved value.
        if (isSms)
        {
            if (_selectedThread is not null)
                SetThreadDisplay(_selectedThread.DisplayName);
            else if (!string.IsNullOrEmpty(sender))
            {
                _selectedThread = new SmsThreadInfo(0, sender, "", "");
                SetThreadDisplay(sender);
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(sender) && string.IsNullOrEmpty(ContactNameEntry.Text))
                ContactNameEntry.Text = sender;
        }
    }

    // ── App picker ────────────────────────────────────────────────────────────

    private async void OnSelectAppTapped(object? sender, TappedEventArgs e)
    {
        if (_appPicker is null)
        {
            await DisplayAlert("Not Available", "App picker is only available on Android.", "OK");
            return;
        }

        var tcs         = new TaskCompletionSource<MessagingAppInfo?>();
        var pickerPage  = new AppPickerPage(_appPicker, tcs);
        await Navigation.PushModalAsync(new NavigationPage(pickerPage));

        var result = await tcs.Task;
        if (result is null) return;

        _selectedApp    = result;
        _selectedThread = null;
        ContactNameEntry.Text = "";

        // Persist app label + SMS flag so it can be restored without re-querying PackageManager
        Preferences.Set("monitor_app_label",  result.Label);
        Preferences.Set("monitor_app_is_sms", result.SupportsSmsThreads);

        // Build ImageSource from icon bytes (if any)
        ImageSource? iconSrc = result.IconPng?.Length > 0
            ? ImageSource.FromStream(() => new MemoryStream(result.IconPng))
            : null;

        SetAppDisplay(result, iconSrc);
        UpdateSaveButton();
    }

    private void SetAppDisplay(MessagingAppInfo app, ImageSource? iconSource)
    {
        SelectedAppLabel.Text      = app.Label;
        SelectedAppLabel.TextColor = null;  // clear Gray400 placeholder colour; inherits theme default

        if (iconSource is not null)
        {
            SelectedAppIcon.Source    = iconSource;
            SelectedAppIcon.IsVisible = true;
        }

        // Show the contact/thread section
        ContactSection.IsVisible = true;

        if (app.SupportsSmsThreads)
        {
            ThreadPickerRow.IsVisible      = true;
            ContactSearchSection.IsVisible = false;
        }
        else
        {
            ThreadPickerRow.IsVisible      = false;
            ContactSearchSection.IsVisible = true;
        }
    }

    // ── Thread picker ─────────────────────────────────────────────────────────

    private async void OnSelectThreadTapped(object? sender, TappedEventArgs e)
    {
        if (_appPicker is null) return;

        var tcs        = new TaskCompletionSource<SmsThreadInfo?>();
        var pickerPage = new ThreadPickerPage(_appPicker, tcs);
        await Navigation.PushModalAsync(new NavigationPage(pickerPage));

        var result = await tcs.Task;
        if (result is null) return;

        _selectedThread = result;
        SetThreadDisplay(result.DisplayName);
        UpdateSaveButton();
    }

    private void SetThreadDisplay(string displayName)
    {
        SelectedThreadLabel.Text      = displayName;
        SelectedThreadLabel.TextColor = null;  // clear Gray400 placeholder colour
    }

    // ── Contact autocomplete ──────────────────────────────────────────────────

    private async void OnContactNameChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateSaveButton();
        HideSuggestions();

        _contactSearchCts?.Cancel();
        _contactSearchCts = new CancellationTokenSource();
        var ct            = _contactSearchCts.Token;

        var query = e.NewTextValue?.Trim() ?? "";
        if (query.Length < 2 || _appPicker is null) return;

        try
        {
            // Small debounce so we don't query on every keystroke
            await Task.Delay(250, ct);

            // Silently request READ_CONTACTS before searching
            var permStatus = await Permissions.RequestAsync<Permissions.ContactsRead>();
            if (permStatus != PermissionStatus.Granted) return;

            var suggestions = await _appPicker.SearchContactsAsync(query, ct);
            if (ct.IsCancellationRequested) return;

            if (suggestions.Count > 0)
            {
                ContactSuggestions.ItemsSource = suggestions;
                SuggestionsBorder.IsVisible    = true;
            }
        }
        catch (OperationCanceledException) { /* new keystroke cancelled this search */ }
        catch { /* silently ignore contact search errors */ }
    }

    private void OnContactSuggestionTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is ContactInfo contact)
        {
            ContactNameEntry.Text = contact.DisplayName;
            HideSuggestions();
        }
    }

    private void HideSuggestions()
    {
        SuggestionsBorder.IsVisible = false;
        ContactSuggestions.ItemsSource = null;
    }

    // ── Permission banner ─────────────────────────────────────────────────────

    private void RefreshPermissionBanner()
    {
#if ANDROID
        // Ask the OS directly whether our package has notification listener access.
        // This updates immediately when the user returns from Settings — unlike
        // IsListening, which depends on the service re-binding (async, delayed).
        var context          = global::Android.App.Application.Context;
        var enabledListeners  = global::AndroidX.Core.App.NotificationManagerCompat
            .GetEnabledListenerPackages(context);
        var pkgName           = context.PackageName ?? string.Empty;
        var hasListenerAccess = enabledListeners is not null && enabledListeners.Contains(pkgName);

        // POST_NOTIFICATIONS is a runtime permission on Android 13+ (API 33).
        // Check current grant state so the banner can prompt for both at once.
        var notifMgr = global::AndroidX.Core.App.NotificationManagerCompat.From(context);
        var hasPostNotif = notifMgr.AreNotificationsEnabled();

        var hasAccess = hasListenerAccess && hasPostNotif;
#else
        var hasAccess = _monitor?.IsListening ?? false;
#endif
        PermissionBanner.IsVisible = !hasAccess;
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    private void UpdateSaveButton()
        => SaveBtn.IsEnabled = _selectedApp is not null;

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_monitor is null)
        {
            ShowStatus("Notification monitor not available on this platform.", Colors.Orange);
            return;
        }

        if (_selectedApp is null)
        {
            ShowStatus("Please select a messaging app first.", Colors.Orange);
            return;
        }

#if ANDROID
        // POST_NOTIFICATIONS is a runtime permission on Android 13+ — request it now
        // so the CalendarParse notification is visible when a schedule image arrives.
        if (await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
            await Permissions.RequestAsync<Permissions.PostNotifications>();
#endif

        _monitor.WatchedPackage = _selectedApp.PackageName;
        Preferences.Set("monitor_watched_package", _selectedApp.PackageName);

        string? senderValue;
        if (_selectedApp.SupportsSmsThreads)
        {
            senderValue = _selectedThread?.DisplayName;
        }
        else
        {
            var name = ContactNameEntry.Text?.Trim();
            senderValue = string.IsNullOrEmpty(name) ? null : name;
        }

        _monitor.WatchedSenderName = senderValue;
        Preferences.Set("monitor_watched_sender", senderValue ?? "");

        ShowStatus("Saved. Listening for notifications.", Colors.Green);
    }

    // ── Open system notification settings ─────────────────────────────────────

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
            await DisplayAlert("Error", "Could not open notification settings. Please open them manually.", "OK");
        }
#else
        await DisplayAlert("Not Supported", "Notification monitoring is only available on Android.", "OK");
#endif
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ShowStatus(string message, Color color)
    {
        StatusLabel.Text      = message;
        StatusLabel.TextColor = color;
        StatusLabel.IsVisible = true;
    }
}

