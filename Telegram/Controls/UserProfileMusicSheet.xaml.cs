using System;
using System.Collections.Generic;
using System.IO;
using Telegram.Models;
using Telegram.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Shapes;

namespace Telegram.Controls
{
    public sealed partial class UserProfileMusicSheet : UserControl
    {
        private const int PageSize = 100;
        private const int MaxHistoryPages = 8;
        private const int MaxTracks = 120;

        private ChatViewModel _chat;
        private int _loadVersion;
        private bool _isShown;
        private bool _isDragging;
        private int _currentTrackIndex = -1;
        private List<object> _currentTracks = new List<object>();
        private double _dragStartY;
        private double _dragStartTransformY;
        private Storyboard _sheetStoryboard;
        private const double SheetHeight = 440.0;
        private const double CloseThreshold = 120.0;

        public UserProfileMusicSheet()
        {
            InitializeComponent();
        }

        public void Show(ChatViewModel chat)
        {
            if (chat == null) return;

            _chat = chat;
            _isShown = true;
            Visibility = Visibility.Visible;

            StopSheetStoryboard();
            SheetTransform.Y = SheetHeight;
            AnimateTo(0, false);

            var nav = Windows.UI.Core.SystemNavigationManager.GetForCurrentView();
            nav.BackRequested += OnBackRequested;

            var ignored = LoadMusicAsync(++_loadVersion);
        }

        public void Hide()
        {
            if (!_isShown) return;
            _isShown = false;
            _loadVersion++;
            AnimateTo(SheetHeight, true);

            if (SearchBox != null)
                SearchBox.Text = string.Empty;

            var nav = Windows.UI.Core.SystemNavigationManager.GetForCurrentView();
            nav.BackRequested -= OnBackRequested;
        }

        private void OnBackRequested(object sender, Windows.UI.Core.BackRequestedEventArgs e)
        {
            Hide();
            e.Handled = true;
        }

