namespace CalendarParse
{
    public partial class AppShell : Shell
    {
        // Tracks the active ShellSection before a tab switch so we can pop
        // its navigation stack if the user returns to the list view.
        private ShellSection? _previousSection;

        public AppShell()
        {
            InitializeComponent();
        }

        protected override void OnNavigated(ShellNavigatedEventArgs args)
        {
            base.OnNavigated(args);

            // When the user switches tabs, pop any pages pushed on top of the previous
            // tab's root so tapping a tab always lands on the list, not a detail page.
            if (args.Source is ShellNavigationSource.ShellSectionChanged
                            or ShellNavigationSource.ShellItemChanged)
            {
                var prev = _previousSection;
                if (prev is not null && prev.Navigation.NavigationStack.Count > 1)
                {
                    MainThread.BeginInvokeOnMainThread(async () =>
                        await prev.Navigation.PopToRootAsync(animated: false));
                }
            }

            _previousSection = CurrentItem?.CurrentItem;
        }
    }
}
