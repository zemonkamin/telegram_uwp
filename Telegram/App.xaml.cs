using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Telegram.Notifications;
using Telegram.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Background;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Telegram
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    sealed partial class App : Application
    {
        private const string AppleEmojiFileName = "AppleColorEmoji.ttc";
        private const string AppleEmojiFamilyName = "Apple Color Emoji";

        // Inline emoji need a smaller layout slot than the Apple font reports.  The visual
        // glyph is positioned independently from the slot: this removes the empty area below
        // message lines and the oversized trailing advance next to normal text.
        internal const double ChatInlineEmojiBoxWidth = 15.0;
        internal const double ChatInlineEmojiBoxHeight = 14.0;
        internal const double ChatInlineEmojiFontSize = 18.0;
        internal const double ChatInlineEmojiLineHeight = 18.0;
        internal const double ChatInlineEmojiLeftOffset = -1.5;
        internal const double ChatInlineEmojiTopOffset = -1.0;
        internal const double ChatInlineEmojiRenderOffsetY = 2.75;
        internal static readonly Thickness ChatInlineEmojiLayoutMargin = new Thickness(0, 0, 0, -3.0);

        internal const double ChatsInlineEmojiBoxWidth = 12.0;
        internal const double ChatsInlineEmojiBoxHeight = 12.0;
        internal const double ChatsInlineEmojiFontSize = 15.0;
        internal const double ChatsInlineEmojiLineHeight = 15.0;
        internal const double ChatsInlineEmojiLeftOffset = -1.1;
        internal const double ChatsInlineEmojiTopOffset = -0.8;
        internal const double ChatsInlineEmojiRenderOffsetY = 2.0;
        internal static readonly Thickness ChatsInlineEmojiLayoutMargin = new Thickness(0, 0, 0, -2.0);

        internal static readonly FontFamily ChatInlineAppleEmojiFont =
            new FontFamily("ms-appx:///Assets/Emoji/" + AppleEmojiFileName + "#" +
                           AppleEmojiFamilyName);

        internal static FrameworkElement CreateChatInlineEmoji(string emoji)
        {
            return CreateInlineAppleEmoji(
                emoji,
                ChatInlineEmojiBoxWidth,
                ChatInlineEmojiBoxHeight,
                ChatInlineEmojiFontSize,
                ChatInlineEmojiLineHeight,
                ChatInlineEmojiLeftOffset,
                ChatInlineEmojiTopOffset,
                ChatInlineEmojiRenderOffsetY,
                ChatInlineEmojiLayoutMargin);
        }

        internal static FrameworkElement CreateChatsInlineEmoji(string emoji)
        {
            return CreateInlineAppleEmoji(
                emoji,
                ChatsInlineEmojiBoxWidth,
                ChatsInlineEmojiBoxHeight,
                ChatsInlineEmojiFontSize,
                ChatsInlineEmojiLineHeight,
                ChatsInlineEmojiLeftOffset,
                ChatsInlineEmojiTopOffset,
                ChatsInlineEmojiRenderOffsetY,
                ChatsInlineEmojiLayoutMargin);
        }

        private static FrameworkElement CreateInlineAppleEmoji(
            string emoji,
            double boxWidth,
            double boxHeight,
            double fontSize,
            double lineHeight,
            double leftOffset,
            double topOffset,
            double renderOffsetY,
            Thickness layoutMargin)
        {
            if (string.IsNullOrEmpty(emoji))
                return null;

            // Keep the inline child simple. A nested TextBlock inside a Canvas is validated
            // only when it is inserted into TextBlock.Inlines on older UWP builds and can
            // produce E_INVALIDARG. FontIcon renders the same font glyph without creating
            // another text layout tree inside the parent text control.
            return new FontIcon
            {
                Glyph = emoji,
                FontFamily = ChatInlineAppleEmojiFont,
                FontSize = fontSize,
                Width = boxWidth,
                Height = boxHeight,
                Margin = layoutMargin,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                UseLayoutRounding = false,
                RenderTransform = new TranslateTransform
                {
                    X = leftOffset,
                    Y = topOffset + renderOffsetY
                }
            };
        }

        internal static void ApplyCompactEmojiLineMetrics(TextBlock textBlock)
        {
            if (textBlock == null || textBlock.FontSize <= 0) return;

            // A fixed block line height prevents Apple Color Emoji's ascender/descender
            // metrics from adding visible space below a message or chat preview.
            textBlock.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            textBlock.LineHeight = Math.Ceiling(textBlock.FontSize * 1.12);
        }

        private FontFamily _appleEmojiFont;
        private Frame _appleEmojiRootFrame;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
            this.EnteredBackground += OnEnteredBackground;
            this.LeavingBackground += OnLeavingBackground;
            this.Resuming += OnResuming;
            this.UnhandledException += App_UnhandledException;
        }

        // TDLib raises its updates on a background thread. Capturing the dispatcher here lets
        // the client marshal view-model changes without probing CoreApplication.MainView from
        // that thread (which throws RPC_E_WRONG_THREAD on every call).
        private static void CaptureUiDispatcher()
        {
            try
            {
                var window = Window.Current;
                if (window != null)
                    TdLibTelegramClient.CaptureUiDispatcher(window.Dispatcher);
            }
            catch
            {
            }
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            ActiveChatService.SetForeground(true);
            CaptureUiDispatcher();
#if DEBUG
            if (System.Diagnostics.Debugger.IsAttached)
            {
                this.DebugSettings.EnableFrameRateCounter = true;
            }
#endif
            Frame rootFrame = Window.Current.Content as Frame;

            // Do not repeat app initialization when the Window already has content,
            // just ensure that the window is active.
            if (rootFrame == null)
            {
                // Create a Frame to act as the navigation context and navigate to the first page.
                rootFrame = new Frame();

                rootFrame.NavigationFailed += OnNavigationFailed;

                if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                    //TODO: Load state from previously suspended application.
                }

                Window.Current.Content = rootFrame;
            }

            InitializeAppleEmoji(rootFrame);

            if (e.PrelaunchActivated == false)
            {
                var authorizedForNotifications = false;
                if (rootFrame.Content == null)
                {
                    var authorized = IsCachedAuthorized();
                    var startPage = authorized ? typeof(AdaptiveShellPage) : typeof(Welcome);
                    authorizedForNotifications = authorized;
                    rootFrame.Navigate(startPage, e.Arguments);
                }
                else
                {
                    authorizedForNotifications = (rootFrame.Content as AdaptiveShellPage) != null;
                }

                Window.Current.Activate();

                if (authorizedForNotifications)
                {
                    var ignoredNotifications = InitializeNotificationsAfterActivationAsync();
                }
            }
        }

        protected override async void OnActivated(IActivatedEventArgs args)
        {
            ActiveChatService.SetForeground(true);
            CaptureUiDispatcher();
            var toastArgs = args as ToastNotificationActivatedEventArgs;
            if (toastArgs != null)
            {
                try
                {
                    await TelegramNotificationRuntime.HandleToastActionAsync(toastArgs.Argument, toastArgs.UserInput);
                }
                catch (Exception ex)
                {
                    SaveStartupError("Toast activation: " + ex.Message);
                }
            }

            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            InitializeAppleEmoji(rootFrame);

            if (rootFrame.Content == null)
            {
                var authorizedForNotifications = IsCachedAuthorized();
                var startPage = authorizedForNotifications ? typeof(AdaptiveShellPage) : typeof(Welcome);

                rootFrame.Navigate(startPage, toastArgs == null ? string.Empty : toastArgs.Argument);
                if (authorizedForNotifications)
                {
                    var ignoredNotifications = InitializeNotificationsAfterActivationAsync();
                }
            }

            Window.Current.Activate();
            try
            {
                if ((rootFrame.Content as AdaptiveShellPage) != null)
                    StartNotificationRuntimeForCurrentMode();
            }
            catch (Exception ex)
            {
                SaveStartupError("Activated poller: " + ex.Message);
            }
        }

        protected override async void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);
            var deferral = args.TaskInstance.GetDeferral();
            try
            {
                await TelegramNotificationBackgroundTask.RunAsync(args.TaskInstance);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void App_UnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            var message = "Unhandled: " + (e == null ? "" : e.Message);
            try { System.Diagnostics.Debug.WriteLine("APP_UNHANDLED " + message); }
            catch { }
            SaveStartupError(message);
            if (e != null)
                e.Handled = true;
        }

        private async void OnEnteredBackground(object sender, EnteredBackgroundEventArgs e)
        {
            ActiveChatService.SetForeground(false);
            var deferral = e.GetDeferral();
            try
            {
                TelegramService.Instance.PrepareForBackgroundNotifications();
                await TelegramContinuousNotificationPoller.EnterBackgroundAsync();
                var ignoredRegister = TelegramNotificationRegistrar.RegisterAsync();
                var ignoredTile = TelegramNotificationRegistrar.RegisterLiveTileAsync();
            }
            catch (Exception ex)
            {
                SaveStartupError("EnteredBackground: " + ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void OnLeavingBackground(object sender, LeavingBackgroundEventArgs e)
        {
            ActiveChatService.SetForeground(true);
            try
            {
                TelegramContinuousNotificationPoller.LeaveBackground();
                if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.Always)
                    await TelegramContinuousNotificationPoller.StartKeepAliveAsync();
                var rootFrame = Window.Current.Content as Frame;
                if (rootFrame != null && (rootFrame.Content as AdaptiveShellPage) != null)
                    StartNotificationRuntimeForCurrentMode();
            }
            catch (Exception ex)
            {
                SaveStartupError("LeavingBackground: " + ex.Message);
            }
        }

        private void OnResuming(object sender, object e)
        {
            ActiveChatService.SetForeground(true);
            try
            {
                if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.Always &&
                    !TelegramContinuousNotificationPoller.KeepAliveActive)
                {
                    var ignoredKeepAlive = TelegramContinuousNotificationPoller.StartKeepAliveAsync();
                }

                var rootFrame = Window.Current.Content as Frame;
                if (rootFrame != null && (rootFrame.Content as AdaptiveShellPage) != null)
                    StartNotificationRuntimeForCurrentMode();
            }
            catch (Exception ex)
            {
                SaveStartupError("Resuming: " + ex.Message);
            }
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails.
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            SaveStartupError("Navigation failed: " + (e == null || e.SourcePageType == null ? "" : e.SourcePageType.FullName));
        }

        /// <summary>
        /// Invoked when application execution is being suspended. Application state is saved
        /// without knowing whether the application will be terminated or resumed with the contents
        /// of memory still intact.
        /// </summary>
        /// <param name="sender">The source of the suspend request.</param>
        /// <param name="e">Details about the suspend request.</param>
        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                ActiveChatService.SetForeground(false);
                TelegramService.Instance.PrepareForBackgroundNotifications();
                await TelegramContinuousNotificationPoller.EnterBackgroundAsync();
                var ignoredRegister = TelegramNotificationRegistrar.RegisterAsync();
            }
            catch (Exception ex)
            {
                SaveStartupError("Suspending: " + ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private static void SaveStartupError(string message)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values["LastUnhandledException"] = message ?? string.Empty;
            }
            catch
            {
            }
        }

        private static void StartNotificationRuntimeForCurrentMode()
        {
            if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.None)
            {
                TelegramContinuousNotificationPoller.Stop();
                TelegramFixedSystemNotificationBridge.Stop();
                return;
            }

            TelegramContinuousNotificationPoller.Start();
            if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.FixedSystem)
            {
                var ignoredWns = TelegramFixedSystemNotificationBridge.RegisterAndStartAsync();
            }

            if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.Always &&
                !TelegramContinuousNotificationPoller.KeepAliveActive)
            {
                var ignoredKeepAlive = TelegramContinuousNotificationPoller.StartKeepAliveAsync();
            }
        }

        private static bool IsCachedAuthorized()
        {
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue("tdlib_authorized", out value) && value != null)
                    return Convert.ToBoolean(value);
            }
            catch
            {
            }

            return false;
        }

        private static async System.Threading.Tasks.Task InitializeNotificationsAfterActivationAsync()
        {
            try
            {
                StartNotificationRuntimeForCurrentMode();
                await TelegramNotificationRegistrar.RegisterAsync();
                await TelegramNotificationRegistrar.RegisterLiveTileAsync();
            }
            catch (Exception ex)
            {
                SaveStartupError("Notification init: " + ex.Message);
            }
        }

        private void InitializeAppleEmoji(Frame rootFrame)
        {
            if (rootFrame == null)
                return;

            if (!object.ReferenceEquals(_appleEmojiRootFrame, rootFrame))
            {
                if (_appleEmojiRootFrame != null)
                    _appleEmojiRootFrame.Navigated -= AppleEmojiRootFrame_Navigated;

                _appleEmojiRootFrame = rootFrame;
                _appleEmojiRootFrame.Navigated += AppleEmojiRootFrame_Navigated;
            }

            if (_appleEmojiFont == null)
            {
                _appleEmojiFont = CreateAppleEmojiFont(
                    "ms-appx:///Assets/Emoji/" + AppleEmojiFileName);
            }

            ApplyAppleEmojiFont(rootFrame, _appleEmojiFont);
        }

        private static FontFamily CreateAppleEmojiFont(string fontFileUri)
        {
            return new FontFamily("Segoe UI, " + fontFileUri + "#" + AppleEmojiFamilyName);
        }

        private void ApplyAppleEmojiFont(Frame rootFrame, FontFamily font)
        {
            if (rootFrame == null || font == null)
                return;

            _appleEmojiFont = font;

            try
            {
                Resources["AppleEmojiFontFamily"] = font;
                Resources["FluentEmojiFontFamily"] = font;
                Resources["ContentControlThemeFontFamily"] = font;
            }
            catch
            {
            }

            if (!IsIconFont(rootFrame.FontFamily))
                rootFrame.FontFamily = font;

            ApplyAppleEmojiFontToVisualTree(rootFrame, font);
        }

        private void AppleEmojiRootFrame_Navigated(object sender, NavigationEventArgs e)
        {
            var frame = sender as Frame;
            if (frame == null || _appleEmojiFont == null)
                return;

            var ignored = frame.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, delegate
            {
                ApplyAppleEmojiFontToVisualTree(frame, _appleEmojiFont);
            });
        }

        private static void ApplyAppleEmojiFontToVisualTree(DependencyObject root, FontFamily font)
        {
            if (root == null || font == null)
                return;

            var textBlock = root as TextBlock;
            if (textBlock != null)
            {
                if (!IsIconFont(textBlock.FontFamily))
                    textBlock.FontFamily = font;
            }
            else
            {
                var richTextBlock = root as RichTextBlock;
                if (richTextBlock != null)
                {
                    if (!IsIconFont(richTextBlock.FontFamily))
                        richTextBlock.FontFamily = font;
                }
                else
                {
                    var control = root as Control;
                    if (control != null && !IsIconFont(control.FontFamily))
                        control.FontFamily = font;
                }
            }

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
                ApplyAppleEmojiFontToVisualTree(VisualTreeHelper.GetChild(root, i), font);
        }

        private static bool IsIconFont(FontFamily font)
        {
            var source = font == null ? string.Empty : font.Source;
            if (string.IsNullOrWhiteSpace(source))
                return false;

            return source.IndexOf("Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   source.IndexOf("Segoe UI Symbol", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   source.IndexOf("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase) >= 0;
        }

    }
}