        private async System.Threading.Tasks.Task LoadMusicAsync(int version)
        {
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;
            MusicList.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Collapsed;

            var tracks = new List<object>();
            try
            {
                var chat = _chat;
                if (chat == null) return;

                var page = await TelegramService.Instance.GetHistoryAsync(chat, PageSize);
                var oldestId = 0;
                var pageNumber = 0;

                while (page != null && page.Count > 0 && pageNumber < MaxHistoryPages && tracks.Count < MaxTracks)
                {
                    if (version != _loadVersion || !_isShown) return;

                    AddMusicFromMessages(page, tracks);

                    oldestId = GetOldestMessageId(page);
                    if (oldestId <= 1 || page.Count < PageSize || tracks.Count >= MaxTracks)
                        break;

                    pageNumber++;
                    page = await TelegramService.Instance.GetHistoryBeforeAsync(chat, oldestId, PageSize);
                }
            }
            catch
            {
                tracks.Clear();
            }
            finally
            {
                if (version == _loadVersion && _isShown)
                {
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                    _currentTracks = tracks;
                    _currentTrackIndex = -1;
                    MusicList.ItemsSource = tracks;
                    MusicList.Visibility = tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    EmptyText.Visibility = tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private static void AddMusicFromMessages(IList<ChatMessageViewModel> messages, List<object> tracks)
        {
            if (messages == null || tracks == null) return;

            for (var i = 0; i < messages.Count && tracks.Count < MaxTracks; i++)
            {
                var message = messages[i];
                if (message == null) continue;

                if (message.MediaItems != null && message.MediaItems.Count > 0)
                {
                    for (var j = 0; j < message.MediaItems.Count && tracks.Count < MaxTracks; j++)
                    {
                        var item = message.MediaItems[j];
                        if (item != null && IsMusic(item.MediaKind) && !tracks.Contains(item))
                            tracks.Add(item);
                    }
                    continue;
                }

                if (IsMusic(message.MediaKind) && !tracks.Contains(message))
                    tracks.Add(message);
            }
        }

        private static bool IsMusic(string kind)
        {
            return string.Equals(kind, "audio", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetOldestMessageId(IList<ChatMessageViewModel> messages)
        {
            var id = 0;
            if (messages == null) return id;

            for (var i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                if (message == null || message.Id <= 0) continue;
                if (id == 0 || message.Id < id) id = message.Id;
            }
            return id;
        }

        private void MusicPlayer_PlaybackStarted(object sender, FfmpegAudioPlaybackEndedEventArgs e)
        {
            _currentTrackIndex = FindTrackIndex(e == null ? null : e.DataContext);
        }

        private void MusicPlayer_PlaybackEnded(object sender, FfmpegAudioPlaybackEndedEventArgs e)
        {
            var index = FindTrackIndex(e == null ? null : e.DataContext);
            if (index < 0) index = _currentTrackIndex;
            PlayTrackAt(index + 1);
        }

        private void MusicPlayer_NextRequested(object sender, FfmpegAudioPlaybackEndedEventArgs e)
        {
            var index = FindTrackIndex(e == null ? null : e.DataContext);
            if (index < 0) index = _currentTrackIndex;
            PlayTrackAt(index + 1);
        }

        private void MusicPlayer_PreviousRequested(object sender, FfmpegAudioPlaybackEndedEventArgs e)
        {
            var index = FindTrackIndex(e == null ? null : e.DataContext);
            if (index < 0) index = _currentTrackIndex;
            PlayTrackAt(index - 1);
        }

        private int FindTrackIndex(object track)
        {
            if (track == null || _currentTracks == null) return -1;
            for (var i = 0; i < _currentTracks.Count; i++)
            {
                if (object.ReferenceEquals(_currentTracks[i], track))
                    return i;
            }
            return -1;
        }

        private async void PlayTrackAt(int index)
        {
            if (_currentTracks == null || _currentTracks.Count == 0) return;

            if (index >= _currentTracks.Count) index = 0;
            if (index < 0) index = _currentTracks.Count - 1;

            _currentTrackIndex = index;
            var track = _currentTracks[index];

            MusicList.ScrollIntoView(track);
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async delegate
            {
                await System.Threading.Tasks.Task.Delay(30);

                var container = MusicList.ContainerFromItem(track) as DependencyObject;
                var player = FindChild<FfmpegMusicPlayerControl>(container);
                if (player == null)
                {
                    MusicList.ScrollIntoView(track);
                    await System.Threading.Tasks.Task.Delay(40);
                    container = MusicList.ContainerFromItem(track) as DependencyObject;
                    player = FindChild<FfmpegMusicPlayerControl>(container);
                }

                if (player != null)
                    await player.PlayAsync();
            });
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            var count = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                var typed = child as T;
                if (typed != null) return typed;

                var nested = FindChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private void MusicPlayer_SourceRequested(object sender, FfmpegAudioSourceRequestedEventArgs e)
        {
            if (e == null) return;

            var message = e.DataContext as ChatMessageViewModel;
            if (message != null)
            {
                e.ReloadTask = DownloadMessageMusicAsync(message);
                return;
            }

            var item = e.DataContext as ChatMediaItemViewModel;
            if (item != null)
                e.ReloadTask = DownloadMediaItemMusicAsync(item);
        }

        private async System.Threading.Tasks.Task<bool> DownloadMessageMusicAsync(ChatMessageViewModel message)
        {
            if (message == null || _chat == null) return false;
            if (!string.IsNullOrEmpty(message.MediaFileUri)) return true;

            message.IsMediaDownloading = true;
            try
            {
                await TelegramService.Instance.DownloadMessageMediaAsync(_chat, message);
                return !string.IsNullOrEmpty(message.MediaFileUri);
            }
            catch
            {
                return false;
            }
            finally
            {
                message.IsMediaDownloading = false;
            }
        }

        private async System.Threading.Tasks.Task<bool> DownloadMediaItemMusicAsync(ChatMediaItemViewModel item)
        {
            if (item == null) return false;
            if (!string.IsNullOrEmpty(item.MediaFileUri)) return true;

            item.IsMediaDownloading = true;
            try
            {
                await TelegramService.Instance.DownloadMessageMediaAsync(item);
                return !string.IsNullOrEmpty(item.MediaFileUri);
            }
            catch
            {
                return false;
            }
            finally
            {
                item.IsMediaDownloading = false;
            }
        }

        private void DragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element == null || e == null) return;

            if (!element.CapturePointer(e.Pointer)) return;

            _isDragging = true;
            _dragStartY = e.GetCurrentPoint(element).Position.Y;
            _dragStartTransformY = SheetTransform.Y;
            e.Handled = true;
        }

        private void DragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging || e == null) return;
            var element = sender as UIElement;
            if (element == null) return;

            var currentY = e.GetCurrentPoint(element).Position.Y;
            var delta = currentY - _dragStartY;
            var newY = _dragStartTransformY + delta;

            // The sheet may only be dragged downward. Pulling upward keeps it
            // fully open instead of exposing empty space below it.
            if (newY < 0) newY = 0;
            if (newY > SheetHeight) newY = SheetHeight;

            SheetTransform.Y = newY;
            e.Handled = true;
        }

        private void DragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;

            var element = sender as UIElement;
            if (element != null && e != null)
            {
                try { element.ReleasePointerCapture(e.Pointer); }
                catch { }
            }

            // Once pulled far enough, finish the closing animation
            // automatically. Otherwise snap the panel back into place.
            if (SheetTransform.Y >= CloseThreshold)
                HideFromCurrentPosition();
            else
                AnimateTo(0, false);

            if (e != null) e.Handled = true;
        }

