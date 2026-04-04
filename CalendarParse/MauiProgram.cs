using CalendarParse.Data;
using CalendarParse.Services;
using CalendarParse.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
#if ANDROID
using CalendarParse.Platforms.Android;
#endif

namespace CalendarParse
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            // ── Data layer ─────────────────────────────────────────────────
            var dbPath = Path.Combine(
                FileSystem.AppDataDirectory, "schedule_history.db");

            builder.Services.AddDbContext<ScheduleHistoryDb>(opts =>
                opts.UseSqlite($"Data Source={dbPath}"),
                ServiceLifetime.Singleton);

            // ── API services ───────────────────────────────────────────────
            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<IServerDiscovery, ManualIpDiscovery>();
            builder.Services.AddSingleton<ApiClient>();
            builder.Services.AddTransient<ConfirmationPageViewModel>();

            // ── Notification monitor (Android only) ────────────────────────
#if ANDROID
            // AndroidNotificationMonitor is bound by the OS as a Service.
            // ISmsMonitorService resolves to the live singleton once Android binds it;
            // callers must handle null when the user hasn't granted notification access.
            builder.Services.AddSingleton<ISmsMonitorService>(_ =>
                AndroidNotificationMonitor.Instance!);

            // Job polling via Android Foreground Service
            builder.Services.AddSingleton<IJobPollingService, Platforms.Android.AndroidJobPollingService>();
#else
            builder.Services.AddSingleton<IJobPollingService, NoOpJobPollingService>();
#endif

            // ── Pages ──────────────────────────────────────────────────────
            builder.Services.AddSingleton<MainPage>();
            builder.Services.AddTransient<Pages.SettingsPage>();
            builder.Services.AddTransient<Pages.MonitorSetupPage>();
            builder.Services.AddTransient<Pages.ConfirmationPage>();
            builder.Services.AddTransient<Pages.HistoryPage>();

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }

        /// <summary>
        /// Applies EF migrations and handles corrupt-DB reset.
        /// Call from App.xaml.cs after MauiApp is built.
        /// </summary>
        /// <summary>
        /// Creates the SQLite schema if it doesn't exist.
        /// Uses EnsureCreated (no migration files needed for mobile — schema is always current).
        /// On corrupt DB, deletes and recreates.
        /// </summary>
        public static async Task InitializeDatabaseAsync(IServiceProvider services)
        {
            var db = services.GetRequiredService<ScheduleHistoryDb>();
            try
            {
                await db.Database.EnsureCreatedAsync();

                // Schema evolution: add every column that might be missing from older DB versions.
                // EnsureCreated only creates missing tables, not missing columns.
                // SQLite ADD COLUMN throws on duplicate — we catch each one and continue.
                // ── ScheduleRuns ──────────────────────────────────────────────
                await AddColumnIfMissingAsync(db, "ALTER TABLE ScheduleRuns ADD COLUMN ConfirmedCount INTEGER NOT NULL DEFAULT 0");
                await AddColumnIfMissingAsync(db, "ALTER TABLE ScheduleRuns ADD COLUMN TotalCount INTEGER NOT NULL DEFAULT 0");
                await AddColumnIfMissingAsync(db, "ALTER TABLE ScheduleRuns ADD COLUMN ShiftsJson TEXT NOT NULL DEFAULT '[]'");
                await AddColumnIfMissingAsync(db, "ALTER TABLE ScheduleRuns ADD COLUMN ImagePath TEXT");
                await AddColumnIfMissingAsync(db, "ALTER TABLE ScheduleRuns ADD COLUMN ImageWidth INTEGER NOT NULL DEFAULT 0");
                await AddColumnIfMissingAsync(db, "ALTER TABLE ScheduleRuns ADD COLUMN ImageHeight INTEGER NOT NULL DEFAULT 0");
                await AddColumnIfMissingAsync(db, "ALTER TABLE ScheduleRuns ADD COLUMN TotalMinutes INTEGER NOT NULL DEFAULT 0");
                await AddColumnIfMissingAsync(db, "ALTER TABLE ScheduleRuns ADD COLUMN WeekStart TEXT NOT NULL DEFAULT ''");
                await AddColumnIfMissingAsync(db, "ALTER TABLE ScheduleRuns ADD COLUMN Status INTEGER NOT NULL DEFAULT 1");
                await AddColumnIfMissingAsync(db, "ALTER TABLE ScheduleRuns ADD COLUMN RemoteJobId TEXT");
                await AddColumnIfMissingAsync(db, "ALTER TABLE ScheduleRuns ADD COLUMN ErrorMessage TEXT");
                // ── AppPreferences ────────────────────────────────────────────
                await AddColumnIfMissingAsync(db, "ALTER TABLE AppPreferences ADD COLUMN EmployeeName TEXT NOT NULL DEFAULT ''");
                await AddColumnIfMissingAsync(db, "ALTER TABLE AppPreferences ADD COLUMN ServerUrl TEXT NOT NULL DEFAULT ''");
                await AddColumnIfMissingAsync(db, "ALTER TABLE AppPreferences ADD COLUMN ServerKey TEXT NOT NULL DEFAULT ''");
                await AddColumnIfMissingAsync(db, "ALTER TABLE AppPreferences ADD COLUMN QuietHoursEnabled INTEGER NOT NULL DEFAULT 0");
                await AddColumnIfMissingAsync(db, "ALTER TABLE AppPreferences ADD COLUMN QuietHoursStart TEXT NOT NULL DEFAULT '22:00'");
                await AddColumnIfMissingAsync(db, "ALTER TABLE AppPreferences ADD COLUMN QuietHoursEnd TEXT NOT NULL DEFAULT '07:00'");
                await AddColumnIfMissingAsync(db, "ALTER TABLE AppPreferences ADD COLUMN PositionOptIn INTEGER");
                // ── PendingConfirmations ───────────────────────────────────────
                await AddColumnIfMissingAsync(db, "ALTER TABLE PendingConfirmations ADD COLUMN ShiftsJson TEXT NOT NULL DEFAULT ''");
                await AddColumnIfMissingAsync(db, "ALTER TABLE PendingConfirmations ADD COLUMN QueuedAt TEXT NOT NULL DEFAULT '2000-01-01'");

                // Backfill: completed runs should have Status=2 (Completed)
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE ScheduleRuns SET Status = 2 WHERE IsComplete = 1 AND Status = 1");
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
                when (ex.SqliteErrorCode == 11 /* SQLITE_CORRUPT */)
            {
                // Corrupt DB — delete file and recreate schema from scratch
                var conn = db.Database.GetDbConnection();
                var path = conn.DataSource;
                conn.Close();
                if (File.Exists(path)) File.Delete(path);
                await db.Database.EnsureCreatedAsync();
            }
        }

        private static async Task AddColumnIfMissingAsync(ScheduleHistoryDb db, string alterSql)
        {
            try   { await db.Database.ExecuteSqlRawAsync(alterSql); }
            catch { /* column already exists — safe to ignore */ }
        }

        /// <summary>
        /// Re-starts polling for any runs that were in Processing state when the app last closed.
        /// Call after InitializeDatabaseAsync.
        /// </summary>
        public static async Task ResumeInFlightJobsAsync(IServiceProvider services)
        {
            try
            {
                var db      = services.GetRequiredService<ScheduleHistoryDb>();
                var polling = services.GetRequiredService<IJobPollingService>();
                var runs    = await db.GetProcessingRunsAsync();
                foreach (var run in runs)
                    polling.StartPolling(run.Id, run.RemoteJobId!);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] ResumeInFlightJobs failed: {ex.Message}");
            }
        }
    }
}
