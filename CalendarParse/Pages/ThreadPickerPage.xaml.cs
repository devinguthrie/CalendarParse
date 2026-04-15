using CalendarParse.Services;

namespace CalendarParse.Pages;

public partial class ThreadPickerPage : ContentPage
{
    private readonly IMessagingAppPickerService         _service;
    private readonly TaskCompletionSource<SmsThreadInfo?> _tcs;

    private IReadOnlyList<SmsThreadInfo> _allThreads = [];

    public ThreadPickerPage(IMessagingAppPickerService service, TaskCompletionSource<SmsThreadInfo?> tcs)
    {
        InitializeComponent();
        _service = service;
        _tcs     = tcs;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadThreadsAsync();
    }

    private async Task LoadThreadsAsync()
    {
        Spinner.IsVisible           = true;
        ThreadList.IsVisible        = false;
        PermissionDeniedView.IsVisible = false;

        // Request both permissions before querying — READ_SMS for threads, READ_CONTACTS for names.
        var smsStatus      = await Permissions.RequestAsync<Permissions.Sms>();
        var contactsStatus = await Permissions.RequestAsync<Permissions.ContactsRead>();

        if (smsStatus != PermissionStatus.Granted)
        {
            Spinner.IsVisible           = false;
            PermissionDeniedView.IsVisible = true;
            return;
        }

        try
        {
            _allThreads = await _service.GetSmsThreadsAsync();
            ShowFiltered(SearchBar.Text);
        }
        catch
        {
            await DisplayAlertAsync("Error", "Could not read SMS conversations.", "OK");
            Complete(null);
            return;
        }
        finally
        {
            Spinner.IsVisible    = false;
            ThreadList.IsVisible = true;
        }
    }

    private void ShowFiltered(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            ThreadList.ItemsSource = _allThreads;
        }
        else
        {
            ThreadList.ItemsSource = _allThreads
                .Where(t => t.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || t.Address.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
        => ShowFiltered(e.NewTextValue);

    private void OnThreadTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is SmsThreadInfo thread)
            Complete(thread);
    }

    private void OnCancelClicked(object? sender, EventArgs e)
        => Complete(null);

    private void Complete(SmsThreadInfo? result)
    {
        _tcs.TrySetResult(result);
        Navigation.PopModalAsync();
    }
}
