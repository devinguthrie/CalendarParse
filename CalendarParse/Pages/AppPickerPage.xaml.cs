using CalendarParse.Services;

namespace CalendarParse.Pages;

// ── View model ────────────────────────────────────────────────────────────────

/// <summary>Wraps MessagingAppInfo for binding — converts IconPng bytes to ImageSource.</summary>
public class AppPickerViewModel
{
    private readonly MessagingAppInfo _info;

    public string       PackageName        => _info.PackageName;
    public string       Label              => _info.Label;
    public bool         SupportsSmsThreads => _info.SupportsSmsThreads;
    public ImageSource? Icon               { get; }

    public MessagingAppInfo Info => _info;

    public AppPickerViewModel(MessagingAppInfo info)
    {
        _info = info;
        if (info.IconPng?.Length > 0)
            Icon = ImageSource.FromStream(() => new MemoryStream(info.IconPng));
    }
}

// ── Page ──────────────────────────────────────────────────────────────────────

public partial class AppPickerPage : ContentPage
{
    private readonly IMessagingAppPickerService          _service;
    private readonly TaskCompletionSource<MessagingAppInfo?> _tcs;

    private IReadOnlyList<AppPickerViewModel> _allApps = [];

    public AppPickerPage(IMessagingAppPickerService service, TaskCompletionSource<MessagingAppInfo?> tcs)
    {
        InitializeComponent();
        _service = service;
        _tcs     = tcs;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAppsAsync();
    }

    private async Task LoadAppsAsync()
    {
        Spinner.IsVisible = true;
        AppList.IsVisible = false;

        try
        {
            var apps = await _service.GetMessagingAppsAsync();
            _allApps = apps.Select(a => new AppPickerViewModel(a)).ToList();
            ShowFiltered(SearchBar.Text);
        }
        catch
        {
            await DisplayAlertAsync("Error", "Could not load installed apps.", "OK");
            Complete(null);
        }
        finally
        {
            Spinner.IsVisible = false;
            AppList.IsVisible = true;
        }
    }

    private void ShowFiltered(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            AppList.ItemsSource = _allApps;
        }
        else
        {
            AppList.ItemsSource = _allApps
                .Where(a => a.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
        => ShowFiltered(e.NewTextValue);

    private void OnAppTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is AppPickerViewModel vm)
            Complete(vm.Info);
    }

    private void OnCancelClicked(object? sender, EventArgs e)
        => Complete(null);

    private void Complete(MessagingAppInfo? result)
    {
        _tcs.TrySetResult(result);
        Navigation.PopModalAsync();
    }
}
