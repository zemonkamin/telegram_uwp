using System;
using System.Collections.Generic;
using System.ComponentModel;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Telegram.Models
{
    public sealed class ChatViewModel : INotifyPropertyChanged
    {
        private string _avatarUri;
        private ImageSource _avatarImageSource;
        private string _avatarImageSourceUri;
        private bool _avatarIsPreview;
        private string _topicIconUri;
        private int _pinnedMessageId;
        private string _pinnedMessagePreview;
        private List<int> _pinnedMessageIds = new List<int>();
        private int _currentPinnedMessageIndex;
        private int _unreadCount;
        private int _readOutboxMaxId;
        private bool _messageViewersUnavailable;
        private bool _isMuted;

        public event PropertyChangedEventHandler PropertyChanged;
        public long PeerId { get; set; }
        public long UserId { get; set; }
        public string PeerType { get; set; }
        public string PeerKey { get; set; }
        public long AccessHash { get; set; }
        public string Title { get; set; }
        public string LastMessage { get; set; }
        public string LastMessageSenderName { get; set; }
        public int LastMessageDate { get; set; }
        public bool LastMessageIsOutgoing { get; set; }
        public int UnreadCount
        {
            get { return _unreadCount; }
            set
            {
                if (_unreadCount == value) return;
                _unreadCount = value;
                OnPropertyChanged("UnreadCount");
                OnPropertyChanged("UnreadBadge");
                OnPropertyChanged("UnreadBadgeVisibility");
            }
        }
        public int TopMessageId { get; set; }
        // Stable TDLib id of the current top message. Unlike TopMessageId, this value
        // does not depend on the process-local compact-id table and can be persisted.
        public long NotificationMessageId { get; set; }
        public int ReadOutboxMaxId
        {
            get { return _readOutboxMaxId; }
            set
            {
                if (_readOutboxMaxId == value) return;
                _readOutboxMaxId = value;
                OnPropertyChanged("ReadOutboxMaxId");
            }
        }
        public int FolderId { get; set; }

        // FolderId reports the chat list the chat was loaded for, so it turns into
        // the custom folder id when a chat belongs to one. IsArchived stays true for
        // every chat that sits in the archive, which is what notifications look at.
        public bool IsArchived { get; set; }
        public bool IsMuted
        {
            get { return _isMuted; }
            set
            {
                if (_isMuted == value) return;
                _isMuted = value;
                OnPropertyChanged("IsMuted");
                OnPropertyChanged("UnreadBadgeBrush");
                OnPropertyChanged("MutedVisibility");
            }
        }
        public bool IsContact { get; set; }
        public bool IsBot { get; set; }
        public long ReplyMarkupMessageId { get; set; }
        public long BotUserId { get; set; }
        public string BotDescription { get; set; }
        public string BotMenuButtonType { get; set; }
        public string BotMenuButtonText { get; set; }
        public string BotMenuButtonUrl { get; set; }
        public List<BotCommandViewModel> BotCommands { get; set; } = new List<BotCommandViewModel>();
        public bool IsGroup { get; set; }
        public bool IsBroadcast { get; set; }
        public bool IsChannel { get; set; }
        public bool IsJoined { get; set; } = true;
        public bool CanJoin { get; set; }
        public bool IsForum { get; set; }
        public bool IsForumTopic { get; set; }
        public bool IsCommentsThread { get; set; }
        public int TopicId { get; set; }
        public int TopicRootMessageId { get; set; }
        public int TopicIconColor { get; set; }
        public long TopicIconEmojiId { get; set; }
        public bool IsTopicClosed { get; set; }
        public string ParentPeerType { get; set; }
        public long ParentPeerId { get; set; }
        public string ParentPeerKey { get; set; }
        public long ParentAccessHash { get; set; }
        public string ParentTitle { get; set; }
        public bool CanSendMessages { get; set; }
        public bool CanPinMessages { get; set; }
        public bool CanDeleteMessages { get; set; }
        public bool NoForwards { get; set; }
        public bool MessageViewersUnavailable
        {
            get { return _messageViewersUnavailable; }
            set
            {
                if (_messageViewersUnavailable == value) return;
                _messageViewersUnavailable = value;
                OnPropertyChanged("MessageViewersUnavailable");
            }
        }
        public bool IsArchiveEntry { get; set; }
        public bool IsPinned { get; set; }
        public int PinnedMessageId
        {
            get { return _pinnedMessageId; }
            set
            {
                if (_pinnedMessageId == value) return;
                _pinnedMessageId = value;
                OnPropertyChanged("PinnedMessageId");
            }
        }

        public string PinnedMessagePreview
        {
            get { return _pinnedMessagePreview; }
            set
            {
                if (_pinnedMessagePreview == value) return;
                _pinnedMessagePreview = value;
                OnPropertyChanged("PinnedMessagePreview");
            }
        }

        public List<int> PinnedMessageIds
        {
            get { return _pinnedMessageIds; }
            set
            {
                _pinnedMessageIds = value ?? new List<int>();
                OnPropertyChanged("PinnedMessageIds");
                OnPropertyChanged("PinnedMessageCount");
                OnPropertyChanged("PinnedCounterText");
            }
        }

        public int CurrentPinnedMessageIndex
        {
            get { return _currentPinnedMessageIndex; }
            set
            {
                if (_currentPinnedMessageIndex == value) return;
                _currentPinnedMessageIndex = value;
                OnPropertyChanged("CurrentPinnedMessageIndex");
                OnPropertyChanged("PinnedCounterText");
            }
        }

        public int PinnedMessageCount
        {
            get { return PinnedMessageIds == null ? 0 : PinnedMessageIds.Count; }
        }

        public string PinnedCounterText
        {
            get { return PinnedMessageCount > 1 ? (CurrentPinnedMessageIndex + 1).ToString() + "/" + PinnedMessageCount.ToString() : string.Empty; }
        }

        public int SubscriberCount { get; set; }
        public int OnlineCount { get; set; }

        private int _lastSeenUnixTime;
        public int LastSeenUnixTime
        {
            get { return _lastSeenUnixTime; }
            set { if (_lastSeenUnixTime != value) { _lastSeenUnixTime = value; OnPropertyChanged("LastSeenUnixTime"); OnPropertyChanged("SubtitleText"); } }
        }

        private string _lastSeenText = "";
        public string LastSeenText
        {
            get { return _lastSeenText; }
            set { if (_lastSeenText != value) { _lastSeenText = value ?? ""; OnPropertyChanged("LastSeenText"); OnPropertyChanged("SubtitleText"); } }
        }

        private string _userStatusKind = "";
        public string UserStatusKind
        {
            get { return _userStatusKind; }
            set { if (_userStatusKind != value) { _userStatusKind = value ?? ""; OnPropertyChanged("UserStatusKind"); OnPropertyChanged("SubtitleText"); } }
        }
        public string Username { get; set; }
        public string Phone { get; set; }
        public string Bio { get; set; }
        public string Birthdate { get; set; }
        public string BirthdayText { get; set; }
        public ChatViewModel ProfileChannel { get; set; }
        public string MemberRole { get; set; }

        public ImageSource AvatarImageSource
        {
            get
            {
                var uri = AvatarUri;
                if (string.IsNullOrEmpty(uri)) return null;
                // Cached: the chat list re-reads this on every binding pass, and a new
                // BitmapImage each time means re-decoding the same avatar over and over.
                if (_avatarImageSource != null && string.Equals(_avatarImageSourceUri, uri, StringComparison.OrdinalIgnoreCase))
                    return _avatarImageSource;
                try
                {
                    var image = new BitmapImage();
                    image.DecodePixelWidth = 64;
                    image.UriSource = new Uri(uri);
                    _avatarImageSource = image;
                    _avatarImageSourceUri = uri;
                    return image;
                }
                catch { return null; }
            }
        }

        public double AvatarImageOpacity
        {
            get { return string.IsNullOrEmpty(AvatarUri) ? 0 : 1; }
        }

        public Visibility InitialsVisibility
        {
            get { return string.IsNullOrEmpty(AvatarUri) ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string IconText { get; set; }
        public string TypeIcon { get; set; }
        public string TypeGlyph { get; set; }

        public bool IsSavedMessages
        {
            get
            {
                var peerType = (PeerType ?? string.Empty).Trim().ToLowerInvariant();
                if (peerType == "self" || peerType == "saved") return true;
                var key = (PeerKey ?? string.Empty).Trim().ToLowerInvariant();
                return key == "self" ||
                       key == "saved" ||
                       key == "savedmessages" ||
                       key == "saved_messages" ||
                       key.StartsWith("self:", StringComparison.Ordinal);
            }
        }

        public string DisplayTitle
        {
            get { return IsSavedMessages ? "Saved Messages" : Title; }
        }

        public string TopicIconUri
        {
            get { return _topicIconUri; }
            set
            {
                if (_topicIconUri == value) return;
                _topicIconUri = value;
                OnPropertyChanged("TopicIconUri");
                OnPropertyChanged("TopicCustomIconVisibility");
                OnPropertyChanged("TopicIconTextVisibility");
            }
        }

        public Visibility TopicCustomIconVisibility
        {
            get { return string.IsNullOrEmpty(TopicIconUri) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility TopicIconTextVisibility
        {
            get { return string.IsNullOrEmpty(TopicIconUri) ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Brush TopicIconBrush
        {
            get
            {
                var color = TopicIconColor == 0 ? 0x6fb9f0 : TopicIconColor;
                return new SolidColorBrush(Color.FromArgb(
                    255,
                    (byte)((color >> 16) & 255),
                    (byte)((color >> 8) & 255),
                    (byte)(color & 255)));
            }
        }

        public string AvatarUri
        {
            get { return _avatarUri; }
            set
            {
                if (_avatarUri == value) return;
                _avatarUri = value;
                OnPropertyChanged("AvatarUri");
                OnPropertyChanged("AvatarImageSource");
                OnPropertyChanged("AvatarImageOpacity");
                OnPropertyChanged("InitialsVisibility");
            }
        }

        public bool AvatarIsPreview
        {
            get { return _avatarIsPreview; }
            set
            {
                if (_avatarIsPreview == value) return;
                _avatarIsPreview = value;
                OnPropertyChanged("AvatarIsPreview");
            }
        }
        public long AvatarPhotoId { get; set; }
        public int AvatarDcId { get; set; }
        public byte[] AvatarStrippedThumb { get; set; }

        public string UnreadBadge
        {
            get { return UnreadCount > 0 ? UnreadCount.ToString() : string.Empty; }
        }

        public Visibility UnreadBadgeVisibility
        {
            get { return UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Brush UnreadBadgeBrush
        {
            get
            {
                return IsMuted
                    ? new SolidColorBrush(Color.FromArgb(255, 145, 145, 145))
                    : CreateAccentBrush();
            }
        }

        private static Brush CreateAccentBrush()
        {
            try
            {
                if (Application.Current != null && Application.Current.Resources != null)
                {
                    object brush;
                    if (Application.Current.Resources.TryGetValue("SystemControlHighlightAccentBrush", out brush))
                    {
                        var accentBrush = brush as Brush;
                        if (accentBrush != null) return accentBrush;
                    }
                }
            }
            catch
            {
            }

            try
            {
                return new SolidColorBrush(new UISettings().GetColorValue(UIColorType.Accent));
            }
            catch
            {
                return new SolidColorBrush(Color.FromArgb(255, 0, 120, 215));
            }
        }

        public Visibility PinnedIconVisibility
        {
            get { return IsPinned && !IsArchiveEntry ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string DisplayLastMessage
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(LastMessage))
                {
                    if (ShouldShowLastMessageSender)
                    {
                        var prefix = LastMessageSenderName.Trim() + ": ";
                        if (!LastMessage.StartsWith(prefix, StringComparison.Ordinal))
                            return prefix + LastMessage;
                    }

                    return LastMessage;
                }
                var subtitle = SubtitleText;
                return string.IsNullOrWhiteSpace(subtitle) ? " " : subtitle;
            }
        }

        private bool ShouldShowLastMessageSender
        {
            get
            {
                return !IsArchiveEntry &&
                       IsGroup &&
                       !string.IsNullOrWhiteSpace(LastMessageSenderName);
            }
        }

        public string LastTimeText
        {
            get
            {
                if (LastMessageDate <= 0) return string.Empty;

                try
                {
                    var utc = new DateTime(1970, 1, 1).AddSeconds(LastMessageDate);
                    return utc.ToLocalTime().ToString("H:mm");
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        public string OutgoingGlyph
        {
            get { return "✓✓"; }
        }

        public Visibility OutgoingGlyphVisibility
        {
            get { return LastMessageIsOutgoing ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string MutedGlyph
        {
            get { return "\uE7ED"; }
        }

        public Visibility MutedVisibility
        {
            get { return IsMuted ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string SubtitleText
        {
            get
            {
                if (PeerType == "self")
                {
                    if (!string.IsNullOrEmpty(LastSeenText)) return LastSeenText;
                    if (UserStatusKind == "online") return "online";
                    if (LastSeenUnixTime > 0) return FormatLastSeen(LastSeenUnixTime);
                    return string.Empty;
                }

                if (PeerType == "user")
                {
                    if (IsBot) return "bot";
                    if (!string.IsNullOrEmpty(LastSeenText)) return LastSeenText;
                    if (UserStatusKind == "online") return "online";
                    if (LastSeenUnixTime > 0) return FormatLastSeen(LastSeenUnixTime);
                    return string.Empty;
                }

                if (IsForumTopic)
                {
                    if (IsTopicClosed)
                        return string.IsNullOrEmpty(ParentTitle) ? "closed topic" : "closed topic in " + ParentTitle;
                    return string.IsNullOrEmpty(ParentTitle) ? "topic" : "topic in " + ParentTitle;
                }

                if (IsBroadcast || (IsChannel && !IsGroup))
                {
                    if (SubscriberCount > 0)
                        return FormatCount(SubscriberCount) + " subscribers";
                    return IsBroadcast ? "channel" : "channel/chat";
                }

                if (IsGroup || PeerType == "chat" || PeerType == "channel")
                {
                    if (IsForum) return "topics";
                    if (SubscriberCount > 0)
                        return FormatCount(SubscriberCount) + " members";
                    return "group";
                }

                return string.Empty;
            }
        }

        private void OnPropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }


        private static string FormatLastSeen(int unixTime)
        {
            try
            {
                var utc = new DateTime(1970, 1, 1).AddSeconds(unixTime);
                var local = utc.ToLocalTime();
                var now = DateTime.Now;
                var diff = now - local;

                if (diff.TotalMinutes < 1) return "last seen just now";
                if (diff.TotalMinutes < 60) return "last seen " + (int)diff.TotalMinutes + " minutes ago";
                if (diff.TotalHours < 24) return "last seen " + (int)diff.TotalHours + " hours ago";
                if (local.Date == now.Date.AddDays(-1)) return "last seen yesterday at " + local.ToString("H:mm");
                return "last seen " + local.ToString("dd.MM.yyyy");
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string FormatCount(int count)
        {
            if (count >= 1000000) return (count / 1000000.0).ToString("0.#") + "M";
            if (count >= 1000) return (count / 1000.0).ToString("0.#") + "K";
            return count.ToString();
        }
    }
}
