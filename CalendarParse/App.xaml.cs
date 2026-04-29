using CalendarParse.Data;
using CalendarParse.Pages;
using CalendarParse.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CalendarParse
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var services = IPlatformApplication.Current!.Services;

#if ANDROID
            WireNotificationMonitor();
#endif

            return new Window(services.GetRequiredService<Pages.LoadingPage>());
        }

        protected override void OnResume()
        {
            base.OnResume();

            var services = IPlatformApplication.Current?.Services;
            if (services is null) return;

            _ = RetryPendingConfirmationsAsync(services);
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
                System.Diagnostics.Debug.WriteLine($"[App] Pending-confirmation retry failed: {ex.Message}");
            }
        }

#if ANDROID
        private static void WireNotificationMonitor()
        {
            // Subscribe immediately if the service is already bound (app was restarted while
            // notification access was already granted).
            if (Platforms.Android.AndroidNotificationMonitor.Instance is { } existing)
            {
                System.Diagnostics.Debug.WriteLine("[App] WireNotificationMonitor — subscribing to existing instance");
                SubscribeToMonitor(existing);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[App] WireNotificationMonitor — no instance yet, waiting for InstanceReady");
            }

            // Subscribe when Android binds the service later (first run after granting access).
            Platforms.Android.AndroidNotificationMonitor.InstanceReady += monitor =>
            {
                System.Diagnostics.Debug.WriteLine("[App] InstanceReady fired — subscribing to monitor");
                SubscribeToMonitor(monitor);
            };
        }

        private static void SubscribeToMonitor(Platforms.Android.AndroidNotificationMonitor monitor)
        {
            monitor.NotificationReceived -= OnScheduleNotificationReceived; // guard double-subscribe
            monitor.NotificationReceived += OnScheduleNotificationReceived;
            System.Diagnostics.Debug.WriteLine(
                $"[App] Subscribed to NotificationReceived on monitor" +
                $" (pkg='{monitor.WatchedPackage ?? "<none>"}'" +
                $" sender='{monitor.WatchedSenderName ?? "<none>"}'" +
                $" listening={monitor.IsListening})");
        }

        private static void OnScheduleNotificationReceived(
            object? sender,
            Services.NotificationReceivedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[App] OnScheduleNotificationReceived — from='{e.SenderName}'" +
                $" pkg='{e.SourcePackage}' at={e.ReceivedAt:T}" +
                $" imageBytes={e.ImageBytes?.Length.ToString() ?? "null"}");

            if (e.ImageBytes is not { } imageBytes)
            {
                // Image extraction failed — nothing to do; the user can use the share-sheet manually.
                System.Diagnostics.Debug.WriteLine("[App] No image bytes — skipping notification");
                return;
            }

            // Save bytes to a temp file; PendingIntents can't carry large byte arrays.
            // Synchronous write: 76 KB to cache is trivially fast and avoids any
            // async-continuation double-dispatch issue on the MAUI Android SynchronizationContext.
            string tempPath;
            try
            {
                tempPath = Path.Combine(
                    FileSystem.CacheDirectory,
                    $"monitor_pending_{DateTime.Now:HHmmss}.jpg");
                File.WriteAllBytes(tempPath, imageBytes);
                System.Diagnostics.Debug.WriteLine($"[App] Saved temp image ({imageBytes.Length:N0} bytes) → {tempPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Failed to save temp image: {ex.Message}");
                return;
            }

            FireScheduleDetectedNotification(tempPath, e.SenderName);
        }

        private static void FireScheduleDetectedNotification(string tempPath, string senderName)
        {
            const int    notifId   = 4001;
            const string channelId = "CalendarParse_ScheduleDetected";

            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

            var context = global::Android.App.Application.Context;

            var piFlags = global::Android.App.PendingIntentFlags.UpdateCurrent
                | (OperatingSystem.IsAndroidVersionAtLeast(23)
                    ? global::Android.App.PendingIntentFlags.Immutable
                    : 0);

            // Yes action → BroadcastReceiver → re-launches app to auto-submit
            var yesIntent = new global::Android.Content.Intent(
                context, typeof(Platforms.Android.ScheduleImageActionReceiver));
            yesIntent.SetAction(Platforms.Android.ScheduleImageActionReceiver.ActionYes);
            yesIntent.PutExtra(Platforms.Android.ScheduleImageActionReceiver.ExtraNotifId, notifId);
            yesIntent.PutExtra(MainActivity.ExtraMonitorImagePath, tempPath);
            var yesPi = global::Android.App.PendingIntent.GetBroadcast(
                context, 0, yesIntent, piFlags)!;

            // No action → BroadcastReceiver → dismiss + delete temp file
            var noIntent = new global::Android.Content.Intent(
                context, typeof(Platforms.Android.ScheduleImageActionReceiver));
            noIntent.SetAction(Platforms.Android.ScheduleImageActionReceiver.ActionNo);
            noIntent.PutExtra(Platforms.Android.ScheduleImageActionReceiver.ExtraNotifId, notifId);
            noIntent.PutExtra(MainActivity.ExtraMonitorImagePath, tempPath);
            var noPi = global::Android.App.PendingIntent.GetBroadcast(
                context, 1, noIntent, piFlags)!;

            // Body tap → open app, load image into Schedule tab (user decides whether to process)
            var tapIntent = context.PackageManager
                ?.GetLaunchIntentForPackage(context.PackageName ?? string.Empty);
            if (tapIntent is null) return;
            tapIntent.SetAction(MainActivity.ActionLoadMonitorImage);
            tapIntent.PutExtra(MainActivity.ExtraMonitorImagePath, tempPath);
            tapIntent.AddFlags(global::Android.Content.ActivityFlags.SingleTop);
            var tapPi = global::Android.App.PendingIntent.GetActivity(
                context, 2, tapIntent, piFlags)!;

            var mgr = context.GetSystemService(
                global::Android.Content.Context.NotificationService)
                as global::Android.App.NotificationManager;
            if (mgr is null) return;

            var notif = new global::Android.App.Notification.Builder(context, channelId)
                .SetContentTitle("Schedule image received")
                .SetContentText($"From {senderName} — process it?")
                .SetSmallIcon(Resource.Mipmap.appicon)
                .SetContentIntent(tapPi)
                // Show as a full-screen overlay so the prompt is visible whether the
                // app is in the foreground or background.  Android 10+ suppresses
                // heads-up pop-ups from an active foreground app without this.
                .SetFullScreenIntent(tapPi, highPriority: true)
                .SetAutoCancel(true)
                .AddAction(new global::Android.App.Notification.Action.Builder(
                    0, "Yes, Process", yesPi).Build())
                .AddAction(new global::Android.App.Notification.Action.Builder(
                    0, "No", noPi).Build())
                .Build()!;

            mgr.Notify(notifId, notif);
            System.Diagnostics.Debug.WriteLine("[App] Fired schedule-detected notification");
        }
#endif
    }
}
