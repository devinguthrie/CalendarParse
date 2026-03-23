using CalendarParse.Services;

namespace CalendarParse
{
    public partial class MainPage : ContentPage
    {
        private readonly ICalendarParseService _parser;
        private CancellationTokenSource? _cts;
        private FileResult? _pendingPhoto;

        public MainPage(ICalendarParseService parser)
        {
            InitializeComponent();
            _parser = parser;
        }

        private async void OnCameraClicked(object? sender, EventArgs e)
        {
            if (!MediaPicker.Default.IsCaptureSupported)
            {
                await DisplayAlertAsync("Unavailable", "Camera capture is not supported on this device.", "OK");
                return;
            }
            var photo = await MediaPicker.Default.CapturePhotoAsync();
            if (photo is not null)
                SetPendingPhoto(photo);
        }

        private async void OnImportClicked(object? sender, EventArgs e)
        {
            var photos = await MediaPicker.Default.PickPhotosAsync();
            var photo = photos.FirstOrDefault();
            if (photo is not null)
                SetPendingPhoto(photo);
        }

        private void SetPendingPhoto(FileResult photo)
        {
            _pendingPhoto = photo;
            ProcessBtn.IsEnabled = true;
            FileNameLabel.Text = photo.FileName;
        }

        private async void OnProcessClicked(object? sender, EventArgs e)
        {
            if (_pendingPhoto is not null)
                await ProcessPhotoAsync(_pendingPhoto);
        }

        private async Task ProcessPhotoAsync(FileResult photo)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            SetBusy(true, "Processing…");

            try
            {
                await using var stream = await photo.OpenReadAsync();
                var json = await _parser.ProcessAsync(stream, SearchEntry.Text ?? string.Empty, _cts.Token);
                ResultLabel.Text = json;
            }
            catch (OperationCanceledException)
            {
                ResultLabel.Text = string.Empty;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy, string status = "")
        {
            Spinner.IsRunning = busy;
            Spinner.IsVisible = busy;
            StatusLabel.Text = status;
            StatusLabel.IsVisible = !string.IsNullOrEmpty(status);
            CameraBtn.IsEnabled = !busy;
            ImportBtn.IsEnabled = !busy;
            ProcessBtn.IsEnabled = !busy && _pendingPhoto is not null;
        }

        private void SetStatus(string status)
        {
            StatusLabel.Text = status;
            StatusLabel.IsVisible = !string.IsNullOrEmpty(status);
        }
    }
}
