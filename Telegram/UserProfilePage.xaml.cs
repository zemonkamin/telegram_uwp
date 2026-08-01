using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using Telegram.Models;
using Telegram.Services;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;

namespace Telegram
{
    public sealed partial class UserProfilePage : Page
    {
        private const double DefaultHeaderHeight = 142;
        private const double FallbackMaxHeaderStretch = 260;

        private ChatViewModel _chat;
        private bool _backRequestedAttached;
        private bool _pointerDown;
        private bool _stretchingHeader;
        private Point _pointerStart;
        private double _pointerStartHeaderHeight;
        private double _lastPointerDelta;
        private double _lastHeaderWidth;
        private double _snapTargetHeight;
        private DispatcherTimer _snapTimer;
        private bool _historyLoadedForTabs;
        private bool _historyLoadingForTabs;
        private bool _historyFullyLoadedForTabs;
        private int _oldestLoadedMessageId;
        private ChatViewModel _profileChannel;
        private List<ProfilePhotoViewModel> _profilePhotos = new List<ProfilePhotoViewModel>();
        private int _profilePhotoIndex;
        private bool _photoSwipeHandled;
        private List<ChatMessageViewModel> _tabMessages;
        private ObservableCollection<ProfileContentItem> _contentItems;
        private readonly List<ImageSource> _mediaOverlayImages = new List<ImageSource>();
        private readonly List<ProfileContentItem> _mediaOverlayItems = new List<ProfileContentItem>();
        private readonly List<Ellipse> _mediaOverlayIndicators = new List<Ellipse>();
        private string _selectedTab = "info";
        private Brush _collapsedSelectedTabBrush;
        private Brush _collapsedRegularTabBrush;
        private readonly Brush _expandedHeaderTabBrush = new SolidColorBrush(Windows.UI.Colors.White);

        public UserProfilePage()
        {
            InitializeComponent();
            _collapsedSelectedTabBrush = InfoTab.Foreground;
            _collapsedRegularTabBrush = MediaTab.Foreground;
            _tabMessages = new List<ChatMessageViewModel>();
            _contentItems = new ObservableCollection<ProfileContentItem>();
            ContentList.ItemsSource = _contentItems;
            MediaGrid.ItemsSource = _contentItems;
            _snapTimer = new DispatcherTimer();
            _snapTimer.Interval = TimeSpan.FromMilliseconds(16);
            _snapTimer.Tick += SnapTimer_Tick;
            UpdateHeaderStretchVisuals();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ConfigureSystemBackButton(true);
            ApplyHeaderGlassSetting();
            _chat = e.Parameter as ChatViewModel;
            ApplyChat(_chat);

            if (_chat == null)
            {
                SetLoading(false, "Profile is unavailable.");
                return;
            }

            try
            {
                SetLoading(true, "Loading profile...");
                var profile = await TelegramService.Instance.GetUserProfileAsync(_chat);
                ApplyProfile(profile);
                await LoadProfilePhotosAsync();
                await LoadProfileChannelPreviewAsync();
                await LoadHistoryForTabsAsync();
                await SelectTabAsync("info");
                SetLoading(false, string.Empty);
            }
            catch (Exception ex)
            {
                SetLoading(false, "Profile error: " + ex.Message);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StatusBarLoadingIndicator.Hide();
            StopHeaderSnap();
            ConfigureSystemBackButton(false);
            base.OnNavigatedFrom(e);
        }

        private void ConfigureSystemBackButton(bool visible)
        {
            var manager = SystemNavigationManager.GetForCurrentView();
            if (visible)
            {
                if (!_backRequestedAttached)
                {
                    manager.BackRequested += SystemBackButton_BackRequested;
                    _backRequestedAttached = true;
                }

                manager.AppViewBackButtonVisibility = AppViewBackButtonVisibility.Visible;
            }
            else
            {
                if (_backRequestedAttached)
                {
                    manager.BackRequested -= SystemBackButton_BackRequested;
                    _backRequestedAttached = false;
                }

                manager.AppViewBackButtonVisibility = AppViewBackButtonVisibility.Collapsed;
            }
        }

        private void SystemBackButton_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (Frame == null) return;
            e.Handled = true;
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(Chats));
        }

        private void ApplyChat(ChatViewModel chat)
        {
            if (chat == null) return;
            ProfileTitle.Text = string.IsNullOrEmpty(chat.Title) ? "Profile" : chat.Title;
            ProfileSubtitle.Text = chat.SubtitleText;
            HeaderInitials.Text = string.IsNullOrEmpty(chat.IconText) ? "?" : chat.IconText;
            CompactInitials.Text = string.IsNullOrEmpty(chat.IconText) ? "?" : chat.IconText;
            ApplyChannelCard(chat.ProfileChannel);
            ApplyAvatar(chat.AvatarUri);
            SetFallbackProfilePhotos(chat.AvatarUri);
        }

        private void ApplyProfile(UserProfileViewModel profile)
        {
            if (profile == null)
            {
                InfoList.ItemsSource = null;
                return;
            }

            ProfileTitle.Text = string.IsNullOrEmpty(profile.Title) ? "Profile" : profile.Title;
            ProfileSubtitle.Text = profile.Subtitle;
            HeaderInitials.Text = string.IsNullOrEmpty(profile.Initials) ? "?" : profile.Initials;
            CompactInitials.Text = string.IsNullOrEmpty(profile.Initials) ? "?" : profile.Initials;
            SectionTitle.Text = profile.SectionTitle;
            SectionCounter.Text = profile.CounterText;
            SectionCounter.Visibility = profile.CounterVisibility;
            FormatPhoneRows(profile);
            InfoList.ItemsSource = profile.Rows;
            ApplyChannelCard(profile.Chat == null ? null : profile.Chat.ProfileChannel);
            ApplyAvatar(profile.AvatarUri);
            SetFallbackProfilePhotos(profile.AvatarUri);
        }

