using Microsoft.Extensions.Logging;
using CalendarParse.Services;

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

            // ── Services ───────────────────────────────────────────────────
            builder.Services.AddSingleton<IImagePreprocessor, ImagePreprocessor>();
            builder.Services.AddSingleton<ITableDetector, TableDetector>();
            builder.Services.AddSingleton<IOcrService, OcrService>();
            builder.Services.AddSingleton<ICalendarStructureAnalyzer, CalendarStructureAnalyzer>();
            builder.Services.AddSingleton<ICalendarParseService, CalendarParseService>();

            // ── Pages ──────────────────────────────────────────────────────
            builder.Services.AddSingleton<MainPage>();

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
