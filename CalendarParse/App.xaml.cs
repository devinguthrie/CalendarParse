using CalendarParse.Data;
using CalendarParse.Pages;

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
            // Initialize DB (EnsureCreated) before first navigation
            _ = InitAsync();

            return new Window(new AppShell());
        }

        private static async Task InitAsync()
        {
            var services = IPlatformApplication.Current!.Services;
            try
            {
                await MauiProgram.InitializeDatabaseAsync(services);
                await MauiProgram.ResumeInFlightJobsAsync(services);
            }
            catch (Exception ex)
            {
                // InitializeDatabaseAsync already handles corrupt-DB reset internally.
                // Any remaining exception is unrecoverable — log and continue.
                System.Diagnostics.Debug.WriteLine($"[App] DB init failed: {ex.Message}");
            }

#if ANDROID
            WireNotificationMonitor();
#endif
        }

#if ANDROID
        private static void WireNotificationMonitor()
        {
            // Subscribe immediately if the service is already bound (app was restarted while
            // notification access was already granted).
            if (Platforms.Android.AndroidNotificationMonitor.Instance is { } existing)
                SubscribeToMonitor(existing);

            // Subscribe when Android binds the service later (first run after granting access).
            Platforms.Android.AndroidNotificationMonitor.InstanceReady += SubscribeToMonitor;
        }

        private static void SubscribeToMonitor(Platforms.Android.AndroidNotificationMonitor monitor)
        {
            monitor.NotificationReceived -= OnScheduleNotificationReceived; // guard double-subscribe
            monitor.NotificationReceived += OnScheduleNotificationReceived;
        }

        private static void OnScheduleNotificationReceived(
            object? sender,
            Services.NotificationReceivedEventArgs e)
        {
            // Navigate to ConfirmationPage in share-prompt mode.
            // The page will show "Open [app]" deep-link and wait for the user to share.
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    var services = IPlatformApplication.Current!.Services;
                    var page     = services.GetRequiredService<ConfirmationPage>();
                    page.ShowSharePrompt(e.SourcePackage);

                    if (Shell.Current is { } shell)
                        await shell.Navigation.PushAsync(page);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[App] Navigation on notification failed: {ex.Message}");
                }
            });
        }
#endif
    }
}
