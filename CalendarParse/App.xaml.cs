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
            // Single-page app: resolve MainPage directly from DI, bypassing Shell routing
            var mainPage = IPlatformApplication.Current!.Services.GetRequiredService<MainPage>();
            return new Window(mainPage);
        }
    }
}