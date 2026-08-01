using Telegram.Services;
using Telegram.Notifications;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Telegram
{
    public sealed partial class AdaptiveShellPage : Page
    {
        public AdaptiveShellPage()
        {
            InitializeComponent();
            Loaded += AdaptiveShellPage_Loaded;
            Unloaded += AdaptiveShellPage_Unloaded;
            SizeChanged += AdaptiveShellPage_SizeChanged;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            EnsureInitialPage();
        }

        private void AdaptiveShellPage_Loaded(object sender, RoutedEventArgs e)
        {
            AdaptiveShellNavigationService.Register(NavigationFrame, DetailFrame, EmptyDetailPanel);
            EnsureInitialPage();
            UpdateLayoutMode();
            AdaptiveShellNavigationService.OpenPendingChat();
            StartNotificationKeepAlive();
        }

        private void StartNotificationKeepAlive()
        {
            if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.None)
            {
                TelegramContinuousNotificationPoller.Stop();
                return;
            }

            TelegramService.Instance.Start();
            TelegramContinuousNotificationPoller.Start();

            if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.FixedSystem)
            {
                var ignoredWns = TelegramNotificationRegistrar.RegisterAsync();
            }

            if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.Always)
            {
                var ignored = TelegramContinuousNotificationPoller.StartKeepAliveAsync();
            }
        }

        private void AdaptiveShellPage_Unloaded(object sender, RoutedEventArgs e)
        {
            AdaptiveShellNavigationService.Unregister(NavigationFrame, DetailFrame);
        }

        private void AdaptiveShellPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateLayoutMode();
        }

        private void EnsureInitialPage()
        {
            if (NavigationFrame != null && NavigationFrame.Content == null)
                NavigationFrame.Navigate(typeof(Chats));
        }

        private void UpdateLayoutMode()
        {
            var landscape = IsLandscapeMode();
            NavigationColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = landscape ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
            DetailPane.Visibility = landscape ? Visibility.Visible : Visibility.Collapsed;
            AdaptiveShellNavigationService.SetLandscape(landscape);
        }

        private bool IsLandscapeMode()
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if ((width <= 0 || height <= 0) && Window.Current != null)
            {
                width = Window.Current.Bounds.Width;
                height = Window.Current.Bounds.Height;
            }

            return width > height;
        }
    }
}
