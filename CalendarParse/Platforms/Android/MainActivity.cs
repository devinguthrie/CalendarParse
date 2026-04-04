using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using CalendarParse.Pages;
using CalendarParse.Platforms.Android;

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
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
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
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            HandleShareIntent(intent);
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
