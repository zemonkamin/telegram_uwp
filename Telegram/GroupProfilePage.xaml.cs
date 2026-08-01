using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Telegram.Models;
using Telegram.Services;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;
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
    public sealed partial class GroupProfilePage : Page
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
        private double _snapTargetHeight;
        private DispatcherTimer _snapTimer;
        private bool _historyLoaded;
        private bool _historyLoading;
        private bool _historyDone;
        private int _oldestMessageId;
        private List<ChatMessageViewModel> _messages = new List<ChatMessageViewModel>();
        private ObservableCollection<ProfileContentItem> _contentItems = new ObservableCollection<ProfileContentItem>();
        private readonly List<ImageSource> _mediaOverlayImages = new List<ImageSource>();
        private readonly List<ProfileContentItem> _mediaOverlayItems = new List<ProfileContentItem>();
        private readonly List<Ellipse> _mediaOverlayIndicators = new List<Ellipse>();
        private List<ProfilePhotoViewModel> _profilePhotos = new List<ProfilePhotoViewModel>();
        private int _profilePhotoIndex;
        private bool _photoSwipeHandled;
        private string _selectedTab = "members";
        private Brush _collapsedSelectedTabBrush;
        private Brush _collapsedRegularTabBrush;
        private readonly Brush _expandedHeaderTabBrush = new SolidColorBrush(Windows.UI.Colors.White);

        public GroupProfilePage()
        {
            InitializeComponent();
            _collapsedSelectedTabBrush = MembersTab.Foreground;
            _collapsedRegularTabBrush = MediaTab.Foreground;
            ContentList.ItemsSource = _contentItems;
            MediaGrid.ItemsSource = _contentItems;
            _snapTimer = new DispatcherTimer();
            _snapTimer.Interval = TimeSpan.FromMilliseconds(16);
            _snapTimer.Tick += SnapTimer_Tick;
            UpdateHeaderVisualState();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ConfigureSystemBackButton(true);
            ApplyHeaderGlassSetting();
            _chat = e.Parameter as ChatViewModel;
            ApplyChat(_chat);
            if (_chat == null) return;

            SetLoading(true);
            try
            {
                var full = await TelegramService.Instance.RefreshFullChatInfoAsync(_chat);
                if (full != null)
                {
                    _chat = full;
                    ApplyChat(_chat);
                }
                await LoadProfilePhotosAsync();
                MembersList.ItemsSource = await TelegramService.Instance.GetChatMembersAsync(_chat, 120);
                await SelectTabAsync("members");
            }
            catch
            {
            }
            SetLoading(false);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StatusBarLoadingIndicator.Hide();
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
            e.Handled = true;
            if (Frame != null && Frame.CanGoBack) Frame.GoBack();
            else if (Frame != null) Frame.Navigate(typeof(Chats));
        }

        private void ApplyChat(ChatViewModel chat)
        {
            if (chat == null) return;
            ProfileTitle.Text = string.IsNullOrEmpty(chat.Title) ? "Group" : chat.Title;
            ProfileSubtitle.Text = BuildMembersText(chat);
            HeaderInitials.Text = string.IsNullOrEmpty(chat.IconText) ? "?" : chat.IconText;
            CompactInitials.Text = HeaderInitials.Text;
            ApplyAvatar(chat.AvatarUri);
            SetFallbackProfilePhotos(chat.AvatarUri);
        }

        private string BuildMembersText(ChatViewModel chat)
        {
            if (chat == null || chat.SubscriberCount <= 0) return "group";
            return ChatViewModel.FormatCount(chat.SubscriberCount) + " members";
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
            catch { }
        }

        private async System.Threading.Tasks.Task LoadProfilePhotosAsync()
        {
            if (_chat == null) return;
            try
            {
                var photos = await TelegramService.Instance.GetProfilePhotosAsync(_chat, 12);
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

        private void SetLoading(bool active)
        {
            StatusBarLoadingIndicator.SetActive(active, TopLoadingBar);
        }

        private async void MembersTab_Tapped(object sender, TappedRoutedEventArgs e) { await SelectTabAsync("members"); }
        private async void MediaTab_Tapped(object sender, TappedRoutedEventArgs e) { await SelectTabAsync("media"); }
        private async void FilesTab_Tapped(object sender, TappedRoutedEventArgs e) { await SelectTabAsync("files"); }
        private async void AudioTab_Tapped(object sender, TappedRoutedEventArgs e) { await SelectTabAsync("audio"); }

        private async System.Threading.Tasks.Task SelectTabAsync(string tab)
        {
            _selectedTab = tab;
            MembersTab.Opacity = tab == "members" ? 1 : 0.68;
            MediaTab.Opacity = tab == "media" ? 1 : 0.68;
            FilesTab.Opacity = tab == "files" ? 1 : 0.68;
            AudioTab.Opacity = tab == "audio" ? 1 : 0.68;

            if (tab == "members")
            {
                MembersList.Visibility = Visibility.Visible;
                ContentList.Visibility = Visibility.Collapsed;
                MediaGrid.Visibility = Visibility.Collapsed;
                LoadMoreContentButton.Visibility = Visibility.Collapsed;
                EmptyContentText.Visibility = Visibility.Collapsed;
                return;
            }

            MembersList.Visibility = Visibility.Collapsed;
            ContentList.Visibility = tab == "media" ? Visibility.Collapsed : Visibility.Visible;
            MediaGrid.Visibility = tab == "media" ? Visibility.Visible : Visibility.Collapsed;
            await LoadHistoryAsync();
            FillContent(tab);
        }

        private async System.Threading.Tasks.Task LoadHistoryAsync()
        {
            if (_historyLoaded || _historyLoading || _chat == null) return;
            _historyLoading = true;
            try
            {
                _messages = await TelegramService.Instance.GetHistoryAsync(_chat, 120);
                if (_messages == null) _messages = new List<ChatMessageViewModel>();
                SortMessages();
                _oldestMessageId = GetOldestMessageId();
                _historyDone = _messages.Count == 0;
            }
            catch
            {
                _messages = new List<ChatMessageViewModel>();
                _historyDone = true;
            }
            _historyLoaded = true;
            _historyLoading = false;
        }

        private async System.Threading.Tasks.Task LoadMoreAsync()
        {
            if (_historyLoading || _historyDone || _oldestMessageId <= 0 || _chat == null) return;
            _historyLoading = true;
            try
            {
                var older = await TelegramService.Instance.GetHistoryBeforeAsync(_chat, _oldestMessageId, 120);
                if (older == null || older.Count == 0) _historyDone = true;
                else
                {
                    MergeMessages(older);
                    _oldestMessageId = GetOldestMessageId();
                }
            }
            catch { _historyDone = true; }
            _historyLoading = false;
        }

        private void FillContent(string tab)
        {
            _contentItems.Clear();
            for (var i = 0; i < _messages.Count; i++)
            {
                var m = _messages[i];
                if (m == null || !m.HasMedia) continue;
                if (m.MediaItems != null && m.MediaItems.Count > 0)
                {
                    for (var j = 0; j < m.MediaItems.Count; j++) AddItem(tab, m, m.MediaItems[j]);
                }
                else AddItem(tab, m, null);
            }
            EmptyContentText.Text = _contentItems.Count == 0 ? "No " + tab + " in this chat." : string.Empty;
            EmptyContentText.Visibility = _contentItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadMoreContentButton.Visibility = _historyDone ? Visibility.Collapsed : Visibility.Visible;
            LoadMoreContentButton.IsEnabled = true;
            LoadMoreContentButton.Content = "Load more";
            if (tab == "media" && _contentItems.Count > 0)
            {
                var ignored = EnsureMediaPreviewsAsync();
            }
        }

        private void AddItem(string tab, ChatMessageViewModel message, ChatMediaItemViewModel media)
        {
            var kind = NormalizeKind(media == null ? message.MediaKind : media.MediaKind, media == null ? message.MediaMimeType : media.MediaMimeType, media == null ? message.MediaFileName : media.MediaFileName);
            if (tab == "media" && !IsMedia(kind)) return;
            if (tab == "audio" && kind != "audio" && kind != "voice") return;
            if (tab == "files" && (IsMedia(kind) || kind == "audio" || kind == "voice")) return;

            var item = new ProfileContentItem();
            item.Message = message;
            item.MediaItem = media;
            item.Kind = kind;
            item.Title = tab == "audio" ? FormatDate(message.Date) : BuildTitle(message, media, kind);
            item.Subtitle = tab == "audio" ? (message.IsOutgoing ? "You" : message.SenderName) : message.TimeText;
            item.IconGlyph = IconForKind(kind);
            item.PreviewSource = BuildPreview(kind, message, media);
            _contentItems.Add(item);
        }

        private string NormalizeKind(string kind, string mime, string fileName)
        {
            if (kind == "voice") return "voice";
            if (kind == "photo" || kind == "video" || kind == "roundvideo") return kind;
            mime = (mime ?? string.Empty).ToLowerInvariant();
            fileName = (fileName ?? string.Empty).ToLowerInvariant();
            if (mime.StartsWith("image/", StringComparison.Ordinal)) return "photo";
            if (mime.StartsWith("video/", StringComparison.Ordinal)) return "video";
            if (mime.StartsWith("audio/", StringComparison.Ordinal) || fileName.EndsWith(".mp3", StringComparison.Ordinal) || fileName.EndsWith(".ogg", StringComparison.Ordinal) || fileName.EndsWith(".m4a", StringComparison.Ordinal)) return "audio";
            return string.IsNullOrEmpty(kind) ? "file" : kind;
        }

        private bool IsMedia(string kind) { return kind == "photo" || kind == "video" || kind == "roundvideo"; }
        private string IconForKind(string kind) { if (kind == "photo") return "\uE91B"; if (kind == "video" || kind == "roundvideo") return "\uE714"; if (kind == "voice") return "\uE720"; if (kind == "audio") return "\uE8D6"; return "\uE8A5"; }

        private ImageSource BuildPreview(string kind, ChatMessageViewModel message, ChatMediaItemViewModel media)
        {
            if (!IsMedia(kind)) return null;
            var uri = media == null ? message.MediaFileUri : media.MediaFileUri;
            if (string.IsNullOrEmpty(uri)) return null;
            try { return new BitmapImage(new Uri(uri)); } catch { return null; }
        }

        private string BuildTitle(ChatMessageViewModel m, ChatMediaItemViewModel media, string kind)
        {
            var text = media == null ? m.MediaTitle : media.MediaTitle;
            if (string.IsNullOrEmpty(text)) text = media == null ? m.MediaFileName : media.MediaFileName;
            if (string.IsNullOrEmpty(text)) text = kind == "photo" ? "Photo" : kind == "video" || kind == "roundvideo" ? "Video" : kind == "audio" ? "Audio" : kind == "voice" ? "Voice message" : "File";
            return text;
        }

        private string FormatDate(int unix)
        {
            try { return new DateTime(1970, 1, 1).AddSeconds(unix).ToLocalTime().ToString("dd.MM.yyyy HH:mm"); }
            catch { return string.Empty; }
        }

        private void MergeMessages(List<ChatMessageViewModel> incoming)
        {
            if (incoming == null) return;
            var ids = new HashSet<int>();
            for (var i = 0; i < _messages.Count; i++) if (_messages[i] != null) ids.Add(_messages[i].Id);
            for (var j = 0; j < incoming.Count; j++) if (incoming[j] != null && !ids.Contains(incoming[j].Id)) _messages.Add(incoming[j]);
            SortMessages();
        }

        private void SortMessages()
        {
            _messages.Sort(delegate(ChatMessageViewModel a, ChatMessageViewModel b) { return (b == null ? 0 : b.Date).CompareTo(a == null ? 0 : a.Date); });
        }

        private int GetOldestMessageId()
        {
            var min = 0;
            for (var i = 0; i < _messages.Count; i++) if (_messages[i] != null && _messages[i].Id > 0 && (min == 0 || _messages[i].Id < min)) min = _messages[i].Id;
            return min;
        }

        private async void LoadMoreContentButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadMoreAsync();
            FillContent(_selectedTab);
        }

        private void ContentList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as ProfileContentItem;
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
            if (item == null || item.Message == null || Frame == null) return;
            var target = new ChatNavigationTarget { Chat = _chat, MessageId = item.Message.Id };
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
                item.PreviewSource = BuildPreview(item.Kind, item.Message, item.MediaItem);
            }
            catch
            {
            }
        }

        private async System.Threading.Tasks.Task EnsureMediaPreviewsAsync()
        {
            var loaded = 0;
            for (var i = 0; i < _contentItems.Count && loaded < 18; i++)
            {
                var item = _contentItems[i];
                if (item == null || item.PreviewSource != null || item.Kind != "photo") continue;
                await EnsureContentItemPreviewAsync(item);
                if (item.PreviewSource != null) loaded++;
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

        private void CloseMediaOverlayButton_Click(object sender, RoutedEventArgs e) { CloseMediaOverlay(); }

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

        private void MembersList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var member = e.ClickedItem as ChatViewModel;
            OpenProfile(member);
        }

        private void ChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chat == null || Frame == null) return;
            if (AdaptiveShellNavigationService.NavigateChat(_chat))
                return;
            Frame.Navigate(typeof(ChatPage), _chat);
        }

        private void OpenProfile(ChatViewModel chat)
        {
            if (chat == null || Frame == null) return;
            if (chat.IsChannel || chat.IsBroadcast)
            {
                if (AdaptiveShellNavigationService.NavigateLeft(typeof(ChannelProfilePage), chat))
                    return;
                Frame.Navigate(typeof(ChannelProfilePage), chat);
            }
            else if (chat.IsGroup)
            {
                if (AdaptiveShellNavigationService.NavigateLeft(typeof(GroupProfilePage), chat))
                    return;
                Frame.Navigate(typeof(GroupProfilePage), chat);
            }
            else
            {
                if (AdaptiveShellNavigationService.NavigateLeft(typeof(UserProfilePage), chat))
                    return;
                Frame.Navigate(typeof(UserProfilePage), chat);
            }
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
            HeaderPhoto.Width = e.NewSize.Width;
            HeaderPhoto.Height = e.NewSize.Width;
            HeaderFallback.Width = e.NewSize.Width;
            HeaderFallback.Height = e.NewSize.Width;
            if (HeaderHost.Height > GetExpandedHeaderHeight())
                HeaderHost.Height = GetExpandedHeaderHeight();
            UpdateHeaderVisualState();
        }

        private void HeaderGestureArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _pointerDown = true;
            _stretchingHeader = false;
            _pointerStart = e.GetCurrentPoint(this).Position;
            _pointerStartHeaderHeight = HeaderHost.Height;
            _lastPointerDelta = 0;
            _photoSwipeHandled = false;
            StopHeaderSnap();
        }

        private void HeaderGestureArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_pointerDown) return;
            var point = e.GetCurrentPoint(this).Position;
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
                if (ProfileScroll == null) return;
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
            UpdateHeaderVisualState();
            e.Handled = true;
        }

        private void HeaderGestureArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _pointerDown = false;
            _photoSwipeHandled = false;
            try { HeaderGestureArea.ReleasePointerCapture(e.Pointer); } catch { }
            if (ProfileScroll != null) ProfileScroll.VerticalScrollMode = ScrollMode.Enabled;
            if (!_stretchingHeader)
            {
                if (TryHandleProfilePhotoTap(e.GetCurrentPoint(this).Position))
                {
                    e.Handled = true;
                    return;
                }

                e.Handled = false;
                return;
            }
            _stretchingHeader = false;
            SnapHeaderAfterGesture();
            UpdateHeaderVisualState();
            e.Handled = true;
        }

        private bool TryHandleProfilePhotoTap(Point point)
        {
            if (_profilePhotos == null || _profilePhotos.Count < 2) return false;
            if (HeaderHost == null || HeaderHost.Height < DefaultHeaderHeight + 80) return false;
            if (HeaderGestureArea == null || HeaderGestureArea.ActualWidth <= 0) return false;

            var movedX = Math.Abs(point.X - _pointerStart.X);
            var movedY = Math.Abs(point.Y - _pointerStart.Y);
            if (movedX > 18 || movedY > 18) return false;

            MoveProfilePhoto(point.X >= HeaderGestureArea.ActualWidth / 2 ? 1 : -1);
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

        private void UpdateHeaderVisualState()
        {
            var delta = HeaderHost.Height - DefaultHeaderHeight;
            if (delta < 0) delta = 0;
            var progress = delta / 120.0;
            if (progress > 1) progress = 1;

            ExpandedHeaderMedia.Opacity = progress;
            CompactAvatar.Opacity = 1 - progress;

            var left = 96.0 - 76.0 * progress;
            var expandedTop = HeaderHost.Height - 104.0;
            if (expandedTop < 32.0) expandedTop = 32.0;
            var top = 32.0 + (expandedTop - 32.0) * progress;
            HeaderTextPanel.Margin = new Thickness(left, top, 12, 0);
            UpdateHeaderTabForegrounds(progress);
        }

        private void UpdateHeaderTabForegrounds(double progress)
        {
            var expanded = progress > 0.35;
            var selectedBrush = expanded ? _expandedHeaderTabBrush : _collapsedSelectedTabBrush;
            var regularBrush = expanded ? _expandedHeaderTabBrush : _collapsedRegularTabBrush;

            if (MembersTab != null) MembersTab.Foreground = selectedBrush;
            if (MediaTab != null) MediaTab.Foreground = regularBrush;
            if (FilesTab != null) FilesTab.Foreground = regularBrush;
            if (AudioTab != null) AudioTab.Foreground = regularBrush;
        }

        private void SnapHeaderAfterGesture()
        {
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
            var width = HeaderHost == null ? 0 : HeaderHost.ActualWidth;
            if (width <= DefaultHeaderHeight) return width > 0 ? 0 : FallbackMaxHeaderStretch;
            return width - DefaultHeaderHeight;
        }

        private void SnapHeaderTo(double targetHeight)
        {
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
            var diff = _snapTargetHeight - HeaderHost.Height;
            if (Math.Abs(diff) < 1.0)
            {
                HeaderHost.Height = _snapTargetHeight;
                StopHeaderSnap();
                UpdateHeaderVisualState();
                return;
            }

            HeaderHost.Height += diff * 0.32;
            UpdateHeaderVisualState();
        }
    }
}
