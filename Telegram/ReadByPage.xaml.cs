using System;
using System.Collections.Generic;
using Telegram.Models;
using Telegram.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Telegram
{
    public sealed partial class ReadByPage : Page
    {
        private ChatViewModel _chat;
        private int _messageId;
        private List<ReadByUserItemViewModel> _allUsers = new List<ReadByUserItemViewModel>();
        private bool _openingUser;

        public ReadByPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var target = e.Parameter as ReadByNavigationTarget;
            if (target != null)
            {
                _chat = target.Chat;
                _messageId = target.MessageId;
                if (!string.IsNullOrWhiteSpace(target.HeaderText))
                    SubHeaderTextBlock.Text = target.HeaderText;
                if (target.CachedViewers != null && target.CachedViewers.Count > 0)
                    SetUsers(target.CachedViewers);
            }

            await LoadUsersAsync();
        }

        private async System.Threading.Tasks.Task LoadUsersAsync()
        {
            if (_chat == null || _messageId <= 0)
            {
                ApplyFilter();
                return;
            }

            if (_allUsers.Count == 0)
            {
                EmptyText.Text = "Loading...";
                EmptyText.Visibility = Visibility.Visible;
            }

            try
            {
                var users = await TelegramService.Instance.GetMessageViewersAsync(_chat, _messageId, 50);
                if (users != null)
                    SetUsers(users);
            }
            catch
            {
                if (_allUsers.Count == 0)
                    EmptyText.Text = "Could not load viewers.";
            }

            ApplyFilter();
        }

        private void SetUsers(IList<CommentAvatarViewModel> users)
        {
            var list = new List<ReadByUserItemViewModel>();
            if (users != null)
            {
                for (var i = 0; i < users.Count; i++)
                {
                    var user = users[i];
                    if (user == null || user.PeerId == 0) continue;
                    list.Add(new ReadByUserItemViewModel(user));
                }
            }
            _allUsers = list;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var query = SearchBox != null ? SearchBox.Text : string.Empty;
            List<ReadByUserItemViewModel> source;
            if (string.IsNullOrWhiteSpace(query))
            {
                source = _allUsers;
            }
            else
            {
                source = new List<ReadByUserItemViewModel>();
                for (var i = 0; i < _allUsers.Count; i++)
                {
                    var item = _allUsers[i];
                    if (item != null && item.Title != null && item.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        source.Add(item);
                }
            }

            UserList.ItemsSource = source;
            EmptyText.Text = string.IsNullOrWhiteSpace(query) ? "No viewers yet." : "No users found.";
            EmptyText.Visibility = source == null || source.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private async void UserList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_openingUser) return;
            var item = e.ClickedItem as ReadByUserItemViewModel;
            if (item == null || item.UserId == 0) return;

            _openingUser = true;
            try
            {
                var chat = await TelegramService.Instance.GetPrivateChatAsync(item.UserId);
                if (chat == null) return;

                if (AdaptiveShellNavigationService.NavigateLeft(typeof(UserProfilePage), chat))
                    return;
                Frame.Navigate(typeof(UserProfilePage), chat);
            }
            finally
            {
                _openingUser = false;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
                Frame.GoBack();
        }
    }

    public sealed class ReadByNavigationTarget
    {
        public ChatViewModel Chat { get; set; }
        public int MessageId { get; set; }
        public string HeaderText { get; set; }
        public IList<CommentAvatarViewModel> CachedViewers { get; set; }
    }

    public sealed class ReadByUserItemViewModel
    {
        private readonly CommentAvatarViewModel _user;

        public ReadByUserItemViewModel(CommentAvatarViewModel user)
        {
            _user = user;
        }

        public long UserId { get { return _user == null ? 0 : _user.PeerId; } }
        public string Title { get { return _user == null || string.IsNullOrWhiteSpace(_user.Title) ? "User" : _user.Title; } }
        public string Initials { get { return _user == null || string.IsNullOrWhiteSpace(_user.Initials) ? "?" : _user.Initials; } }
        public ImageSource AvatarImageSource { get { return _user == null ? null : _user.AvatarImageSource; } }
    }
}
