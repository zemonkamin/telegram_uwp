using System;
using Telegram.Notifications;
using Telegram.Services;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Telegram
{
    public sealed partial class SettingsPage : Page
    {
        private bool _backRequestedAttached;
        private bool _refreshingSettings;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += SettingsPage_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ConfigureSystemBackButton(true);
            RefreshProxyState();
            RefreshAppSettingsState();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ConfigureSystemBackButton(false);
            base.OnNavigatedFrom(e);
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshProxyState();
            RefreshAppSettingsState();
        }

        private async void NotificationModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_refreshingSettings) return;

            try
            {
                var mode = GetSelectedNotificationMode();
                TelegramAppSettings.NotificationMode = mode;

                if (mode == TelegramNotificationMode.None)
                {
                    TelegramNotificationRegistrar.Disable();
                    TelegramContinuousNotificationPoller.Stop();
                    UpdateNotificationStatusText();
                }
                else
                {
                    NotificationStatusText.Text = mode == TelegramNotificationMode.Always
                        ? "Enabling always-on background notifications..."
                        : (mode == TelegramNotificationMode.FixedSystem
                            ? "Enabling fixed system notifications through WNS..."
                            : "Enabling periodic notifications...");
                    await TelegramNotificationRegistrar.RegisterAndStartAsync();
                    if (mode == TelegramNotificationMode.Periodic || mode == TelegramNotificationMode.FixedSystem)
                        TelegramContinuousNotificationPoller.Start();
                    UpdateNotificationStatusText();
                }
            }
            catch (Exception ex)
            {
                NotificationStatusText.Text = "Notification setting error: " + ex.Message;
            }
        }

        private void SaveMessageBatchSizeButton_Click(object sender, RoutedEventArgs e)
        {
            int size;
            if (!int.TryParse(MessageBatchSizeBox.Text, out size))
            {
                MessageBatchSizeStatusText.Text = "Enter a number.";
                return;
            }

            size = TelegramAppSettings.NormalizeMessageBatchSize(size);
            TelegramAppSettings.ChatPageMessageBatchSize = size;
            MessageBatchSizeBox.Text = size.ToString();
            MessageBatchSizeStatusText.Text = "ChatPage will load " + size.ToString() + " messages per request.";
        }

        private void AutoDownloadCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_refreshingSettings) return;

            TelegramAppSettings.ChatAutoDownloadPhotosEnabled = AutoDownloadPhotosCheckBox.IsChecked == true;
            TelegramAppSettings.ChatAutoDownloadGifsEnabled = AutoDownloadGifsCheckBox.IsChecked == true;
            TelegramAppSettings.ChatAutoDownloadStickersEnabled = AutoDownloadStickersCheckBox.IsChecked == true;
            TelegramAppSettings.ChatAutoDownloadVideosEnabled = AutoDownloadVideosCheckBox.IsChecked == true;
            TelegramAppSettings.ChatAutoDownloadOtherEnabled = AutoDownloadOtherCheckBox.IsChecked == true;
            UpdateAutoDownloadStatusText();
        }

        private void SaveChatsDisplayButton_Click(object sender, RoutedEventArgs e)
        {
            SaveChatsDisplaySettings(true);
        }

        private void ChatsDisplayCountBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_refreshingSettings) return;
            SaveChatsDisplaySettings(false);
        }

        private void ChatsShowAllImmediatelySwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_refreshingSettings) return;

            TelegramAppSettings.ChatsShowAllImmediately = ChatsShowAllImmediatelySwitch.IsOn;
            Chats.NotifyChatDisplaySettingsChanged();
            UpdateChatsDisplayStatusText();
        }

        private void SaveChatsDisplaySettings(bool showInvalidMessage)
        {
            int initialCount;
            int incrementalCount;
            if (!int.TryParse(ChatsInitialDisplayCountBox.Text, out initialCount) ||
                !int.TryParse(ChatsIncrementalDisplayCountBox.Text, out incrementalCount))
            {
                if (showInvalidMessage)
                    ChatsDisplayStatusText.Text = "Enter numbers for both chat list limits.";
                return;
            }

            initialCount = TelegramAppSettings.NormalizeChatsDisplayCount(initialCount);
            incrementalCount = TelegramAppSettings.NormalizeChatsDisplayCount(incrementalCount);
            TelegramAppSettings.ChatsInitialDisplayCount = initialCount;
            TelegramAppSettings.ChatsIncrementalDisplayCount = incrementalCount;
            Chats.NotifyChatDisplaySettingsChanged();
            if (showInvalidMessage)
            {
                ChatsInitialDisplayCountBox.Text = initialCount.ToString();
                ChatsIncrementalDisplayCountBox.Text = incrementalCount.ToString();
            }
            UpdateChatsDisplayStatusText();
        }

        private void ContactSyncPromptSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_refreshingSettings) return;

            TelegramAppSettings.ContactSyncPromptEnabled = ContactSyncPromptSwitch.IsOn;
            ContactSyncPromptStatusText.Text = ContactSyncPromptSwitch.IsOn
                ? "Contacts will ask before adding missing Telegram contacts to Windows."
                : "Contacts will not offer to add Telegram contacts to Windows.";
        }

        private void GlassEffectSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_refreshingSettings) return;

            TelegramAppSettings.GlassEffectEnabled = GlassEffectSwitch.IsOn;
            GlassEffectStatusText.Text = GlassEffectSwitch.IsOn
                ? "Glass blur is enabled for profiles and Chats app bar."
                : "Glass blur is disabled. Profiles use the original dark gradient.";
        }

        private async void LiveTileSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_refreshingSettings) return;

            try
            {
                TelegramAppSettings.LiveTileEnabled = LiveTileSwitch.IsOn;
                if (LiveTileSwitch.IsOn)
                {
                    LiveTileStatusText.Text = "Enabling live tile...";
                    await TelegramNotificationRegistrar.RegisterLiveTileAsync();
                }
                else
                {
                    TelegramNotificationRegistrar.DisableLiveTile();
                }

                UpdateLiveTileStatusText();
            }
            catch (Exception ex)
            {
                LiveTileStatusText.Text = "Live tile setting error: " + ex.Message;
            }
        }

        private void ProxyButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame != null)
                Frame.Navigate(typeof(ProxyPage));
        }

        private async void ResetCacheButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ResetCacheButton.IsEnabled = false;
                CacheStatusText.Text = "Resetting cache...";
                TelegramService.Instance.ClearDialogsCache();
                await Chats.ClearCacheAsync();
                await ClearStickerCacheAsync();
                ArchivePage.ClearCache();
                SearchPage.ClearCache();
                ContactsPage.ClearCache();
                CacheStatusText.Text = "Cache was reset. Chats, archive, search, contacts and stickers will reload from Telegram.";
            }
            catch (Exception ex)
            {
                CacheStatusText.Text = "Cache reset error: " + ex.Message;
            }
            finally
            {
                ResetCacheButton.IsEnabled = true;
            }
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LogoutButton.IsEnabled = false;
                LogoutStatusText.Text = "Signing out...";

                TelegramService.Instance.ClearDialogsCache();
                await Chats.ClearCacheAsync();
                ArchivePage.ClearCache();
                SearchPage.ClearCache();
                ContactsPage.ClearCache();
                await TelegramService.Instance.ResetSessionAsync();

                var rootFrame = Window.Current == null ? null : Window.Current.Content as Frame;
                if (rootFrame != null)
                {
                    rootFrame.Navigate(typeof(Welcome));
                    rootFrame.BackStack.Clear();
                    return;
                }

                if (Frame != null)
                {
                    Frame.Navigate(typeof(Welcome));
                    Frame.BackStack.Clear();
                }
            }
            catch (Exception ex)
            {
                LogoutStatusText.Text = "Sign out error: " + ex.Message;
                LogoutButton.IsEnabled = true;
            }
        }

        private async System.Threading.Tasks.Task ClearStickerCacheAsync()
        {
            try
            {
                var mediaFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("chat_media", CreationCollisionOption.OpenIfExists);
                var items = await mediaFolder.GetItemsAsync();
                for (var i = 0; i < items.Count; i++)
                {
                    var file = items[i] as StorageFile;
                    if (file == null || !IsStickerCacheFile(file.Name)) continue;
                    try
                    {
                        await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private bool IsStickerCacheFile(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.StartsWith("sticker_", StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshProxyState()
        {
            if (ProxyStore.Enabled)
            {
                var profiles = ProxyStore.LoadProfiles();
                var id = ProxyStore.SelectedId;
                ProxyProfile selected = null;
                for (var i = 0; i < profiles.Count; i++)
                {
                    if (profiles[i].Id == id)
                    {
                        selected = profiles[i];
                        break;
                    }
                }
                if (selected == null && profiles.Count > 0)
                    selected = profiles[0];

                ProxySubtitleText.Text = selected != null
                    ? ProxyProfile.ModeLabel(selected.Mode) + " · " + selected.Title
                    : "Proxy enabled";
                return;
            }

            ProxySubtitleText.Text = "System proxy / VPN";
        }

        private void RefreshAppSettingsState()
        {
            _refreshingSettings = true;
            try
            {
                var notificationMode = TelegramAppSettings.NotificationMode;
                NotificationsPeriodicRadio.IsChecked = notificationMode == TelegramNotificationMode.Periodic;
                NotificationsAlwaysRadio.IsChecked = notificationMode == TelegramNotificationMode.Always;
                NotificationsFixedSystemRadio.IsEnabled = TelegramAppSettings.FixedSystemNotificationsAvailable;
                NotificationsFixedSystemRadio.IsChecked = notificationMode == TelegramNotificationMode.FixedSystem;
                NotificationsNoneRadio.IsChecked = notificationMode == TelegramNotificationMode.None;
                UpdateNotificationStatusText();
                ContactSyncPromptSwitch.IsOn = TelegramAppSettings.ContactSyncPromptEnabled;
                ContactSyncPromptStatusText.Text = ContactSyncPromptSwitch.IsOn
                    ? "Contacts will ask before adding missing Telegram contacts to Windows."
                    : "Contacts will not offer to add Telegram contacts to Windows.";
                GlassEffectSwitch.IsOn = TelegramAppSettings.GlassEffectEnabled;
                GlassEffectStatusText.Text = GlassEffectSwitch.IsOn
                    ? "Glass blur is enabled for profiles and Chats app bar."
                    : "Glass blur is disabled. Profiles use the original dark gradient.";
                MessageBatchSizeBox.Text = TelegramAppSettings.ChatPageMessageBatchSize.ToString();
                MessageBatchSizeStatusText.Text = "ChatPage will load " + TelegramAppSettings.ChatPageMessageBatchSize.ToString() + " messages per request.";
                AutoDownloadPhotosCheckBox.IsChecked = TelegramAppSettings.ChatAutoDownloadPhotosEnabled;
                AutoDownloadGifsCheckBox.IsChecked = TelegramAppSettings.ChatAutoDownloadGifsEnabled;
                AutoDownloadStickersCheckBox.IsChecked = TelegramAppSettings.ChatAutoDownloadStickersEnabled;
                AutoDownloadVideosCheckBox.IsChecked = TelegramAppSettings.ChatAutoDownloadVideosEnabled;
                AutoDownloadOtherCheckBox.IsChecked = TelegramAppSettings.ChatAutoDownloadOtherEnabled;
                UpdateAutoDownloadStatusText();
                ChatsShowAllImmediatelySwitch.IsOn = TelegramAppSettings.ChatsShowAllImmediately;
                ChatsInitialDisplayCountBox.Text = TelegramAppSettings.ChatsInitialDisplayCount.ToString();
                ChatsIncrementalDisplayCountBox.Text = TelegramAppSettings.ChatsIncrementalDisplayCount.ToString();
                UpdateChatsDisplayStatusText();
                LiveTileSwitch.IsOn = TelegramAppSettings.LiveTileEnabled;
                UpdateLiveTileStatusText();
                WallpaperDimmingSlider.Value = TelegramAppSettings.WallpaperDimming;
                UpdateWallpaperDimmingStatus();
            }
            finally
            {
                _refreshingSettings = false;
            }
        }

        private void WallpaperDimmingSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_refreshingSettings) return;
            TelegramAppSettings.WallpaperDimming = (int)WallpaperDimmingSlider.Value;
            UpdateWallpaperDimmingStatus();
        }

        private void UpdateWallpaperDimmingStatus()
        {
            WallpaperDimmingStatusText.Text = "Custom chat wallpapers are dimmed by " +
                TelegramAppSettings.WallpaperDimming.ToString() + "%.";
        }

        private void UpdateAutoDownloadStatusText()
        {
            if (!TelegramAppSettings.AnyChatAutoDownloadEnabled)
            {
                AutoDownloadStatusText.Text = "Auto-download is off. ChatPage opens faster and media loads only when opened.";
                return;
            }

            var text = "Auto-download enabled for: ";
            var separator = string.Empty;
            AppendAutoDownloadStatusPart(ref text, ref separator, TelegramAppSettings.ChatAutoDownloadPhotosEnabled, "photos");
            AppendAutoDownloadStatusPart(ref text, ref separator, TelegramAppSettings.ChatAutoDownloadGifsEnabled, "GIFs");
            AppendAutoDownloadStatusPart(ref text, ref separator, TelegramAppSettings.ChatAutoDownloadStickersEnabled, "stickers");
            AppendAutoDownloadStatusPart(ref text, ref separator, TelegramAppSettings.ChatAutoDownloadVideosEnabled, "videos");
            AppendAutoDownloadStatusPart(ref text, ref separator, TelegramAppSettings.ChatAutoDownloadOtherEnabled, "other media except files");
            if (TelegramAppSettings.ChatAutoDownloadVideosEnabled)
                text += ". Video auto-download can slow ChatPage and use more traffic.";
            else
                text += ".";
            AutoDownloadStatusText.Text = text;
        }

        private void AppendAutoDownloadStatusPart(ref string text, ref string separator, bool enabled, string name)
        {
            if (!enabled) return;
            text += separator + name;
            separator = ", ";
        }

        private void UpdateChatsDisplayStatusText()
        {
            if (TelegramAppSettings.ChatsShowAllImmediately)
            {
                ChatsDisplayStatusText.Text = "Chats will show all loaded items immediately. Incremental display limits are ignored.";
                return;
            }

            ChatsDisplayStatusText.Text = "Chats show " + TelegramAppSettings.ChatsInitialDisplayCount.ToString() +
                " first, then add " + TelegramAppSettings.ChatsIncrementalDisplayCount.ToString() + " near the bottom.";
        }

        private void UpdateLiveTileStatusText()
        {
            if (!TelegramAppSettings.LiveTileEnabled)
            {
                LiveTileStatusText.Text = "Live tile is disabled.";
                return;
            }

            var status = TelegramNotificationRegistrar.LastLiveTileStatus;
            LiveTileStatusText.Text = "Live tile shows the latest notification with the chat image in the background. " + status;
        }

        private TelegramNotificationMode GetSelectedNotificationMode()
        {
            if (NotificationsAlwaysRadio != null && NotificationsAlwaysRadio.IsChecked == true)
                return TelegramNotificationMode.Always;
            if (TelegramAppSettings.FixedSystemNotificationsAvailable &&
                NotificationsFixedSystemRadio != null &&
                NotificationsFixedSystemRadio.IsChecked == true)
                return TelegramNotificationMode.FixedSystem;
            if (NotificationsNoneRadio != null && NotificationsNoneRadio.IsChecked == true)
                return TelegramNotificationMode.None;
            return TelegramNotificationMode.Periodic;
        }

        private void UpdateNotificationStatusText()
        {
            var mode = TelegramAppSettings.NotificationMode;
            if (mode == TelegramNotificationMode.None)
            {
                NotificationStatusText.Text = "Notifications are fully disabled. Background checks and toast replies are off.";
                return;
            }

            if (mode == TelegramNotificationMode.Always)
            {
                var keepAlive = TelegramContinuousNotificationPoller.LastKeepAliveStatus;
                if (string.IsNullOrEmpty(keepAlive))
                    keepAlive = TelegramContinuousNotificationPoller.KeepAliveActive
                        ? "Always-on background session is active."
                        : "Always-on background session will start when Windows allows location tracking.";
                NotificationStatusText.Text = "Always-on notifications are enabled. Periodic wake-up tasks are disabled. " + keepAlive;
                return;
            }

            if (mode == TelegramNotificationMode.FixedSystem)
            {
                NotificationStatusText.Text = "Fixed system notifications are enabled through WNS. " +
                    TelegramFixedSystemNotificationBridge.LastStatus;
                return;
            }

            NotificationStatusText.Text = "Periodic notifications are enabled. " + TelegramNotificationRegistrar.LastStatus;
        }

        private void ConfigureSystemBackButton(bool enabled)
        {
            var navigation = SystemNavigationManager.GetForCurrentView();
            if (navigation == null) return;
            navigation.AppViewBackButtonVisibility = enabled ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
            if (enabled && !_backRequestedAttached)
            {
                navigation.BackRequested += SystemNavigation_BackRequested;
                _backRequestedAttached = true;
            }
            else if (!enabled && _backRequestedAttached)
            {
                navigation.BackRequested -= SystemNavigation_BackRequested;
                _backRequestedAttached = false;
            }
        }

        private void SystemNavigation_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }
    }
}