        private void HideFromCurrentPosition()
        {
            if (!_isShown) return;
            _isShown = false;
            _loadVersion++;
            AnimateTo(SheetHeight, true);
        }

        private void StopSheetStoryboard()
        {
            if (_sheetStoryboard != null)
            {
                _sheetStoryboard.Stop();
                _sheetStoryboard = null;
            }
        }

        private void AnimateTo(double targetY, bool collapseAfter)
        {
            StopSheetStoryboard();

            // Capture the real current position, then set the base value to the target so
            // that when the animation stops it settles exactly there (no end-of-slide jump).
            var fromY = SheetTransform.Y;
            SheetTransform.Y = targetY;

            var animation = new DoubleAnimation
            {
                From = fromY,
                To = targetY,
                Duration = new Duration(TimeSpan.FromMilliseconds(190)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };

            Storyboard.SetTarget(animation, SheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Completed += delegate
            {
                if (collapseAfter && !_isShown)
                    Visibility = Visibility.Collapsed;
            };

            _sheetStoryboard = storyboard;
            storyboard.Begin();
        }

        private void Overlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            Hide();
            if (e != null) e.Handled = true;
        }

        private void SheetPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (e != null) e.Handled = true;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_currentTracks == null || _currentTracks.Count == 0) return;

            var query = SearchBox.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                MusicList.ItemsSource = _currentTracks;
            }
            else
            {
                var filtered = new List<object>();
                for (var i = 0; i < _currentTracks.Count; i++)
                {
                    var track = _currentTracks[i];
                    var title = GetTrackTitle(track);
                    if (title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        filtered.Add(track);
                }
                MusicList.ItemsSource = filtered;
            }
        }

        private static string GetTrackTitle(object track)
        {
            var message = track as ChatMessageViewModel;
            if (message != null)
            {
                var name = message.MediaFileName ?? message.MediaTitle ?? string.Empty;
                var performer = message.MediaPerformer;
                if (!string.IsNullOrEmpty(performer))
                    return performer + " - " + name;
                if (!string.IsNullOrEmpty(name))
                    return name;
                return message.Text ?? string.Empty;
            }

            var item = track as ChatMediaItemViewModel;
            if (item != null)
            {
                var name = item.MediaFileName ?? item.MediaTitle ?? string.Empty;
                var performer = item.MediaPerformer;
                if (!string.IsNullOrEmpty(performer))
                    return performer + " - " + name;
                return name;
            }

            return string.Empty;
        }

        private void DownloadTrackButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;