        // Pretty-print the info rows: phone as "+<code> <grouped local>" (area code in
        // parentheses for +1), and the username prefixed with "@".
        private static void FormatPhoneRows(UserProfileViewModel profile)
        {
            if (profile == null || profile.Rows == null) return;
            for (var i = 0; i < profile.Rows.Count; i++)
            {
                var row = profile.Rows[i];
                if (row == null) continue;

                if (string.Equals(row.Label, "Phone", StringComparison.OrdinalIgnoreCase))
                    row.Value = FormatPhoneDisplay(row.Value);
                else if (string.Equals(row.Label, "Username", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(row.Value) && !row.Value.StartsWith("@", StringComparison.Ordinal))
                    row.Value = "@" + row.Value;
            }
        }

        private static string FormatPhoneDisplay(string raw)
        {
            var digits = PhoneDigitsOnly(raw);
            if (digits.Length == 0) return raw;

            var code = MatchCountryCode(digits);
            if (!string.IsNullOrEmpty(code) && digits.Length > code.Length)
            {
                var local = digits.Substring(code.Length);
                string localFormatted;
                if (code == "1" && local.Length > 3)
                    localFormatted = "(" + local.Substring(0, 3) + ") " + GroupPhoneDigits(local.Substring(3));
                else
                    localFormatted = GroupPhoneDigits(local);
                return "+" + code + " " + localFormatted;
            }

            return "+" + GroupPhoneDigits(digits);
        }

        // Longest-prefix match against the known dialing codes.
        private static string MatchCountryCode(string digits)
        {
            string best = null;
            var all = CountryCatalog.All;
            for (var i = 0; i < all.Count; i++)
            {
                var code = PhoneDigitsOnly(all[i].PhoneCode);
                if (code.Length == 0) continue;
                if (digits.StartsWith(code, StringComparison.Ordinal) && (best == null || code.Length > best.Length))
                    best = code;
            }
            return best;
        }

        private static string PhoneDigitsOnly(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
                if (char.IsDigit(value[i])) sb.Append(value[i]);
            return sb.ToString();
        }

        // Groups digits left-to-right in blocks of 3, letting the last block hold 2-4
        // digits so there is never a lone trailing digit.
        private static string GroupPhoneDigits(string digits)
        {
            if (string.IsNullOrEmpty(digits)) return string.Empty;

            var sb = new StringBuilder();
            var i = 0;
            while (digits.Length - i > 4)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(digits.Substring(i, 3));
                i += 3;
            }
            if (i < digits.Length)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(digits.Substring(i));
            }
            return sb.ToString();
        }

        private void ApplyAvatar(string avatarUri)
        {
            if (string.IsNullOrEmpty(avatarUri))
            {
                HeaderPhoto.Opacity = 0;
                HeaderPhoto.Source = null;
                HeaderFallback.Visibility = Visibility.Visible;
                CompactAvatarImage.Opacity = 0;
                CompactAvatarBrush.ImageSource = null;
                CompactInitials.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                var image = new BitmapImage(new Uri(avatarUri));
                HeaderPhoto.Source = image;
                CompactAvatarBrush.ImageSource = image;
                HeaderPhoto.Opacity = 1;
                CompactAvatarImage.Opacity = 1;
                HeaderFallback.Visibility = Visibility.Collapsed;
                CompactInitials.Visibility = Visibility.Collapsed;
            }
            catch
            {
                HeaderPhoto.Opacity = 0;
                HeaderPhoto.Source = null;
                HeaderFallback.Visibility = Visibility.Visible;
                CompactAvatarImage.Opacity = 0;
                CompactAvatarBrush.ImageSource = null;
                CompactInitials.Visibility = Visibility.Visible;
            }
        }

        private async System.Threading.Tasks.Task LoadProfilePhotosAsync()
        {
            if (_chat == null) return;
            try
            {
                var photos = await TelegramService.Instance.GetProfilePhotosAsync(_chat, 8);
                if (photos != null && photos.Count > 0)
                    SetProfilePhotos(photos);
            }
            catch
            {
            }
        }

        private void SetFallbackProfilePhotos(string avatarUri)
        {
            if (_profilePhotos != null && _profilePhotos.Count > 1)
                return;

            _profilePhotos = new List<ProfilePhotoViewModel>();
            if (!string.IsNullOrEmpty(avatarUri))
                _profilePhotos.Add(new ProfilePhotoViewModel { Uri = avatarUri });
            _profilePhotoIndex = 0;
            UpdateProfilePhotoIndicators();
        }

        private void SetProfilePhotos(List<ProfilePhotoViewModel> photos)
        {
            var merged = new List<ProfilePhotoViewModel>();
            var keepFallback = true;
            if (_chat != null && _chat.AvatarPhotoId != 0 && photos != null && photos.Count > 0 && photos[0] != null && photos[0].PhotoId == _chat.AvatarPhotoId)
                keepFallback = false;
            if (keepFallback && _profilePhotos != null && _profilePhotos.Count > 0)
                AddProfilePhotoIfMissing(merged, _profilePhotos[0]);

            if (photos != null)
            {
                for (var i = 0; i < photos.Count; i++)
                    AddProfilePhotoIfMissing(merged, photos[i]);
            }

            _profilePhotos = merged;
            _profilePhotoIndex = 0;
            ApplySelectedProfilePhoto();
        }

        private void AddProfilePhotoIfMissing(List<ProfilePhotoViewModel> target, ProfilePhotoViewModel photo)
        {
            if (target == null || photo == null || string.IsNullOrEmpty(photo.Uri)) return;
            for (var i = 0; i < target.Count; i++)
            {
                var existing = target[i];
                if (existing == null) continue;
                if (photo.PhotoId != 0 && existing.PhotoId == photo.PhotoId) return;
                if (!string.IsNullOrEmpty(existing.Uri) && string.Equals(existing.Uri, photo.Uri, StringComparison.OrdinalIgnoreCase)) return;
            }
            target.Add(photo);
        }

        private void ApplySelectedProfilePhoto()
        {
            if (_profilePhotos != null && _profilePhotos.Count > 0)
            {
                if (_profilePhotoIndex < 0) _profilePhotoIndex = 0;
                if (_profilePhotoIndex >= _profilePhotos.Count) _profilePhotoIndex = _profilePhotos.Count - 1;
                ApplyAvatar(_profilePhotos[_profilePhotoIndex].Uri);
            }
            UpdateProfilePhotoIndicators();
        }

        private void MoveProfilePhoto(int delta)
        {
            if (_profilePhotos == null || _profilePhotos.Count < 2) return;
            var next = _profilePhotoIndex + delta;
            if (next < 0) next = _profilePhotos.Count - 1;
            if (next >= _profilePhotos.Count) next = 0;
            if (next == _profilePhotoIndex) return;
            _profilePhotoIndex = next;
            ApplySelectedProfilePhoto();
        }

        private void UpdateProfilePhotoIndicators()
        {
            if (HeaderPhotoIndicators == null) return;
            HeaderPhotoIndicators.Children.Clear();
            HeaderPhotoIndicators.ColumnDefinitions.Clear();

            var count = _profilePhotos == null ? 0 : _profilePhotos.Count;
            HeaderPhotoIndicators.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (count <= 1) return;

            for (var i = 0; i < count; i++)
            {
                HeaderPhotoIndicators.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var bar = new Border
                {
                    Margin = new Thickness(2, 0, 2, 0),
                    CornerRadius = new CornerRadius(0),
                    Background = new SolidColorBrush(i == _profilePhotoIndex
                        ? Windows.UI.Colors.White
                        : Windows.UI.Color.FromArgb(110, 255, 255, 255))
                };
                Grid.SetColumn(bar, i);
                HeaderPhotoIndicators.Children.Add(bar);
            }
        }

        private void ApplyChannelCard(ChatViewModel chat)
        {
            if (chat == null || string.IsNullOrEmpty(chat.Title))
            {
                _profileChannel = null;
                ChannelPanel.Visibility = Visibility.Collapsed;
                return;
            }

            _profileChannel = chat;
            ChannelPanel.Visibility = Visibility.Visible;
            ChannelTitleText.Text = chat.Title;
            ChannelSubtitleText.Text = ShortenPreview(string.IsNullOrEmpty(chat.LastMessage) ? chat.SubtitleText : chat.LastMessage, 44);
            ChannelTimeText.Text = chat.LastTimeText;
            ChannelInitials.Text = string.IsNullOrEmpty(chat.IconText) ? "?" : chat.IconText;
            ChannelCounterText.Text = chat.SubscriberCount > 0
                ? ChatViewModel.FormatCount(chat.SubscriberCount).ToUpperInvariant() + " SUBSCRIBERS"
                : string.Empty;
            ApplyChannelAvatar(chat.AvatarUri);
        }

        private void ApplyChannelAvatar(string avatarUri)
        {
            if (string.IsNullOrEmpty(avatarUri))
            {
                ChannelAvatarImage.Opacity = 0;
                ChannelAvatarBrush.ImageSource = null;
                ChannelInitials.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                ChannelAvatarBrush.ImageSource = new BitmapImage(new Uri(avatarUri));
                ChannelAvatarImage.Opacity = 1;
                ChannelInitials.Visibility = Visibility.Collapsed;
            }
            catch
            {
                ChannelAvatarImage.Opacity = 0;
                ChannelAvatarBrush.ImageSource = null;
                ChannelInitials.Visibility = Visibility.Visible;
            }
        }

        private async System.Threading.Tasks.Task LoadProfileChannelPreviewAsync()
        {
            if (_profileChannel == null) return;

            try
            {
                var messages = await TelegramService.Instance.GetHistoryAsync(_profileChannel, 1);
                var latest = GetLatestMessage(messages);
                if (latest == null) return;

                _profileChannel.LastMessage = BuildMessagePreview(latest);
                _profileChannel.LastMessageDate = latest.Date;
                _profileChannel.LastMessageIsOutgoing = latest.IsOutgoing;
                ApplyChannelCard(_profileChannel);
            }
            catch
            {
            }
        }

        private ChatMessageViewModel GetLatestMessage(List<ChatMessageViewModel> messages)
        {
            if (messages == null || messages.Count == 0) return null;
            ChatMessageViewModel latest = null;
            for (var i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                if (message == null) continue;
                if (latest == null || message.Date > latest.Date || (message.Date == latest.Date && message.Id > latest.Id))
                    latest = message;
            }
            return latest;
        }

        private string BuildMessagePreview(ChatMessageViewModel message)
        {
            if (message == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(message.Text)) return ShortenPreview(message.Text, 44);
            var kind = NormalizeMediaKind(message.MediaKind, message.MediaMimeType, message.MediaFileName);
            return ShortenPreview(TitleForKind(kind), 44);
        }

        private string ShortenPreview(string text, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
                text = text.Replace("  ", " ");
            if (maxLength > 3 && text.Length > maxLength)
                return text.Substring(0, maxLength - 3) + "...";
            return text;
        }

        private void SetLoading(bool active, string text)
        {
            StatusBarLoadingIndicator.SetActive(active, TopLoadingBar);
            LoadingText.Text = text ?? string.Empty;
            LoadingText.Visibility = !active && !string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void InfoTab_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await SelectTabAsync("info");
        }

        private async void MediaTab_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await SelectTabAsync("media");
        }

        private async void FilesTab_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await SelectTabAsync("files");
        }

        private async void AudioTab_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await SelectTabAsync("audio");
        }

        private async System.Threading.Tasks.Task SelectTabAsync(string tab)
        {
            _selectedTab = string.IsNullOrEmpty(tab) ? "info" : tab;
            UpdateTabVisuals();

            if (_selectedTab == "info")
            {
                InfoPanel.Visibility = Visibility.Visible;
                ContentList.Visibility = Visibility.Collapsed;
                MediaGrid.Visibility = Visibility.Collapsed;
                LoadMoreContentButton.Visibility = Visibility.Collapsed;
                EmptyContentText.Visibility = Visibility.Collapsed;
                return;
            }

            InfoPanel.Visibility = Visibility.Collapsed;
            ContentList.Visibility = _selectedTab == "media" ? Visibility.Collapsed : Visibility.Visible;
            MediaGrid.Visibility = _selectedTab == "media" ? Visibility.Visible : Visibility.Collapsed;
            await LoadHistoryForTabsAsync();
            FillContentTab(_selectedTab);
            await EnsureTabHasServerContentAsync(_selectedTab);
            FillContentTab(_selectedTab);
            UpdateLoadMoreButton();
        }

        private void UpdateTabVisuals()
        {
            SetTabOpacity(InfoTab, _selectedTab == "info");
            SetTabOpacity(MediaTab, _selectedTab == "media");
            SetTabOpacity(FilesTab, _selectedTab == "files");
            SetTabOpacity(AudioTab, _selectedTab == "audio");
        }

        private void SetTabOpacity(TextBlock tab, bool selected)
        {
            if (tab != null) tab.Opacity = selected ? 1.0 : 0.68;
        }

        private async System.Threading.Tasks.Task LoadHistoryForTabsAsync()
        {
            if (_historyLoadedForTabs || _historyLoadingForTabs || _chat == null) return;
            _historyLoadingForTabs = true;
            try
            {
                var messages = await TelegramService.Instance.GetHistoryAsync(_chat, 120);
                _tabMessages = messages == null ? new List<ChatMessageViewModel>() : messages;
                SortMessagesNewestFirst(_tabMessages);
                _oldestLoadedMessageId = GetOldestMessageId(_tabMessages);
                _historyFullyLoadedForTabs = _tabMessages.Count == 0 || _oldestLoadedMessageId <= 1;
            }
            catch
            {
                _tabMessages = new List<ChatMessageViewModel>();
                _oldestLoadedMessageId = 0;
                _historyFullyLoadedForTabs = true;
            }
            _historyLoadedForTabs = true;
            _historyLoadingForTabs = false;
        }

        private async System.Threading.Tasks.Task LoadMoreHistoryForTabsAsync()
        {
            if (_historyLoadingForTabs || _historyFullyLoadedForTabs || _chat == null) return;
            if (_oldestLoadedMessageId <= 0)
            {
                _historyFullyLoadedForTabs = true;
                return;
            }

            _historyLoadingForTabs = true;
            UpdateLoadMoreButton();

            try
            {
                var older = await TelegramService.Instance.GetHistoryBeforeAsync(_chat, _oldestLoadedMessageId, 120);
                if (older == null || older.Count == 0)
                {
                    _historyFullyLoadedForTabs = true;
                }
                else
                {
                    MergeOlderMessages(older);
                    _oldestLoadedMessageId = GetOldestMessageId(_tabMessages);
                    if (older.Count < 120 || _oldestLoadedMessageId <= 1)
                        _historyFullyLoadedForTabs = true;
                }
            }
            catch
            {
                _historyFullyLoadedForTabs = true;
            }

            _historyLoadingForTabs = false;
        }

        private void FillContentTab(string tab)
        {
            _contentItems.Clear();
            if (_tabMessages == null) _tabMessages = new List<ChatMessageViewModel>();

            for (var i = 0; i < _tabMessages.Count; i++)
            {
                var message = _tabMessages[i];
                if (message == null || !message.HasMedia) continue;

                if (message.MediaItems != null && message.MediaItems.Count > 0)
                {
                    for (var j = 0; j < message.MediaItems.Count; j++)
                        AddContentItemIfMatches(tab, message, message.MediaItems[j]);
                }
                else
                {
                    AddContentItemIfMatches(tab, message, null);
                }
            }

            EmptyContentText.Text = _contentItems.Count == 0 ? EmptyTextForTab(tab) : string.Empty;
            EmptyContentText.Visibility = _contentItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateLoadMoreButton();

            if (tab == "media" && _contentItems.Count > 0)
            {
                var ignored = EnsureMediaPreviewsAsync();
            }
        }

        private async System.Threading.Tasks.Task EnsureTabHasServerContentAsync(string tab)
        {
            if (tab != "audio") return;
            var attempts = 0;
            while (_contentItems.Count == 0 && !_historyFullyLoadedForTabs && attempts < 6)
            {
                await LoadMoreHistoryForTabsAsync();
                FillContentTab(tab);
                attempts++;
            }
        }

        private void AddContentItemIfMatches(string tab, ChatMessageViewModel message, ChatMediaItemViewModel mediaItem)
        {
            var kind = mediaItem == null ? message.MediaKind : mediaItem.MediaKind;
            var mime = mediaItem == null ? message.MediaMimeType : mediaItem.MediaMimeType;
            var fileName = mediaItem == null ? message.MediaFileName : mediaItem.MediaFileName;
            kind = NormalizeMediaKind(kind, mime, fileName);
            if (!MatchesTab(tab, kind, mime, fileName)) return;

            var item = new ProfileContentItem();
            item.Message = message;
            item.MediaItem = mediaItem;
            item.Kind = kind;
            if (tab == "audio")
            {
                item.Title = BuildAudioTitle(message);
                item.Subtitle = BuildAudioSubtitle(message);
            }
            else
            {
                item.Title = BuildContentTitle(message, mediaItem);
                item.Subtitle = BuildContentSubtitle(message, mediaItem);
            }
            item.IconGlyph = IconForKind(kind);
            item.PreviewSource = BuildPreviewSource(kind, message, mediaItem);
            _contentItems.Add(item);
        }

        private bool MatchesTab(string tab, string kind, string mime, string fileName)
        {
            if (tab == "media") return IsMediaKind(kind);
            if (tab == "audio") return IsAudioKind(kind) || StartsWith(mime, "audio/") || HasAudioExtension(fileName);
            if (tab == "files") return !IsMediaKind(kind) && !IsAudioKind(kind) && !StartsWith(mime, "audio/") && !HasAudioExtension(fileName);
            return false;
        }

        private string NormalizeMediaKind(string kind, string mime, string fileName)
        {
            if (kind == "voice") return "voice";
            if (IsMediaKind(kind)) return kind;
            if (StartsWith(mime, "image/")) return "photo";
            if (StartsWith(mime, "video/")) return "video";
            if (StartsWith(mime, "audio/") || HasAudioExtension(fileName)) return "audio";
            if (IsAudioKind(kind) || kind == "file") return kind;
            return string.IsNullOrEmpty(kind) ? "file" : kind;
        }

        private bool IsMediaKind(string kind)
        {
            return kind == "photo" || kind == "video" || kind == "roundvideo";
        }

        private bool IsAudioKind(string kind)
        {
            return kind == "audio" || kind == "voice";
        }

        private bool StartsWith(string value, string prefix)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(prefix)) return false;
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private bool HasAudioExtension(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            fileName = fileName.ToLowerInvariant();
            return fileName.EndsWith(".mp3", StringComparison.Ordinal) ||
                fileName.EndsWith(".m4a", StringComparison.Ordinal) ||
                fileName.EndsWith(".ogg", StringComparison.Ordinal) ||
                fileName.EndsWith(".opus", StringComparison.Ordinal) ||
                fileName.EndsWith(".wav", StringComparison.Ordinal) ||
                fileName.EndsWith(".flac", StringComparison.Ordinal) ||
                fileName.EndsWith(".aac", StringComparison.Ordinal);
        }

        private ImageSource BuildPreviewSource(string kind, ChatMessageViewModel message, ChatMediaItemViewModel mediaItem)
        {
            if (!IsMediaKind(kind)) return null;
            var uri = mediaItem == null
                ? (message == null ? null : message.MediaFileUri)
                : mediaItem.MediaFileUri;
            if (string.IsNullOrEmpty(uri)) return null;

            try { return new BitmapImage(new Uri(uri)); }
            catch { return null; }
        }

        private string BuildContentTitle(ChatMessageViewModel message, ChatMediaItemViewModel mediaItem)
        {
            var title = mediaItem == null ? message.MediaTitle : mediaItem.MediaTitle;
            if (string.IsNullOrEmpty(title)) title = mediaItem == null ? message.MediaFileName : mediaItem.MediaFileName;
            if (string.IsNullOrEmpty(title)) title = message.Text;
            if (string.IsNullOrEmpty(title)) title = TitleForKind(mediaItem == null ? message.MediaKind : mediaItem.MediaKind);
            return title;
        }

        private string BuildContentSubtitle(ChatMessageViewModel message, ChatMediaItemViewModel mediaItem)
        {
            var subtitle = message == null ? string.Empty : message.TimeText;
            var size = mediaItem == null ? (message == null ? 0 : message.MediaSize) : mediaItem.MediaSize;
            if (size > 0)
            {
                if (!string.IsNullOrEmpty(subtitle)) subtitle += " · ";
                subtitle += FormatSize(size);
            }
            return subtitle;
        }

        private string BuildAudioTitle(ChatMessageViewModel message)
        {
            if (message == null || message.Date <= 0) return string.Empty;
            try
            {
                var utc = new DateTime(1970, 1, 1).AddSeconds(message.Date);
                return utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
            }
            catch
            {
                return message.TimeText;
            }
        }

        private string BuildAudioSubtitle(ChatMessageViewModel message)
        {
            if (message == null) return string.Empty;
            if (message.IsOutgoing) return "You";
            if (!string.IsNullOrEmpty(message.SenderName)) return message.SenderName;
            return _chat == null ? string.Empty : _chat.Title;
        }

        private string TitleForKind(string kind)
        {
            if (kind == "photo") return "Photo";
            if (kind == "video" || kind == "roundvideo") return "Video";
            if (kind == "audio") return "Audio";
            if (kind == "voice") return "Voice message";
            return "File";
        }

        private string IconForKind(string kind)
        {
            if (kind == "photo") return "\uE91B";
            if (kind == "video" || kind == "roundvideo") return "\uE714";
            if (kind == "voice") return "\uE720";
            if (kind == "audio") return "\uE8D6";
            return "\uE8A5";
        }

        private string FormatSize(long size)
        {
            if (size >= 1024 * 1024) return (size / 1024.0 / 1024.0).ToString("0.#") + " MB";
            if (size >= 1024) return (size / 1024.0).ToString("0.#") + " KB";
            return size.ToString() + " B";
        }

        private string EmptyTextForTab(string tab)
        {
            if (tab == "media") return "No media in this chat.";
            if (tab == "files") return "No files in this chat.";
            if (tab == "audio") return "No audio in this chat.";
            return string.Empty;
        }

        private async void ContentList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as ProfileContentItem;
            if (item == null) return;
            OpenContentInChat(item);
        }

        private async void MediaGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as ProfileContentItem;
            if (item == null) return;
            if (item.Kind != "photo")
            {
                OpenContentInChat(item);
                return;
            }

            await EnsureContentItemPreviewAsync(item);
            ShowMediaOverlay(item);
        }

        private void OpenContentInChat(ProfileContentItem item)
        {
            if (item == null || item.Message == null || _chat == null || Frame == null) return;
            var target = new ChatNavigationTarget
            {
                Chat = _chat,
                MessageId = item.Message.Id
            };
            if (!AdaptiveShellNavigationService.NavigateChat(target))
                Frame.Navigate(typeof(ChatPage), target);
        }

        private async System.Threading.Tasks.Task EnsureContentItemPreviewAsync(ProfileContentItem item)
        {
            if (item == null || item.PreviewSource != null || item.Kind != "photo") return;
            try
            {
                if (item.MediaItem != null)
                    await TelegramService.Instance.DownloadMessageMediaAsync(item.MediaItem);
                else if (item.Message != null)
                    await TelegramService.Instance.DownloadMessageMediaAsync(item.Message);
                item.PreviewSource = BuildPreviewSource(item.Kind, item.Message, item.MediaItem);
            }
            catch
            {
            }
        }

        private void ShowMediaOverlay(ProfileContentItem selectedItem)
        {
            _mediaOverlayImages.Clear();
            _mediaOverlayItems.Clear();
            _mediaOverlayIndicators.Clear();
            MediaOverlayIndicatorPanel.Children.Clear();
            MediaOverlayFlipView.SelectionChanged -= MediaOverlayFlipView_SelectionChanged;

            var selectedIndex = 0;
            for (var i = 0; i < _contentItems.Count; i++)
            {
                var item = _contentItems[i];
                if (item == null || item.Kind != "photo") continue;
                if (AddMediaOverlayItem(item) && object.ReferenceEquals(item, selectedItem))
                    selectedIndex = _mediaOverlayImages.Count - 1;
            }

            if (_mediaOverlayImages.Count == 0 && selectedItem != null && AddMediaOverlayItem(selectedItem))
                selectedIndex = 0;
            if (_mediaOverlayImages.Count == 0) return;

            MediaOverlayFlipView.ItemsSource = null;
            MediaOverlayFlipView.ItemsSource = _mediaOverlayImages;
            MediaOverlayFlipView.SelectedIndex = selectedIndex;

            for (var i = 0; i < _mediaOverlayImages.Count; i++)
            {
                var ellipse = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Margin = new Thickness(5, 0, 5, 0),
                    Fill = i == selectedIndex ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromArgb(255, 120, 120, 120))
                };
                _mediaOverlayIndicators.Add(ellipse);
                MediaOverlayIndicatorPanel.Children.Add(ellipse);
            }

            UpdateMediaOverlayCounter(selectedIndex);
            MediaOverlayFlipView.SelectionChanged += MediaOverlayFlipView_SelectionChanged;
            MediaOverlay.Visibility = Visibility.Visible;
        }

        private bool AddMediaOverlayItem(ProfileContentItem item)
        {
            if (item == null) return false;
            var source = item.PreviewSource;
            if (source == null)
            {
                var uri = GetContentItemUri(item);
                if (string.IsNullOrEmpty(uri)) return false;
                try { source = new BitmapImage(new Uri(uri)); }
                catch { return false; }
            }
            _mediaOverlayImages.Add(source);
            _mediaOverlayItems.Add(item);
            return true;
        }

        private string GetContentItemUri(ProfileContentItem item)
        {
            if (item == null) return string.Empty;
            return item.MediaItem == null ? (item.Message == null ? string.Empty : item.Message.MediaFileUri) : item.MediaItem.MediaFileUri;
        }

        private void MediaOverlayFlipView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedIndex = MediaOverlayFlipView.SelectedIndex;
            if (selectedIndex < 0 || selectedIndex >= _mediaOverlayIndicators.Count) return;
            for (var i = 0; i < _mediaOverlayIndicators.Count; i++)
                _mediaOverlayIndicators[i].Fill = i == selectedIndex ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromArgb(255, 120, 120, 120));
            UpdateMediaOverlayCounter(selectedIndex);
        }

        private void UpdateMediaOverlayCounter(int index)
        {
            MediaOverlayCounter.Text = _mediaOverlayImages.Count <= 0 ? string.Empty : (index + 1).ToString() + " / " + _mediaOverlayImages.Count.ToString();
        }

        private void CloseMediaOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            CloseMediaOverlay();
        }

        private void MediaOverlayBackground_Tapped(object sender, TappedRoutedEventArgs e)
        {
            CloseMediaOverlay();
            e.Handled = true;
        }

        private void CloseMediaOverlay()
        {
            MediaOverlay.Visibility = Visibility.Collapsed;
            MediaOverlayFlipView.SelectionChanged -= MediaOverlayFlipView_SelectionChanged;
            MediaOverlayFlipView.ItemsSource = null;
            MediaOverlayIndicatorPanel.Children.Clear();
            _mediaOverlayImages.Clear();
            _mediaOverlayItems.Clear();
            _mediaOverlayIndicators.Clear();
            MediaOverlayCounter.Text = string.Empty;
        }

        private void GoToMediaMessageButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedMediaOverlayItem();
            CloseMediaOverlay();
            OpenContentInChat(item);
        }

        private async void DownloadMediaOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedMediaOverlayItem();
            if (item == null) return;

            try
            {
                var sourceFile = await GetMediaOverlayStorageFileAsync(item);
                if (sourceFile == null) return;

                var picker = new FileSavePicker();
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                picker.SuggestedFileName = GetMediaOverlaySuggestedFileName(sourceFile, item);
                picker.FileTypeChoices.Add("Image", new List<string> { GetMediaOverlayFileExtension(sourceFile, item) });
                var target = await picker.PickSaveFileAsync();
                if (target == null) return;

                CachedFileManager.DeferUpdates(target);
                await sourceFile.CopyAndReplaceAsync(target);
                await CachedFileManager.CompleteUpdatesAsync(target);
            }
            catch
            {
            }
        }

        private ProfileContentItem GetSelectedMediaOverlayItem()
        {
            var index = MediaOverlayFlipView.SelectedIndex;
            if (index < 0 || index >= _mediaOverlayItems.Count) return null;
            return _mediaOverlayItems[index];
        }

        private async System.Threading.Tasks.Task<StorageFile> GetMediaOverlayStorageFileAsync(ProfileContentItem item)
        {
            if (item == null) return null;
            try
            {
                if (item.MediaItem != null)
                    return await TelegramService.Instance.DownloadOriginalPhotoAsync(item.MediaItem);
                if (item.Message != null)
                    return await TelegramService.Instance.DownloadOriginalPhotoAsync(_chat, item.Message);
            }
            catch
            {
            }

            var uri = GetContentItemUri(item);
            if (string.IsNullOrEmpty(uri)) return null;
            try { return await StorageFile.GetFileFromApplicationUriAsync(new Uri(uri)); }
            catch { return null; }
        }

        private string GetMediaOverlaySuggestedFileName(StorageFile sourceFile, ProfileContentItem item)
        {
            var name = sourceFile == null ? null : sourceFile.Name;
            if (string.IsNullOrEmpty(name) && item != null)
                name = item.MediaItem == null ? (item.Message == null ? null : item.Message.MediaFileName) : item.MediaItem.MediaFileName;
            if (string.IsNullOrEmpty(name)) name = "photo.jpg";
            return System.IO.Path.GetFileNameWithoutExtension(name);
        }

        private string GetMediaOverlayFileExtension(StorageFile sourceFile, ProfileContentItem item)
        {
            var name = sourceFile == null ? null : sourceFile.Name;
            if (string.IsNullOrEmpty(name) && item != null)
                name = item.MediaItem == null ? (item.Message == null ? null : item.Message.MediaFileName) : item.MediaItem.MediaFileName;
            var extension = System.IO.Path.GetExtension(name);
            return string.IsNullOrEmpty(extension) ? ".jpg" : extension;
        }

        private void MusicButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chat == null || ProfileMusicSheet == null) return;
            ProfileMusicSheet.Show(_chat);
        }

        private void CallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chat == null || Frame == null) return;
            Frame.Navigate(typeof(CallPage), _chat);
        }

        private void ChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chat == null || Frame == null) return;
            if (AdaptiveShellNavigationService.NavigateChat(_chat))
                return;
            Frame.Navigate(typeof(ChatPage), _chat);
        }

        private void ChannelPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_profileChannel == null || Frame == null) return;
            if (AdaptiveShellNavigationService.NavigateChat(_profileChannel))
            {
                e.Handled = true;
                return;
            }
            Frame.Navigate(typeof(ChatPage), _profileChannel);
            e.Handled = true;
        }

        private async void LoadMoreContentButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTab == "info") return;
            await LoadMoreHistoryForTabsAsync();
            FillContentTab(_selectedTab);
        }

        private async System.Threading.Tasks.Task OpenContentItemAsync(ProfileContentItem item)
        {
            try
            {
                SetLoading(true, "Opening...");
                if (item.MediaItem != null)
                    await TelegramService.Instance.DownloadMessageMediaAsync(item.MediaItem);
                else if (item.Message != null)
                    await TelegramService.Instance.DownloadMessageMediaAsync(item.Message);

                var uri = item.MediaItem == null
                    ? (item.Message == null ? null : item.Message.MediaFileUri)
                    : item.MediaItem.MediaFileUri;

                item.PreviewSource = BuildPreviewSource(item.Kind, item.Message, item.MediaItem);

                if (!string.IsNullOrEmpty(uri))
                    await Launcher.LaunchUriAsync(new Uri(uri));
            }
            catch (Exception ex)
            {
                EmptyContentText.Text = "Open error: " + ex.Message;
                EmptyContentText.Visibility = Visibility.Visible;
            }
            finally
            {
                SetLoading(false, string.Empty);
            }
        }

        private async System.Threading.Tasks.Task EnsureMediaPreviewsAsync()
        {
            var loaded = 0;
            for (var i = 0; i < _contentItems.Count && loaded < 12; i++)
            {
                var item = _contentItems[i];
                if (item == null || item.PreviewSource != null || item.Kind != "photo") continue;

                try
                {
                    if (item.MediaItem != null)
                        await TelegramService.Instance.DownloadMessageMediaAsync(item.MediaItem);
                    else if (item.Message != null)
                        await TelegramService.Instance.DownloadMessageMediaAsync(item.Message);

                    item.PreviewSource = BuildPreviewSource(item.Kind, item.Message, item.MediaItem);
                    loaded++;
                }
                catch
                {
                }
            }
        }

        private void UpdateLoadMoreButton()
        {
            if (LoadMoreContentButton == null) return;
            // Hide the button entirely once everything is loaded instead of showing "No more".
            var visible = _selectedTab != "info" && !_historyFullyLoadedForTabs;
            LoadMoreContentButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            LoadMoreContentButton.IsEnabled = !_historyLoadingForTabs;
            LoadMoreContentButton.Content = _historyLoadingForTabs ? "Loading..." : "Load more";
        }

        private int GetOldestMessageId(List<ChatMessageViewModel> messages)
        {
            var min = 0;
            if (messages == null) return min;
            for (var i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                if (message == null || message.Id <= 0) continue;
                if (min == 0 || message.Id < min) min = message.Id;
            }
            return min;
        }

        private void MergeOlderMessages(List<ChatMessageViewModel> older)
        {
            if (_tabMessages == null) _tabMessages = new List<ChatMessageViewModel>();
            if (older == null) return;

            var existing = new HashSet<int>();
            for (var i = 0; i < _tabMessages.Count; i++)
            {
                var message = _tabMessages[i];
                if (message != null && message.Id > 0) existing.Add(message.Id);
            }

            for (var j = 0; j < older.Count; j++)
            {
                var message = older[j];
                if (message == null || message.Id <= 0 || existing.Contains(message.Id)) continue;
                _tabMessages.Add(message);
                existing.Add(message.Id);
            }

            _tabMessages.Sort(delegate(ChatMessageViewModel a, ChatMessageViewModel b)
            {
                var ad = a == null ? 0 : a.Date;
                var bd = b == null ? 0 : b.Date;
                return bd.CompareTo(ad);
            });
        }

        private void SortMessagesNewestFirst(List<ChatMessageViewModel> messages)
        {
            if (messages == null) return;
            messages.Sort(delegate(ChatMessageViewModel a, ChatMessageViewModel b)
            {
                var ad = a == null ? 0 : a.Date;
                var bd = b == null ? 0 : b.Date;
                return bd.CompareTo(ad);
            });
        }

        private void HeaderGestureArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _pointerDown = true;
            _stretchingHeader = false;
            _pointerStart = e.GetCurrentPoint(RootGrid).Position;
            _pointerStartHeaderHeight = HeaderHost == null ? DefaultHeaderHeight : HeaderHost.Height;
            _lastPointerDelta = 0;
            _photoSwipeHandled = false;
            StopHeaderSnap();
        }

        private void HeaderGestureArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_pointerDown || ProfileScroll == null || HeaderHost == null) return;

            var point = e.GetCurrentPoint(RootGrid).Position;
            var deltaX = point.X - _pointerStart.X;
            var delta = point.Y - _pointerStart.Y;
            _lastPointerDelta = delta;

            if (TryHandleProfilePhotoSwipe(deltaX, delta))
            {
                e.Handled = true;
                return;
            }

            if (!_stretchingHeader)
            {
                var canResizeHeader = HeaderHost.Height > DefaultHeaderHeight || (ProfileScroll.VerticalOffset <= 0 && delta > 0);
                if (!canResizeHeader) return;
                _stretchingHeader = true;
                ProfileScroll.VerticalScrollMode = ScrollMode.Disabled;
                HeaderGestureArea.CapturePointer(e.Pointer);
            }

            var height = _pointerStartHeaderHeight + delta;
            if (height < DefaultHeaderHeight) height = DefaultHeaderHeight;
            if (height > GetExpandedHeaderHeight()) height = GetExpandedHeaderHeight();

            HeaderHost.Height = height;
            UpdateHeaderStretchVisuals();
            e.Handled = true;
        }

        private void HeaderGestureArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _pointerDown = false;
            _photoSwipeHandled = false;
            if (HeaderGestureArea != null)
            {
                try { HeaderGestureArea.ReleasePointerCapture(e.Pointer); }
                catch { }
            }
            if (ProfileScroll != null) ProfileScroll.VerticalScrollMode = ScrollMode.Enabled;
            if (!_stretchingHeader)
            {
                if (TryHandleProfilePhotoTap(e.GetCurrentPoint(RootGrid).Position))
                {
                    e.Handled = true;
                    return;
                }

                e.Handled = false;
                return;
            }
            _stretchingHeader = false;
            SnapHeaderAfterGesture();
            UpdateHeaderStretchVisuals();
            e.Handled = true;
        }

        private bool TryHandleProfilePhotoTap(Point point)
        {
            if (_profilePhotos == null || _profilePhotos.Count < 2) return false;
            if (HeaderHost == null || HeaderHost.Height < DefaultHeaderHeight + 80) return false;
            if (HeaderGestureArea == null || HeaderGestureArea.ActualWidth <= 0) return false;

            var released = point;
            var movedX = Math.Abs(released.X - _pointerStart.X);
            var movedY = Math.Abs(released.Y - _pointerStart.Y);
            if (movedX > 18 || movedY > 18) return false;

            MoveProfilePhoto(released.X >= HeaderGestureArea.ActualWidth / 2 ? 1 : -1);
            return true;
        }

        private bool TryHandleProfilePhotoSwipe(double deltaX, double deltaY)
        {
            if (_photoSwipeHandled) return true;
            if (_profilePhotos == null || _profilePhotos.Count < 2) return false;
            if (HeaderHost == null || HeaderHost.Height < DefaultHeaderHeight + 80) return false;

            var absX = Math.Abs(deltaX);
            var absY = Math.Abs(deltaY);
            if (absX < 36 || absX < absY * 1.2) return false;

            MoveProfilePhoto(deltaX < 0 ? 1 : -1);
            _photoSwipeHandled = true;
            return true;
        }

        private void ProfileScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
        }


        private void HeaderGlassGradient_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyHeaderGlassSetting();
        }

        private void ApplyHeaderGlassSetting()
        {
            var enabled = TelegramAppSettings.GlassEffectEnabled;

            if (HeaderGlassGradient != null)
            {
                HeaderGlassGradient.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
                if (enabled)
                    HeaderGlassEffectHelper.AttachGradient(HeaderGlassGradient);
                else
                    HeaderGlassEffectHelper.Detach(HeaderGlassGradient);
            }

            if (HeaderDarkGradient != null)
                HeaderDarkGradient.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        }

        private void HeaderHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _lastHeaderWidth = e.NewSize.Width;
            if (HeaderPhoto != null)
            {
                HeaderPhoto.Width = _lastHeaderWidth;
                HeaderPhoto.Height = _lastHeaderWidth;
            }
            if (HeaderFallback != null)
            {
                HeaderFallback.Width = _lastHeaderWidth;
                HeaderFallback.Height = _lastHeaderWidth;
            }

            if (HeaderHost != null && HeaderHost.Height > GetExpandedHeaderHeight())
                HeaderHost.Height = GetExpandedHeaderHeight();

            UpdateHeaderStretchVisuals();
        }

        private void UpdateHeaderStretchVisuals()
        {
            if (HeaderHost == null || ExpandedHeaderMedia == null || CompactAvatar == null) return;

            var delta = HeaderHost.Height - DefaultHeaderHeight;
            if (delta < 0) delta = 0;
            var progress = delta / 96.0;
            if (progress > 1) progress = 1;

            ExpandedHeaderMedia.Opacity = progress;
            CompactAvatar.Opacity = 1.0 - progress;

            if (HeaderTextPanel != null)
            {
                var left = 78.0 - 66.0 * progress;
                var expandedTop = HeaderHost.Height - 104.0;
                if (expandedTop < 30.0) expandedTop = 30.0;
                var top = 30.0 + (expandedTop - 30.0) * progress;
                HeaderTextPanel.Margin = new Thickness(left, top, 12, 0);
            }

            UpdateHeaderTabForegrounds(progress);
        }

        private void UpdateHeaderTabForegrounds(double progress)
        {
            var expanded = progress > 0.35;
            var selectedBrush = expanded ? _expandedHeaderTabBrush : _collapsedSelectedTabBrush;
            var regularBrush = expanded ? _expandedHeaderTabBrush : _collapsedRegularTabBrush;

            if (InfoTab != null) InfoTab.Foreground = selectedBrush;
            if (MediaTab != null) MediaTab.Foreground = regularBrush;
            if (FilesTab != null) FilesTab.Foreground = regularBrush;
            if (AudioTab != null) AudioTab.Foreground = regularBrush;
        }

        private void SnapHeaderAfterGesture()
        {
            if (HeaderHost == null) return;

            var openHeight = GetExpandedHeaderHeight();
            if (_lastPointerDelta > 24)
                SnapHeaderTo(openHeight);
            else if (_lastPointerDelta < -24)
                SnapHeaderTo(DefaultHeaderHeight);
            else
                SnapHeaderTo(HeaderHost.Height >= DefaultHeaderHeight + GetMaxHeaderStretch() / 2 ? openHeight : DefaultHeaderHeight);
        }

        private double GetExpandedHeaderHeight()
        {
            return DefaultHeaderHeight + GetMaxHeaderStretch();
        }

        private double GetMaxHeaderStretch()
        {
            var width = _lastHeaderWidth;
            if (width <= 0 && HeaderHost != null) width = HeaderHost.ActualWidth;
            if (width <= DefaultHeaderHeight) return width > 0 ? 0 : FallbackMaxHeaderStretch;
            return width - DefaultHeaderHeight;
        }

        private void SnapHeaderTo(double targetHeight)
        {
            if (HeaderHost == null) return;
            _snapTargetHeight = targetHeight;
            if (_snapTimer != null && !_snapTimer.IsEnabled)
                _snapTimer.Start();
        }

        private void StopHeaderSnap()
        {
            if (_snapTimer != null && _snapTimer.IsEnabled)
                _snapTimer.Stop();
        }

        private void SnapTimer_Tick(object sender, object e)
        {
            if (HeaderHost == null)
            {
                StopHeaderSnap();
                return;
            }

            var diff = _snapTargetHeight - HeaderHost.Height;
            if (Math.Abs(diff) < 1.0)
            {
                HeaderHost.Height = _snapTargetHeight;
                StopHeaderSnap();
                UpdateHeaderStretchVisuals();
                return;
            }

            HeaderHost.Height += diff * 0.32;
            UpdateHeaderStretchVisuals();
        }
    }

    public sealed class ChatNavigationTarget
    {
        public ChatViewModel Chat { get; set; }
        public int MessageId { get; set; }
    }

    public sealed class ProfileContentItem : INotifyPropertyChanged
    {
        private ImageSource _previewSource;

        public event PropertyChangedEventHandler PropertyChanged;

        public ChatMessageViewModel Message { get; set; }
        public ChatMediaItemViewModel MediaItem { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string IconGlyph { get; set; }
        public string PreviewLabel
        {
            get
            {
                if (Kind == "voice") return "VOICE";
                if (Kind == "audio") return "AUDIO";
                return string.Empty;
            }
        }

        public ImageSource PreviewSource
        {
            get { return _previewSource; }
            set
            {
                if (_previewSource == value) return;
                _previewSource = value;
                OnPropertyChanged("PreviewSource");
                OnPropertyChanged("PreviewVisibility");
                OnPropertyChanged("IconVisibility");
            }
        }

        public Visibility PreviewVisibility
        {
            get { return PreviewSource == null ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility IconVisibility
        {
            get { return PreviewSource == null && AudioPreviewVisibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility AudioPreviewVisibility
        {
            get { return Kind == "voice" || Kind == "audio" ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility VideoBadgeVisibility
        {
            get { return Kind == "video" || Kind == "roundvideo" ? Visibility.Visible : Visibility.Collapsed; }
        }

        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
