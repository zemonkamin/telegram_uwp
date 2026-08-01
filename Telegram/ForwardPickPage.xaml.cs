using System;
using System.Collections.Generic;
using Telegram.Models;
using Telegram.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram
{
    public sealed partial class ForwardPickPage : Page
    {
        private List<ChatViewModel> _allChats = new List<ChatViewModel>();
        private List<FolderViewModel> _folders = new List<FolderViewModel>();

        public ForwardPickPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await LoadChatsAsync();
        }

        private async System.Threading.Tasks.Task LoadChatsAsync()
        {
            try
            {
                _folders = await TelegramService.Instance.GetFoldersAsync();
            }
            catch
            {
                _folders = new List<FolderViewModel>();
            }

            if (_folders == null || _folders.Count == 0)
            {
                _folders = new List<FolderViewModel>
                {
                    new FolderViewModel { Id = -1, Title = "All chats" }
                };
            }

            for (var i = 0; i < _folders.Count; i++)
            {
                _folders[i].IsSelected = i == 0;
            }

            FolderList.ItemsSource = _folders;

            if (_folders.Count > 0)
            {
                FolderList.SelectedIndex = 0;
                await LoadFolderChatsAsync(_folders[0].Id);
            }
        }

        private async System.Threading.Tasks.Task LoadFolderChatsAsync(int folderId)
        {
            var result = new List<ChatViewModel>();
            try
            {
                var page = await TelegramService.Instance.GetChatsPageAsync(folderId, 0, 0);
                var chats = page == null ? null : page.Item1;
                if (chats != null)
                {
                    for (var i = 0; i < chats.Count; i++)
                    {
                        var chat = chats[i];
                        if (chat != null && !chat.IsArchiveEntry && chat.CanSendMessages)
                            result.Add(chat);
                    }
                }
            }
            catch { }

            _allChats = result;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var query = SearchBox != null ? SearchBox.Text : string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                ChatList.ItemsSource = _allChats;
            }
            else
            {
                var filtered = new List<ChatViewModel>();
                for (var i = 0; i < _allChats.Count; i++)
                {
                    var chat = _allChats[i];
                    if (chat.Title != null && chat.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        filtered.Add(chat);
                }
                ChatList.ItemsSource = filtered;
            }

            var items = ChatList.ItemsSource as System.Collections.IList;
            EmptyText.Visibility = items == null || items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var index = FolderList.SelectedIndex;
            if (index < 0 || index >= _folders.Count) return;

            for (var i = 0; i < _folders.Count; i++)
                _folders[i].IsSelected = i == index;

            var ignored = LoadFolderChatsAsync(_folders[index].Id);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void ChatList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chat = e.ClickedItem as ChatViewModel;
            if (chat == null) return;

            ForwardPickResult.SelectedChat = chat;
            if (Frame.CanGoBack)
                Frame.GoBack();
        }
    }

    public static class ForwardPickResult
    {
        public static ChatViewModel SelectedChat { get; set; }
    }
}
