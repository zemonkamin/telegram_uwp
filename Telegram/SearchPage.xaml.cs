using System;
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
    public sealed partial class SearchPage : Page
    {
        private const int DefaultPreviewLimit = 10;
        private static List<ChatViewModel> _cachedDefaultChats;
        private static int _cacheResetVersion;
        private int _appliedCacheResetVersion;
        private int _searchVersion;
        private bool _backRequestedAttached;
        private bool _loadingDefaultChats;
        private List<ChatViewModel> _defaultChats = new List<ChatViewModel>();

        public static void ClearCache()
        {
            _cachedDefaultChats = null;
            _cacheResetVersion++;
        }

        public SearchPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ConfigureSystemBackButton(true);
            ApplyExternalCacheReset();
            ApplyCachedDefaultChats();
            if (SearchTextBox != null)
                SearchTextBox.Focus(FocusState.Programmatic);
            await LoadDefaultChatsAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _searchVersion++;
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

        private async void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = SearchTextBox == null ? string.Empty : SearchTextBox.Text;
            await StartSearchAsync(text);
        }

        private async System.Threading.Tasks.Task StartSearchAsync(string text)
        {
            var query = (text ?? string.Empty).Trim();
            var version = ++_searchVersion;

            if (query.Length < 2)
            {
                ShowDefaultChats();
                if (_defaultChats.Count == 0)
                    await LoadDefaultChatsAsync();
                return;
            }

            SetLoading(true);
            HideEmpty();
            try
            {
                var results = await TelegramService.Instance.SearchChatsAsync(query);
                if (version != _searchVersion) return;
                SetLoading(false);
                ResultList.ItemsSource = results;
                if (results == null || results.Count == 0)
                    ShowEmpty("Nothing found");
                else
                    HideEmpty();
            }
            catch (Exception ex)
            {
                if (version != _searchVersion) return;
                SetLoading(false);
                ResultList.ItemsSource = null;
                ShowEmpty("Search error: " + ex.Message);
            }
        }

        private void ResultList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chat = e.ClickedItem as ChatViewModel;
            if (chat == null || Frame == null) return;

            if (chat.IsForum && !chat.IsForumTopic && chat.PeerType == "channel")
            {
                if (AdaptiveShellNavigationService.NavigateLeft(typeof(TopicListPage), chat))
                    return;
                Frame.Navigate(typeof(TopicListPage), chat);
                return;
            }

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

        private async System.Threading.Tasks.Task LoadDefaultChatsAsync()
        {
            if (_loadingDefaultChats) return;
            _loadingDefaultChats = true;
            var currentQuery = SearchTextBox == null ? string.Empty : SearchTextBox.Text;
            if (string.IsNullOrWhiteSpace(currentQuery))
                SetLoading(true);

            try
            {
                var page = await TelegramService.Instance.GetChatsPageAsync(-1, 0, DefaultPreviewLimit);
                var chats = page == null ? null : page.Item1;
                _cachedDefaultChats = CopyChats(chats);
                _defaultChats = CopyChats(chats);

                currentQuery = SearchTextBox == null ? string.Empty : SearchTextBox.Text;
                if (string.IsNullOrWhiteSpace(currentQuery))
                    ShowDefaultChats();
            }
            catch (Exception ex)
            {
                currentQuery = SearchTextBox == null ? string.Empty : SearchTextBox.Text;
                if (string.IsNullOrWhiteSpace(currentQuery))
                    ShowEmpty("Chats preview error: " + ex.Message);
            }

            currentQuery = SearchTextBox == null ? string.Empty : SearchTextBox.Text;
            if (string.IsNullOrWhiteSpace(currentQuery))
                SetLoading(false);
            _loadingDefaultChats = false;
        }

        private void ApplyCachedDefaultChats()
        {
            if (_cachedDefaultChats == null || _cachedDefaultChats.Count == 0) return;
            _defaultChats = CopyChats(_cachedDefaultChats);
            ShowDefaultChats();
        }

        private void ShowDefaultChats()
        {
            SetLoading(false);
            ResultList.ItemsSource = _defaultChats;
            if (_defaultChats == null || _defaultChats.Count == 0)
                ShowEmpty("Loading chats...");
            else
                HideEmpty();
        }

        private void SetLoading(bool active)
        {
            StatusBarLoadingIndicator.SetActive(active, TopLoadingBar);
        }

        private void ShowEmpty(string text)
        {
            EmptyText.Text = text ?? string.Empty;
            EmptyText.Visibility = string.IsNullOrEmpty(EmptyText.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void HideEmpty()
        {
            EmptyText.Text = string.Empty;
            EmptyText.Visibility = Visibility.Collapsed;
        }

        private void ApplyExternalCacheReset()
        {
            if (_appliedCacheResetVersion == _cacheResetVersion) return;
            _appliedCacheResetVersion = _cacheResetVersion;
            _searchVersion++;
            _loadingDefaultChats = false;
            _defaultChats.Clear();
            if (ResultList != null)
                ResultList.ItemsSource = null;
            HideEmpty();
        }

        private static List<ChatViewModel> CopyChats(IList<ChatViewModel> source)
        {
            var result = new List<ChatViewModel>();
            if (source == null) return result;
            for (var i = 0; i < source.Count; i++)
                if (source[i] != null) result.Add(source[i]);
            return result;
        }
    }
}
