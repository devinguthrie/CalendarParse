using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using CalendarParse.Pages;
using CalendarParse.Platforms.Android;
using Sentry;

namespace CalendarParse
{
    [Activity(
        Theme             = "@style/Maui.SplashTheme",
        MainLauncher      = true,
        LaunchMode        = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation
            | ConfigChanges.UiMode | ConfigChanges.ScreenLayout
            | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    [IntentFilter(
        [Intent.ActionSend],
        Categories  = [Intent.CategoryDefault],
        DataMimeType = "image/*",
        Label       = "CalendarParse — process schedule")]
    public class MainActivity : MauiAppCompatActivity
    {
        // Action strings and extras for the notification-monitor image flow.
        // Used by ScheduleImageActionReceiver (broadcast) and OnNewIntent (activity).
        internal const string ActionAutoProcessMonitorImage = "calendarparse.action.AUTO_PROCESS_MONITOR";
        internal const string ActionLoadMonitorImage        = "calendarparse.action.LOAD_MONITOR";
        internal const string ExtraMonitorImagePath         = "calendarparse.extra.MONITOR_IMAGE_PATH";

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            // Pass null to discard Android's saved Fragment back stack.
            // MAUI pages created with `new` (non-DI) leave stale fragments in the back
            // stack whose MauiContext service provider is disposed after Activity recreation,
            // causing a fatal ObjectDisposedException on relaunch.
            base.OnCreate(null);

            // Capture any unhandled Java/Android exceptions that survive the .NET layer.
            Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, args) =>
            {
                SentrySdk.CaptureException(args.Exception);
                SentrySdk.Flush(TimeSpan.FromSeconds(2));
            };

            CreateNotificationChannels();
            HandleShareIntent(Intent);
        }

        private void CreateNotificationChannels()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

            var mgr = GetSystemService(global::Android.Content.Context.NotificationService)
                as global::Android.App.NotificationManager;
            if (mgr is null) return;

            // Low-importance ongoing notification shown while the foreground service runs
            var processingName = new global::Java.Lang.String("Schedule Processing");
            var processing = new global::Android.App.NotificationChannel(
                JobPollingForegroundService.ChannelProcessing,
                processingName,
                global::Android.App.NotificationImportance.Low);
            processing.Description = "Shown while a schedule is being processed on the server";
            mgr.CreateNotificationChannel(processing);

            // High-importance notification fired when processing completes (or fails)
            var readyName = new global::Java.Lang.String("Schedule Ready");
            var ready = new global::Android.App.NotificationChannel(
                JobPollingForegroundService.ChannelReady,
                readyName,
                global::Android.App.NotificationImportance.High);
            ready.Description = "Fires when a schedule is ready to review";
            mgr.CreateNotificationChannel(ready);

            // High-importance prompt shown when the notification monitor detects a schedule image
            var detectedName = new global::Java.Lang.String("Schedule Detected");
            var detected = new global::Android.App.NotificationChannel(
                "CalendarParse_ScheduleDetected",
                detectedName,
                global::Android.App.NotificationImportance.High);
            detected.Description = "Asks whether to process a schedule image received in a monitored chat";
            mgr.CreateNotificationChannel(detected);
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            HandleShareIntent(intent);
            HandleMonitorIntent(intent);
        }

        private void HandleMonitorIntent(Intent? intent)
        {
            if (intent is null) return;
            var action = intent.Action;
            if (action != ActionAutoProcessMonitorImage && action != ActionLoadMonitorImage) return;

            var path = intent.GetStringExtra(ExtraMonitorImagePath);
            if (string.IsNullOrEmpty(path)) return;

            bool autoProcess = action == ActionAutoProcessMonitorImage;
            System.Diagnostics.Debug.WriteLine(
                $"[MainActivity] HandleMonitorIntent autoProcess={autoProcess} path='{path}'");
            _ = LoadMonitorImageIntoMainPageAsync(path, autoProcess);
        }

        private async Task LoadMonitorImageIntoMainPageAsync(string path, bool autoProcess)
        {
            try
            {
                byte[]? bytes = null;
                if (File.Exists(path))
                {
                    bytes = await File.ReadAllBytesAsync(path);
                    File.Delete(path); // consumed — clean up
                }

                if (bytes is null || bytes.Length == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[MainActivity] Monitor image file missing or empty");
                    return;
                }

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (Shell.Current is { } shell)
                        await shell.GoToAsync("//MainPage");

                    var services = IPlatformApplication.Current!.Services;
                    var mainPage = services.GetRequiredService<MainPage>();
                    await mainPage.LoadMonitorImageAsync(bytes, autoProcess);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MainActivity] LoadMonitorImageIntoMainPageAsync failed: {ex.Message}");
            }
        }

        private void HandleShareIntent(Intent? intent)
        {
            if (intent?.Action != Intent.ActionSend) return;
            if (intent.Type?.StartsWith("image/") != true) return;

            global::Java.Lang.Object? uriObj;
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                uriObj = intent.GetParcelableExtra(Intent.ExtraStream,
                    global::Java.Lang.Class.FromType(typeof(global::Android.Net.Uri)));
            }
            else
            {
#pragma warning disable CS0618
                uriObj = intent.GetParcelableExtra(Intent.ExtraStream);
#pragma warning restore CS0618
            }
            if (uriObj is not global::Android.Net.Uri uri) return;

            _ = ProcessSharedImageAsync(uri);
        }

        private async Task ProcessSharedImageAsync(global::Android.Net.Uri uri)
        {
            try
            {
                byte[]? imageBytes = null;
                using (var stream = ContentResolver?.OpenInputStream(uri))
                {
                    if (stream is not null)
                    {
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms);
                        imageBytes = ms.ToArray();
                    }
                }

                if (imageBytes is null || imageBytes.Length == 0) return;

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    var services = IPlatformApplication.Current!.Services;
                    var page     = services.GetRequiredService<ConfirmationPage>();
                    await page.StartWithImageAsync(imageBytes);

                    if (Shell.Current is { } shell)
                        await shell.Navigation.PushAsync(page);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MainActivity] Share intent handling failed: {ex.Message}");
            }
        }
    }
}
