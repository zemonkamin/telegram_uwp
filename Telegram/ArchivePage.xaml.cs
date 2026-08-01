using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using Telegram.Models;
using Telegram.Services;
using Windows.UI.Core;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Telegram
{
    public sealed partial class ArchivePage : Page
    {
        private const int ArchiveFolderId = 1;
        private const int ArchivePageSize = 40;
        private static List<ChatViewModel> _cachedArchiveChats;
        private static int _cacheResetVersion;
        private int _appliedCacheResetVersion;
        private ObservableCollection<ChatViewModel> _chats = new ObservableCollection<ChatViewModel>();
        private HashSet<string> _keys = new HashSet<string>();
        private int _archiveServerOffset;
        private bool _hasMore;
        private bool _loading;
        private bool _backRequestedAttached;

        public static void ClearCache()
        {
            _cachedArchiveChats = null;
            _cacheResetVersion++;
        }

        public ArchivePage()
        {
            InitializeComponent();
            ChatList.ItemsSource = _chats;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ConfigureSystemBackButton(true);
            ApplyExternalCacheReset();
            ApplyCachedArchive();
            await LoadArchiveAsync(true);
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
            if (Frame == null) return;
            e.Handled = true;
            if (Frame.CanGoBack) Frame.GoBack();
            else Frame.Navigate(typeof(Chats));
        }

        private async System.Threading.Tasks.Task LoadArchiveAsync(bool refresh)
        {
            if (_loading) return;
            _loading = true;
            SetLoading(true);
            HideStatus();
            try
            {
                if (refresh) _archiveServerOffset = 0;
                var offset = refresh ? 0 : _archiveServerOffset;
                var page = await TelegramService.Instance.GetChatsPageAsync(ArchiveFolderId, offset, ArchivePageSize, refresh);
                var fresh = page == null ? null : page.Item1;
                _archiveServerOffset += fresh == null ? 0 : fresh.Count;
                if (refresh)
                    ReplaceChats(fresh);
                else
                    AddChats(fresh);
                _cachedArchiveChats = CopyChats(_chats);
                _hasMore = page != null && page.Item2;
                SortChatsForDisplay(_chats);
                LoadMoreButton.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;

                if (_chats.Count == 0)
                    ShowStatus("Archive is empty.");
            }
            catch (Exception ex)
            {
                ShowStatus("Archive error: " + ex.Message);
            }
            SetLoading(false);
            _loading = false;
        }

        private void ApplyCachedArchive()
        {
            if (_cachedArchiveChats == null || _cachedArchiveChats.Count == 0) return;
            ReplaceChats(_cachedArchiveChats);
            _archiveServerOffset = _chats.Count;
            SortChatsForDisplay(_chats);
            LoadMoreButton.Visibility = Visibility.Collapsed;
            HideStatus();
        }

        private void ReplaceChats(IList<ChatViewModel> source)
        {
            _chats.Clear();
            _keys.Clear();
            AddChats(source);
        }

        private void AddChats(IList<ChatViewModel> source)
        {
            if (source == null) return;
            for (var i = 0; i < source.Count; i++)
            {
                var chat = source[i];
                var key = GetChatKey(chat);
                if (string.IsNullOrEmpty(key)) continue;
                if (_keys.Contains(key)) continue;
                _keys.Add(key);
                _chats.Add(chat);
            }
        }

        private static List<ChatViewModel> CopyChats(IList<ChatViewModel> source)
        {
            var result = new List<ChatViewModel>();
            if (source == null) return result;
            for (var i = 0; i < source.Count; i++)
                if (source[i] != null) result.Add(source[i]);
            return result;
        }

        private async void LoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadArchiveAsync(false);
        }

        private void ChatList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chat = e.ClickedItem as ChatViewModel;
            if (chat == null || Frame == null) return;
            if (AdaptiveShellNavigationService.NavigateChat(chat))
                return;
            Frame.Navigate(typeof(ChatPage), chat);
        }

        private void ListItem_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState == HoldingState.Started)
                e.Handled = true;
        }

        private void ListItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void SetLoading(bool active)
        {
            StatusBarLoadingIndicator.SetActive(active, TopLoadingBar);
        }

        private void ShowStatus(string text)
        {
            StatusText.Text = text ?? string.Empty;
            StatusText.Visibility = string.IsNullOrEmpty(StatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void HideStatus()
        {
            StatusText.Text = string.Empty;
            StatusText.Visibility = Visibility.Collapsed;
        }

        private void ApplyExternalCacheReset()
        {
            if (_appliedCacheResetVersion == _cacheResetVersion) return;
            _appliedCacheResetVersion = _cacheResetVersion;
            _loading = false;
            _hasMore = false;
            _archiveServerOffset = 0;
            ReplaceChats(null);
            LoadMoreButton.Visibility = Visibility.Collapsed;
            HideStatus();
        }

        private static string GetChatKey(ChatViewModel chat)
        {
            if (chat == null) return null;
            if (!string.IsNullOrEmpty(chat.PeerKey)) return chat.PeerKey;
            return (chat.PeerType ?? string.Empty) + ":" + chat.PeerId;
        }

        private static void SortChatsForDisplay(IList<ChatViewModel> chats)
        {
            if (chats == null || chats.Count < 2) return;
            var sorted = new List<ChatViewModel>();
            for (var i = 0; i < chats.Count; i++)
                if (chats[i] != null) sorted.Add(chats[i]);
            sorted.Sort(delegate(ChatViewModel a, ChatViewModel b)
            {
                // Pinned chats stay on top, then most-recent first.
                var ap = a != null && a.IsPinned;
                var bp = b != null && b.IsPinned;
                if (ap != bp) return ap ? -1 : 1;
                var ad = a == null ? 0 : a.LastMessageDate;
                var bd = b == null ? 0 : b.LastMessageDate;
                return bd.CompareTo(ad);
            });
            for (var i = 0; i < sorted.Count; i++)
                chats[i] = sorted[i];
        }
    }
}
