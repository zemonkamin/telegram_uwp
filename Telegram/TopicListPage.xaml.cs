using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using Telegram.Models;
using Telegram.Services;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Telegram
{
    public sealed partial class TopicListPage : Page
    {
        private const int TopicLoadLimit = 100;
        private readonly ObservableCollection<ChatViewModel> _topics = new ObservableCollection<ChatViewModel>();
        private ChatViewModel _forum;
        private bool _backRequestedAttached;
        private int _loadVersion;

        public TopicListPage()
        {
            InitializeComponent();
            TopicList.ItemsSource = _topics;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ConfigureSystemBackButton(true);
            _forum = e.Parameter as ChatViewModel;
            ApplyHeader();
            await LoadTopicsAsync(false);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _loadVersion++;
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
            GoBack();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            GoBack();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadTopicsAsync(true);
        }

        private void Header_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_forum == null || Frame == null) return;
            if (AdaptiveShellNavigationService.NavigateLeft(typeof(GroupProfilePage), _forum))
                return;
            Frame.Navigate(typeof(GroupProfilePage), _forum);
        }

        private void TopicList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var topic = e.ClickedItem as ChatViewModel;
            if (topic == null || Frame == null) return;
            if (AdaptiveShellNavigationService.NavigateChat(topic))
                return;
            Frame.Navigate(typeof(ChatPage), topic);
        }

        private void GoBack()
        {
            if (Frame == null) return;
            if (Frame.CanGoBack) Frame.GoBack();
            else Frame.Navigate(typeof(Chats));
        }

        private void ApplyHeader()
        {
            var title = _forum == null ? string.Empty : _forum.Title;
            HeaderTitle.Text = string.IsNullOrWhiteSpace(title) ? "Topics" : title;
            HeaderSubtitle.Text = "topics";
        }

        private async System.Threading.Tasks.Task LoadTopicsAsync(bool refresh)
        {
            var version = ++_loadVersion;
            SetLoading(true);
            HideEmpty();

            try
            {
                if (_forum == null || !_forum.IsForum || _forum.PeerType != "channel")
                {
                    _topics.Clear();
                    ShowEmpty("This chat has no topics.");
                    return;
                }

                var list = await TelegramService.Instance.GetForumTopicsAsync(_forum, TopicLoadLimit);
                if (version != _loadVersion) return;

                var topics = NormalizeTopics(list);
                _topics.Clear();
                for (var i = 0; i < topics.Count; i++)
                    _topics.Add(topics[i]);

                if (_topics.Count == 0)
                    ShowEmpty(refresh ? "No topics found." : "Loading topics...");
            }
            catch (Exception ex)
            {
                if (version != _loadVersion) return;
                _topics.Clear();
                ShowEmpty("Topics loading error: " + ex.Message);
            }
            finally
            {
                if (version == _loadVersion)
                    SetLoading(false);
            }
        }

        private List<ChatViewModel> NormalizeTopics(IList<ChatViewModel> loaded)
        {
            var result = new List<ChatViewModel>();
            var seen = new HashSet<string>();
            var hasGeneral = false;

            if (loaded != null)
            {
                for (var i = 0; i < loaded.Count; i++)
                {
                    if (loaded[i] != null && loaded[i].TopicId == 1) hasGeneral = true;
                    AddTopic(result, seen, loaded[i]);
                }
            }

            if (!hasGeneral)
                AddTopic(result, seen, CreateGeneralTopic());

            result.Sort(delegate(ChatViewModel a, ChatViewModel b)
            {
                if (a == null && b == null) return 0;
                if (a == null) return 1;
                if (b == null) return -1;
                if (a.TopicId == 1 && b.TopicId != 1) return -1;
                if (a.TopicId != 1 && b.TopicId == 1) return 1;
                if (a.IsPinned != b.IsPinned) return a.IsPinned ? -1 : 1;
                var date = b.LastMessageDate.CompareTo(a.LastMessageDate);
                if (date != 0) return date;
                return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });

            return result;
        }

        private void AddTopic(List<ChatViewModel> result, HashSet<string> seen, ChatViewModel topic)
        {
            if (result == null || seen == null || topic == null) return;
            var key = string.IsNullOrEmpty(topic.PeerKey) ? BuildTopicKey(topic.TopicId) : topic.PeerKey;
            if (string.IsNullOrEmpty(key) || seen.Contains(key)) return;
            seen.Add(key);
            result.Add(topic);
        }

        private ChatViewModel CreateGeneralTopic()
        {
            var title = "General";
            return new ChatViewModel
            {
                PeerId = _forum.PeerId,
                PeerType = _forum.PeerType,
                PeerKey = BuildTopicKey(1),
                AccessHash = _forum.AccessHash,
                Title = title,
                LastMessage = _forum.LastMessage,
                LastMessageDate = _forum.LastMessageDate,
                LastMessageIsOutgoing = _forum.LastMessageIsOutgoing,
                UnreadCount = _forum.UnreadCount,
                TopMessageId = _forum.TopMessageId,
                ReadOutboxMaxId = _forum.ReadOutboxMaxId,
                FolderId = _forum.FolderId,
                IsMuted = _forum.IsMuted,
                IsGroup = true,
                IsChannel = true,
                IsForum = true,
                IsForumTopic = true,
                TopicId = 1,
                TopicRootMessageId = 1,
                ParentPeerType = _forum.PeerType,
                ParentPeerId = _forum.PeerId,
                ParentPeerKey = _forum.PeerKey,
                ParentAccessHash = _forum.AccessHash,
                ParentTitle = _forum.Title,
                CanSendMessages = _forum.CanSendMessages,
                CanPinMessages = _forum.CanPinMessages,
                CanDeleteMessages = _forum.CanDeleteMessages,
                NoForwards = _forum.NoForwards,
                SubscriberCount = _forum.SubscriberCount,
                TopicIconColor = 0x6fb9f0,
                IconText = "#"
            };
        }

        private string BuildTopicKey(int topicId)
        {
            if (_forum == null) return null;
            var parentKey = string.IsNullOrEmpty(_forum.PeerKey) ? (_forum.PeerType + ":" + _forum.PeerId.ToString()) : _forum.PeerKey;
            return parentKey + ":topic:" + topicId.ToString();
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
    }
}