            var parent = btn.Parent as Windows.UI.Xaml.DependencyObject;
            while (parent != null)
            {
                var container = parent as ContentControl;
                if (container != null && container.DataContext != null)
                {
                    DownloadTrack(container.DataContext);
                    return;
                }
                parent = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
            }
        }

        private void ForwardTrackButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn == null) return;

            var parent = btn.Parent as Windows.UI.Xaml.DependencyObject;
            while (parent != null)
            {
                var container = parent as ContentControl;
                if (container != null && container.DataContext != null)
                {
                    ForwardTrack(container.DataContext);
                    return;
                }
                parent = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
            }
        }

        private async void ForwardTrack(object track)
        {
            var message = track as ChatMessageViewModel;
            var item = track as ChatMediaItemViewModel;
            if (message == null && item != null)
                message = item.OwnerMessage;
            if (message == null || !message.CanForward || _chat == null) return;

            try
            {
                var picker = new ChatPickerSheet();
                var selected = await picker.ShowAsync("Send music to");
                if (selected == null) return;

                await TelegramService.Instance.ForwardMessageAsync(_chat, message, selected);
            }
            catch { }
        }

        private async void DownloadTrack(object track)
        {
            var message = track as ChatMessageViewModel;
            if (message != null)
            {
                if (_chat == null) return;

                try
                {
                    if (string.IsNullOrEmpty(message.MediaFileUri))
                    {
                        message.IsMediaDownloading = true;
                        await TelegramService.Instance.DownloadMessageMediaAsync(_chat, message);
                    }

                    if (string.IsNullOrEmpty(message.MediaFileUri))
                        return;

                    var sourceFile = await GetStorageFileFromUri(message.MediaFileUri);
                    if (sourceFile == null) return;

                    var targetName = SanitizeFileName(message.MediaFileName, sourceFile.Name);
                    var targetFile = await PickSaveFileAsync(targetName);
                    if (targetFile == null) return;

                    await CopyFileAsync(sourceFile, targetFile);
                }
                catch { }
                finally
                {
                    message.IsMediaDownloading = false;
                }
                return;
            }

            var item = track as ChatMediaItemViewModel;
            if (item != null)
            {
                try
                {
                    if (string.IsNullOrEmpty(item.MediaFileUri))
                    {
                        item.IsMediaDownloading = true;
                        await TelegramService.Instance.DownloadMessageMediaAsync(item);
                    }

                    if (string.IsNullOrEmpty(item.MediaFileUri))
                        return;

                    var sourceFile = await GetStorageFileFromUri(item.MediaFileUri);
                    if (sourceFile == null) return;

                    var targetName = SanitizeFileName(item.MediaFileName, sourceFile.Name);
                    var targetFile = await PickSaveFileAsync(targetName);
                    if (targetFile == null) return;

                    await CopyFileAsync(sourceFile, targetFile);
                }
                catch { }
                finally
                {
                    item.IsMediaDownloading = false;
                }
            }
        }

        private static async System.Threading.Tasks.Task<StorageFile> GetStorageFileFromUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return null;

            try
            {
                Uri parsed;
                if (Uri.TryCreate(uri, UriKind.Absolute, out parsed) &&
                    string.Equals(parsed.Scheme, "file", StringComparison.OrdinalIgnoreCase))
                {
                    var localPath = parsed.LocalPath;
                    if (!string.IsNullOrEmpty(localPath))
                    {
                        try { localPath = Uri.UnescapeDataString(localPath); }
                        catch { }
                        return await StorageFile.GetFileFromPathAsync(localPath);
                    }
                }
            }
            catch { }

            try
            {
                if (uri.Length > 3 && uri[1] == ':' && (uri[2] == '\\' || uri[2] == '/'))
                    return await StorageFile.GetFileFromPathAsync(uri.Replace('/', '\\'));
            }
            catch { }

            try
            {
                var folderItem = await ApplicationData.Current.LocalFolder.TryGetItemAsync("chat_media");
                var folder = folderItem as StorageFolder;
                if (folder != null)
                {
                    var fileName = System.IO.Path.GetFileName(uri);
                    if (!string.IsNullOrEmpty(fileName))
                        return await folder.GetFileAsync(fileName);
                }
            }
            catch { }

            return null;
        }

        private static string SanitizeFileName(string name, string fallback)
        {
            if (string.IsNullOrEmpty(name)) name = fallback;
            if (string.IsNullOrEmpty(name)) name = "file";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static string GetFileExtension(string name)
        {
            var ext = System.IO.Path.GetExtension(name);
            return string.IsNullOrEmpty(ext) ? ".mp3" : ext;
        }

        private static async System.Threading.Tasks.Task<StorageFile> PickSaveFileAsync(string fileName)
        {
            try
            {
                var picker = new FileSavePicker();
                picker.SuggestedStartLocation = PickerLocationId.Downloads;
                picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(fileName);
                picker.FileTypeChoices.Add("Audio", new System.Collections.Generic.List<string> { GetFileExtension(fileName) });
                return await picker.PickSaveFileAsync();
            }
            catch { return null; }
        }

        private static async System.Threading.Tasks.Task CopyFileAsync(StorageFile source, StorageFile target)
        {
            using (var input = await source.OpenStreamForReadAsync())
            using (var output = await target.OpenStreamForWriteAsync())
            {
                output.SetLength(0);
                await input.CopyToAsync(output);
            }
        }

    }
}
