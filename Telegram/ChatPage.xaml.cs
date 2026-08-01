using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using FFmpegInterop;
using Telegram.Controls;
using Telegram.Models;
using Telegram.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System.Display;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Core;
using Windows.UI.Xaml.Shapes;
using Windows.UI.Text;

namespace Telegram
{
    public sealed partial class ChatPage : Page
    {
        private ChatViewModel _chat;
        private bool _loading;
        private bool _polling;
        private bool _realtimeEventsSubscribed;
        private bool _realtimeDrainRunning;
        private bool _realtimeDrainAgain;
        private bool _historyLoaded;
        private int _replyToMessageId;
        private ObservableCollection<object> _messages;
        private ObservableCollection<PendingPhotoAttachment> _pendingPhotoAttachments;
        private ObservableCollection<StickerSetViewModel> _stickerSets;
        private ObservableCollection<StickerItemViewModel> _stickerItems;
        private readonly HashSet<string> _messageKeys = new HashSet<string>();
        private readonly HashSet<long> _albumCompletionRequested = new HashSet<long>();
        private readonly Dictionary<long, int> _albumCompletionAttempts = new Dictionary<long, int>();
        private readonly DispatcherTimer _pollTimer;
        private readonly DispatcherTimer _headerTimer;
        private readonly DispatcherTimer _typingCancelTimer;
        private readonly DispatcherTimer _topLoadWatchTimer;
        private readonly DispatcherTimer _recordPressTimer;
        private readonly DispatcherTimer _recordDurationTimer;
        private DateTime _lastTypingActionSentUtc = DateTime.MinValue;
        private bool _typingActionActive;
        private bool _suppressComposerTextChanged;
        private bool _refreshingHeader;
        private bool _autoLoadingOlder;
        private bool _olderLoadQueued;
        private bool _olderPostLoadWorkQueued;
        private bool _topLoadLayoutCheckQueued;
        private bool _autoMediaDownloading;
        private bool _autoMediaDownloadAgain;
        private bool _autoMediaDownloadQueued;
        private long _autoMediaBackoffUntilTicks;
        private bool _videoPreviewLoading;
        private bool _videoPreviewLoadAgain;
        private bool _videoPreviewQueued;
        private readonly HashSet<string> _videoPreviewRequestedKeys = new HashSet<string>();
        private readonly Dictionary<string, int> _videoPreviewRetryCounts = new Dictionary<string, int>();
        private readonly HashSet<string> _videoPreviewRetryQueuedKeys = new HashSet<string>();
        private readonly HashSet<string> _autoMediaDownloadFailedKeys = new HashSet<string>();
        private readonly Queue<object> _viewedPhotoDownloadQueue = new Queue<object>();
        private readonly HashSet<string> _viewedPhotoDownloadQueuedKeys = new HashSet<string>();
        private bool _viewedPhotoDownloadRunning;
        private bool _noMoreOlderMessages;
        private int _olderEmptyResponseCount;
        private bool _markingRead;
        private int _lastMarkedReadMaxId;
        private int _targetMessageId;
        private int _pendingMessageIdSeed = -1;
        private ScrollViewer _messageScrollViewer;
        private bool _stickToBottom = true;
        private bool _viewportCorrectionQueued;
        private int _viewportCorrectionVersion;
        private bool _pendingViewportKeepBottom;
        private ScrollViewportAnchor _pendingViewportAnchor;
        private long _suppressViewportCorrectionsUntilTicks;
        private bool _hasUnreadSeparator;
        private int _currentChatAudioTrackIndex = -1;
        private List<object> _currentChatAudioTracks = new List<object>();
        private bool _initialMessageListRevealed;
        private bool _initialBottomPositionPending;
        private bool _messageListIsLoaded;
        private long _ignoreScrollTrackingUntilTicks;
        private bool _backRequestedAttached;
        private MediaCapture _voiceCapture;
        private bool _videoNoteCameraIsFrontFacing = true;
        private StorageFile _voiceRecordFile;
        private bool _isVoiceRecording;
        private bool _isVideoNoteMode;
        private bool _isVideoNoteRecording;
        private bool _recordPressPending;
        private bool _suppressNextEmptySendButtonClick;
        private UIElement _recordCapturedElement;
        private Pointer _recordCapturedPointer;
        private bool _voiceRecordStarted;
        private bool _voiceRecordCanceled;
        private bool _voiceFinishInProgress;
        private double _voiceRecordStartX;
        private DateTime _voiceRecordStartedAt;
        private System.Threading.Tasks.Task _voiceStartTask;
        private static DeviceAccessStatus? _audioCaptureAccessStatus;
        private static DeviceAccessStatus? _videoCaptureAccessStatus;
        private static System.Threading.Tasks.Task<DeviceAccessStatus> _audioCaptureAccessTask;
        private static System.Threading.Tasks.Task<DeviceAccessStatus> _videoCaptureAccessTask;
        private readonly DisplayRequest _voiceDisplayRequest = new DisplayRequest();
        private readonly DisplayRequest _videoLoadingDisplayRequest = new DisplayRequest();
        private int _videoLoadingDisplayRequestCount;
        private bool _loadingReactions;
        private bool _customReactionIconLoadQueued;
        private bool _messageActionsFlyoutOpen;
        private bool _deferredInitialChatWorkQueued;
        private bool _shortViewportFillQueued;
        private bool _notificationToggleRunning;
        private int _notificationMenuRefreshVersion;
        private bool _chatAlertOpen;
        private bool _stickerPanelLoaded;
        private bool _stickerPanelLoading;
        private bool _stickerSetSelectionChanging;
        private bool _joinedCurrentChat;
        private bool _pinnedPreviewLoading;
        private int _pinnedPreviewRequestedId;
        private readonly HashSet<int> _pinnedPreviewLoadAttemptedIds = new HashSet<int>();
        private readonly Dictionary<int, string> _pinnedPreviewCache = new Dictionary<int, string>();
        private bool _pinnedMessagesLoading;
        private bool _loadingReadByUsers;
        private readonly HashSet<int> _readByLoadRequestedIds = new HashSet<int>();
        private bool _messageSwipeTracking;
        private bool _messageSwipeActive;
        private Point _messageSwipeStartPoint;
        private FrameworkElement _messageSwipeElement;
        private ChatMessageViewModel _messageSwipeMessage;
        private long _suppressMessageRightTappedUntilTicks;
        private readonly HashSet<int> _reactionLoadRequestedIds = new HashSet<int>();
        private readonly HashSet<long> _customReactionIconRequestedIds = new HashSet<long>();
        private bool _fastReactionRefreshRunning;
        private long _lastFastReactionRefreshTicks;
        private long _lastHistoryFallbackTicks;
        private bool _botNeedsStart;
        private ChatMessageViewModel _activeBotReplyMarkupMessage;
        private bool _botReplyKeyboardExplicitlyRemoved;
        private bool _botReplyMarkupLoading;
        private long _loadedBotReplyMarkupMessageId;
        private readonly List<ImageSource> _photoOverlayImages = new List<ImageSource>();
        private readonly List<string> _photoOverlayUris = new List<string>();
        private readonly List<object> _photoOverlaySources = new List<object>();
        private readonly List<Ellipse> _photoOverlayIndicators = new List<Ellipse>();
        private readonly HashSet<string> _ffmpegPreparedVideoKeys = new HashSet<string>();
        private readonly HashSet<string> _ffmpegFailedVideoKeys = new HashSet<string>();
        private readonly Dictionary<string, object> _ffmpegInteropObjects = new Dictionary<string, object>();
        private readonly Dictionary<string, long> _ffmpegRetryProgressBytes = new Dictionary<string, long>();
        private readonly HashSet<string> _ffmpegForceVideoDecodeKeys = new HashSet<string>();
        private readonly Dictionary<string, int> _ffmpegBlankVideoRetryCounts = new Dictionary<string, int>();
        private readonly Dictionary<int, DateTime> _pendingPollLocalSelectionUntil = new Dictionary<int, DateTime>();
        private Brush _chatHeaderBarBackground;
        private Brush _pinnedMessageBarBackground;
        private Brush _chatInputBarBackground;
        private Thickness _chatHeaderBarBorderThickness;
        private bool _chatHeaderBarBorderThicknessCaptured;
        private Thickness _messageListBasePadding;
        private bool _messageListBasePaddingCaptured;
        private static readonly string[] QuickReactions = new[] { "👍", "❤", "🔥", "😁", "😢", "👏", "🤯", "👎" };
        private DisplayInformation _displayInformation;
        private static HashSet<string> LocalEmojiAssetKeys = BuildLocalEmojiAssetKeySet(AllLocalEmojiAssetKeys);
        private static int MaxLocalEmojiTextLength = BuildMaxLocalEmojiTextLength(AllLocalEmojiAssetKeys);
        private static readonly Dictionary<string, string> LocalEmojiFallbackAssetKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "D83EDD2F", "D83DDE31" }, // exploding head -> face screaming in fear
            { "D83EDD70", "D83DDE0D" }, // smiling face with hearts -> heart eyes
            { "D83EDD7A", "D83DDE22" }, // pleading face -> crying face
            { "D83EDD72", "D83DDE0A" }, // smiling face with tear -> smiling face
            { "D83EDDD0", "D83DDE10" }, // neutral newer face -> neutral face
            { "D83EDD2C", "D83DDE21" }, // face with symbols on mouth -> angry face
            { "D83EDD21", "D83DDE02" }, // clown face -> tears of joy
            { "D83EDD73", "D83CDF89" }, // partying face -> party popper
            { "D83EDD29", "D83DDE03" }, // star-struck -> smiling face
            { "D83EDEE1", "D83DDC4D" }, // saluting face -> thumbs up
            { "D83EDD78", "D83DDE0E" }, // disguised face -> sunglasses
            { "D83EDD76", "2744" },     // cold face -> snowflake
            { "D83EDD75", "D83DDE13" }, // hot face -> face with sweat
            { "D83EDEE0", "D83DDE2D" }, // melting face -> loudly crying face
            { "D83EDD79", "D83DDE22" }, // holding back tears -> crying face
            { "D83EDEE2", "D83DDE10" }, // face with open eyes and hand over mouth -> neutral face
            { "D83EDEE3", "D83DDE44" }, // face with peeking eye -> rolling eyes
            { "D83EDEF6", "D83DDC95" }  // heart hands -> two hearts
        };
        private static int InitialHistoryLimit { get { return TelegramAppSettings.ChatPageMessageBatchSize; } }
        private static int OlderHistoryLimit { get { return TelegramAppSettings.ChatPageMessageBatchSize; } }
        private static int FreshHistoryLimit { get { return TelegramAppSettings.ChatPageMessageBatchSize; } }
        private const double ScrollDownButtonRightMargin = 15.0;
        private const double ScrollDownButtonInputGap = 12.0;
        private const int PinnedJumpHistoryPageLimit = 100;
        private const int PinnedJumpMaxPageLoads = 500;

        private sealed class ScrollViewportAnchor
        {
            public object Item { get; set; }
            public double Top { get; set; }
        }

        private static string NormalizeEmojiAssetKey(string key)
        {
            return string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim().ToUpperInvariant();
        }

        private static string BuildEmojiAssetKey(string emoji)
        {
            if (string.IsNullOrEmpty(emoji)) return "EMPTY";
            var parts = new List<string>();
            for (var i = 0; i < emoji.Length; i++)
            {
                var codeUnit = (int)emoji[i];
                if (codeUnit == 0xFE0F) continue;
                parts.Add(codeUnit.ToString("X4"));
            }
            return string.Join("", parts);
        }

        internal static string ResolveLocalEmojiAssetUri(string emoji)
        {
            var key = ResolveLocalEmojiAssetKey(emoji);
            if (string.IsNullOrEmpty(key)) return "";
            return "ms-appx:///Assets/Emoji/Static/" + key + ".png";
        }

        internal static bool TryReadLocalEmojiAsset(string text, int index, out string emoji, out string uri, out int length)
        {
            emoji = null;
            uri = null;
            length = 0;
            if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length) return false;
            if (!CanStartLocalEmoji(text[index])) return false;

            var maxLength = Math.Min(MaxLocalEmojiTextLength, text.Length - index);
            for (var candidateLength = maxLength; candidateLength > 0; candidateLength--)
            {
                var candidate = text.Substring(index, candidateLength);
                var candidateKey = ResolveLocalEmojiAssetKey(candidate);
                if (string.IsNullOrEmpty(candidateKey)) continue;

                emoji = candidate;
                uri = "ms-appx:///Assets/Emoji/Static/" + candidateKey + ".png";
                length = candidateLength;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Cheap test for whether a character can begin one of the bundled emoji assets. Every
        /// catalog key starts either above U+2000 (which includes all surrogate pairs) or with
        /// '#', a digit, U+00A9 or U+00AE, so ordinary Latin and Cyrillic text can be rejected
        /// with a single comparison instead of running the full resolver at every offset.
        /// </summary>
        private static bool CanStartLocalEmoji(char ch)
        {
            if (ch >= ' ') return true;
            // (the literal above is U+2000)
            if (ch == '#') return true;
            if (ch >= '0' && ch <= '9') return true;
            return ch == '©' || ch == '®';
        }

        private static bool ContainsLocalEmojiCandidate(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (var i = 0; i < text.Length; i++)
            {
                if (CanStartLocalEmoji(text[i])) return true;
            }
            return false;
        }

        private static readonly Dictionary<string, string> LocalEmojiAssetKeyCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Message mapping resolves emoji URIs on the TDLib receive thread while the UI resolves
        // them on the dispatcher, so the cache has to be guarded.
        private static readonly object LocalEmojiAssetKeyCacheGate = new object();

        private static string ResolveLocalEmojiAssetKey(string emoji)
        {
            if (string.IsNullOrEmpty(emoji)) return "";

            string cached;
            lock (LocalEmojiAssetKeyCacheGate)
            {
                if (LocalEmojiAssetKeyCache.TryGetValue(emoji, out cached)) return cached;
            }

            var resolved = ResolveLocalEmojiAssetKeyCore(emoji);

            lock (LocalEmojiAssetKeyCacheGate)
            {
                // Bounded so a chat full of unique text cannot grow this without limit.
                if (LocalEmojiAssetKeyCache.Count > 4096) LocalEmojiAssetKeyCache.Clear();
                LocalEmojiAssetKeyCache[emoji] = resolved;
            }

            return resolved;
        }

        private static string ResolveLocalEmojiAssetKeyCore(string emoji)
        {
            var candidates = BuildEmojiAssetKeyCandidates(emoji);
            for (var i = 0; i < candidates.Count; i++)
            {
                var key = NormalizeEmojiAssetKey(candidates[i]);
                if (!string.IsNullOrEmpty(key) && LocalEmojiAssetKeys.Contains(key))
                    return key;
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                var key = NormalizeEmojiAssetKey(candidates[i]);
                string fallbackKey;
                if (!string.IsNullOrEmpty(key) &&
                    LocalEmojiFallbackAssetKeys.TryGetValue(key, out fallbackKey) &&
                    LocalEmojiAssetKeys.Contains(fallbackKey))
                    return fallbackKey;
            }
            return "";
        }

        private static List<string> BuildEmojiAssetKeyCandidates(string emoji)
        {
            var result = new List<string>();
            var key = BuildEmojiAssetKey(emoji);
            AddEmojiAssetKeyCandidate(result, key);

            var withoutSkinTone = RemoveEmojiSkinToneKeyParts(key);
            AddEmojiAssetKeyCandidate(result, withoutSkinTone);
            AddEmojiJoinerSegmentCandidates(result, key);
            AddEmojiJoinerSegmentCandidates(result, withoutSkinTone);

            return result;
        }

        private static void AddEmojiJoinerSegmentCandidates(List<string> result, string key)
        {
            if (result == null || string.IsNullOrEmpty(key) || key.IndexOf("200D", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            var parts = key.Split(new[] { "200D" }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
                AddEmojiAssetKeyCandidate(result, RemoveEmojiSkinToneKeyParts(parts[i]));
        }

        private static string RemoveEmojiSkinToneKeyParts(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            return key
                .Replace("D83CDFFB", "")
                .Replace("D83CDFFC", "")
                .Replace("D83CDFFD", "")
                .Replace("D83CDFFE", "")
                .Replace("D83CDFFF", "");
        }

        private static void AddEmojiAssetKeyCandidate(List<string> result, string key)
        {
            key = NormalizeEmojiAssetKey(key);
            if (result == null || string.IsNullOrEmpty(key)) return;
            for (var i = 0; i < result.Count; i++)
            {
                if (string.Equals(result[i], key, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            result.Add(key);
        }

        private static HashSet<string> BuildLocalEmojiAssetKeySet(string[] keys)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (keys == null) return result;
            for (var i = 0; i < keys.Length; i++)
            {
                var key = NormalizeEmojiAssetKey(keys[i]);
                if (!string.IsNullOrEmpty(key))
                    result.Add(key);
            }
            return result;
        }

        private static int BuildMaxLocalEmojiTextLength(string[] keys)
        {
            var max = 1;
            if (keys == null) return max;
            for (var i = 0; i < keys.Length; i++)
            {
                var key = NormalizeEmojiAssetKey(keys[i]);
                if (key.Length > 0)
                    max = Math.Max(max, (key.Length / 4) + 4);
            }
            return max;
        }

        // Per-template container pools, ported from Unigram's ChatView.OnChoosingItemContainer.
        //
        // ListView keeps a single recycle queue. With one item template that is fine, but as soon
        // as a DataTemplateSelector is in play the queue hands back containers built from the
        // wrong template, and every mismatch throws the container away and inflates a new one.
        // That is what makes a fast flick outrun the list and leave blank space behind: the panel
        // is not recycling at all, it is rebuilding. Keeping one queue per template means a
        // recycled row is almost always reusable as-is.
        private const string MessageTemplateKeyDate = "date";
        private const string MessageTemplateKeyUnread = "unread";
        private const string MessageTemplateKeyService = "service";
        private const string MessageTemplateKeyText = "text";
        private const string MessageTemplateKeyMedia = "media";

        private readonly Dictionary<string, DataTemplate> _messageTemplates = new Dictionary<string, DataTemplate>();
        private readonly Dictionary<string, HashSet<SelectorItem>> _messageContainerPools = new Dictionary<string, HashSet<SelectorItem>>();

        private void InitializeMessageContainerPools()
        {
            AddMessageTemplate(MessageTemplateKeyDate, "MessageDateSeparatorTemplate");
            AddMessageTemplate(MessageTemplateKeyUnread, "MessageUnreadSeparatorTemplate");
            AddMessageTemplate(MessageTemplateKeyService, "MessageServiceTemplate");
            AddMessageTemplate(MessageTemplateKeyText, "MessageTextTemplate");
            AddMessageTemplate(MessageTemplateKeyMedia, "MessageMediaTemplate");
        }

        private void AddMessageTemplate(string key, string resourceKey)
        {
            object resource;
            if (!Resources.TryGetValue(resourceKey, out resource)) return;
            var template = resource as DataTemplate;
            if (template == null) return;

            _messageTemplates[key] = template;
            _messageContainerPools[key] = new HashSet<SelectorItem>();
        }

        private static string SelectMessageTemplateKey(object item)
        {
            if (item != null && item.GetType() == typeof(DateSeparatorItem)) return MessageTemplateKeyDate;
            if (item is UnreadSeparatorItem) return MessageTemplateKeyUnread;

            var msg = item as ChatMessageViewModel;
            if (msg == null) return MessageTemplateKeyText;
            if (msg.IsServiceMessage && !string.IsNullOrEmpty(msg.ServiceActionText)) return MessageTemplateKeyService;
            return msg.HasAnyMediaContent ? MessageTemplateKeyMedia : MessageTemplateKeyText;
        }

        private void MessageList_ChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
        {
            if (args == null) return;

            var key = SelectMessageTemplateKey(args.Item);
            HashSet<SelectorItem> pool;
            if (!_messageContainerPools.TryGetValue(key, out pool) || pool == null) return;

            if (args.ItemContainer != null)
            {
                if (key.Equals(args.ItemContainer.Tag))
                {
                    // The suggestion already uses the template we need.
                    pool.Remove(args.ItemContainer);
                }
                else
                {
                    // Wrong template. Leave it in its own pool - XAML will offer it again for a
                    // row that can actually use it.
                    args.ItemContainer = null;
                }
            }

            if (args.ItemContainer == null)
            {
                if (pool.Count > 0)
                {
                    foreach (var candidate in pool)
                    {
                        args.ItemContainer = candidate;
                        break;
                    }
                    pool.Remove(args.ItemContainer);
                }
                else
                {
                    args.ItemContainer = CreateMessageContainer(key);
                }
            }

            args.IsContainerPrepared = true;
        }

        private SelectorItem CreateMessageContainer(string key)
        {
            var item = new ListViewItem();
            item.Tag = key;

            DataTemplate template;
            if (_messageTemplates.TryGetValue(key, out template))
                item.ContentTemplate = template;

            if (MessageList != null && MessageList.ItemContainerStyle != null)
                item.Style = MessageList.ItemContainerStyle;

            return item;
        }

        private void ReturnMessageContainerToPool(SelectorItem container)
        {
            if (container == null) return;
            var key = container.Tag as string;
            if (string.IsNullOrEmpty(key)) return;

            HashSet<SelectorItem> pool;
            if (_messageContainerPools.TryGetValue(key, out pool) && pool != null)
                pool.Add(container);
        }

        private static ChatMessageViewModel AsMessage(object item)
        {
            return item as ChatMessageViewModel;
        }

        private static bool IsDateSeparator(object item)
        {
            return item != null && item.GetType() == typeof(DateSeparatorItem);
        }

        private static bool IsListSeparator(object item)
        {
            return item is DateSeparatorItem;
        }

        public ChatPage()
        {
            InitializeComponent();
            _messages = new ObservableCollection<object>();
            _pendingPhotoAttachments = new ObservableCollection<PendingPhotoAttachment>();
            _stickerSets = new ObservableCollection<StickerSetViewModel>();
            _stickerItems = new ObservableCollection<StickerItemViewModel>();
            MessageList.ItemsSource = _messages;
            AttachmentPreviewList.ItemsSource = _pendingPhotoAttachments;
            StickerSetTabsList.ItemsSource = _stickerSets;
            StickerItemsControl.ItemsSource = _stickerItems;
            UpdateComposerState();
            _pollTimer = new DispatcherTimer();
            _pollTimer.Interval = TimeSpan.FromSeconds(8);
            _pollTimer.Tick += PollTimer_Tick;
            _headerTimer = new DispatcherTimer();
            _headerTimer.Interval = TimeSpan.FromSeconds(90);
            _headerTimer.Tick += HeaderTimer_Tick;
            _typingCancelTimer = new DispatcherTimer();
            _typingCancelTimer.Interval = TimeSpan.FromSeconds(5);
            _typingCancelTimer.Tick += TypingCancelTimer_Tick;
            _topLoadWatchTimer = new DispatcherTimer();
            _topLoadWatchTimer.Interval = TimeSpan.FromMilliseconds(300);
            _topLoadWatchTimer.Tick += TopLoadWatchTimer_Tick;
            _recordPressTimer = new DispatcherTimer();
            _recordPressTimer.Interval = TimeSpan.FromMilliseconds(260);
            _recordPressTimer.Tick += RecordPressTimer_Tick;
            _recordDurationTimer = new DispatcherTimer();
            _recordDurationTimer.Interval = TimeSpan.FromMilliseconds(250);
            _recordDurationTimer.Tick += RecordDurationTimer_Tick;
            MessageList.Loaded += MessageList_Loaded;
            MessageList.LayoutUpdated += MessageList_LayoutUpdated;
            MessageList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(MessageList_UserInteractionStarted), true);
            MessageList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(MessageList_UserInteractionStarted), true);
            InitializeMessageContainerPools();
            MessageList.ChoosingItemContainer += MessageList_ChoosingItemContainer;
            MessageList.ContainerContentChanging += MessageList_ContainerContentChanging;
            ChatHeaderBar.SizeChanged += ChatChrome_SizeChanged;
            ChatInputBar.SizeChanged += ChatChrome_SizeChanged;
            PinnedMessageBar.SizeChanged += ChatChrome_SizeChanged;
            SendButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SendButton_PointerPressed), true);
            SendButton.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(SendButton_PointerMoved), true);
            SendButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SendButton_PointerReleased), true);
            SendButton.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(SendButton_PointerCanceled), true);
            SendButton.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(SendButton_PointerCanceled), true);
            Loaded += ChatPage_Loaded;
            Unloaded += ChatPage_Unloaded;
            SizeChanged += ChatPage_SizeChanged;
            try
            {
                _displayInformation = DisplayInformation.GetForCurrentView();
                if (_displayInformation != null)
                    _displayInformation.DpiChanged += DisplayInformation_DpiChanged;
            }
            catch
            {
            }
        }

        private void ChatPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var keepBottom = ShouldKeepBottomDuringLayoutChange();
            var anchor = keepBottom ? null : CaptureScrollViewportAnchor();

            UpdateEmojiKeyboardHeight();
            UpdateMessageListChromePadding();
            UpdateScrollDownButtonPlacement();
            NotifyMessageLayoutMetricsChanged();

            QueueViewportCorrectionAfterLayout(keepBottom, anchor);
        }

        private void ChatChrome_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var keepBottom = ShouldKeepBottomDuringLayoutChange();
            var anchor = keepBottom ? null : CaptureScrollViewportAnchor();

            if (object.ReferenceEquals(sender, PinnedMessageBar))
                ApplyTopChromeGlassSetting();
            UpdateMessageListChromePadding();
            UpdateScrollDownButtonPlacement();

            QueueViewportCorrectionAfterLayout(keepBottom, anchor);
        }

        private void DisplayInformation_DpiChanged(DisplayInformation sender, object args)
        {
            var keepBottom = ShouldKeepBottomDuringLayoutChange();
            var anchor = keepBottom ? null : CaptureScrollViewportAnchor();
            NotifyMessageLayoutMetricsChanged();
            QueueViewportCorrectionAfterLayout(keepBottom, anchor);
        }

        private void NotifyMessageLayoutMetricsChanged()
        {
            if (_messages == null || _messages.Count == 0) return;
            for (var i = 0; i < _messages.Count; i++)
            {
                var message = _messages[i] as ChatMessageViewModel;
                if (message != null)
                    message.NotifyLayoutMetricsChanged();
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ConfigureSystemBackButton(true);
            ApplyGlassSetting();
            DetachChatPropertyChanged();
            var target = e.Parameter as ChatNavigationTarget;
            if (target != null)
            {
                _chat = target.Chat;
                _targetMessageId = target.MessageId;
            }
            else
            {
                _chat = e.Parameter as ChatViewModel;
                _targetMessageId = 0;
            }
            NormalizeTopicChatPresentation(_chat);
            CancelPendingViewportCorrection();
            _suppressViewportCorrectionsUntilTicks = 0;
            _initialMessageListRevealed = false;
            if (MessageList != null) MessageList.Opacity = 0;
            _videoPreviewRequestedKeys.Clear();
            _videoPreviewRetryCounts.Clear();
            _videoPreviewRetryQueuedKeys.Clear();
            _videoPreviewLoadAgain = false;
            _videoPreviewQueued = false;
            _stickToBottom = _targetMessageId <= 0;
            _initialBottomPositionPending = _chat != null && _targetMessageId <= 0;
            _lastMarkedReadMaxId = 0;
            _joinedCurrentChat = false;
            _pinnedPreviewLoading = false;
            _pinnedPreviewRequestedId = 0;
            _pinnedPreviewLoadAttemptedIds.Clear();
            _pinnedPreviewCache.Clear();
            _pinnedMessagesLoading = false;
            _botNeedsStart = false;
            _botReplyKeyboardExplicitlyRemoved = false;
            _activeBotReplyMarkupMessage = null;
            _botReplyMarkupLoading = false;
            _loadedBotReplyMarkupMessageId = 0;
            AttachChatPropertyChanged();
            if (_chat == null)
            {
                HeaderTitle.Text = "Chat";
                return;
            }
            ActiveChatService.SetActive(_chat);

            HeaderTitle.Text = GetHeaderTitle();
            HeaderSubtitle.Text = _chat.SubtitleText;
            HeaderInitials.Text = _chat.IconText;
            ApplyHeaderAvatar();
            UpdatePinnedMessageBar();
            var ignoredWallpaper = ApplyChatWallpaperAsync(_chat);

            ApplyPermissionsToUi(false);
            UpdateComposerState();
            SubscribeRealtimeEvents();
            if (_topLoadWatchTimer != null && !_topLoadWatchTimer.IsEnabled) _topLoadWatchTimer.Start();
            await LoadHistoryAsync();
            QueueBotReplyMarkupRefresh();
            QueuePinnedMessagesLoad(false);
            var ignoredRefresh = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                await System.Threading.Tasks.Task.Delay(600);
                await RefreshFullChatInfoAsync(false);
                QueuePinnedMessagesLoad(false);
            });
            StartPolling();
            StartHeaderRefresh();
        }

        private async System.Threading.Tasks.Task ApplyChatWallpaperAsync(ChatViewModel chat)
        {
            try
            {
                if (ChatWallpaper == null) return;
                ChatWallpaper.Visibility = Visibility.Collapsed;
                ChatWallpaper.Background = null;
                if (ChatWallpaperDim != null)
                    ChatWallpaperDim.Visibility = Visibility.Collapsed;
                if (chat == null) return;

                var info = await TelegramService.Instance.GetChatWallpaperAsync(chat);
                // Ignore if the user navigated to a different chat while we were loading.
                if (info == null || !object.ReferenceEquals(chat, _chat) || ChatWallpaper == null) return;

                var brush = BuildChatWallpaperBrush(info);
                if (brush == null) return;

                ChatWallpaper.Background = brush;
                ChatWallpaper.Visibility = Visibility.Visible;

                if (ChatWallpaperDim != null)
                {
                    var dim = TelegramAppSettings.NormalizeWallpaperDimming(TelegramAppSettings.WallpaperDimming);
                    ChatWallpaperDim.Opacity = dim / 100.0;
                    ChatWallpaperDim.Visibility = dim > 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch
            {
            }
        }

        private static Windows.UI.Xaml.Media.Brush BuildChatWallpaperBrush(ChatWallpaperInfo info)
        {
            if (info == null) return null;

            if (!string.IsNullOrEmpty(info.ImageUri))
            {
                try
                {
                    return new Windows.UI.Xaml.Media.ImageBrush
                    {
                        ImageSource = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(info.ImageUri)),
                        Stretch = Windows.UI.Xaml.Media.Stretch.UniformToFill
                    };
                }
                catch
                {
                }
            }

            if (info.HasGradient)
            {
                var gradient = new Windows.UI.Xaml.Media.LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0, 1)
                };
                gradient.GradientStops.Add(new Windows.UI.Xaml.Media.GradientStop { Color = FromTdColor(info.GradientTopColor), Offset = 0 });
                gradient.GradientStops.Add(new Windows.UI.Xaml.Media.GradientStop { Color = FromTdColor(info.GradientBottomColor), Offset = 1 });
                return gradient;
            }

            if (info.FreeformColors != null && info.FreeformColors.Length > 0)
            {
                var gradient = new Windows.UI.Xaml.Media.LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1)
                };
                var count = info.FreeformColors.Length;
                for (var i = 0; i < count; i++)
                {
                    var offset = count == 1 ? 0.0 : (double)i / (count - 1);
                    gradient.GradientStops.Add(new Windows.UI.Xaml.Media.GradientStop { Color = FromTdColor(info.FreeformColors[i]), Offset = offset });
                }
                return gradient;
            }

            if (info.HasSolid)
                return new Windows.UI.Xaml.Media.SolidColorBrush(FromTdColor(info.SolidColor));

            return null;
        }

        private static Windows.UI.Color FromTdColor(int rgb)
        {
            return Windows.UI.Color.FromArgb(255, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StatusBarLoadingIndicator.Hide();
            DetachGlass();
            UnsubscribeRealtimeEvents();
            StopPolling();
            StopHeaderRefresh();
            FfmpegAudioPlayerControl.StopAnyPlayback();
            FfmpegMusicPlayerControl.StopAnyPlayback();
            ClearFfmpegVideoCache();
            ReleaseAllVideoLoadingDisplayRequests();
            if (_typingCancelTimer != null) _typingCancelTimer.Stop();
            if (_topLoadWatchTimer != null) _topLoadWatchTimer.Stop();
            CancelPendingViewportCorrection();
            _suppressViewportCorrectionsUntilTicks = 0;
            DetachMessageScrollViewer();
            if (_recordPressTimer != null) _recordPressTimer.Stop();
            StopRecordDurationTimer();
            if (_isVoiceRecording || _isVideoNoteRecording)
            {
                var ignored = FinishVoiceRecordingAsync(true);
            }
            SendChatActionFireAndForget("cancel");
            DetachChatPropertyChanged();
            if (_displayInformation != null)
            {
                try
                {
                    _displayInformation.DpiChanged -= DisplayInformation_DpiChanged;
                }
                catch
                {
                }
                _displayInformation = null;
            }
            ActiveChatService.ClearActive(_chat);
            ConfigureSystemBackButton(false);
            base.OnNavigatedFrom(e);
        }

        private void ChatPage_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateMessageListChromePadding();
            UpdateScrollDownButtonPlacement();
            ApplyGlassSetting();
        }

        private void ChatPage_Unloaded(object sender, RoutedEventArgs e)
        {
            DetachGlass();
        }

        private void ApplyGlassSetting()
        {
            if (ChatHeaderBar != null && _chatHeaderBarBackground == null)
                _chatHeaderBarBackground = ChatHeaderBar.Background;
            if (PinnedMessageBar != null && _pinnedMessageBarBackground == null)
                _pinnedMessageBarBackground = PinnedMessageBar.Background;
            if (ChatInputBar != null && _chatInputBarBackground == null)
                _chatInputBarBackground = ChatInputBar.Background;
            CaptureTopChromeBorderThickness();

            if (TelegramAppSettings.GlassEffectEnabled)
            {
                if (ChatInputBar != null && ChatInputGlass != null)
                {
                    ChatInputBar.Background = new SolidColorBrush(Colors.Transparent);
                    ChatInputGlass.Background = new SolidColorBrush(Colors.Transparent);
                    FluentGlassEffectHelper.AttachBottomBar(ChatInputGlass, _chatInputBarBackground);
                }
                ApplyTopChromeGlassSetting();
            }
            else
            {
                DetachGlass();
            }

            UpdateMessageListChromePadding();
        }

        private void DetachGlass()
        {
            if (ChatHeaderBar != null)
            {
                if (_chatHeaderBarBackground != null)
                    ChatHeaderBar.Background = _chatHeaderBarBackground;
                RestoreTopChromeBorderThickness();
            }
            if (ChatTopChromeGlass != null)
            {
                FluentGlassEffectHelper.Detach(ChatTopChromeGlass);
            }
            if (ChatInputBar != null)
            {
                if (ChatInputGlass != null)
                    FluentGlassEffectHelper.Detach(ChatInputGlass);
                if (_chatInputBarBackground != null)
                    ChatInputBar.Background = _chatInputBarBackground;
            }
            if (PinnedMessageBar != null)
            {
                if (_pinnedMessageBarBackground != null)
                    PinnedMessageBar.Background = _pinnedMessageBarBackground;
            }
        }

        private void ApplyTopChromeGlassSetting()
        {
            if (!TelegramAppSettings.GlassEffectEnabled || ChatTopChromeGlass == null)
                return;

            if (ChatHeaderBar != null && _chatHeaderBarBackground == null)
                _chatHeaderBarBackground = ChatHeaderBar.Background;
            if (ChatHeaderBar != null)
                ChatHeaderBar.Background = new SolidColorBrush(Colors.Transparent);

            if (PinnedMessageBar != null && _pinnedMessageBarBackground == null)
                _pinnedMessageBarBackground = PinnedMessageBar.Background;
            if (PinnedMessageBar != null)
                PinnedMessageBar.Background = new SolidColorBrush(Colors.Transparent);

            ChatTopChromeGlass.Background = new SolidColorBrush(Colors.Transparent);
            UpdateTopChromeGlassHeight();
            UpdateTopChromeBorderThickness();
            FluentGlassEffectHelper.AttachTopBar(ChatTopChromeGlass, _chatHeaderBarBackground);
        }

        private void UpdateTopChromeGlassHeight()
        {
            if (ChatTopChromeGlass == null) return;

            var headerHeight = ChatHeaderBar == null ? 58.0 : ChatHeaderBar.ActualHeight;
            if (headerHeight <= 0)
                headerHeight = 58.0;

            var pinnedHeight = 0.0;
            if (PinnedMessageBar != null && PinnedMessageBar.Visibility == Visibility.Visible)
            {
                pinnedHeight = PinnedMessageBar.ActualHeight;
                if (pinnedHeight <= 0)
                    pinnedHeight = 46.0;
            }

            ChatTopChromeGlass.Height = headerHeight + pinnedHeight;
        }

        private void CaptureTopChromeBorderThickness()
        {
            if (ChatHeaderBar == null || _chatHeaderBarBorderThicknessCaptured)
                return;

            _chatHeaderBarBorderThickness = ChatHeaderBar.BorderThickness;
            _chatHeaderBarBorderThicknessCaptured = true;
        }

        private void UpdateTopChromeBorderThickness()
        {
            if (ChatHeaderBar == null) return;

            CaptureTopChromeBorderThickness();
            ChatHeaderBar.BorderThickness = TelegramAppSettings.GlassEffectEnabled && PinnedMessageBar != null && PinnedMessageBar.Visibility == Visibility.Visible
                ? new Thickness(0)
                : _chatHeaderBarBorderThickness;
        }

        private void RestoreTopChromeBorderThickness()
        {
            if (ChatHeaderBar != null && _chatHeaderBarBorderThicknessCaptured)
                ChatHeaderBar.BorderThickness = _chatHeaderBarBorderThickness;
        }

        private void UpdateMessageListChromePadding()
        {
            if (MessageList == null) return;

            if (!_messageListBasePaddingCaptured)
            {
                _messageListBasePadding = MessageList.Padding;
                _messageListBasePaddingCaptured = true;
            }

            var headerHeight = ChatHeaderBar == null ? 58.0 : ChatHeaderBar.ActualHeight;
            if (headerHeight <= 0)
                headerHeight = 58.0;

            var pinnedHeight = 0.0;
            if (PinnedMessageBar != null && PinnedMessageBar.Visibility == Visibility.Visible)
                pinnedHeight = PinnedMessageBar.ActualHeight;

            var inputHeight = ChatInputBar == null ? 56.0 : ChatInputBar.ActualHeight;
            if (inputHeight <= 0)
                inputHeight = 56.0;

            MessageList.Padding = new Thickness(
                _messageListBasePadding.Left,
                _messageListBasePadding.Top + headerHeight + pinnedHeight,
                _messageListBasePadding.Right,
                _messageListBasePadding.Bottom + inputHeight);

            UpdateScrollDownButtonPlacement();
        }

        private void AttachChatPropertyChanged()
        {
            if (_chat != null) _chat.PropertyChanged += Chat_PropertyChanged;
        }

        private void DetachChatPropertyChanged()
        {
            if (_chat != null) _chat.PropertyChanged -= Chat_PropertyChanged;
        }

        private async void Chat_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e == null) return;
            if (e.PropertyName != "AvatarUri" && e.PropertyName != "IconText" && e.PropertyName != "Title" && e.PropertyName != "LastSeenText" && e.PropertyName != "UserStatusKind" &&
                e.PropertyName != "PinnedMessageId" && e.PropertyName != "PinnedMessagePreview" && e.PropertyName != "PinnedMessageIds" && e.PropertyName != "CurrentPinnedMessageIndex" &&
                e.PropertyName != "ReadOutboxMaxId") return;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                if (_chat == null || !object.ReferenceEquals(sender, _chat)) return;
                if (e.PropertyName == "ReadOutboxMaxId")
                {
                    UpdateOutgoingMessageStates();
                    return;
                }
                HeaderTitle.Text = GetHeaderTitle();
                HeaderSubtitle.Text = _chat.SubtitleText;
                HeaderInitials.Text = _chat.IconText;
                ApplyHeaderAvatar();
                UpdatePinnedMessageBar();
            });
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
            StopPolling();
            StopHeaderRefresh();
            if (Frame.CanGoBack)
                Frame.GoBack();
            else if (AdaptiveShellNavigationService.ClearDetail())
                return;
            else
                Frame.Navigate(typeof(Chats));
        }

        private async System.Threading.Tasks.Task ShowChatAlertAsync(string title, string message)
        {
            if (string.IsNullOrWhiteSpace(title)) title = "Telegram";
            if (string.IsNullOrWhiteSpace(message)) message = title;
            if (_chatAlertOpen) return;

            _chatAlertOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.WrapWholeWords
                    },
                    PrimaryButtonText = "OK",
                    FullSizeDesired = false
                };
                await dialog.ShowAsync();
            }
            catch
            {
            }
            finally
            {
                _chatAlertOpen = false;
            }
        }

        private void ShowChatAlert(string title, string message)
        {
            var ignored = ShowChatAlertAsync(title, message);
        }

        private static string AlertErrorMessage(Exception ex, string fallback)
        {
            return ex == null || string.IsNullOrEmpty(ex.Message) ? fallback : ex.Message;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            StopPolling();
            if (Frame == null) return;
            if (Frame.CanGoBack)
                Frame.GoBack();
            else if (AdaptiveShellNavigationService.ClearDetail())
                return;
            else
                Frame.Navigate(typeof(Chats));
        }

        private async System.Threading.Tasks.Task RefreshFullChatInfoAsync()
        {
            await RefreshFullChatInfoAsync(false);
        }

        private async System.Threading.Tasks.Task RefreshFullChatInfoAsync(bool refreshDialogList)
        {
            if (_chat == null || _refreshingHeader) return;
            HeaderSubtitle.Text = _chat.SubtitleText;
            _refreshingHeader = true;
            try
            {
                await TelegramService.Instance.RefreshFullChatInfoAsync(_chat, refreshDialogList);
                HeaderTitle.Text = GetHeaderTitle();
                HeaderSubtitle.Text = _chat.SubtitleText;
                HeaderInitials.Text = _chat.IconText;
                ApplyHeaderAvatar();
                UpdatePinnedMessageBar();
                QueuePinnedMessagesLoad(false);
                UpdateBotInterfaceState();
                await RefreshBotReplyMarkupFromChatAsync();
                ApplyPermissionsToUi(_historyLoaded && !_loading);
                UpdateOutgoingMessageStates();
            }
            catch
            {
                // Subscriber count and online status are optional UI data; do not block chat opening if Telegram refuses full info.
            }
            finally
            {
                _refreshingHeader = false;
            }
        }

        private void ApplyHeaderAvatar()
        {
            if (_chat == null) return;
            if (_chat.IsForumTopic)
            {
                HeaderAvatar.ImageSource = null;
                HeaderInitials.Visibility = Visibility.Visible;
                return;
            }

            if (!string.IsNullOrEmpty(_chat.AvatarUri))
            {
                try
                {
                    var avatar = new BitmapImage();
                    avatar.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    avatar.DecodePixelWidth = 64;
                    avatar.UriSource = new Uri(_chat.AvatarUri);
                    HeaderAvatar.ImageSource = avatar;
                    HeaderInitials.Visibility = Visibility.Collapsed;
                    return;
                }
                catch
                {
                }
            }

            HeaderAvatar.ImageSource = null;
            HeaderInitials.Visibility = Visibility.Visible;
        }

        private async void HeaderTimer_Tick(object sender, object e)
        {
            await RefreshFullChatInfoAsync();
        }

        private void StartHeaderRefresh()
        {
            if (!_headerTimer.IsEnabled) _headerTimer.Start();
        }

        private void StopHeaderRefresh()
        {
            if (_headerTimer.IsEnabled) _headerTimer.Stop();
        }

        private void Header_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (IsFromHeaderMoreButton(e.OriginalSource as DependencyObject)) return;
            if (_chat == null || Frame == null) return;
            if (_chat.IsForumTopic)
            {
                if (AdaptiveShellNavigationService.NavigateLeft(typeof(GroupProfilePage), BuildParentForumChat(_chat)))
                    return;
                Frame.Navigate(typeof(GroupProfilePage), BuildParentForumChat(_chat));
                return;
            }
            if (_chat.IsBroadcast || (_chat.IsChannel && !_chat.IsGroup))
            {
                if (AdaptiveShellNavigationService.NavigateLeft(typeof(ChannelProfilePage), _chat))
                    return;
                Frame.Navigate(typeof(ChannelProfilePage), _chat);
            }
            else if (_chat.IsGroup || _chat.PeerType == "chat" || _chat.PeerType == "channel")
            {
                if (AdaptiveShellNavigationService.NavigateLeft(typeof(GroupProfilePage), _chat))
                    return;
                Frame.Navigate(typeof(GroupProfilePage), _chat);
            }
            else
            {
                if (AdaptiveShellNavigationService.NavigateLeft(typeof(UserProfilePage), _chat))
                    return;
                Frame.Navigate(typeof(UserProfilePage), _chat);
            }
        }

        private string GetHeaderTitle()
        {
            if (_chat == null) return "Chat";
            var title = _chat.DisplayTitle;
            return string.IsNullOrWhiteSpace(title) ? "Chat" : title;
        }

        private ChatViewModel BuildParentForumChat(ChatViewModel topic)
        {
            if (topic == null) return null;
            return new ChatViewModel
            {
                PeerId = topic.ParentPeerId != 0 ? topic.ParentPeerId : topic.PeerId,
                PeerType = string.IsNullOrEmpty(topic.ParentPeerType) ? topic.PeerType : topic.ParentPeerType,
                PeerKey = string.IsNullOrEmpty(topic.ParentPeerKey) ? null : topic.ParentPeerKey,
                AccessHash = topic.ParentAccessHash != 0 ? topic.ParentAccessHash : topic.AccessHash,
                Title = string.IsNullOrEmpty(topic.ParentTitle) ? topic.Title : topic.ParentTitle,
                IsGroup = true,
                IsChannel = true,
                IsForum = true,
                CanSendMessages = topic.CanSendMessages,
                CanPinMessages = topic.CanPinMessages,
                CanDeleteMessages = topic.CanDeleteMessages,
                NoForwards = topic.NoForwards,
                SubscriberCount = topic.SubscriberCount,
                OnlineCount = topic.OnlineCount,
                IconText = string.IsNullOrEmpty(topic.ParentTitle) ? topic.IconText : BuildSimpleIconText(topic.ParentTitle),
                AvatarUri = topic.AvatarUri,
                AvatarIsPreview = topic.AvatarIsPreview,
                AvatarPhotoId = topic.AvatarPhotoId,
                AvatarDcId = topic.AvatarDcId,
                AvatarStrippedThumb = topic.AvatarStrippedThumb
            };
        }

        private static void NormalizeTopicChatPresentation(ChatViewModel chat)
        {
            if (chat == null || !chat.IsForumTopic) return;
            chat.IsGroup = true;
            chat.IsChannel = true;
            chat.IsBroadcast = false;
            chat.IsForum = true;
        }

        private static string BuildSimpleIconText(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "?";
            title = title.Trim();
            var parts = title.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return (parts[0].Substring(0, 1) + parts[1].Substring(0, 1)).ToUpperInvariant();
            return title.Substring(0, Math.Min(2, title.Length)).ToUpperInvariant();
        }

        private void SenderProfile_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var msg = fe == null ? null : fe.Tag as ChatMessageViewModel;
            if (msg == null || Frame == null) return;

            var senderChat = BuildSenderProfileChat(msg);
            if (senderChat == null) return;

            e.Handled = true;
            if (senderChat.IsBroadcast || (senderChat.IsChannel && !senderChat.IsGroup))
            {
                if (AdaptiveShellNavigationService.NavigateLeft(typeof(ChannelProfilePage), senderChat))
                    return;
                Frame.Navigate(typeof(ChannelProfilePage), senderChat);
            }
            else if (senderChat.IsGroup || senderChat.PeerType == "chat" || senderChat.PeerType == "channel")
            {
                if (AdaptiveShellNavigationService.NavigateLeft(typeof(GroupProfilePage), senderChat))
                    return;
                Frame.Navigate(typeof(GroupProfilePage), senderChat);
            }
            else
            {
                if (AdaptiveShellNavigationService.NavigateLeft(typeof(UserProfilePage), senderChat))
                    return;
                Frame.Navigate(typeof(UserProfilePage), senderChat);
            }
        }

        private ChatViewModel BuildSenderProfileChat(ChatMessageViewModel msg)
        {
            if (msg == null || msg.SenderPeerId == 0 || string.IsNullOrEmpty(msg.SenderName)) return null;

            var peerType = string.IsNullOrEmpty(msg.SenderPeerType) ? "user" : msg.SenderPeerType;
            return new ChatViewModel
            {
                PeerId = msg.SenderPeerId,
                PeerType = peerType,
                PeerKey = msg.SenderPeerKey,
                AccessHash = msg.SenderAccessHash,
                Title = msg.SenderName,
                IconText = msg.SenderInitials,
                AvatarUri = msg.SenderAvatarUri,
                AvatarPhotoId = msg.SenderAvatarPhotoId,
                AvatarDcId = msg.SenderAvatarDcId,
                AvatarStrippedThumb = msg.SenderAvatarStrippedThumb,
                IsGroup = msg.SenderIsGroup || peerType == "chat",
                IsChannel = msg.SenderIsChannel || peerType == "channel",
                IsBroadcast = msg.SenderIsBroadcast,
                CanSendMessages = true
            };
        }

        private void HeaderMoreButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            if (button == null || _chat == null) return;

            var flyout = new MenuFlyout();
            var notificationAction = new MenuFlyoutItem();
            ApplyNotificationMenuText(notificationAction, _chat.IsMuted, false);
            notificationAction.Click += NotificationMenuItem_Click;
            flyout.Items.Add(notificationAction);

            if (HasChatAction())
            {
                flyout.Items.Add(new MenuFlyoutSeparator());

                var action = new MenuFlyoutItem();
                action.Text = GetChatActionText();
                action.Tag = GetChatActionKind();
                action.Click += ChatActionMenuItem_Click;
                flyout.Items.Add(action);
            }
            flyout.ShowAt(button);

            var refreshVersion = ++_notificationMenuRefreshVersion;
            var ignored = RefreshNotificationMenuItemAsync(notificationAction, refreshVersion);
        }

        private async System.Threading.Tasks.Task RefreshNotificationMenuItemAsync(MenuFlyoutItem item, int version)
        {
            if (item == null || _chat == null) return;
            try
            {
                var muted = await TelegramService.Instance.GetNotificationsMutedAsync(_chat);
                if (version != _notificationMenuRefreshVersion || item == null || _chat == null) return;
                _chat.IsMuted = muted;
                ApplyNotificationMenuText(item, muted, false);
            }
            catch
            {
                if (version == _notificationMenuRefreshVersion)
                    ApplyNotificationMenuText(item, _chat != null && _chat.IsMuted, false);
            }
        }

        private void ApplyNotificationMenuText(MenuFlyoutItem item, bool muted, bool busy)
        {
            if (item == null) return;
            item.Text = busy ? "Updating notifications..." : (muted ? "Turn on notifications" : "Turn off notifications");
            item.Tag = muted ? "unmute" : "mute";
            item.IsEnabled = !busy;
        }

        private async void NotificationMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            if (_chat == null || item == null || _notificationToggleRunning) return;

            _notificationToggleRunning = true;
            ApplyNotificationMenuText(item, _chat.IsMuted, true);
            try
            {
                var currentlyMuted = await TelegramService.Instance.GetNotificationsMutedAsync(_chat);
                _chat.IsMuted = currentlyMuted;
                var newMuted = !currentlyMuted;
                await TelegramService.Instance.SetNotificationsMutedAsync(_chat, newMuted);
                _chat.IsMuted = newMuted;
                ApplyNotificationMenuText(item, newMuted, false);
                ApplyPermissionsToUi(_historyLoaded && !_loading);
            }
            catch (Exception ex)
            {
                ApplyNotificationMenuText(item, _chat != null && _chat.IsMuted, false);
                await ShowChatAlertAsync("Notifications error", AlertErrorMessage(ex, "Could not update notification settings."));
            }
            finally
            {
                _notificationToggleRunning = false;
            }
        }

        private bool IsFromHeaderMoreButton(DependencyObject source)
        {
            while (source != null)
            {
                if (object.ReferenceEquals(source, HeaderMoreButton)) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        private string GetChatActionText()
        {
            if (_chat == null) return "Delete chat";
            if (CanJoinCurrentChat()) return "Join";
            if (ShouldLeaveChat(_chat))
                return (_chat.IsBroadcast || (_chat.IsChannel && !_chat.IsGroup)) ? "Leave channel" : "Leave group";
            return "Delete chat";
        }

        private string GetChatActionKind()
        {
            if (_chat == null) return "delete";
            if (CanJoinCurrentChat()) return "join";
            if (ShouldLeaveChat(_chat)) return "leave";
            return "delete";
        }

        private bool HasChatAction()
        {
            if (_chat == null) return false;
            if (_chat.IsForumTopic || _chat.IsCommentsThread) return false;
            if (_chat.PeerType == "self") return false;
            return true;
        }

        private static bool ShouldLeaveChat(ChatViewModel chat)
        {
            if (chat == null) return false;
            if (chat.IsForumTopic || chat.IsCommentsThread) return false;
            if (chat.IsBroadcast || chat.IsGroup) return true;
            if (chat.PeerType == "chat") return true;
            return chat.PeerType == "channel" && (chat.IsChannel || chat.IsGroup || chat.IsBroadcast);
        }

        private bool CanJoinCurrentChat()
        {
            if (_chat == null) return false;
            if (_joinedCurrentChat) return false;
            if (_chat.PeerType != "channel") return false;
            if (_chat.IsForumTopic || _chat.IsCommentsThread) return false;
            if (_chat.CanJoin || !_chat.IsJoined) return true;

            return _chat.IsBroadcast &&
                   !_chat.CanSendMessages &&
                   _chat.TopMessageId <= 0 &&
                   string.IsNullOrEmpty(_chat.LastMessage);
        }

        private async System.Threading.Tasks.Task JoinCurrentChatAsync()
        {
            if (!CanJoinCurrentChat()) return;
            await TelegramService.Instance.JoinChatAsync(_chat);
            _chat.IsJoined = true;
            _chat.CanJoin = false;
            _joinedCurrentChat = true;
            await RefreshFullChatInfoAsync();
            ApplyPermissionsToUi(_historyLoaded && !_loading);
            HeaderTitle.Text = GetHeaderTitle();
        }

        private async void ChatActionMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            var action = item == null ? string.Empty : item.Tag as string;
            if (_chat == null || string.IsNullOrEmpty(action)) return;

            try
            {
                if (action == "join")
                {
                    await JoinCurrentChatAsync();
                    return;
                }

                if (action == "leave")
                    await TelegramService.Instance.LeaveChatAsync(_chat);
                else
                    await TelegramService.Instance.DeleteChatAsync(_chat);

                TelegramService.Instance.NotifyChatRemoved(_chat);
                TelegramService.Instance.ClearDialogsCache();
                StopPolling();
                StopHeaderRefresh();
                if (Frame != null && Frame.CanGoBack)
                    Frame.GoBack();
                else if (AdaptiveShellNavigationService.ClearDetail())
                    return;
                else if (Frame != null)
                    Frame.Navigate(typeof(Chats));
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync(GetChatActionText() + " error", AlertErrorMessage(ex, "Could not update this chat."));
            }
        }

        private async void RefreshHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshFullChatInfoAsync();
            ApplyPermissionsToUi(false);
            if (_historyLoaded)
                await RefreshCurrentHistoryAsync();
            else
                await LoadHistoryAsync();
        }

        private async System.Threading.Tasks.Task RefreshCurrentHistoryAsync()
        {
            if (_chat == null || _loading || _polling) return;
            _loading = true;
            try
            {
                var wasAtBottom = ShouldStickToBottom();
                SetTopLoading(true);
                var messages = await TelegramService.Instance.GetHistoryAsync(_chat, InitialHistoryLimit);
                var added = MergeMessages(messages, false);
                await CompleteTopBoundaryAlbumAsync();
                await CompleteVisibleGroupedAlbumsAsync();
                QueueVisibleVideoPreviews();
                BeginAutoDownloadMedia();
                StartBackgroundReactionLoad();
                UpdateOutgoingMessageStates();
                UpdateBotInterfaceState();
                HeaderTitle.Text = GetHeaderTitle();
                HeaderSubtitle.Text = _chat.SubtitleText;
                if (added > 0 && wasAtBottom)
                    KeepBottomIfStillRequested();
                UpdateScrollDownButton();
                QueueMarkVisibleMessagesRead();
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Refresh error", AlertErrorMessage(ex, "Could not refresh this chat."));
            }
            finally
            {
                SetTopLoading(false);
                _loading = false;
                SetComposerEnabled(true);
                UpdateTopLoadMorePanel();
                if (_chat != null && _historyLoaded)
                    QueueRealtimeMessageDrain(_chat.PeerId);
            }
        }

        private async void LoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadOlderMessagesAsync();
        }

        private async void PollTimer_Tick(object sender, object e)
        {
            if (!_historyLoaded || _loading || _polling || _autoLoadingOlder || _chat == null) return;
            _polling = true;
            try
            {
                var wasAtBottom = ShouldStickToBottom();
                var removed = RemoveMessagesById(await TelegramService.Instance.TakeDeletedMessageIdsAsync(_chat));
                if (TelegramService.Instance.ConsumeReplyMarkupReset(_chat))
                {
                    _botReplyKeyboardExplicitlyRemoved = true;
                    _activeBotReplyMarkupMessage = null;
                    UpdateBotReplyKeyboardPanel();
                }
                var updates = await TelegramService.Instance.TakeMessageUpdatesAsync(_chat);
                var added = MergeMessages(updates, false);
                // TDLib already pushes message updates into TakeMessageUpdatesAsync.
                // Re-querying chat history on every idle poll caused periodic UI stalls on low-end ARM devices.
                // Keep a slow safety-net refresh only for cases where an update was missed.
                if (added == 0 && removed == 0 && ShouldRunHistoryFallback())
                {
                    var maxId = GetNewestMessageId();
                    var fresh = await TelegramService.Instance.GetHistorySinceAsync(_chat, maxId, FreshHistoryLimit);
                    added = MergeMessages(fresh, false);
                }
                if (added > 0) added += await CompleteVisibleGroupedAlbumsAsync();
                if (added > 0)
                {
                    BeginAutoDownloadMedia();
                    StartBackgroundReactionLoad();
                    StartFastReactionRefresh();
                }
                UpdateOutgoingMessageStates();
                if (added > 0 && wasAtBottom && ShouldStickToBottom())
                {
                    ScrollToBottom(false);
                    await MarkVisibleMessagesReadAsync();
                    QueueBottomPinBurst();
                }
                if (removed > 0 && wasAtBottom && ShouldStickToBottom())
                    KeepBottomIfStillRequested();
                UpdateScrollDownButton();
            }
            catch
            {
                // Keep the chat usable even if one background refresh fails.
            }
            finally
            {
                _polling = false;
            }
        }


        private bool ShouldRunHistoryFallback()
        {
            var now = DateTime.UtcNow.Ticks;
            if (_lastHistoryFallbackTicks != 0 && now - _lastHistoryFallbackTicks < TimeSpan.FromSeconds(90).Ticks)
                return false;
            _lastHistoryFallbackTicks = now;
            return true;
        }

        private async System.Threading.Tasks.Task LoadHistoryAsync()
        {
            if (_chat == null || _loading) return;
            _loading = true;
            try
            {
                SetComposerEnabled(false);
                HeaderTitle.Text = GetHeaderTitle();
                SetTopLoading(true);
                await TelegramService.Instance.OpenChatAsync(_chat);
                if (_targetMessageId > 0)
                    _stickToBottom = false;
                var messages = _targetMessageId > 0
                    ? await TelegramService.Instance.GetHistoryAroundAsync(_chat, _targetMessageId, InitialHistoryLimit)
                    : await TelegramService.Instance.GetHistoryAsync(_chat, InitialHistoryLimit);
                if (_targetMessageId > 0 && (messages == null || messages.Count < Math.Min(5, InitialHistoryLimit)))
                {
                    var latestMessages = await TelegramService.Instance.GetHistoryAsync(_chat, InitialHistoryLimit);
                    if (latestMessages != null && latestMessages.Count > 0)
                    {
                        if (messages == null || messages.Count == 0)
                        {
                            messages = latestMessages;
                        }
                        else
                        {
                            var seen = new HashSet<int>();
                            var combined = new List<ChatMessageViewModel>();
                            for (var i = 0; i < messages.Count; i++)
                            {
                                var msg = messages[i];
                                if (msg == null || msg.Id <= 0 || seen.Contains(msg.Id)) continue;
                                seen.Add(msg.Id);
                                combined.Add(msg);
                            }
                            for (var i = 0; i < latestMessages.Count; i++)
                            {
                                var msg = latestMessages[i];
                                if (msg == null || msg.Id <= 0 || seen.Contains(msg.Id)) continue;
                                seen.Add(msg.Id);
                                combined.Add(msg);
                            }
                            messages = combined;
                        }
                    }
                }
                _noMoreOlderMessages = false;
                _olderEmptyResponseCount = 0;
                MergeMessages(messages, true);
                if (_targetMessageId > 0)
                {
                    var targetMessages = await TelegramService.Instance.GetMessagesByIdAsync(_chat, _targetMessageId);
                    MergeMessages(targetMessages, false);
                }
                UpdateOutgoingMessageStates();
                HeaderTitle.Text = GetHeaderTitle();
                HeaderSubtitle.Text = _chat.SubtitleText;
                _historyLoaded = true;
                // Catch up: process any updates that arrived during initial load
                Debug.WriteLine("RT_CATCHUP _historyLoaded=true, checking pending updates...");
                var pendingUpdates = await TelegramService.Instance.TakeMessageUpdatesAsync(_chat);
                Debug.WriteLine("RT_CATCHUP pendingUpdates=" + (pendingUpdates != null ? pendingUpdates.Count.ToString() : "null"));
                if (pendingUpdates != null && pendingUpdates.Count > 0)
                    MergeMessages(pendingUpdates, false);
                var pendingDeleted = await TelegramService.Instance.TakeDeletedMessageIdsAsync(_chat);
                Debug.WriteLine("RT_CATCHUP pendingDeleted=" + (pendingDeleted != null ? pendingDeleted.Count.ToString() : "null"));
                if (pendingDeleted != null && pendingDeleted.Count > 0)
                    RemoveMessagesById(pendingDeleted);
                _lastHistoryFallbackTicks = DateTime.UtcNow.Ticks;
                AttachMessageScrollViewer();
                if (_targetMessageId > 0)
                {
                    _initialBottomPositionPending = false;
                    await ScrollToMessageAsync(_targetMessageId);
                }
                else
                {
                    await PositionInitialMessageListAtBottomAsync();
                    QueueBottomPinBurst();
                }
                RevealMessageList();
                QueueVisibleVideoPreviews();
                QueueDeferredInitialChatWork();
                if (_targetMessageId <= 0)
                    QueueShortViewportFill();
                UpdateScrollDownButton();
                QueueMarkVisibleMessagesRead();
            }
            catch (Exception ex)
            {
                _initialBottomPositionPending = false;
                await ShowChatAlertAsync("Load error", AlertErrorMessage(ex, "Could not load this chat."));
                RevealMessageList();
            }
            finally
            {
                SetTopLoading(false);
                _loading = false;
                SetComposerEnabled(true);
                UpdateTopLoadMorePanel();
                if (_chat != null && _historyLoaded)
                    QueueRealtimeMessageDrain(_chat.PeerId);
            }
        }

        private async System.Threading.Tasks.Task LoadOlderMessagesAsync()
        {
            if (_chat == null || _loading || _autoLoadingOlder || _noMoreOlderMessages || _messages.Count == 0) return;
            var oldestId = GetOldestMessageId();
            if (oldestId <= 0) return;

            _autoLoadingOlder = true;
            try
            {
                SetTopLoading(true);

                var added = 0;
                var requestBeforeId = oldestId;
                var requestBeforeSortId = GetMessageSortIdById(oldestId);
                for (var attempt = 0; attempt < 3 && added == 0; attempt++)
                {
                    var older = await TelegramService.Instance.GetHistoryBeforeAsync(_chat, requestBeforeId, OlderHistoryLimit);
                    if (older == null) return;
                    if (older.Count == 0)
                    {
                        _olderEmptyResponseCount++;
                        _noMoreOlderMessages = _olderEmptyResponseCount >= 2;
                        return;
                    }

                    added = MergeMessages(older, false);
                    if (added > 0) break;

                    var fetchedOldestId = GetOldestMessageId(older);
                    var fetchedOldestSortId = GetOldestMessageSortId(older);
                    if (fetchedOldestId <= 0 || fetchedOldestSortId == 0 || (requestBeforeSortId != 0 && fetchedOldestSortId >= requestBeforeSortId)) break;
                    requestBeforeId = fetchedOldestId;
                    requestBeforeSortId = fetchedOldestSortId;
                }
                if (added > 0)
                {
                    _olderEmptyResponseCount = 0;
                    QueuePostOlderLoadWork();
                }
                UpdateOutgoingMessageStates();
                UpdateScrollDownButton();
            }
            catch
            {
                // Older history can be unavailable or exhausted; do not show an error over the chat.
            }
            finally
            {
                SetTopLoading(false);
                _autoLoadingOlder = false;
                UpdateTopLoadMorePanel();
            }
        }

        private void QueuePostOlderLoadWork()
        {
            if (_olderPostLoadWorkQueued) return;
            _olderPostLoadWorkQueued = true;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                _olderPostLoadWorkQueued = false;
                await System.Threading.Tasks.Task.Delay(260);
                if (!_historyLoaded || _loading || _autoLoadingOlder || _messages == null || _messages.Count == 0) return;

                var keepBottom = ShouldStickToBottom();
                var added = 0;
                added += await CompleteTopBoundaryAlbumAsync();
                if (!_historyLoaded || _loading || _autoLoadingOlder) return;
                added += await CompleteVisibleGroupedAlbumsAsync();
                if (added > 0)
                {
                    UpdateOutgoingMessageStates();
                    if (keepBottom) KeepBottomIfStillRequested();
                }

                QueueVisibleVideoPreviews();
                QueueAutoDownloadMedia();
                StartBackgroundReactionLoad();
                QueueVisibleViewedAutoDownloads();
            });
        }

        private async System.Threading.Tasks.Task AutoFillShortViewportAsync()
        {
            if (_chat == null || _messages == null || _messages.Count == 0 || _noMoreOlderMessages) return;

            AttachMessageScrollViewer();
            var keepBottom = ShouldKeepBottomDuringLayoutChange();
            for (var i = 0; i < 2 && !_noMoreOlderMessages && !IsMessageListScrollable(); i++)
            {
                var oldestId = GetOldestMessageId();
                if (oldestId <= 0) return;

                var older = await TelegramService.Instance.GetHistoryBeforeAsync(_chat, oldestId, OlderHistoryLimit);
                if (older == null) return;
                if (older.Count == 0)
                {
                    _olderEmptyResponseCount++;
                    _noMoreOlderMessages = _olderEmptyResponseCount >= 2;
                    return;
                }

                var added = MergeMessages(older, false);
                if (added > 0) _olderEmptyResponseCount = 0;
                if (added > 0) added += await CompleteTopBoundaryAlbumAsync();
                if (added > 0) added += await CompleteVisibleGroupedAlbumsAsync();
                if (added > 0)
                {
                    BeginAutoDownloadMedia();
                    StartBackgroundReactionLoad();
                    UpdateOutgoingMessageStates();
                    TryUpdateMessageListLayout("AutoFillShortViewportAsync");
                    AttachMessageScrollViewer();
                    if (keepBottom)
                    {
                        _stickToBottom = true;
                        IgnoreScrollTrackingBriefly();
                        ScrollToBottomNow(false);
                    }
                    continue;
                }

                var currentOldestSortId = GetOldestMessageSortId();
                var previousOldestSortId = GetMessageSortIdById(oldestId);
                if (currentOldestSortId == 0 || previousOldestSortId == 0 || currentOldestSortId >= previousOldestSortId)
                    return;
            }

            if (keepBottom)
            {
                _stickToBottom = true;
                QueueBottomPinBurst();
            }
        }

        private void QueueShortViewportFill()
        {
            if (_shortViewportFillQueued) return;
            _shortViewportFillQueued = true;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                try
                {
                    if (!_historyLoaded || _autoLoadingOlder) return;
                    await System.Threading.Tasks.Task.Delay(300);
                    if (!_historyLoaded || _autoLoadingOlder) return;
                    await AutoFillShortViewportAsync();
                }
                finally
                {
                    _shortViewportFillQueued = false;
                }
            });
        }

        private void QueueDeferredInitialChatWork()
        {
            if (_deferredInitialChatWorkQueued) return;
            _deferredInitialChatWorkQueued = true;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                _deferredInitialChatWorkQueued = false;
                if (!_historyLoaded || _messages == null || _messages.Count == 0) return;
                await System.Threading.Tasks.Task.Delay(450);
                if (!_historyLoaded || _messages == null || _messages.Count == 0) return;

                var keepBottom = ShouldStickToBottom();
                var added = 0;
                added += await CompleteTopBoundaryAlbumAsync();
                added += await CompleteVisibleGroupedAlbumsAsync();
                if (added > 0)
                {
                    UpdateOutgoingMessageStates();
                    if (keepBottom) KeepBottomIfStillRequested();
                    QueueAutoDownloadMedia();
                }

                QueueVisibleVideoPreviews();
                StartBackgroundReactionLoad();
                QueueCustomReactionIconLoadForVisibleMessagesCoalesced();
                StartBackgroundReadByLoad();
                QueueVisibleViewedAutoDownloads();
            });
        }

        private void QueueBotReplyMarkupRefresh()
        {
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                await System.Threading.Tasks.Task.Delay(250);
                if (!_historyLoaded || _chat == null) return;
                await RefreshBotReplyMarkupFromChatAsync();
            });
        }

        private void QueueMarkVisibleMessagesRead()
        {
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                await System.Threading.Tasks.Task.Delay(250);
                await MarkVisibleMessagesReadAsync();
            });
        }

        private bool TryUpdateMessageListLayout(string reason)
        {
            if (MessageList == null) return false;
            try
            {
                MessageList.UpdateLayout();
                return true;
            }
            catch (COMException ex)
            {
                Debug.WriteLine("CHAT_UPDATE_LAYOUT_FAIL reason=" + (reason ?? "-") + " hresult=0x" + ex.HResult.ToString("X8") + " message=" + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("CHAT_UPDATE_LAYOUT_FAIL reason=" + (reason ?? "-") + " type=" + ex.GetType().Name + " message=" + ex.Message);
                return false;
            }
        }

        private async System.Threading.Tasks.Task ScrollToMessageAsync(int messageId)
        {
            if (messageId <= 0) return;

            var target = FindMessageById(messageId);
            for (var i = 0; target == null && i < 40 && !_noMoreOlderMessages; i++)
            {
                await LoadOlderMessagesForTargetAsync();
                target = FindMessageById(messageId);
            }

            if (target == null)
            {
                ScrollToBottom(false);
                return;
            }

            _stickToBottom = false;
            IgnoreScrollTrackingBriefly();
            TryUpdateMessageListLayout("ScrollToMessageAsync-initial");
            MessageList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, delegate
            {
                IgnoreScrollTrackingBriefly();
                TryUpdateMessageListLayout("ScrollToMessageAsync-deferred");
                MessageList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
                UpdateScrollDownButton();
            });
        }

        private async System.Threading.Tasks.Task LoadOlderMessagesForTargetAsync()
        {
            if (_chat == null || _autoLoadingOlder || _noMoreOlderMessages || _messages.Count == 0) return;
            var oldestId = GetOldestMessageId();
            if (oldestId <= 0) return;

            _autoLoadingOlder = true;
            try
            {
                var older = await TelegramService.Instance.GetHistoryBeforeAsync(_chat, oldestId, OlderHistoryLimit);
                if (older == null) return;
                if (older.Count == 0)
                {
                    _olderEmptyResponseCount++;
                    _noMoreOlderMessages = _olderEmptyResponseCount >= 2;
                    return;
                }
                var added = MergeMessages(older, false);
                if (added > 0) _olderEmptyResponseCount = 0;
                if (added > 0) added += await CompleteTopBoundaryAlbumAsync();
                if (added > 0) added += await CompleteVisibleGroupedAlbumsAsync();
                if (added > 0)
                {
                    BeginAutoDownloadMedia();
                    StartBackgroundReactionLoad();
                }
                UpdateOutgoingMessageStates();
                UpdateScrollDownButton();
            }
            catch
            {
                _olderEmptyResponseCount++;
                _noMoreOlderMessages = _olderEmptyResponseCount >= 2;
            }
            finally
            {
                _autoLoadingOlder = false;
                UpdateTopLoadMorePanel();
            }
        }

        private int MergeMessages(IList<ChatMessageViewModel> incoming, bool replaceAll)
        {
            if (replaceAll)
                return MergeInitialMessagesFast(incoming);

            if (_messages == null)
            {
                _messages = new ObservableCollection<object>();
                if (MessageList != null) MessageList.ItemsSource = _messages;
            }

            if (incoming == null || incoming.Count == 0)
            {
                UpdateBotInterfaceState();
                return 0;
            }
            var added = 0;
            var hasGroupedIncoming = false;
            for (var i = 0; i < incoming.Count; i++)
            {
                var msg = incoming[i];
                if (msg == null) continue;
                if (msg.HasReplyKeyboard) _botReplyKeyboardExplicitlyRemoved = false;
                if (msg.RemovesReplyKeyboard) _botReplyKeyboardExplicitlyRemoved = true;
                if (!IsDisplayableMessage(msg))
                {
                    DebugChatMessage("ui-skip-not-displayable", msg);
                    continue;
                }
                if (!HasVisibleText(msg.Text))
                    DebugChatMessage("ui-displayable-without-visible-text", msg);
                if (msg.IsOutgoing && msg.Id > 0)
                    RemoveMatchingPendingOutgoing(msg);

                if (msg.GroupedId != 0)
                {
                    hasGroupedIncoming = true;
                    EnsureGroupedMessageMediaItem(msg);
                    NormalizeGroupedMessageContainer(msg);
                }

                var key = MessageKey(msg);
                if (_messageKeys.Contains(key))
                {
                    if (!HasVisibleText(msg.Text))
                        DebugChatMessage("ui-duplicate-update-without-visible-text", msg);
                    UpdateExistingMessageState(msg);
                    if (msg.GroupedId != 0) MergeGroupedMessage(msg);
                    continue;
                }

                if (msg.GroupedId != 0 && MergeGroupedMessage(msg))
                {
                    if (!HasVisibleText(msg.Text))
                        DebugChatMessage("ui-merged-grouped-without-visible-text", msg);
                    _messageKeys.Add(key);
                    continue;
                }

                _messageKeys.Add(key);
                InsertSorted(msg);
                added++;
            }
            // Album coalescing is O(n^2); do not run it for ordinary reaction/read/edit updates.
            if (hasGroupedIncoming) MergeExistingGroupedMessages();

            var structureChanged = added > 0 || hasGroupedIncoming;
            if (structureChanged)
            {
                UpdateMessageGrouping();
                ResolveReplyPreviews();
                UpdatePinnedMessageBar();
                UpdateTopLoadMorePanel();
                UpdateBotInterfaceState();
            }

            QueueCustomReactionIconLoadForVisibleMessagesCoalesced();
            // Read-by users are loaded lazily from the message action menu.
            // For outgoing group messages we still probe a tiny recent window
            // only to update the checkmark: viewers.Count > 0 means somebody
            // has read the message. This does not change bubble layout.
            StartBackgroundReadByLoad();
            UpdateDateSeparators(replaceAll);
            if (DeduplicateSystemTimelineItems())
                UpdateMessageGrouping();
            return added;
        }

        private int MergeInitialMessagesFast(IList<ChatMessageViewModel> incoming)
        {
            _messageKeys.Clear();
            _albumCompletionRequested.Clear();
            _albumCompletionAttempts.Clear();
            _hasUnreadSeparator = false;
            _olderEmptyResponseCount = 0;
            ClearFfmpegVideoCache();
            _readByLoadRequestedIds.Clear();
            _reactionLoadRequestedIds.Clear();
            _customReactionIconRequestedIds.Clear();
            _videoPreviewRequestedKeys.Clear();
            _videoPreviewRetryCounts.Clear();
            _videoPreviewRetryQueuedKeys.Clear();
            _autoMediaDownloadFailedKeys.Clear();

            var rows = new List<ChatMessageViewModel>();
            var byKey = new Dictionary<string, ChatMessageViewModel>();
            var groupedKeepers = new Dictionary<long, ChatMessageViewModel>();
            var added = 0;

            if (incoming != null)
            {
                for (var i = 0; i < incoming.Count; i++)
                {
                    var msg = incoming[i];
                    if (msg == null) continue;
                    if (msg.HasReplyKeyboard) _botReplyKeyboardExplicitlyRemoved = false;
                    if (msg.RemovesReplyKeyboard) _botReplyKeyboardExplicitlyRemoved = true;
                    if (!IsDisplayableMessage(msg))
                    {
                        DebugChatMessage("ui-skip-not-displayable", msg);
                        continue;
                    }
                    if (!HasVisibleText(msg.Text))
                        DebugChatMessage("ui-displayable-without-visible-text", msg);

                    var key = MessageKey(msg);
                    ChatMessageViewModel existing;
                    if (byKey.TryGetValue(key, out existing))
                    {
                        existing.UpdateFrom(msg);
                        continue;
                    }

                    _messageKeys.Add(key);
                    byKey[key] = msg;

                    if (msg.GroupedId != 0)
                    {
                        EnsureGroupedMessageMediaItem(msg);
                        NormalizeGroupedMessageContainer(msg);

                        ChatMessageViewModel keeper;
                        if (groupedKeepers.TryGetValue(msg.GroupedId, out keeper))
                        {
                            MergeGroupedMessageIntoKeeper(keeper, msg);
                            continue;
                        }

                        groupedKeepers[msg.GroupedId] = msg;
                    }

                    rows.Add(msg);
                    added++;
                }
            }

            rows.Sort(CompareMessagePosition);
            for (var i = 0; i < rows.Count; i++)
            {
                var msg = rows[i];
                if (msg == null || msg.GroupedId == 0) continue;
                NormalizeGroupedMessageContainer(msg);
                msg.NotifyContentChanged();
            }

            ReplaceMessageCollection(rows);
            UpdateMessageGrouping();
            ResolveReplyPreviews();
            UpdatePinnedMessageBar();
            UpdateTopLoadMorePanel();
            UpdateBotInterfaceState();
            UpdateDateSeparators(true);
            if (DeduplicateSystemTimelineItems())
                UpdateMessageGrouping();
            BindMessageCollection();

            return added;
        }

        private void ReplaceMessageCollection(IList<ChatMessageViewModel> messages)
        {
            var rows = new List<object>(messages == null ? 0 : messages.Count);
            if (messages != null)
            {
                for (var i = 0; i < messages.Count; i++)
                    if (messages[i] != null) rows.Add(messages[i]);
            }

            if (MessageList != null)
                MessageList.ItemsSource = null;
            _messages = new ObservableCollection<object>(rows);
        }

        private void BindMessageCollection()
        {
            if (MessageList != null)
                MessageList.ItemsSource = _messages;
        }

        private void MergeGroupedMessageIntoKeeper(ChatMessageViewModel keeper, ChatMessageViewModel incoming)
        {
            if (keeper == null || incoming == null) return;

            MergeGroupedMessageMetadata(keeper, incoming);
            if (incoming.MediaItems != null)
            {
                for (var i = 0; i < incoming.MediaItems.Count; i++)
                {
                    var item = incoming.MediaItems[i];
                    if (item == null || HasMediaItem(keeper, item)) continue;
                    keeper.AddMediaItem(item);
                }
            }

            NormalizeGroupedMessageContainer(keeper);
        }

        private void UpdateDateSeparators(bool fullRebuild)
        {
            if (!fullRebuild) return; // Incremental: handled by InsertSorted

            // Full rebuild only on initial load (replaceAll=true).
            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                if (IsDateSeparator(_messages[i]))
                    _messages.RemoveAt(i);
            }
            int lastDateDay = -1;
            for (var i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg == null || msg.Date <= 0) continue;
                var day = DateToDay(msg.Date);
                if (day != lastDateDay)
                {
                    lastDateDay = day;
                    _messages.Insert(i, new DateSeparatorItem { DateText = BuildDateSeparatorText(msg.Date), DateUnix = msg.Date });
                    i++;
                }
            }
            InsertUnreadSeparatorIfNeeded();
        }

        private bool DeduplicateSystemTimelineItems()
        {
            if (_messages == null || _messages.Count == 0) return false;

            var changed = false;
            var seenDateDays = new HashSet<int>();
            var unreadSeen = false;
            var previousWasDateSeparator = false;
            string previousServiceKey = null;
            int previousServiceDate = 0;

            for (var i = 0; i < _messages.Count; i++)
            {
                var unread = _messages[i] as UnreadSeparatorItem;
                if (unread != null)
                {
                    if (unreadSeen)
                    {
                        _messages.RemoveAt(i);
                        i--;
                        changed = true;
                        continue;
                    }

                    unreadSeen = true;
                    previousWasDateSeparator = false;
                    continue;
                }

                if (IsDateSeparator(_messages[i]))
                {
                    var separator = _messages[i] as DateSeparatorItem;
                    var day = separator == null ? -1 : DateToDay(separator.DateUnix);
                    if (previousWasDateSeparator || seenDateDays.Contains(day))
                    {
                        _messages.RemoveAt(i);
                        i--;
                        changed = true;
                        continue;
                    }

                    seenDateDays.Add(day);
                    previousWasDateSeparator = true;
                    continue;
                }

                previousWasDateSeparator = false;

                var msg = _messages[i] as ChatMessageViewModel;
                if (msg == null)
                {
                    previousServiceKey = null;
                    previousServiceDate = 0;
                    continue;
                }

                if (!msg.IsServiceMessage || string.IsNullOrWhiteSpace(msg.ServiceActionText))
                {
                    previousServiceKey = null;
                    previousServiceDate = 0;
                    continue;
                }

                var serviceKey = DateToDay(msg.Date).ToString() + ":" + NormalizeSystemText(msg.ServiceActionText);
                if (previousServiceKey == serviceKey && Math.Abs(msg.Date - previousServiceDate) <= 120)
                {
                    _messages.RemoveAt(i);
                    i--;
                    changed = true;
                    continue;
                }

                previousServiceKey = serviceKey;
                previousServiceDate = msg.Date;
            }

            _hasUnreadSeparator = unreadSeen;
            return changed;
        }

        private static string NormalizeSystemText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return text.Replace("\r", " ").Replace("\n", " ").Trim().ToLowerInvariant();
        }

        private static int DateToDay(int unixSeconds)
        {
            var utc = new DateTime(1970, 1, 1).AddSeconds(unixSeconds);
            return utc.DayOfYear + utc.Year * 366;
        }

        private static string BuildDateSeparatorText(int unixSeconds)
        {
            var local = new DateTime(1970, 1, 1).AddSeconds(unixSeconds).ToLocalTime();
            if (local.Date == DateTime.Now.Date) return "Today";
            return local.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        }

        private void InsertUnreadSeparatorIfNeeded()
        {
            RemoveUnreadSeparator();
            if (_chat == null || _chat.UnreadCount <= 0 || _messages == null || _messages.Count == 0) return;

            var unreadIncomingToMark = _chat.UnreadCount;
            var incomingSeen = 0;
            var targetIndex = -1;
            var firstRealMessageIndex = -1;

            for (var i = 0; i < _messages.Count; i++)
            {
                if (_messages[i] is ChatMessageViewModel)
                {
                    firstRealMessageIndex = i;
                    break;
                }
            }

            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg == null) continue;
                if (targetIndex < 0) targetIndex = i;
                if (msg.IsOutgoing) continue;
                incomingSeen++;
                targetIndex = i;
                if (incomingSeen >= unreadIncomingToMark) break;
            }

            if (targetIndex < 0) targetIndex = firstRealMessageIndex;
            if (targetIndex < 0) return;
            _messages.Insert(targetIndex, new UnreadSeparatorItem());
            _hasUnreadSeparator = true;
        }

        private void RemoveUnreadSeparator()
        {
            if (_messages == null) return;
            if (!_hasUnreadSeparator) return;
            _hasUnreadSeparator = false;
            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                if (_messages[i] is UnreadSeparatorItem)
                    _messages.RemoveAt(i);
            }
        }

        private void DismissUnreadSeparatorAfterOutgoing()
        {
            RemoveUnreadSeparator();
            if (_chat != null) _chat.UnreadCount = 0;
            UpdateScrollDownButton();
        }

        private int RemoveMessagesById(IList<int> deletedIds)
        {
            if (deletedIds == null || deletedIds.Count == 0 || _messages == null) return 0;
            var removed = 0;
            for (var d = 0; d < deletedIds.Count; d++)
            {
                var id = deletedIds[d];
                if (id <= 0) continue;
                if (RemoveMessageRowById(id))
                {
                    removed++;
                    continue;
                }

                if (RemoveAlbumItemByMessageId(id))
                    removed++;
            }

            if (removed > 0)
            {
                UpdateMessageGrouping();
                ResolveReplyPreviews();
                UpdateTopLoadMorePanel();
                UpdateScrollDownButton();
            }
            return removed;
        }

        private bool RemoveMessageRowById(int id)
        {
            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg == null || msg.Id != id) continue;
                _messageKeys.Remove(MessageKey(msg));
                _messages.RemoveAt(i);
                return true;
            }
            return false;
        }

        private bool RemoveAlbumItemByMessageId(int id)
        {
            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg == null || msg.MediaItems == null || msg.MediaItems.Count == 0) continue;
                if (!msg.RemoveMediaItemBySourceMessageId(id)) continue;
                if (msg.MediaItems.Count == 0)
                {
                    _messageKeys.Remove(MessageKey(msg));
                    _messages.RemoveAt(i);
                }
                else
                {
                    NormalizeGroupedMessageContainer(msg);
                    msg.NotifyContentChanged();
                    ReplaceMessage(msg);
                }
                return true;
            }
            return false;
        }

        private bool IsDisplayableMessage(ChatMessageViewModel msg)
        {
            if (msg == null) return false;
            if (msg.IsServiceMessage && !string.IsNullOrEmpty(msg.ServiceActionText)) return true;
            if (HasVisibleText(msg.Text)) return true;
            if (msg.HasMedia) return true;
            if (msg.MediaItems != null && msg.MediaItems.Count > 0) return true;
            if (!string.IsNullOrEmpty(msg.ForwardedFrom)) return true;
            if (msg.ReplyToMessageId > 0) return true;
            if (msg.CanOpenComments) return true;
            return false;
        }

        private static bool HasVisibleText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (char.IsWhiteSpace(ch)) continue;
                if (ch == '\u200b' || ch == '\u200c' || ch == '\u200d' || ch == '\ufeff') continue;
                return true;
            }
            return false;
        }

        private void DebugChatMessage(string stage, ChatMessageViewModel msg)
        {
            try
            {
                Debug.WriteLine(
                    "TG_EMPTY_MESSAGE " + stage +
                    " chat=" + DebugSafe(_chat == null ? null : _chat.PeerKey) +
                    " chatTitle=" + DebugSafe(_chat == null ? null : _chat.Title) +
                    " id=" + (msg == null ? 0 : msg.Id).ToString() +
                    " date=" + (msg == null ? 0 : msg.Date).ToString() +
                    " outgoing=" + (msg != null && msg.IsOutgoing).ToString() +
                    " hasMedia=" + (msg != null && msg.HasMedia).ToString() +
                    " mediaKind=" + DebugSafe(msg == null ? null : msg.MediaKind) +
                    " mediaTitle=" + DebugSafe(msg == null ? null : msg.MediaTitle) +
                    " mediaFile=" + DebugSafe(msg == null ? null : msg.MediaFileName) +
                    " mediaUri=" + DebugSafe(msg == null ? null : msg.MediaFileUri) +
                    " mediaItems=" + (msg == null || msg.MediaItems == null ? 0 : msg.MediaItems.Count).ToString() +
                    " grouped=" + (msg == null ? 0 : msg.GroupedId).ToString() +
                    " replyTo=" + (msg == null ? 0 : msg.ReplyToMessageId).ToString() +
                    " comments=" + (msg == null ? false : msg.CanOpenComments).ToString() +
                    " forwarded=" + DebugSafe(msg == null ? null : msg.ForwardedFrom) +
                    " textLen=" + DebugTextLength(msg == null ? null : msg.Text).ToString() +
                    " textVisibility=" + (msg == null ? "-" : msg.TextVisibility.ToString()) +
                    " fileVisibility=" + (msg == null ? "-" : msg.FileVisibility.ToString()) +
                    " mediaPlaceholder=" + (msg == null ? "-" : msg.MediaDownloadPlaceholderVisibility.ToString()) +
                    " structured=" + (msg == null ? "-" : msg.StructuredMediaVisibility.ToString()) +
                    " textPreview=" + DebugPreview(msg == null ? null : msg.Text));
            }
            catch
            {
            }
        }

        private static string DebugSafe(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            return value.Replace("\r", " ").Replace("\n", " ");
        }

        private static int DebugTextLength(string value)
        {
            return value == null ? 0 : value.Length;
        }

        private static string DebugPreview(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            value = value.Replace("\r", "\\r").Replace("\n", "\\n");
            return value.Length > 80 ? value.Substring(0, 80) : value;
        }

        private void UpdateExistingMessageState(ChatMessageViewModel incoming)
        {
            if (incoming == null || incoming.Id <= 0) return;
            var existing = FindMessageById(incoming.Id);
            if (existing == null) return;
            if (ShouldIgnoreStalePollRefresh(existing, incoming)) return;
            RemovePendingPollLocalSelection(existing.Id);

            // Reaction/read-state updates are frequent. Rebuilding RichText/emoji runs for an
            // unchanged message is one of the most visible micro-freezes on low-end phones.
            var textChanged = !string.Equals(existing.Text, incoming.Text, StringComparison.Ordinal);
            existing.UpdateFrom(incoming);
            if (textChanged) RefreshVisibleMessageMarkdown(existing);
        }

        private void RefreshVisibleMessageMarkdown(ChatMessageViewModel msg)
        {
            if (msg == null || MessageList == null) return;
            var container = MessageList.ContainerFromItem(msg) as FrameworkElement;
            if (container == null) return;
            var textBlock = FindNamedChild<TextBlock>(container, "MessageMarkdownText");
            if (textBlock != null)
            {
                ApplyMarkdownText(textBlock, msg);
                return;
            }

            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
            {
                var delayedContainer = MessageList == null ? null : MessageList.ContainerFromItem(msg) as FrameworkElement;
                var delayedTextBlock = FindNamedChild<TextBlock>(delayedContainer, "MessageMarkdownText");
                if (delayedTextBlock != null) ApplyMarkdownText(delayedTextBlock, msg);
            });
        }

        private async System.Threading.Tasks.Task<int> CompleteTopBoundaryAlbumAsync()
        {
            if (_chat == null || _messages == null || _messages.Count == 0) return 0;

            var totalAdded = 0;
            for (var pass = 0; pass < 3; pass++)
            {
                var first = FirstRealMessage();
                if (first == null || first.GroupedId == 0 || first.Id <= 0) break;

                var groupId = first.GroupedId;
                var older = await TelegramService.Instance.GetHistoryBeforeAsync(_chat, first.Id, OlderHistoryLimit);
                if (older == null) return totalAdded;
                if (older.Count == 0) break;

                var sameAlbum = new List<ChatMessageViewModel>();
                for (var i = 0; i < older.Count; i++)
                {
                    var msg = older[i];
                    if (msg != null && msg.GroupedId == groupId)
                        sameAlbum.Add(msg);
                }

                if (sameAlbum.Count == 0) break;

                var newParts = 0;
                for (var i = 0; i < sameAlbum.Count; i++)
                {
                    if (!_messageKeys.Contains(MessageKey(sameAlbum[i])))
                        newParts++;
                }

                var added = MergeMessages(sameAlbum, false);
                if (added <= 0 && newParts <= 0) break;
                totalAdded += Math.Max(added, newParts);
            }

            return totalAdded;
        }

        private async System.Threading.Tasks.Task<int> CompleteVisibleGroupedAlbumsAsync()
        {
            if (_chat == null || _messages == null || _messages.Count == 0) return 0;

            var candidates = new List<ChatMessageViewModel>();
            for (var i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg == null || msg.GroupedId == 0 || msg.Id <= 0) continue;
                var count = msg.MediaItems == null ? 0 : msg.MediaItems.Count;
                if (count > 1) continue;
                if (_albumCompletionRequested.Contains(msg.GroupedId)) continue;
                int attempts;
                if (_albumCompletionAttempts.TryGetValue(msg.GroupedId, out attempts) && attempts >= 3) continue;
                candidates.Add(msg);
                if (candidates.Count >= 3) break;
            }

            var totalAdded = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate == null || candidate.GroupedId == 0 || candidate.Id <= 0) continue;
                try
                {
                    IncrementAlbumCompletionAttempt(candidate.GroupedId);
                    var window = await TelegramService.Instance.GetHistoryAroundAsync(_chat, candidate.Id, 80);
                    if (window == null || window.Count == 0) continue;

                    var sameAlbum = new List<ChatMessageViewModel>();
                    for (var j = 0; j < window.Count; j++)
                    {
                        var msg = window[j];
                        if (msg != null && msg.GroupedId == candidate.GroupedId)
                            sameAlbum.Add(msg);
                    }

                    if (sameAlbum.Count <= 1) continue;
                    _albumCompletionRequested.Add(candidate.GroupedId);
                    totalAdded += MergeMessages(sameAlbum, false);
                }
                catch
                {
                }
            }

            return totalAdded;
        }

        private void IncrementAlbumCompletionAttempt(long groupedId)
        {
            int attempts;
            _albumCompletionAttempts.TryGetValue(groupedId, out attempts);
            _albumCompletionAttempts[groupedId] = attempts + 1;
        }


        private void MergeExistingGroupedMessages()
        {
            if (_messages == null || _messages.Count < 2) return;

            for (var i = 0; i < _messages.Count; i++)
            {
                var keeper = _messages[i] as ChatMessageViewModel;
                if (keeper == null || keeper.GroupedId == 0) continue;

                EnsureGroupedMessageMediaItem(keeper);

                for (var j = _messages.Count - 1; j > i; j--)
                {
                    var other = _messages[j] as ChatMessageViewModel;
                    if (other == null || other.GroupedId != keeper.GroupedId) continue;

                    EnsureGroupedMessageMediaItem(other);

                    MergeGroupedMessageMetadata(keeper, other);

                    if (other.MediaItems != null)
                    {
                        for (var k = 0; k < other.MediaItems.Count; k++)
                        {
                            var item = other.MediaItems[k];
                            if (item == null || HasMediaItem(keeper, item)) continue;
                            keeper.AddMediaItem(item);
                        }
                    }

                    _messageKeys.Remove(MessageKey(other));
                    _messages.RemoveAt(j);
                }

                NormalizeGroupedMessageContainer(keeper);
                keeper.NotifyContentChanged();
                ReplaceMessage(keeper);
                RefreshVisibleMessageMarkdown(keeper);
            }
        }


        private bool MergeGroupedMessage(ChatMessageViewModel incoming)
        {
            if (incoming == null || incoming.GroupedId == 0) return false;
            EnsureGroupedMessageMediaItem(incoming);

            for (var i = 0; i < _messages.Count; i++)
            {
                var existing = _messages[i] as ChatMessageViewModel;
                if (existing == null || existing.GroupedId != incoming.GroupedId) continue;

                MergeGroupedMessageMetadata(existing, incoming);

                if (incoming.MediaItems != null)
                {
                    for (var j = 0; j < incoming.MediaItems.Count; j++)
                    {
                        var item = incoming.MediaItems[j];
                        if (item == null || HasMediaItem(existing, item)) continue;
                        existing.AddMediaItem(item);
                    }
                }

                NormalizeGroupedMessageContainer(existing);
                existing.NotifyContentChanged();
                ReplaceMessage(existing);
                RefreshVisibleMessageMarkdown(existing);
                return true;
            }

            return false;
        }

        private void MergeGroupedMessageMetadata(ChatMessageViewModel target, ChatMessageViewModel source)
        {
            if (target == null || source == null) return;

            if (ShouldReplaceGroupedText(target, source))
            {
                target.Text = source.Text;
                target.SetTextEntities(source.TextEntities);
            }

            if (string.IsNullOrEmpty(target.SenderName) && !string.IsNullOrEmpty(source.SenderName)) target.SenderName = source.SenderName;
            if (string.IsNullOrEmpty(target.PostAuthor) && !string.IsNullOrEmpty(source.PostAuthor)) target.SetPostAuthor(source.PostAuthor);
            if (target.EditDate == 0 && source.EditDate > 0) target.SetEditDate(source.EditDate);
            if (!target.IsChannelPost && source.IsChannelPost) target.IsChannelPost = true;
            if (string.IsNullOrEmpty(target.SenderInitials) && !string.IsNullOrEmpty(source.SenderInitials)) target.SenderInitials = source.SenderInitials;
            if (string.IsNullOrEmpty(target.SenderAvatarUri) && !string.IsNullOrEmpty(source.SenderAvatarUri)) target.SenderAvatarUri = source.SenderAvatarUri;
            if (string.IsNullOrEmpty(target.SenderPeerKey) && !string.IsNullOrEmpty(source.SenderPeerKey)) target.SenderPeerKey = source.SenderPeerKey;
            if (string.IsNullOrEmpty(target.SenderPeerType) && !string.IsNullOrEmpty(source.SenderPeerType)) target.SenderPeerType = source.SenderPeerType;
            if (target.SenderPeerId == 0 && source.SenderPeerId != 0) target.SenderPeerId = source.SenderPeerId;
            if (target.SenderAccessHash == 0 && source.SenderAccessHash != 0) target.SenderAccessHash = source.SenderAccessHash;
            if (!target.SenderIsGroup && source.SenderIsGroup) target.SenderIsGroup = true;
            if (!target.SenderIsChannel && source.SenderIsChannel) target.SenderIsChannel = true;
            if (!target.SenderIsBroadcast && source.SenderIsBroadcast) target.SenderIsBroadcast = true;
            if (target.SenderAvatarPhotoId == 0 && source.SenderAvatarPhotoId != 0) target.SenderAvatarPhotoId = source.SenderAvatarPhotoId;
            if (target.SenderAvatarDcId == 0 && source.SenderAvatarDcId != 0) target.SenderAvatarDcId = source.SenderAvatarDcId;
            if ((target.SenderAvatarStrippedThumb == null || target.SenderAvatarStrippedThumb.Length == 0) && source.SenderAvatarStrippedThumb != null) target.SenderAvatarStrippedThumb = source.SenderAvatarStrippedThumb;

            if (source.CanOpenComments || source.CommentsCount > target.CommentsCount)
            {
                target.CommentsCount = source.CommentsCount;
                target.CommentsChannelId = source.CommentsChannelId;
                target.CommentsMaxId = source.CommentsMaxId;
                target.CommentsReadMaxId = source.CommentsReadMaxId;
                target.CommentsDiscussionTitle = source.CommentsDiscussionTitle;
                target.CommentsDiscussionAccessHash = source.CommentsDiscussionAccessHash;
                target.CommentsDiscussionCanSend = source.CommentsDiscussionCanSend;
                target.CanOpenComments = source.CanOpenComments;
                target.SetCommentAvatars(source.CommentAvatars);
            }

            if (source.Reactions != null && source.Reactions.Count > 0)
                target.SetReactions(source.Reactions);
            if (!target.CanReact && source.CanReact) target.CanReact = true;
        }

        private bool ShouldReplaceGroupedText(ChatMessageViewModel target, ChatMessageViewModel source)
        {
            if (source == null || !HasDisplayText(source.Text)) return false;
            if (target == null || !HasDisplayText(target.Text)) return true;
            return IsLegacyMediaFallbackText(target.Text);
        }

        private bool IsLegacyMediaFallbackText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var value = text.Trim();
            return string.Equals(value, "Photo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Video", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Video message", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Round video", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "GIF", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Sticker", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Voice message", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Audio", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "File", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Album", StringComparison.OrdinalIgnoreCase);
        }

        private bool HasDisplayText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (char.IsWhiteSpace(ch)) continue;
                if (ch == '\u200b' || ch == '\u200c' || ch == '\u200d' || ch == '\ufeff') continue;
                return true;
            }
            return false;
        }

        private bool IsSameGroupedChat(ChatMessageViewModel a, ChatMessageViewModel b)
        {
            // ChatPage already displays a single dialog, and Telegram grouped_id belongs to one album.
            // Do not split incoming albums by sender flags: some incoming media parts may have incomplete from_id.
            return a != null && b != null && a.GroupedId != 0 && a.GroupedId == b.GroupedId;
        }

        private void EnsureGroupedMessageMediaItem(ChatMessageViewModel message)
        {
            if (message == null || message.GroupedId == 0 || !message.HasMedia) return;
            if (message.MediaItems != null && message.MediaItems.Count > 0) return;

            var item = new ChatMediaItemViewModel
            {
                SourceMessageId = message.Id,
                MediaKind = message.MediaKind,
                MediaFileName = message.MediaFileName,
                MediaPerformer = message.MediaPerformer,
                MediaMimeType = message.MediaMimeType,
                MediaId = message.MediaId,
                MediaFullId = message.MediaFullId,
                MediaAccessHash = message.MediaAccessHash,
                MediaDcId = message.MediaDcId,
                MediaFileReference = message.MediaFileReference,
                MediaPreviewId = message.MediaPreviewId,
                MediaThumbSize = message.MediaThumbSize,
                FullPhotoThumbSize = message.FullPhotoThumbSize,
                MediaThumbBytes = message.MediaThumbBytes,
                MediaPreviewUri = message.MediaPreviewUri,
                MediaSize = message.MediaSize,
                MediaIsPhoto = message.MediaIsPhoto,
                MediaDurationSeconds = message.MediaDurationSeconds,
                MediaTitle = message.MediaTitle,
                MediaFileUri = message.MediaFileUri,
                MediaFullUri = message.MediaFullUri,
                MediaErrorText = message.MediaErrorText,
                HasPlaybackError = message.HasPlaybackError
            };

            message.AddMediaItem(item);
            NormalizeGroupedMessageContainer(message);
        }

        private void NormalizeGroupedMessageContainer(ChatMessageViewModel message)
        {
            if (message == null || message.GroupedId == 0) return;
            if (message.MediaItems == null || message.MediaItems.Count == 0) return;

            // A grouped/album message is rendered only through MediaItems.
            // The root media state is kept only for the single "download all" placeholder.
            message.MediaKind = "grouped";
            message.MediaTitle = string.Empty;
            message.MediaErrorText = string.Empty;
            message.HasPlaybackError = false;

            if (AreAllGroupedMediaItemsDownloaded(message))
                message.MediaFileUri = "grouped://loaded";
            else
                message.MediaFileUri = null;
        }

        private bool AreAllGroupedMediaItemsDownloaded(ChatMessageViewModel message)
        {
            if (message == null || message.MediaItems == null || message.MediaItems.Count == 0) return false;
            for (var i = 0; i < message.MediaItems.Count; i++)
            {
                var item = message.MediaItems[i];
                if (item == null || string.IsNullOrEmpty(item.MediaFileUri)) return false;
            }
            return true;
        }

        private bool HasMediaItem(ChatMessageViewModel message, ChatMediaItemViewModel item)
        {
            if (message == null || item == null || message.MediaItems == null) return false;
            for (var i = 0; i < message.MediaItems.Count; i++)
            {
                var existing = message.MediaItems[i];
                if (existing == null) continue;
                if (existing.SourceMessageId > 0 && item.SourceMessageId > 0 && existing.SourceMessageId == item.SourceMessageId)
                    return true;
                if (existing.MediaId != 0 && item.MediaId != 0)
                {
                    if (existing.MediaId == item.MediaId &&
                        existing.MediaAccessHash == item.MediaAccessHash &&
                        string.Equals(existing.MediaKind, item.MediaKind, StringComparison.OrdinalIgnoreCase))
                        return true;
                    continue;
                }
                if (IsGenericAlbumFileName(existing.MediaFileName) || IsGenericAlbumFileName(item.MediaFileName)) continue;
                if (!string.IsNullOrEmpty(existing.MediaFileName) &&
                    existing.MediaFileName == item.MediaFileName &&
                    existing.MediaSize > 0 &&
                    existing.MediaSize == item.MediaSize &&
                    string.Equals(existing.MediaKind, item.MediaKind, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private bool IsGenericAlbumFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return true;
            return string.Equals(fileName, "photo.jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "video.mp4", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "animation.gif", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "file", StringComparison.OrdinalIgnoreCase);
        }

        private void RemoveMatchingPendingOutgoing(ChatMessageViewModel serverMessage)
        {
            if (serverMessage == null || !serverMessage.IsOutgoing || serverMessage.Id <= 0) return;
            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                var pending = _messages[i] as ChatMessageViewModel;
                if (pending == null || !pending.IsOutgoing || pending.Id == serverMessage.Id) continue;
                if (pending.Id > 0 && !pending.IsSending) continue;
                if (!IsSameOutgoingPending(pending, serverMessage)) continue;
                _messageKeys.Remove(MessageKey(pending));
                _messages.RemoveAt(i);
                return;
            }
        }

        private bool IsSameOutgoingPending(ChatMessageViewModel pending, ChatMessageViewModel serverMessage)
        {
            if (pending == null || serverMessage == null) return false;
            var pendingText = pending.Text == null ? string.Empty : pending.Text.Trim();
            var serverText = serverMessage.Text == null ? string.Empty : serverMessage.Text.Trim();
            var isPendingPlaceholder = IsPendingMediaPlaceholder(pendingText, serverMessage);
            if (!IsCompatibleOutgoingMediaKind(pending.MediaKind, serverMessage.MediaKind) && !isPendingPlaceholder) return false;
            if (pendingText != serverText && !IsPendingMediaPlaceholder(pendingText, serverMessage)) return false;
            if (pending.Date > 0 && serverMessage.Date > 0 && Math.Abs(serverMessage.Date - pending.Date) > 600) return false;
            return true;
        }

        private bool IsCompatibleOutgoingMediaKind(string pendingKind, string serverKind)
        {
            if (string.Equals(pendingKind, serverKind, StringComparison.OrdinalIgnoreCase)) return true;
            if (IsAudioLikeKind(pendingKind) && IsAudioLikeKind(serverKind)) return true;
            return false;
        }

        private bool IsAudioLikeKind(string kind)
        {
            return string.Equals(kind, "voice", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, "audio", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPendingMediaPlaceholder(string pendingText, ChatMessageViewModel serverMessage)
        {
            if (serverMessage == null) return false;
            if (string.IsNullOrEmpty(pendingText)) return false;
            if (!pendingText.StartsWith("Sending ", StringComparison.Ordinal)) return false;
            if (serverMessage.HasMedia || (serverMessage.MediaItems != null && serverMessage.MediaItems.Count > 0)) return true;
            return string.IsNullOrEmpty(serverMessage.Text);
        }

        private string MessageKey(ChatMessageViewModel msg)
        {
            if (msg == null) return string.Empty;
            return msg.Id.ToString();
        }

        private static long MessageSortId(ChatMessageViewModel msg)
        {
            if (msg == null) return 0;
            return msg.SortId != 0 ? msg.SortId : msg.Id;
        }

        private static int CompareMessagePosition(ChatMessageViewModel left, ChatMessageViewModel right)
        {
            var leftSortId = MessageSortId(left);
            var rightSortId = MessageSortId(right);
            if (leftSortId != 0 || rightSortId != 0)
                return leftSortId.CompareTo(rightSortId);
            var leftDate = left == null ? 0 : left.Date;
            var rightDate = right == null ? 0 : right.Date;
            return leftDate.CompareTo(rightDate);
        }

        private void InsertSorted(ChatMessageViewModel msg)
        {
            if (msg == null) return;

            var msgDay = DateToDay(msg.Date);

            // Fast path: 99% of messages arrive newest-first → just append.
            if (_messages.Count == 0)
            {
                _messages.Add(msg);
                EnsureSeparatorsAround(0, msgDay, msg.Date);
                return;
            }

            var lastIdx = _messages.Count - 1;
            var lastItem = _messages[lastIdx];
            if (IsListSeparator(lastItem))
            {
                // Last item is a separator — find the last real message.
                lastIdx--;
                while (lastIdx >= 0 && IsListSeparator(_messages[lastIdx]))
                    lastIdx--;
            }

            if (lastIdx >= 0)
            {
                var lastMsg = _messages[lastIdx] as ChatMessageViewModel;
                if (lastMsg != null && CompareMessagePosition(msg, lastMsg) >= 0)
                {
                    // New message is newer than the last → append at end.
                    var insertIdx = _messages.Count;
                    _messages.Add(msg);
                    EnsureSeparatorsAround(insertIdx, msgDay, msg.Date);
                    return;
                }
            }

            // Slow path: out-of-order message (edit, history load, etc.) — linear scan.
            for (var i = 0; i < _messages.Count; i++)
            {
                var current = _messages[i] as ChatMessageViewModel;
                if (current != null && CompareMessagePosition(msg, current) < 0)
                {
                    _messages.Insert(i, msg);
                    EnsureSeparatorsAround(i, msgDay, msg.Date);
                    return;
                }
            }
            var appendIdx = _messages.Count;
            _messages.Add(msg);
            EnsureSeparatorsAround(appendIdx, msgDay, msg.Date);
        }

        /// <summary>
        /// O(1) separator maintenance after inserting a message at insertIdx.
        /// Removes redundant separators, inserts new ones where day boundaries changed.
        /// </summary>
        private void EnsureSeparatorsAround(int insertIdx, int msgDay, int msgUnix)
        {
            // Check separator BEFORE the new message (at insertIdx-1).
            if (insertIdx > 0 && IsDateSeparator(_messages[insertIdx - 1]))
            {
                var prevMsgIdx = insertIdx - 2;
                while (prevMsgIdx >= 0 && IsListSeparator(_messages[prevMsgIdx]))
                    prevMsgIdx--;
                if (prevMsgIdx >= 0)
                {
                    var prevDay = DateToDay(((_messages[prevMsgIdx] as ChatMessageViewModel).Date));
                    if (prevDay == msgDay)
                        _messages.RemoveAt(insertIdx - 1);
                }
            }

            // Check separator AFTER the new message (now at insertIdx+1).
            if (insertIdx + 1 < _messages.Count && IsDateSeparator(_messages[insertIdx + 1]))
            {
                var nextMsgIdx = insertIdx + 2;
                while (nextMsgIdx < _messages.Count && IsListSeparator(_messages[nextMsgIdx]))
                    nextMsgIdx++;
                if (nextMsgIdx < _messages.Count)
                {
                    var nextDay = DateToDay(((_messages[nextMsgIdx] as ChatMessageViewModel).Date));
                    if (nextDay == msgDay)
                        _messages.RemoveAt(insertIdx + 1);
                }
            }

            // Check if we need a NEW separator before the message.
            var beforeIdx = insertIdx - 1;
            while (beforeIdx >= 0 && IsListSeparator(_messages[beforeIdx]))
                beforeIdx--;
            if (beforeIdx >= 0)
            {
                var beforeDay = DateToDay(((_messages[beforeIdx] as ChatMessageViewModel).Date));
                if (beforeDay != msgDay)
                {
                    _messages.Insert(beforeIdx + 1, new DateSeparatorItem
                    {
                        DateText = BuildDateSeparatorText(((_messages[beforeIdx] as ChatMessageViewModel).Date)),
                        DateUnix = ((_messages[beforeIdx] as ChatMessageViewModel).Date)
                    });
                }
            }
            else if (insertIdx == 0)
            {
                _messages.Insert(0, new DateSeparatorItem
                {
                    DateText = BuildDateSeparatorText(msgUnix),
                    DateUnix = msgUnix
                });
            }
        }

        private void ReplaceMessage(ChatMessageViewModel msg)
        {
            if (msg == null || _messages == null) return;
            var anchor = CaptureScrollViewportAnchor();
            for (var i = 0; i < _messages.Count; i++)
            {
                var current = _messages[i] as ChatMessageViewModel;
                if (current == null || current.Id != msg.Id) continue;

                // In-place media/reaction/download updates already notify the
                // bound UI through INotifyPropertyChanged. Do not rescan the
                // whole chat sender grouping for the same object.
                if (object.ReferenceEquals(current, msg))
                {
                    QueueRestoreScrollViewportAnchor(anchor);
                    return;
                }

                _messages[i] = msg;
                UpdateMessageGroupingAroundIndex(i);
                QueueRestoreScrollViewportAnchor(anchor);
                return;
            }
        }

        private void UpdateMessageGroupingAroundIndex(int index)
        {
            if (_messages == null || _messages.Count == 0) return;
            if (index < 0) index = 0;
            if (index >= _messages.Count) index = _messages.Count - 1;

            var start = Math.Max(0, index - 1);
            var end = Math.Min(_messages.Count - 1, index + 1);
            for (var i = start; i <= end; i++)
            {
                var current = _messages[i] as ChatMessageViewModel;
                if (current == null) continue;

                ChatMessageViewModel previous = null;
                for (var p = i - 1; p >= 0; p--)
                {
                    previous = _messages[p] as ChatMessageViewModel;
                    if (previous != null) break;
                }

                current.IsFirstInSenderGroup = !IsSameSenderAsPrevious(previous, current);
            }
        }

        private void UpdateMessageGrouping()
        {
            ChatMessageViewModel previous = null;
            for (var i = 0; i < _messages.Count; i++)
            {
                var current = _messages[i] as ChatMessageViewModel;
                if (current == null) continue;
                current.IsFirstInSenderGroup = !IsSameSenderAsPrevious(previous, current);
                previous = current;
            }
        }

        private void ResolveReplyPreviews()
        {
            if (_messages == null || _messages.Count == 0) return;

            // Build the lookup once. The previous implementation called FindMessageById
            // for every reply and became O(n^2) as history grew.
            var byId = new Dictionary<int, ChatMessageViewModel>();
            for (var i = 0; i < _messages.Count; i++)
            {
                var item = _messages[i] as ChatMessageViewModel;
                if (item != null && item.Id > 0) byId[item.Id] = item;
            }

            for (var i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg == null || msg.ReplyToMessageId <= 0) continue;

                ChatMessageViewModel reply;
                if (!byId.TryGetValue(msg.ReplyToMessageId, out reply) || reply == null) continue;

                var sender = reply.IsOutgoing ? "You" : reply.SenderName;
                if (string.IsNullOrEmpty(sender)) sender = "Message";

                var text = reply.Text;
                if (string.IsNullOrEmpty(text)) text = reply.MediaTitle;
                if (string.IsNullOrEmpty(text))
                {
                    if (reply.MediaKind == "photo") text = "Photo";
                    else if (reply.MediaKind == "video" || reply.MediaKind == "roundvideo") text = "Video";
                    else if (reply.MediaKind == "voice") text = "Voice message";
                    else if (reply.MediaKind == "audio") text = "Audio";
                    else if (reply.HasMedia) text = "File";
                }

                if (HasVisibleText(msg.ReplyToText) &&
                    (!HasVisibleText(text) || IsUnsupportedReplyPreviewText(text)))
                    text = msg.ReplyToText;

                if (!string.Equals(msg.ReplyToSenderName, sender, StringComparison.Ordinal) ||
                    !string.Equals(msg.ReplyToText, text, StringComparison.Ordinal))
                    msg.SetReplyPreview(sender, text);
            }
        }

        private static bool IsUnsupportedReplyPreviewText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return string.Equals(text.Trim(), "Unsupported message", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateOutgoingMessageStates()
        {
            if (_messages == null || _chat == null) return;
            var readOutboxMaxId = _chat.ReadOutboxMaxId;
            for (var i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg == null || !msg.IsOutgoing) continue;
                msg.IsSending = msg.Id <= 0 || msg.IsSending;
                msg.IsRead = msg.Id > 0 && readOutboxMaxId > 0 && msg.Id <= readOutboxMaxId;
            }
        }

        private bool IsSameSenderAsPrevious(ChatMessageViewModel previous, ChatMessageViewModel current)
        {
            if (previous == null || current == null) return false;
            if (previous.IsOutgoing != current.IsOutgoing) return false;

            if (current.IsOutgoing && previous.IsOutgoing) return true;

            if (!string.IsNullOrEmpty(previous.SenderPeerKey) && !string.IsNullOrEmpty(current.SenderPeerKey))
                return previous.SenderPeerKey == current.SenderPeerKey;

            if (!string.IsNullOrEmpty(previous.SenderName) && !string.IsNullOrEmpty(current.SenderName))
                return previous.SenderName == current.SenderName;

            return false;
        }

        private int GetNewestMessageId()
        {
            if (_messages == null || _messages.Count == 0) return 0;
            ChatMessageViewModel newest = null;
            for (var i = 0; i < _messages.Count; i++)
            {
                var m = _messages[i] as ChatMessageViewModel;
                if (m == null || m.Id <= 0) continue;
                if (newest == null || CompareMessagePosition(m, newest) > 0)
                    newest = m;
            }
            return newest == null ? 0 : newest.Id;
        }

        private int GetNewestMessageId(IList<ChatMessageViewModel> messages)
        {
            ChatMessageViewModel newest = null;
            if (messages == null) return 0;
            for (var i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m == null || m.Id <= 0) continue;
                if (newest == null || CompareMessagePosition(m, newest) > 0)
                    newest = m;
            }
            return newest == null ? 0 : newest.Id;
        }

        private long GetNewestMessageSortId(IList<ChatMessageViewModel> messages)
        {
            ChatMessageViewModel newest = null;
            if (messages == null) return 0;
            for (var i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m == null || m.Id <= 0) continue;
                if (newest == null || CompareMessagePosition(m, newest) > 0)
                    newest = m;
            }
            return MessageSortId(newest);
        }

        private int GetOldestMessageId()
        {
            ChatMessageViewModel oldest = null;
            for (var i = 0; i < _messages.Count; i++)
            {
                var m = _messages[i] as ChatMessageViewModel;
                if (m == null || m.Id <= 0) continue;
                if (oldest == null || CompareMessagePosition(m, oldest) < 0)
                    oldest = m;
            }
            return oldest == null ? 0 : oldest.Id;
        }

        private long GetOldestMessageSortId()
        {
            ChatMessageViewModel oldest = null;
            if (_messages == null) return 0;
            for (var i = 0; i < _messages.Count; i++)
            {
                var m = _messages[i] as ChatMessageViewModel;
                if (m == null || m.Id <= 0) continue;
                if (oldest == null || CompareMessagePosition(m, oldest) < 0)
                    oldest = m;
            }
            return MessageSortId(oldest);
        }

        private int GetOldestMessageId(IList<ChatMessageViewModel> messages)
        {
            ChatMessageViewModel oldest = null;
            if (messages == null) return 0;
            for (var i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m == null || m.Id <= 0) continue;
                if (oldest == null || CompareMessagePosition(m, oldest) < 0)
                    oldest = m;
            }
            return oldest == null ? 0 : oldest.Id;
        }

        private long GetOldestMessageSortId(IList<ChatMessageViewModel> messages)
        {
            ChatMessageViewModel oldest = null;
            if (messages == null) return 0;
            for (var i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m == null || m.Id <= 0) continue;
                if (oldest == null || CompareMessagePosition(m, oldest) < 0)
                    oldest = m;
            }
            return MessageSortId(oldest);
        }

        private long GetMessageSortIdById(int id)
        {
            if (id <= 0 || _messages == null) return 0;
            for (var i = 0; i < _messages.Count; i++)
            {
                var m = _messages[i] as ChatMessageViewModel;
                if (m != null && m.Id == id) return MessageSortId(m);
            }
            return id;
        }


        private ChatMessageViewModel FirstRealMessage()
        {
            for (var i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg != null && msg.Id != 0) return msg;
            }
            return null;
        }

        private ChatMessageViewModel LastRealMessage()
        {
            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg != null && msg.Id != 0) return msg;
            }
            return null;
        }

        private bool IsAtBottom()
        {
            AttachMessageScrollViewer();
            var sv = _messageScrollViewer;
            if (sv == null) return true;
            return sv.ScrollableHeight - sv.VerticalOffset < 160;
        }

        private bool IsNearTop(double threshold)
        {
            AttachMessageScrollViewer();
            var sv = _messageScrollViewer;
            if (sv == null) return false;
            return sv.VerticalOffset <= threshold;
        }

        private bool IsFirstRealMessageNearTop()
        {
            if (MessageList == null || _messages == null || _messages.Count == 0) return false;
            var first = FirstRealMessage();
            if (first == null) return false;
            var container = MessageList.ContainerFromItem(first) as FrameworkElement;
            if (container == null || container.ActualHeight <= 0) return false;
            try
            {
                var bounds = container.TransformToVisual(MessageList).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                return bounds.Bottom >= -24 && bounds.Top <= 180;
            }
            catch
            {
                return false;
            }
        }

        private bool IsFirstListItemNearTop(double threshold)
        {
            if (MessageList == null || _messages == null || _messages.Count == 0) return false;
            var count = Math.Min(_messages.Count, 8);
            for (var i = 0; i < count; i++)
            {
                var item = _messages[i];
                if (item == null) continue;
                var container = MessageList.ContainerFromItem(item) as FrameworkElement;
                if (container == null || container.ActualHeight <= 0) continue;
                try
                {
                    var bounds = container.TransformToVisual(MessageList).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                    if (bounds.Bottom >= -24 && bounds.Top <= threshold)
                        return true;
                }
                catch
                {
                }
            }
            return false;
        }

        private bool IsTopLoadViewportActive()
        {
            return IsNearTop(180) || IsFirstRealMessageNearTop() || IsFirstListItemNearTop(220);
        }

        private bool IsNearBottom(double threshold)
        {
            AttachMessageScrollViewer();
            var sv = _messageScrollViewer;
            if (sv == null) return true;
            return sv.ScrollableHeight - sv.VerticalOffset < threshold;
        }

        private bool IsMessageListScrollable()
        {
            if (MessageList == null) return false;
            AttachMessageScrollViewer();
            var sv = _messageScrollViewer;
            return sv != null && sv.ScrollableHeight > 8;
        }

        private ScrollViewportAnchor CaptureScrollViewportAnchor()
        {
            if (MessageList == null || _messages == null || _messages.Count == 0) return null;
            AttachMessageScrollViewer();
            if (_messageScrollViewer == null || IsNearBottom(160)) return null;

            var viewportHeight = MessageList.ActualHeight;
            if (viewportHeight <= 0) return null;

            for (var i = 0; i < _messages.Count; i++)
            {
                var item = _messages[i];
                if (!(item is ChatMessageViewModel)) continue;

                var container = MessageList.ContainerFromItem(item) as FrameworkElement;
                if (container == null || container.ActualHeight <= 0) continue;

                try
                {
                    var bounds = container.TransformToVisual(MessageList).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                    if (bounds.Bottom <= 0 || bounds.Top >= viewportHeight) continue;

                    return new ScrollViewportAnchor
                    {
                        Item = item,
                        Top = bounds.Top
                    };
                }
                catch
                {
                }
            }

            return null;
        }

        private void QueueRestoreScrollViewportAnchor(ScrollViewportAnchor anchor)
        {
            if (anchor == null) return;
            ScheduleViewportCorrection(false, anchor);
        }

        private void RestoreScrollViewportAnchor(ScrollViewportAnchor anchor)
        {
            if (anchor == null || anchor.Item == null || MessageList == null) return;
            AttachMessageScrollViewer();
            var sv = _messageScrollViewer;
            if (sv == null) return;

            var container = MessageList.ContainerFromItem(anchor.Item) as FrameworkElement;
            if (container == null || container.ActualHeight <= 0) return;

            try
            {
                TryUpdateMessageListLayout("RestoreScrollViewportAnchor");
                var bounds = container.TransformToVisual(MessageList).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
                var delta = bounds.Top - anchor.Top;
                if (Math.Abs(delta) < 0.5) return;

                _stickToBottom = false;
                IgnoreScrollTrackingBriefly();
                sv.ChangeView(null, sv.VerticalOffset + delta, null, true);
                UpdateScrollDownButton();
            }
            catch
            {
            }
        }

        private void SetMessageMediaDownloadingPreservingViewport(ChatMessageViewModel msg, bool value)
        {
            if (msg == null)
                return;

            var keepBottom = ShouldKeepBottomDuringLayoutChange();
            var anchor = keepBottom ? null : CaptureScrollViewportAnchor();
            msg.IsMediaDownloading = value;
            QueueViewportCorrectionAfterLayout(keepBottom, anchor);
        }

        private void SetMediaItemDownloadingPreservingViewport(ChatMediaItemViewModel item, bool value)
        {
            if (item == null)
                return;

            var keepBottom = ShouldKeepBottomDuringLayoutChange();
            var anchor = keepBottom ? null : CaptureScrollViewportAnchor();
            item.IsMediaDownloading = value;
            QueueViewportCorrectionAfterLayout(keepBottom, anchor);
        }

        private bool ShouldStickToBottom()
        {
            if (_stickToBottom)
            {
                if (IsNearBottom(240)) return true;
                _stickToBottom = false;
                return false;
            }
            if (IsNearBottom(64))
            {
                _stickToBottom = true;
                return true;
            }
            return false;
        }

        private async System.Threading.Tasks.Task PositionInitialMessageListAtBottomAsync()
        {
            if (_messages == null || _messages.Count == 0 || MessageList == null)
            {
                _initialBottomPositionPending = false;
                return;
            }

            _initialBottomPositionPending = true;
            _stickToBottom = true;
            CancelPendingViewportCorrection();
            try
            {
                // On the first visit the history can arrive before the ListView receives its first
                // real measure. ScrollableHeight == 0 at that moment does not mean that the list is
                // already at the bottom; it only means that the containers do not exist yet. Keep
                // the list hidden until the last message has been realized and two consecutive
                // layout passes agree that the bottom is stable.
                var stablePasses = 0;
                var previousScrollableHeight = -1.0;
                for (var i = 0; i < 16; i++)
                {
                    var layoutReady = false;
                    var atBottom = false;
                    var currentScrollableHeight = -1.0;

                    await Dispatcher.RunAsync(i < 2 ? CoreDispatcherPriority.Normal : CoreDispatcherPriority.Low, delegate
                    {
                        if (MessageList == null || _messages == null || _messages.Count == 0) return;

                        var last = LastRealMessage();
                        if (last != null)
                        {
                            IgnoreScrollTrackingBriefly();
                            MessageList.ScrollIntoView(last, ScrollIntoViewAlignment.Default);
                        }

                        TryUpdateMessageListLayout("PositionInitialMessageListAtBottomAsync");
                        AttachMessageScrollViewer();
                        var sv = _messageScrollViewer;
                        if (sv == null || MessageList.ActualHeight <= 0) return;

                        IgnoreScrollTrackingBriefly();
                        sv.ChangeView(null, sv.ScrollableHeight, null, true);
                        currentScrollableHeight = sv.ScrollableHeight;

                        var lastContainer = last == null ? null : MessageList.ContainerFromItem(last) as FrameworkElement;
                        layoutReady = _messageListIsLoaded && lastContainer != null && lastContainer.ActualHeight > 0;
                        atBottom = sv.ScrollableHeight - sv.VerticalOffset <= 1.5;
                    });

                    if (layoutReady && atBottom && previousScrollableHeight >= 0 &&
                        Math.Abs(currentScrollableHeight - previousScrollableHeight) < 0.5)
                    {
                        stablePasses++;
                        if (stablePasses >= 2) break;
                    }
                    else
                    {
                        stablePasses = 0;
                    }

                    previousScrollableHeight = currentScrollableHeight;
                    await System.Threading.Tasks.Task.Delay(i < 2 ? 25 : 55);
                }

                // Final dispatcher pass catches the container realization caused by ScrollIntoView.
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
                {
                    _stickToBottom = true;
                    PinMessageListToBottomOnce();
                });
            }
            finally
            {
                _initialBottomPositionPending = false;
                _stickToBottom = true;
                UpdateScrollDownButton();
            }
        }

        private bool ShouldKeepBottomDuringLayoutChange()
        {
            // _stickToBottom represents user intent. Do not clear it merely because an image
            // temporarily increased ScrollableHeight before the correction was applied.
            return _initialBottomPositionPending || _stickToBottom;
        }

        private void QueueViewportCorrectionAfterLayout(bool keepBottom, ScrollViewportAnchor anchor)
        {
            ScheduleViewportCorrection(keepBottom, anchor);
        }

        private void ScheduleViewportCorrection(bool keepBottom, ScrollViewportAnchor anchor)
        {
            if (!_initialBottomPositionPending && DateTime.UtcNow.Ticks < _suppressViewportCorrectionsUntilTicks)
                return;

            if (keepBottom)
            {
                _pendingViewportKeepBottom = true;
                _pendingViewportAnchor = null;
            }
            else if (!_pendingViewportKeepBottom && _pendingViewportAnchor == null && anchor != null)
            {
                // Keep the first anchor for the whole batch. Every later photo is measured from
                // the same stable viewport instead of creating corrections that fight each other.
                _pendingViewportAnchor = anchor;
            }

            _viewportCorrectionVersion++;
            if (_viewportCorrectionQueued) return;

            _viewportCorrectionQueued = true;
            var ignored = RunViewportCorrectionAsync();
        }

        private async System.Threading.Tasks.Task RunViewportCorrectionAsync()
        {
            try
            {
                while (true)
                {
                    var version = _viewportCorrectionVersion;
                    await System.Threading.Tasks.Task.Delay(85);
                    if (version != _viewportCorrectionVersion)
                        continue;

                    var keepBottom = _pendingViewportKeepBottom;
                    var anchor = _pendingViewportAnchor;
                    _pendingViewportKeepBottom = false;
                    _pendingViewportAnchor = null;

                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
                    {
                        if (version != _viewportCorrectionVersion) return;

                        if (keepBottom)
                        {
                            // Do not drag the user back after an explicit user scroll. Layout
                            // growth alone must not cancel bottom affinity.
                            if (!_initialBottomPositionPending && !_stickToBottom)
                                return;

                            _stickToBottom = true;
                            PinMessageListToBottomOnce();
                        }
                        else
                        {
                            RestoreScrollViewportAnchor(anchor);
                        }
                    });

                    // One extra layout pass is enough for a newly decoded image. Unlike the old
                    // 0/80/220/520/900 ms burst, this pass is cancelled as soon as a newer layout
                    // change or a user scroll occurs.
                    if (keepBottom && version == _viewportCorrectionVersion)
                    {
                        await System.Threading.Tasks.Task.Delay(55);
                        if (version == _viewportCorrectionVersion)
                        {
                            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
                            {
                                if (version != _viewportCorrectionVersion) return;
                                if (_initialBottomPositionPending || _stickToBottom)
                                    PinMessageListToBottomOnce();
                            });
                        }
                    }

                    if (version == _viewportCorrectionVersion)
                        break;
                }
            }
            finally
            {
                _viewportCorrectionQueued = false;
                if (_pendingViewportKeepBottom || _pendingViewportAnchor != null)
                {
                    _viewportCorrectionQueued = true;
                    var ignored = RunViewportCorrectionAsync();
                }
            }
        }

        private void CancelPendingViewportCorrection()
        {
            _viewportCorrectionVersion++;
            _pendingViewportKeepBottom = false;
            _pendingViewportAnchor = null;
        }

        private void PinMessageListToBottomOnce()
        {
            if (_messages == null || _messages.Count == 0 || MessageList == null) return;

            TryUpdateMessageListLayout("PinMessageListToBottomOnce");
            AttachMessageScrollViewer();
            var sv = _messageScrollViewer;
            if (sv != null)
            {
                IgnoreScrollTrackingBriefly();
                sv.ChangeView(null, sv.ScrollableHeight, null, true);
                return;
            }

            var last = LastRealMessage();
            if (last != null)
            {
                IgnoreScrollTrackingBriefly();
                MessageList.ScrollIntoView(last, ScrollIntoViewAlignment.Default);
            }
        }

        private void MessageList_UserInteractionStarted(object sender, PointerRoutedEventArgs e)
        {
            if (_initialBottomPositionPending) return;
            _suppressViewportCorrectionsUntilTicks = DateTime.UtcNow.AddMilliseconds(1200).Ticks;
            CancelPendingViewportCorrection();
        }

        private void IgnoreScrollTrackingBriefly()
        {
            _ignoreScrollTrackingUntilTicks = DateTime.UtcNow.AddMilliseconds(450).Ticks;
        }

        private bool ShouldTrackScrollChange(ScrollViewerViewChangedEventArgs e)
        {
            if (DateTime.UtcNow.Ticks < _ignoreScrollTrackingUntilTicks) return false;
            return true;
        }

        private void RevealMessageList()
        {
            if (_initialMessageListRevealed || MessageList == null) return;
            _initialMessageListRevealed = true;
            MessageList.Opacity = 1;
            if (_stickToBottom) QueueBottomPinBurst();
        }

        private void KeepBottomIfStillRequested()
        {
            if (!ShouldStickToBottom()) return;
            ScrollToBottom(false);
        }

        private void QueueBottomPinBurst()
        {
            if (!_initialBottomPositionPending && !_stickToBottom) return;
            _stickToBottom = true;
            ScheduleViewportCorrection(true, null);
        }

        private void MessageList_Loaded(object sender, RoutedEventArgs e)
        {
            _messageListIsLoaded = true;
            UpdateMessageListChromePadding();
            AttachMessageScrollViewer();
            UpdateScrollDownButton();
            if (!_initialBottomPositionPending)
                CheckTopLoadTrigger();
            BeginAutoDownloadMedia();
            if (_stickToBottom) QueueBottomPinBurst();
        }

        private void MessageList_LayoutUpdated(object sender, object e)
        {
            if (_initialBottomPositionPending) return;
            QueueTopLoadLayoutCheck();
        }

        private void TopLoadWatchTimer_Tick(object sender, object e)
        {
            CheckTopLoadTrigger();
        }

        private void QueueTopLoadLayoutCheck()
        {
            if (_topLoadLayoutCheckQueued) return;
            _topLoadLayoutCheckQueued = true;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
            {
                _topLoadLayoutCheckQueued = false;
                CheckTopLoadTrigger();
            });
        }

        private void MessageMarkdownText_Loaded(object sender, RoutedEventArgs e)
        {
            var textBlock = sender as TextBlock;
            if (textBlock == null) return;
            if (textBlock.Inlines.Count > 0) return;
            ApplyMarkdownText(textBlock, ResolveMarkdownTextBlockMessage(textBlock, null));
        }

        private void MessageMarkdownText_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var textBlock = sender as TextBlock;
            if (textBlock == null) return;

            var msg = args == null ? null : args.NewValue as ChatMessageViewModel;
            if (msg == null) msg = ResolveMarkdownTextBlockMessage(textBlock, null);

            // Built synchronously, on purpose.
            //
            // Deferring this to a low-priority dispatcher callback starves it for as long as the
            // user keeps scrolling, so every recycled row arrives with an empty bubble. An empty
            // bubble also measures to almost nothing, which collapses the panel's extent estimate
            // and makes the scroll position lurch once the text finally lands - the blank stretch
            // followed by a jump. The build is cheap now (plain-text fast path plus the emoji
            // pre-filter), so there is nothing left worth deferring.
            ApplyMarkdownText(textBlock, msg);
        }

        private void LocalEmojiTextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyLocalEmojiTextBlock(sender as TextBlock);
        }

        private void LocalEmojiTextBlock_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var textBlock = sender as TextBlock;
            if (textBlock == null) return;
            textBlock.Text = string.Empty;
            textBlock.Inlines.Clear();

            // These blocks live in the reply-preview and structured-media areas, which are
            // collapsed for most messages, so reading the source text first avoids doing any
            // work at all for them. When there is text it is applied straight away rather than
            // queued, for the same reason as the message body above.
            if (!HasVisibleText(GetLocalEmojiTextBlockSource(textBlock))) return;

            ApplyLocalEmojiTextBlock(textBlock);
        }

        private static string GetLocalEmojiTextBlockSource(TextBlock textBlock)
        {
            var msg = textBlock == null ? null : textBlock.DataContext as ChatMessageViewModel;
            if (msg == null) return string.Empty;
            if (string.Equals(textBlock.Name, "ReplyPreviewText", StringComparison.Ordinal))
                return msg.ReplyToTextDisplay;
            if (string.Equals(textBlock.Name, "StructuredMediaTextBlock", StringComparison.Ordinal))
                return msg.StructuredMediaText;
            return string.Empty;
        }

        private void ApplyLocalEmojiTextBlock(TextBlock textBlock)
        {
            if (textBlock == null) return;
            var text = GetLocalEmojiTextBlockSource(textBlock);

            textBlock.Text = string.Empty;
            textBlock.Inlines.Clear();
            text = SanitizeRichTextRunText(text);
            if (HasVisibleText(text))
                AddMarkdownRun(textBlock.Inlines, text, false, false, false);
        }

        private ChatMessageViewModel ResolveMarkdownTextBlockMessage(TextBlock textBlock, ChatMessageViewModel requested)
        {
            if (textBlock == null) return requested;

            var dataContext = textBlock.DataContext as ChatMessageViewModel;
            var tagged = textBlock.Tag as ChatMessageViewModel;

            if (requested == null)
                return dataContext ?? tagged;

            if (dataContext != null && !object.ReferenceEquals(dataContext, requested))
                return dataContext;

            if (tagged != null && !object.ReferenceEquals(tagged, requested))
                return tagged;

            return requested;
        }

        private void ApplyMarkdownText(TextBlock textBlock, ChatMessageViewModel msg)
        {
            if (textBlock == null) return;
            msg = ResolveMarkdownTextBlockMessage(textBlock, msg);

            var text = msg == null ? null : msg.VisibleText;
            text = SanitizeRichTextRunText(text);
            ApplyPlainMarkdownText(textBlock, text);
            if (!HasVisibleText(text)) return;

            // Most messages carry no entities and no markdown. Re-parsing them only to arrive at
            // the same single run costs a full scan plus throwaway inlines on every row that
            // scrolls into view.
            if ((msg == null || !msg.HasTextEntities) && IsPlainMarkdownText(text)) return;

            try
            {
                if (msg != null && msg.HasTextEntities)
                {
                    textBlock.Text = string.Empty;
                    textBlock.Inlines.Clear();
                    AddTdLibTextEntityInlines(textBlock.Inlines, text, msg.TextEntities);
                    if (HasVisibleInlineText(textBlock.Inlines))
                        return;
                    textBlock.Inlines.Clear();
                }

                Dictionary<string, string> footnotes;
                List<string> footnoteOrder;
                var body = ExtractMarkdownFootnotes(text, out footnotes, out footnoteOrder);

                textBlock.Text = string.Empty;
                textBlock.Inlines.Clear();
                AddMarkdownInlines(textBlock.Inlines, body, footnotes);
                if (!HasVisibleInlineText(textBlock.Inlines) && HasVisibleText(body))
                {
                    textBlock.Inlines.Clear();
                    AddMarkdownRun(textBlock.Inlines, body, false, false, false);
                }

                if (footnoteOrder.Count > 0)
                {
                    if (textBlock.Inlines.Count > 0)
                        AddMarkdownRun(textBlock.Inlines, "\n", false, false, false);

                    for (var i = 0; i < footnoteOrder.Count; i++)
                    {
                        var id = footnoteOrder[i];
                        string value;
                        if (!footnotes.TryGetValue(id, out value)) continue;

                        AddFootnoteReferenceRun(textBlock.Inlines, id);
                        AddMarkdownRun(textBlock.Inlines, " ", false, false, false);
                        var before = textBlock.Inlines.Count;
                        AddMarkdownInlines(textBlock.Inlines, value, footnotes);
                        if (textBlock.Inlines.Count == before && HasVisibleText(value))
                            AddMarkdownRun(textBlock.Inlines, value, false, false, false);
                        if (i + 1 < footnoteOrder.Count)
                            AddMarkdownRun(textBlock.Inlines, "\n", false, false, false);
                    }
                }

                if (!HasVisibleInlineText(textBlock.Inlines))
                {
                    DebugMarkdownFallback("markdown-empty", msg, null);
                    ApplyPlainMarkdownText(textBlock, text);
                }
            }
            catch (Exception ex)
            {
                DebugMarkdownFallback("markdown-exception", msg, ex);
                ApplyPlainMarkdownText(textBlock, text);
            }
        }

        private void AddTdLibTextEntityInlines(InlineCollection inlines, string text, IList<MessageTextEntityViewModel> entities)
        {
            if (inlines == null || string.IsNullOrEmpty(text)) return;
            if (entities == null || entities.Count == 0)
            {
                AddMarkdownRun(inlines, text, false, false, false);
                return;
            }

            var sorted = new List<MessageTextEntityViewModel>();
            for (var i = 0; i < entities.Count; i++)
            {
                var entity = entities[i];
                if (entity == null || entity.Length <= 0) continue;
                if (entity.Offset < 0 || entity.Offset >= text.Length) continue;
                sorted.Add(entity);
            }

            sorted.Sort(delegate(MessageTextEntityViewModel a, MessageTextEntityViewModel b)
            {
                var compare = a.Offset.CompareTo(b.Offset);
                return compare != 0 ? compare : b.Length.CompareTo(a.Length);
            });

            var index = 0;
            for (var i = 0; i < sorted.Count; i++)
            {
                var entity = sorted[i];
                var start = Math.Max(0, entity.Offset);
                var end = Math.Min(text.Length, start + entity.Length);
                if (end <= start || start < index) continue;

                if (start > index)
                    AddMarkdownRun(inlines, text.Substring(index, start - index), false, false, false);

                AddTdLibEntityInline(inlines, text.Substring(start, end - start), entity);
                index = end;
            }

            if (index < text.Length)
                AddMarkdownRun(inlines, text.Substring(index), false, false, false);
        }

        private void AddTdLibEntityInline(InlineCollection inlines, string value, MessageTextEntityViewModel entity)
        {
            value = SanitizeRichTextRunText(value);
            if (string.IsNullOrEmpty(value)) return;

            var url = GetTdLibEntityUrl(value, entity);
            if (!string.IsNullOrEmpty(url))
            {
                var link = new Hyperlink();
                ConfigureHyperlink(link, url);
                AddTdLibEntityContentInlines(link.Inlines, value, entity);
                inlines.Add(link);
                return;
            }

            AddTdLibEntityContentInlines(inlines, value, entity);
        }

        private void AddTdLibEntityContentInlines(InlineCollection inlines, string value, MessageTextEntityViewModel entity)
        {
            if (inlines == null || string.IsNullOrEmpty(value)) return;
            var type = entity == null ? string.Empty : (entity.Type ?? string.Empty);
            var bold = type == "textEntityTypeBold";
            var italic = type == "textEntityTypeItalic";
            var code = type == "textEntityTypeCode" || type == "textEntityTypePre" || type == "textEntityTypePreCode";

            if (!code && TryAddLocalEmojiRuns(inlines, value, bold, italic))
                return;

            inlines.Add(CreateTdLibEntityRun(value, entity));
        }

        private Run CreateTdLibEntityRun(string value, MessageTextEntityViewModel entity)
        {
            var run = new Run { Text = value };
            var type = entity == null ? string.Empty : (entity.Type ?? string.Empty);
            if (type == "textEntityTypeBold")
                run.FontWeight = FontWeights.SemiBold;
            else if (type == "textEntityTypeItalic")
                run.FontStyle = FontStyle.Italic;
            else if (type == "textEntityTypeCode" || type == "textEntityTypePre" || type == "textEntityTypePreCode")
                run.FontFamily = new FontFamily("Consolas");
            return run;
        }

        private string GetTdLibEntityUrl(string value, MessageTextEntityViewModel entity)
        {
            if (entity == null) return "";
            var type = entity.Type ?? string.Empty;
            if (type == "textEntityTypeTextUrl")
                return entity.Url ?? "";
            if (type == "textEntityTypeUrl")
                return value;
            if (type == "textEntityTypeMention" && !string.IsNullOrWhiteSpace(value) && value[0] == '@')
                return "https://t.me/" + value.Substring(1);
            if (type == "textEntityTypeEmailAddress")
                return "mailto:" + value;
            if (type == "textEntityTypePhoneNumber")
                return "tel:" + value;
            return "";
        }

        private void ApplyPlainMarkdownText(TextBlock textBlock, string text)
        {
            if (textBlock == null) return;
            textBlock.Text = string.Empty;
            textBlock.Inlines.Clear();
            text = SanitizeRichTextRunText(text);
            if (HasVisibleText(text))
                AddMarkdownRun(textBlock.Inlines, text, false, false, false);
        }

        private bool HasVisibleInlineText(InlineCollection inlines)
        {
            if (inlines == null || inlines.Count == 0) return false;
            var builder = new System.Text.StringBuilder();
            AppendInlineText(builder, inlines);
            return HasVisibleText(builder.ToString());
        }

        private void AppendInlineText(System.Text.StringBuilder builder, InlineCollection inlines)
        {
            if (builder == null || inlines == null) return;

            foreach (var inline in inlines)
            {
                var run = inline as Run;
                if (run != null)
                {
                    builder.Append(run.Text);
                    continue;
                }

                var span = inline as Span;
                if (span != null)
                {
                    AppendInlineText(builder, span.Inlines);
                    continue;
                }

                var hyperlink = inline as Hyperlink;
                if (hyperlink != null)
                {
                    AppendInlineText(builder, hyperlink.Inlines);
                    continue;
                }

                var inlineContainer = inline as InlineUIContainer;
                var child = inlineContainer == null ? null : inlineContainer.Child as FrameworkElement;
                var emoji = child == null ? null : child.Tag as string;
                if (!string.IsNullOrEmpty(emoji))
                    builder.Append(emoji);
            }
        }

        private void DebugMarkdownFallback(string stage, ChatMessageViewModel msg, Exception ex)
        {
            try
            {
                Debug.WriteLine(
                    "TG_EMPTY_MESSAGE " + stage +
                    " chat=" + DebugSafe(_chat == null ? null : _chat.PeerKey) +
                    " chatTitle=" + DebugSafe(_chat == null ? null : _chat.Title) +
                    " id=" + (msg == null ? 0 : msg.Id).ToString() +
                    " hasMedia=" + (msg != null && msg.HasMedia).ToString() +
                    " mediaKind=" + DebugSafe(msg == null ? null : msg.MediaKind) +
                    " mediaTitle=" + DebugSafe(msg == null ? null : msg.MediaTitle) +
                    " textLen=" + DebugTextLength(msg == null ? null : msg.Text).ToString() +
                    " textPreview=" + DebugPreview(msg == null ? null : msg.Text) +
                    " error=" + (ex == null ? "-" : ex.GetType().Name) +
                    " errorMessage=" + DebugSafe(ex == null ? null : ex.Message));
            }
            catch
            {
            }
        }

        private string ExtractMarkdownFootnotes(string text, out Dictionary<string, string> footnotes, out List<string> order)
        {
            footnotes = new Dictionary<string, string>();
            order = new List<string>();
            if (string.IsNullOrEmpty(text)) return text;

            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');
            var body = new List<string>();

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("[^", StringComparison.Ordinal))
                {
                    var close = trimmed.IndexOf("]:", StringComparison.Ordinal);
                    if (close > 2)
                    {
                        var id = trimmed.Substring(2, close - 2).Trim();
                        var value = trimmed.Substring(close + 2).TrimStart();
                        if (!string.IsNullOrEmpty(id))
                        {
                            if (!footnotes.ContainsKey(id))
                            {
                                footnotes[id] = value;
                                order.Add(id);
                            }
                            else if (!string.IsNullOrEmpty(value))
                            {
                                footnotes[id] = footnotes[id] + " " + value;
                            }
                            continue;
                        }
                    }
                }

                body.Add(line);
            }

            return string.Join("\n", body).TrimEnd('\n');
        }

        private void AddMarkdownInlines(InlineCollection inlines, string text, Dictionary<string, string> footnotes)
        {
            if (inlines == null || string.IsNullOrEmpty(text)) return;

            var index = 0;
            while (index < text.Length)
            {
                if (TryAddBlockquoteLine(inlines, text, ref index)) continue;
                if (TryAddFootnoteReference(inlines, text, ref index, footnotes)) continue;
                if (TryAddMarkdownLink(inlines, text, ref index)) continue;
                if (TryAddPlainUrl(inlines, text, ref index)) continue;
                if (TryAddTelegramMention(inlines, text, ref index)) continue;
                if (TryAddDelimitedRun(inlines, text, ref index, "`", false, false, true)) continue;
                if (TryAddDelimitedRun(inlines, text, ref index, "**", true, false, false)) continue;
                if (TryAddDelimitedRun(inlines, text, ref index, "__", true, false, false)) continue;
                if (TryAddDelimitedRun(inlines, text, ref index, "*", false, true, false)) continue;
                if (TryAddDelimitedRun(inlines, text, ref index, "_", false, true, false)) continue;

                var next = FindNextMarkdownToken(text, index + 1);
                if (next <= index) next = index + 1;
                AddMarkdownRun(inlines, text.Substring(index, next - index), false, false, false);
                index = next;
            }
        }

        private bool TryAddBlockquoteLine(InlineCollection inlines, string text, ref int index)
        {
            if (index < 0 || index >= text.Length) return false;
            if (index + 1 >= text.Length || text[index] != '>' || text[index + 1] != ' ') return false;

            var start = index + 2;
            var end = text.IndexOf('\n', start);
            var hasNewLine = end >= 0;
            if (!hasNewLine) end = text.Length;

            var marker = new Run { Text = "\u2502 " };
            marker.FontWeight = FontWeights.SemiBold;
            if (index > 0 && text[index - 1] != '\n') AddMarkdownRun(inlines, "\n", false, false, false);
            inlines.Add(marker);
            AddMarkdownRun(inlines, text.Substring(start, end - start), false, true, false);
            if (hasNewLine) AddMarkdownRun(inlines, "\n", false, false, false);

            index = hasNewLine ? end + 1 : end;
            return true;
        }

        private bool TryAddFootnoteReference(InlineCollection inlines, string text, ref int index, Dictionary<string, string> footnotes)
        {
            if (index + 3 > text.Length) return false;
            if (!string.Equals(text.Substring(index, 2), "[^", StringComparison.Ordinal)) return false;

            var close = text.IndexOf(']', index + 2);
            if (close <= index + 2) return false;

            var id = text.Substring(index + 2, close - index - 2).Trim();
            if (string.IsNullOrEmpty(id)) return false;

            AddFootnoteReferenceRun(inlines, id);
            index = close + 1;
            return true;
        }

        private bool TryAddMarkdownLink(InlineCollection inlines, string text, ref int index)
        {
            if (index >= text.Length || text[index] != '[') return false;

            var closeLabel = text.IndexOf("](", index, StringComparison.Ordinal);
            if (closeLabel <= index) return false;
            var closeUrl = text.IndexOf(')', closeLabel + 2);
            if (closeUrl <= closeLabel + 2) return false;

            var label = SanitizeRichTextRunText(text.Substring(index + 1, closeLabel - index - 1));
            var url = text.Substring(closeLabel + 2, closeUrl - closeLabel - 2);
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                return false;

            if (!HasVisibleText(label))
                label = url;

            var link = new Hyperlink();
            ConfigureHyperlink(link, url);
            AddMarkdownRun(link.Inlines, label, false, false, false);
            inlines.Add(link);
            index = closeUrl + 1;
            return true;
        }

        private bool TryAddPlainUrl(InlineCollection inlines, string text, ref int index)
        {
            if (index < 0 || index >= text.Length) return false;
            if (!StartsWithUrl(text, index)) return false;

            var end = index;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '<' && text[end] != '>')
                end++;

            var raw = text.Substring(index, end - index);
            var label = raw.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
            if (label.Length == 0) return false;

            Uri uri;
            if (!Uri.TryCreate(label, UriKind.Absolute, out uri)) return false;

            var link = new Hyperlink();
            ConfigureHyperlink(link, label);
            AddMarkdownRun(link.Inlines, label, false, false, false);
            inlines.Add(link);

            var trailing = raw.Substring(label.Length);
            if (!string.IsNullOrEmpty(trailing))
                AddMarkdownRun(inlines, trailing, false, false, false);

            index = end;
            return true;
        }

        private bool StartsWithUrl(string text, int index)
        {
            if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length) return false;
            return StartsWithOrdinalIgnoreCase(text, index, "https://") ||
                   StartsWithOrdinalIgnoreCase(text, index, "http://") ||
                   StartsWithOrdinalIgnoreCase(text, index, "tg://");
        }

        private bool StartsWithOrdinalIgnoreCase(string text, int index, string value)
        {
            if (index + value.Length > text.Length) return false;
            return string.Compare(text, index, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        private void ConfigureHyperlink(Hyperlink link, string url)
        {
            if (link == null) return;
            if (IsTelegramLink(url))
            {
                link.Click += async delegate(Hyperlink sender, HyperlinkClickEventArgs args)
                {
                    await OpenTelegramLinkAsync(url);
                };
                return;
            }

            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri))
                link.NavigateUri = uri;
        }

        private bool IsTelegramHttpLink(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            var host = (uri.Host ?? string.Empty).ToLowerInvariant();
            if (host == "www.t.me") host = "t.me";
            return host == "t.me" || host == "telegram.me";
        }

        private bool IsTelegramLink(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            if (string.Equals(uri.Scheme, "tg", StringComparison.OrdinalIgnoreCase)) return true;
            return IsTelegramHttpLink(url);
        }

        private async System.Threading.Tasks.Task OpenTelegramLinkAsync(string url)
        {
            try
            {
                ProxySettings proxyConfig;
                if (ProxySettings.TryParseProxyLink(url, out proxyConfig))
                {
                    await ShowProxySettingsDialogAsync(proxyConfig);
                    return;
                }

                var target = await TelegramService.Instance.ResolveTelegramLinkAsync(url);
                if (target == null || target.Chat == null)
                {
                    await ShowChatAlertAsync("Link error", "Link is not available.");
                    return;
                }

                if (target.MessageId > 0)
                {
                    var navigationTarget = new ChatNavigationTarget { Chat = target.Chat, MessageId = target.MessageId };
                    if (!AdaptiveShellNavigationService.NavigateChat(navigationTarget))
                        Frame.Navigate(typeof(ChatPage), navigationTarget);
                }
                else
                {
                    if (!AdaptiveShellNavigationService.NavigateChat(target.Chat))
                        Frame.Navigate(typeof(ChatPage), target.Chat);
                }
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Link error", AlertErrorMessage(ex, "Could not open this link."));
            }
        }

        private async System.Threading.Tasks.Task ShowProxySettingsDialogAsync(ProxySettings proxyConfig)
        {
            if (proxyConfig == null) return;

            var statusButton = new Button
            {
                Content = "Check",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0),
                MinWidth = 0
            };

            var table = BuildProxyTable(proxyConfig, statusButton);
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "You can change this proxy later in Settings.",
                TextWrapping = TextWrapping.WrapWholeWords,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16),
                FontSize = 15
            });
            panel.Children.Add(new TextBlock
            {
                Text = "This proxy server may show a sponsor channel in your chat list. It does not reveal your traffic.",
                TextWrapping = TextWrapping.WrapWholeWords,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 18),
                FontSize = 15
            });
            panel.Children.Add(table);

            var dialog = new ContentDialog
            {
                Title = "Proxy Settings",
                Content = panel,
                PrimaryButtonText = "Connect",
                SecondaryButtonText = "Cancel",
                FullSizeDesired = false
            };

            statusButton.Click += async delegate
            {
                try
                {
                    statusButton.IsEnabled = false;
                    statusButton.Content = "Checking...";
                    await System.Threading.Tasks.Task.Delay(1);
                    statusButton.Content = "Ready";
                }
                catch (Exception ex)
                {
                    statusButton.Content = "Error: " + ex.Message;
                }
                finally
                {
                    statusButton.IsEnabled = true;
                }
            };

            var proxyConnected = false;
            dialog.PrimaryButtonClick += async delegate(ContentDialog sender, ContentDialogButtonClickEventArgs args)
            {
                var deferral = args.GetDeferral();
                try
                {
                    TelegramService.Instance.ApplyProxySettings(proxyConfig);
                    await TelegramService.Instance.ApplyConnectionSettingsAsync();
                    proxyConnected = true;
                }
                catch (Exception ex)
                {
                    args.Cancel = true;
                    statusButton.Content = "Connect error: " + ex.Message;
                }
                finally
                {
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
            if (proxyConnected)
                await ShowChatAlertAsync("Proxy connected", "Proxy connected.");
        }

        private Grid BuildProxyTable(ProxySettings proxyConfig, Button statusButton)
        {
            var grid = new Grid
            {
                Margin = new Thickness(0, 0, 0, 4)
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(94) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            AddProxyTableRow(grid, 0, "Server", proxyConfig.Server);
            AddProxyTableRow(grid, 1, "Port", proxyConfig.Port);
            AddProxyTableRow(grid, 2, "Key", proxyConfig.Secret);
            AddProxyTableRow(grid, 3, "Status", statusButton);
            return grid;
        }

        private void AddProxyTableRow(Grid grid, int row, string label, string value)
        {
            AddProxyTableRow(grid, row, label, new TextBlock
            {
                Text = value ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 15
            });
        }

        private void AddProxyTableRow(Grid grid, int row, string label, FrameworkElement valueElement)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var left = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 68, 68, 68)),
                BorderThickness = new Thickness(0, 0, 1, row == 3 ? 0 : 1),
                Padding = new Thickness(12, 12, 10, 12),
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 160, 160, 160)),
                    FontSize = 15
                }
            };
            Grid.SetRow(left, row);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var right = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 68, 68, 68)),
                BorderThickness = new Thickness(0, 0, 0, row == 3 ? 0 : 1),
                Padding = new Thickness(14, 12, 12, 12),
                Child = valueElement
            };
            Grid.SetRow(right, row);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }

        private bool TryAddTelegramMention(InlineCollection inlines, string text, ref int index)
        {
            if (index < 0 || index >= text.Length || text[index] != '@') return false;
            if (index > 0 && IsTelegramUsernameChar(text[index - 1])) return false;

            var end = index + 1;
            while (end < text.Length && IsTelegramUsernameChar(text[end]))
                end++;

            var username = text.Substring(index + 1, end - index - 1);
            if (!IsValidTelegramUsername(username)) return false;

            var link = new Hyperlink();
            ConfigureHyperlink(link, "https://t.me/@" + username);
            AddMarkdownRun(link.Inlines, "@" + username, false, false, false);
            inlines.Add(link);
            index = end;
            return true;
        }

        private bool IsTelegramUsernameChar(char c)
        {
            return (c >= 'a' && c <= 'z') ||
                   (c >= 'A' && c <= 'Z') ||
                   (c >= '0' && c <= '9') ||
                   c == '_';
        }

        private bool IsValidTelegramUsername(string username)
        {
            if (string.IsNullOrEmpty(username) || username.Length < 5 || username.Length > 32) return false;
            for (var i = 0; i < username.Length; i++)
                if (!IsTelegramUsernameChar(username[i])) return false;
            return true;
        }

        private bool TryAddDelimitedRun(InlineCollection inlines, string text, ref int index, string marker, bool bold, bool italic, bool code)
        {
            if (string.IsNullOrEmpty(marker) || index + marker.Length > text.Length) return false;
            if (!string.Equals(text.Substring(index, marker.Length), marker, StringComparison.Ordinal)) return false;

            var start = index + marker.Length;
            var end = text.IndexOf(marker, start, StringComparison.Ordinal);
            if (end <= start) return false;

            AddMarkdownRun(inlines, text.Substring(start, end - start), bold, italic, code);
            index = end + marker.Length;
            return true;
        }

        private static readonly string[] MarkdownTokens =
            { "\n> ", "[^", "[", "https://", "http://", "@", "`", "**", "__", "*", "_" };

        private int FindNextMarkdownToken(string text, int start)
        {
            var next = text.Length;
            for (var i = 0; i < MarkdownTokens.Length; i++)
            {
                var token = MarkdownTokens[i];
                var comparison = token.IndexOf("http", StringComparison.Ordinal) == 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var found = text.IndexOf(token, start, comparison);
                if (found >= 0 && found < next) next = found;
            }
            return next;
        }

        /// <summary>
        /// True when the text contains nothing the markdown pass could change, so the plain run
        /// that has already been applied is the final result.
        /// </summary>
        private static bool IsPlainMarkdownText(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            for (var i = 0; i < MarkdownTokens.Length; i++)
            {
                var token = MarkdownTokens[i];
                var comparison = token.IndexOf("http", StringComparison.Ordinal) == 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (text.IndexOf(token, comparison) >= 0) return false;
            }
            return true;
        }

        private void AddMarkdownRun(InlineCollection inlines, string text, bool bold, bool italic, bool code)
        {
            text = SanitizeRichTextRunText(text);
            if (string.IsNullOrEmpty(text)) return;

            if (!code && TryAddLocalEmojiRuns(inlines, text, bold, italic))
                return;

            AddStyledRun(inlines, text, bold, italic, code);
        }

        private bool TryAddLocalEmojiRuns(InlineCollection inlines, string text, bool bold, bool italic)
        {
            if (inlines == null || string.IsNullOrEmpty(text)) return false;
            // One allocation-free pass rejects the overwhelming majority of message texts before
            // the resolver (which builds candidate lists and hex keys per offset) is ever entered.
            if (!ContainsLocalEmojiCandidate(text)) return false;

            var usedEmoji = false;
            var segmentStart = 0;
            var index = 0;
            while (index < text.Length)
            {
                if (!CanStartLocalEmoji(text[index]))
                {
                    index++;
                    continue;
                }

                string emoji;
                string key;
                int length;
                if (!TryReadLocalEmoji(text, index, out emoji, out key, out length))
                {
                    index++;
                    continue;
                }

                if (index > segmentStart)
                    AddStyledRun(inlines, text.Substring(segmentStart, index - segmentStart), bold, italic, false);

                AddLocalEmojiInline(inlines, emoji, key);
                index += length;
                segmentStart = index;
                usedEmoji = true;
            }

            if (!usedEmoji) return false;
            if (segmentStart < text.Length)
                AddStyledRun(inlines, text.Substring(segmentStart), bold, italic, false);
            return true;
        }

        private bool TryReadLocalEmoji(string text, int index, out string emoji, out string key, out int length)
        {
            emoji = null;
            key = null;
            length = 0;
            if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length) return false;
            if (!CanStartLocalEmoji(text[index])) return false;

            var maxLength = Math.Min(MaxLocalEmojiTextLength, text.Length - index);
            for (var candidateLength = maxLength; candidateLength > 0; candidateLength--)
            {
                var candidate = text.Substring(index, candidateLength);
                var candidateKey = ResolveLocalEmojiAssetKey(candidate);
                if (string.IsNullOrEmpty(candidateKey)) continue;

                emoji = candidate;
                key = candidateKey;
                length = candidateLength;
                return true;
            }

            emoji = null;
            key = null;
            length = 0;
            return false;
        }

        private void AddLocalEmojiInline(InlineCollection inlines, string emoji, string key)
        {
            if (inlines == null || string.IsNullOrEmpty(key)) return;
            try
            {
                var image = new Image
                {
                    Width = 20,
                    Height = 20,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(1, 0, 1, -3),
                    Tag = emoji,
                    Source = new BitmapImage(new Uri("ms-appx:///Assets/Emoji/Static/" + key + ".png"))
                };
                inlines.Add(new InlineUIContainer { Child = image });
            }
            catch
            {
                AddStyledRun(inlines, emoji, false, false, false);
            }
        }

        private void AddStyledRun(InlineCollection inlines, string text, bool bold, bool italic, bool code)
        {
            if (inlines == null || string.IsNullOrEmpty(text)) return;
            var run = new Run { Text = text };
            if (bold) run.FontWeight = FontWeights.SemiBold;
            if (italic) run.FontStyle = FontStyle.Italic;
            if (code) run.FontFamily = new FontFamily("Consolas");
            inlines.Add(run);
        }

        private string SanitizeRichTextRunText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var chars = text.ToCharArray();
            var changed = false;
            for (var i = 0; i < chars.Length; i++)
            {
                var ch = chars[i];
                if (ch == '\ufffc')
                {
                    chars[i] = '\u2726';
                    changed = true;
                    continue;
                }

                if (char.IsHighSurrogate(ch))
                {
                    if (i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
                    {
                        i++;
                        continue;
                    }

                    chars[i] = '\u25a1';
                    changed = true;
                    continue;
                }

                if (char.IsLowSurrogate(ch))
                {
                    chars[i] = '\u25a1';
                    changed = true;
                }
            }

            return changed ? new string(chars) : text;
        }

        private void AddFootnoteReferenceRun(InlineCollection inlines, string id)
        {
            if (inlines == null || string.IsNullOrEmpty(id)) return;

            var run = new Run { Text = "[" + id + "]" };
            run.FontSize = 12;
            run.FontWeight = FontWeights.SemiBold;
            inlines.Add(run);
        }

        // Do NOT introduce phased rendering here by setting args.Handled in phase 0. Handled
        // suppresses ListViewBase's own processing for the phase, which is what applies the item
        // to the container - the row then renders with no DataContext, every {Binding ...Visibility}
        // falls back to Visible and the message shows its poll, audio, music and media blocks all
        // at once. The per-row cost is kept down by the plain-text fast path in ApplyMarkdownText
        // and the emoji pre-filter instead.
        private void MessageList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args == null) return;
            if (args.InRecycleQueue)
            {
                // The row left the viewport: hand its container back to the pool for its template.
                ReturnMessageContainerToPool(args.ItemContainer);
                return;
            }

            // Message text is built by MessageMarkdownText_DataContextChanged, which fires both on
            // first realization and on every recycle, and does it synchronously. Doing it here as
            // well would only duplicate the work: this event runs before the container's
            // DataContext is swapped, so on a recycled row it would build the previous message.
            var msg = args.Item as ChatMessageViewModel;

            if (args.ItemIndex >= 0 && args.ItemIndex <= 3)
                QueueLoadOlderMessages();

            if (msg != null && (ShouldLoadVideoPreview(msg) || HasPendingVideoPreview(msg)))
                QueueVisibleVideoPreviews();

            if (!ShouldAutoDownloadMedia(msg)) return;
            QueueAutoDownloadMedia();
        }

        private void AttachMessageScrollViewer()
        {
            // Called from every IsAtBottom/IsNearTop probe, which run several times per scroll
            // frame. Once the ScrollViewer is known there is nothing to look up, and walking the
            // visual tree of a list full of realized containers on every probe is what makes
            // scrolling stutter.
            if (_messageScrollViewer != null) return;

            var sv = FindVisualChild<ScrollViewer>(MessageList);
            if (sv == null || object.ReferenceEquals(sv, _messageScrollViewer)) return;

            if (_messageScrollViewer != null)
            {
                _messageScrollViewer.ViewChanging -= MessageScrollViewer_ViewChanging;
                _messageScrollViewer.ViewChanged -= MessageScrollViewer_ViewChanged;
            }

            _messageScrollViewer = sv;
            _messageScrollViewer.ViewChanging += MessageScrollViewer_ViewChanging;
            _messageScrollViewer.ViewChanged += MessageScrollViewer_ViewChanged;
        }

        private void DetachMessageScrollViewer()
        {
            if (_messageScrollViewer == null) return;
            try
            {
                _messageScrollViewer.ViewChanging -= MessageScrollViewer_ViewChanging;
                _messageScrollViewer.ViewChanged -= MessageScrollViewer_ViewChanged;
            }
            catch
            {
            }
            _messageScrollViewer = null;
        }

        private void MessageScrollViewer_ViewChanging(object sender, ScrollViewerViewChangingEventArgs e)
        {
            // Fires on every frame of an inertial scroll, so this must stay cheap. User input is
            // detected by PointerPressed/PointerWheelChanged, which also cancels stale corrections.
            CheckTopLoadTrigger(true);
        }

        private void MessageScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            var intermediate = e != null && e.IsIntermediate;
            var userInteractionActive = DateTime.UtcNow.Ticks < _suppressViewportCorrectionsUntilTicks;
            if (userInteractionActive)
                _suppressViewportCorrectionsUntilTicks = DateTime.UtcNow.AddMilliseconds(intermediate ? 500 : 180).Ticks;
            if (!_initialBottomPositionPending && ShouldTrackScrollChange(e))
            {
                var nearBottom = IsNearBottom(160);
                if (userInteractionActive)
                    _stickToBottom = nearBottom;
                else if (!_stickToBottom && nearBottom)
                    _stickToBottom = true;
            }
            UpdateScrollDownButton();
            var sv = sender as ScrollViewer;
            if (sv == null || !_historyLoaded || _loading || _autoLoadingOlder) return;
            CheckTopLoadTrigger(intermediate);
            if (!intermediate)
            {
                QueueVisibleVideoPreviews();
                QueueAutoDownloadMedia();
            }
            if (!intermediate && IsAtBottom())
            {
                var ignored = MarkVisibleMessagesReadAsync();
            }
        }

        private void CheckTopLoadTrigger()
        {
            CheckTopLoadTrigger(false);
        }

        /// <param name="cheapOnly">
        /// When true only the scroll offset is examined. The container-based probes walk the
        /// realized rows and force transforms, which is too expensive to do mid-scroll.
        /// </param>
        private void CheckTopLoadTrigger(bool cheapOnly)
        {
            if (_initialBottomPositionPending || !_initialMessageListRevealed || _shortViewportFillQueued) return;
            if (!_historyLoaded || _loading || _autoLoadingOlder || !CanLoadOlderManually()) return;
            AttachMessageScrollViewer();
            if (!IsMessageListScrollable()) return;
            if (cheapOnly ? IsNearTop(180) : IsTopLoadViewportActive())
                QueueLoadOlderMessages();
        }

        private void QueueLoadOlderMessages()
        {
            if (_olderLoadQueued || _autoLoadingOlder || _loading || !CanLoadOlderManually()) return;
            _olderLoadQueued = true;
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, async delegate
            {
                _olderLoadQueued = false;
                if (!_historyLoaded || _loading || _autoLoadingOlder || !CanLoadOlderManually()) return;
                AttachMessageScrollViewer();
                if (!IsTopLoadViewportActive()) return;
                await LoadOlderMessagesAsync();
                await System.Threading.Tasks.Task.Delay(220);
                if (!_historyLoaded || _loading || _autoLoadingOlder || !CanLoadOlderManually()) return;
                if (IsTopLoadViewportActive())
                    QueueLoadOlderMessages();
            });
        }

        private void QueueAutoDownloadMedia()
        {
            if (!TelegramAppSettings.AnyChatAutoDownloadEnabled)
            {
                _autoMediaDownloadAgain = false;
                _autoMediaDownloadQueued = false;
                return;
            }
            if (_autoMediaDownloading)
            {
                _autoMediaDownloadAgain = true;
                return;
            }
            if (_autoMediaDownloadQueued) return;
            _autoMediaDownloadQueued = true;
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, delegate
            {
                _autoMediaDownloadQueued = false;
                BeginAutoDownloadMedia();
            });
        }

        private void QueueVisibleVideoPreviews()
        {
            if (_videoPreviewQueued) return;
            _videoPreviewQueued = true;
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, async delegate
            {
                _videoPreviewQueued = false;
                await System.Threading.Tasks.Task.Delay(180);
                await LoadVisibleVideoPreviewsAsync();
            });
        }

        private async void ScrollDownButton_Click(object sender, RoutedEventArgs e)
        {
            ScrollToBottom(true);
            await MarkVisibleMessagesReadAsync();
            UpdateScrollDownButton();
        }

        private async System.Threading.Tasks.Task MarkVisibleMessagesReadAsync()
        {
            if (_chat == null || _markingRead) return;
            var visibleIds = BuildVisibleIncomingMessageIdsToRead();
            if (visibleIds.Count == 0) return;
            var newestVisibleId = visibleIds[visibleIds.Count - 1];
            if (_chat.UnreadCount <= 0 && newestVisibleId <= _lastMarkedReadMaxId) return;

            _markingRead = true;
            try
            {
                await TelegramService.Instance.MarkChatMessagesReadAsync(_chat, visibleIds);
                if (newestVisibleId > _lastMarkedReadMaxId)
                    _lastMarkedReadMaxId = newestVisibleId;
                if (IsAtBottom() || newestVisibleId >= _chat.TopMessageId)
                    _chat.UnreadCount = 0;
                UpdateScrollDownButton();
            }
            catch
            {
                // Reading state is synchronized opportunistically; do not block the chat UI.
            }
            finally
            {
                _markingRead = false;
            }
        }

        private List<int> BuildVisibleIncomingMessageIdsToRead()
        {
            var ids = new List<int>();
            if (_messages == null) return ids;
            for (var i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg == null || msg.Id <= 0 || msg.IsOutgoing) continue;
                if (msg.Id <= _lastMarkedReadMaxId && _chat != null && _chat.UnreadCount <= 0) continue;
                if (!IsMessageVisible(msg)) continue;
                ids.Add(msg.Id);
            }
            ids.Sort();
            return ids;
        }

        private void ScrollToBottom(bool animated)
        {
            if (_messages == null || _messages.Count == 0) return;
            _stickToBottom = true;
            CancelPendingViewportCorrection();
            ScrollToBottomNow(animated);
            ScheduleViewportCorrection(true, null);
            UpdateScrollDownButton();
            BeginAutoDownloadMedia();
        }

        private void ScrollToBottomSoon()
        {
            if (_messages == null || _messages.Count == 0) return;
            _stickToBottom = true;
            ScheduleViewportCorrection(true, null);
        }

        private void ScrollMessageIntoViewSoon(ChatMessageViewModel message)
        {
            if (message == null || MessageList == null) return;
            _stickToBottom = true;
            IgnoreScrollTrackingBriefly();
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, delegate
            {
                if (MessageList == null) return;
                IgnoreScrollTrackingBriefly();
                MessageList.ScrollIntoView(message, ScrollIntoViewAlignment.Default);
                UpdateScrollDownButton();
            });
        }

        private void ScrollToBottomNow(bool animated)
        {
            if (_messages == null || _messages.Count == 0 || MessageList == null) return;
            TryUpdateMessageListLayout("ScrollToBottomNow");
            AttachMessageScrollViewer();
            var sv = _messageScrollViewer;
            if (sv != null)
            {
                IgnoreScrollTrackingBriefly();
                sv.ChangeView(null, sv.ScrollableHeight, null, !animated);
                return;
            }

            var last = LastRealMessage();
            if (last == null) return;
            IgnoreScrollTrackingBriefly();
            MessageList.ScrollIntoView(last, ScrollIntoViewAlignment.Default);
        }

        private void UpdateScrollDownButton()
        {
            if (ScrollDownButton == null) return;
            UpdateScrollDownButtonPlacement();

            // Runs on every scroll frame: only touch the properties that actually changed, since
            // reassigning Visibility or Margin invalidates layout even when the value is equal.
            var visibility = IsAtBottom() ? Visibility.Collapsed : Visibility.Visible;
            if (ScrollDownButton.Visibility != visibility)
                ScrollDownButton.Visibility = visibility;

            if (ScrollDownUnreadBadge == null) return;

            if (ScrollDownUnreadText != null && _chat != null && _chat.UnreadCount > 0)
            {
                var text = _chat.UnreadCount > 99 ? "99+" : _chat.UnreadCount.ToString();
                if (!string.Equals(ScrollDownUnreadText.Text, text, StringComparison.Ordinal))
                    ScrollDownUnreadText.Text = text;
                if (ScrollDownUnreadBadge.Visibility != Visibility.Visible)
                    ScrollDownUnreadBadge.Visibility = Visibility.Visible;
            }
            else if (ScrollDownUnreadBadge.Visibility != Visibility.Collapsed)
            {
                ScrollDownUnreadBadge.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateScrollDownButtonPlacement()
        {
            if (ScrollDownButton == null) return;

            var inputHeight = ChatInputBar == null ? 56.0 : ChatInputBar.ActualHeight;
            if (inputHeight <= 0)
                inputHeight = 56.0;

            var margin = new Thickness(
                0,
                0,
                ScrollDownButtonRightMargin,
                inputHeight + ScrollDownButtonInputGap);

            var current = ScrollDownButton.Margin;
            if (Math.Abs(current.Bottom - margin.Bottom) < 0.5 && Math.Abs(current.Right - margin.Right) < 0.5) return;
            ScrollDownButton.Margin = margin;
        }

        private void SetTopLoading(bool active)
        {
            StatusBarLoadingIndicator.SetActive(active, TopLoadingBar);
        }

        private bool CanLoadOlderManually()
        {
            return _historyLoaded && !_loading && !_autoLoadingOlder && !_noMoreOlderMessages && _messages != null && _messages.Count > 0;
        }

        private void UpdateTopLoadMorePanel()
        {
            SetTopLoading(_loading || _autoLoadingOlder);
        }

        private T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var typed = child as T;
                if (typed != null) return typed;
                var nested = FindVisualChild<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private T FindNamedChild<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            if (root == null) return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var fe = child as T;
                if (fe != null && fe.Name == name) return fe;
                var nested = FindNamedChild<T>(child, name);
                if (nested != null) return nested;
            }
            return null;
        }


        private async void BeginAutoDownloadMedia()
        {
            if (!TelegramAppSettings.AnyChatAutoDownloadEnabled)
            {
                _autoMediaDownloadAgain = false;
                _autoMediaDownloadQueued = false;
                return;
            }
            if (!_historyLoaded || _messages == null) return;
            if (_autoMediaBackoffUntilTicks > DateTime.UtcNow.Ticks) return;
            if (_autoMediaDownloading)
            {
                _autoMediaDownloadAgain = true;
                return;
            }
            _autoMediaDownloading = true;
            try
            {
                if (TelegramAppSettings.ChatAutoDownloadVideosEnabled || TelegramAppSettings.ChatAutoDownloadOtherEnabled)
                    await LoadVisibleVideoPreviewsAsync();
                var keepBottom = ShouldStickToBottom();
                var snapshot = new List<ChatMessageViewModel>();
                int firstVisible;
                int lastVisible;
                if (TryGetVisibleMessageRange(out firstVisible, out lastVisible))
                {
                    // Walk only what is on screen instead of the whole loaded history.
                    if (lastVisible >= _messages.Count) lastVisible = _messages.Count - 1;
                    for (var i = firstVisible; i <= lastVisible; i++)
                    {
                        var msg = _messages[i] as ChatMessageViewModel;
                        if (!ShouldAutoDownloadMedia(msg)) continue;
                        snapshot.Add(msg);
                        if (snapshot.Count >= 4) break;
                    }
                }

                var downloadedCount = 0;
                for (var i = 0; i < snapshot.Count; i++)
                {
                    if (_autoMediaBackoffUntilTicks > DateTime.UtcNow.Ticks) break;
                    var msg = snapshot[i];
                    if (!ShouldAutoDownloadMedia(msg) || !IsMessageVisible(msg)) continue;
                    try
                    {
                        if (msg.MediaItems != null && msg.MediaItems.Count > 0)
                        {
                            var albumLimit = Math.Min(10, msg.MediaItems.Count);
                            downloadedCount += await AutoDownloadGroupedSpecialMediaAsync(msg, albumLimit);
                            NormalizeGroupedMessageContainer(msg);
                            ReplaceMessage(msg);
                            if (keepBottom) KeepBottomIfStillRequested();
                            continue;
                        }

                        if (downloadedCount >= 4) break;

                        SetMessageMediaDownloadingPreservingViewport(msg, true);
                        if (IsVideoMediaKind(msg.MediaKind)) BeginVideoLoadingDisplayRequest();
                        try
                        {
                            await TelegramService.Instance.DownloadMessageMediaAsync(_chat, msg);
                            downloadedCount++;
                        }
                        finally
                        {
                            if (IsVideoMediaKind(msg.MediaKind)) EndVideoLoadingDisplayRequest();
                        }
                        msg.MediaTitle = string.Empty;
                        msg.MediaErrorText = string.Empty;
                        msg.HasPlaybackError = false;
                        if (keepBottom) KeepBottomIfStillRequested();
                    }
                    catch (Exception ex)
                    {
                        RememberAutoMediaFailure(msg);
                        ApplyAutoMediaFloodBackoff(ex);
                        msg.MediaTitle = string.Empty;
                        msg.MediaErrorText = string.Empty;
                        msg.HasPlaybackError = false;
                    }
                    finally
                    {
                        SetMessageMediaDownloadingPreservingViewport(msg, false);
                        if (keepBottom) KeepBottomIfStillRequested();
                    }
                }
            }
            finally
            {
                _autoMediaDownloading = false;
                if (_autoMediaDownloadAgain)
                {
                    _autoMediaDownloadAgain = false;
                    if (TelegramAppSettings.AnyChatAutoDownloadEnabled && _autoMediaBackoffUntilTicks <= DateTime.UtcNow.Ticks)
                    {
                        var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, delegate
                        {
                            BeginAutoDownloadMedia();
                        });
                    }
                }
            }
        }

        private void StartFastReactionRefresh()
        {
            if (!_historyLoaded || _chat == null || _messages == null || _messages.Count == 0) return;
            if (_fastReactionRefreshRunning || _loadingReactions) return;

            var nowTicks = DateTime.UtcNow.Ticks;
            if (_lastFastReactionRefreshTicks != 0 && nowTicks - _lastFastReactionRefreshTicks < TimeSpan.FromSeconds(30).Ticks)
                return;
            _lastFastReactionRefreshTicks = nowTicks;

            var ids = BuildFastReactionRefreshIds();
            if (ids == null || ids.Count == 0) return;

            StartFastReactionRefreshAsync(ids);
        }

        private void QueueCustomReactionIconLoadForVisibleMessagesCoalesced()
        {
            if (_customReactionIconLoadQueued) return;
            _customReactionIconLoadQueued = true;

            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(80);
                    QueueCustomReactionIconLoadForVisibleMessages();
                }
                finally
                {
                    _customReactionIconLoadQueued = false;
                }
            });
        }

        private void QueueCustomReactionIconLoadForVisibleMessages()
        {
            if (_messages == null || _messages.Count == 0) return;
            for (var i = 0; i < _messages.Count; i++)
                QueueCustomReactionIconLoad(_messages[i] as ChatMessageViewModel);
        }

        private void QueueCustomReactionIconLoad(ChatMessageViewModel msg)
        {
            if (msg == null || msg.Reactions == null || msg.Reactions.Count == 0) return;

            var ids = new List<long>();
            for (var i = 0; i < msg.Reactions.Count; i++)
            {
                var reaction = msg.Reactions[i];
                if (reaction == null || reaction.CustomEmojiDocumentId == 0 || !string.IsNullOrEmpty(reaction.CustomEmojiUri)) continue;

                var id = reaction.CustomEmojiDocumentId;
                if (_customReactionIconRequestedIds.Contains(id)) continue;
                _customReactionIconRequestedIds.Add(id);
                ids.Add(id);
            }

            if (ids.Count > 0)
                LoadCustomReactionIconsAsync(ids);
        }

        private async void LoadCustomReactionIconsAsync(List<long> customEmojiIds)
        {
            if (customEmojiIds == null || customEmojiIds.Count == 0) return;
            for (var i = 0; i < customEmojiIds.Count; i++)
            {
                var customEmojiId = customEmojiIds[i];
                if (customEmojiId == 0) continue;

                string uri;
                try
                {
                    uri = await TelegramService.Instance.GetCustomEmojiStickerUriAsync(customEmojiId);
                }
                catch
                {
                    _customReactionIconRequestedIds.Remove(customEmojiId);
                    continue;
                }

                if (string.IsNullOrEmpty(uri))
                {
                    _customReactionIconRequestedIds.Remove(customEmojiId);
                    continue;
                }
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
                {
                    ApplyCustomReactionIconUri(customEmojiId, uri);
                });
            }
        }

        private void ApplyCustomReactionIconUri(long customEmojiId, string uri)
        {
            if (customEmojiId == 0 || string.IsNullOrEmpty(uri) || _messages == null) return;
            for (var i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg == null || msg.Reactions == null || msg.Reactions.Count == 0) continue;
                for (var j = 0; j < msg.Reactions.Count; j++)
                {
                    var reaction = msg.Reactions[j];
                    if (reaction != null && reaction.CustomEmojiDocumentId == customEmojiId)
                        reaction.CustomEmojiUri = uri;
                }
            }
        }

        private async void StartFastReactionRefreshAsync(List<int> ids)
        {
            if (ids == null || ids.Count == 0 || _chat == null) return;
            _fastReactionRefreshRunning = true;
            try
            {
                var reactions = await TelegramService.Instance.GetMessageReactionsAsync(_chat, ids);
                if (reactions == null || reactions.Count == 0) return;

                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
                {
                    for (var i = 0; i < ids.Count; i++)
                    {
                        var msg = FindMessageById(ids[i]);
                        if (msg == null) continue;
                        List<MessageReactionViewModel> list;
                        if (reactions.TryGetValue(ids[i], out list))
                        {
                            msg.SetReactions(list);
                            QueueCustomReactionIconLoad(msg);
                        }
                    }
                });
            }
            catch
            {
                // Reaction refresh must not block the normal new-message poll.
            }
            finally
            {
                _fastReactionRefreshRunning = false;
            }
        }

        private List<int> BuildFastReactionRefreshIds()
        {
            var ids = new List<int>();
            var seen = new HashSet<int>();
            if (_messages == null || _messages.Count == 0) return ids;

            for (var i = _messages.Count - 1; i >= 0 && ids.Count < 24; i--)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg == null || !IsMessageVisible(msg)) continue;
                AddReactionRefreshId(ids, seen, msg);
            }

            for (var i = _messages.Count - 1; i >= 0 && ids.Count < 36; i--)
                AddReactionRefreshId(ids, seen, _messages[i] as ChatMessageViewModel);

            return ids;
        }

        private void AddReactionRefreshId(List<int> ids, HashSet<int> seen, ChatMessageViewModel msg)
        {
            if (ids == null || seen == null || msg == null || msg.Id <= 0) return;
            if (!msg.CanReact && (msg.Reactions == null || msg.Reactions.Count == 0)) return;
            if (seen.Contains(msg.Id)) return;
            seen.Add(msg.Id);
            ids.Add(msg.Id);
        }

        private async void StartBackgroundReactionLoad()
        {
            if (_chat == null || _messages == null || _messages.Count == 0 || _loadingReactions || _fastReactionRefreshRunning) return;

            var ids = new List<int>();
            for (var i = _messages.Count - 1; i >= 0 && ids.Count < 40; i--)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg == null || msg.Id <= 0 || !msg.CanReact) continue;
                if (_reactionLoadRequestedIds.Contains(msg.Id)) continue;
                _reactionLoadRequestedIds.Add(msg.Id);
                ids.Add(msg.Id);
            }

            if (ids.Count == 0) return;

            _loadingReactions = true;
            try
            {
                var reactions = await TelegramService.Instance.GetMessageReactionsAsync(_chat, ids);
                if (reactions == null || reactions.Count == 0) return;

                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
                {
                    for (var i = 0; i < ids.Count; i++)
                    {
                        var msg = FindMessageById(ids[i]);
                        if (msg == null) continue;
                        List<MessageReactionViewModel> list;
                        if (reactions.TryGetValue(ids[i], out list))
                        {
                            msg.SetReactions(list);
                            QueueCustomReactionIconLoad(msg);
                        }
                    }
                });
            }
            catch
            {
                for (var i = 0; i < ids.Count; i++)
                    _reactionLoadRequestedIds.Remove(ids[i]);
            }
            finally
            {
                _loadingReactions = false;
            }
        }

        private async void StartBackgroundReadByLoad()
        {
            if (_chat == null || _messages == null || _messages.Count == 0 || _loadingReadByUsers) return;
            if (_chat.MessageViewersUnavailable) return;
            if (_chat.SubscriberCount > 100) return;
            if (!_chat.IsGroup && !_chat.IsForumTopic) return;

            var ids = new List<int>();
            for (var i = _messages.Count - 1; i >= 0 && ids.Count < 10; i--)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (!CanShowReadByAction(msg)) continue;
                if (msg.IsRead) continue;
                if (_readByLoadRequestedIds.Contains(msg.Id)) continue;
                _readByLoadRequestedIds.Add(msg.Id);
                ids.Add(msg.Id);
            }

            if (ids.Count == 0) return;

            _loadingReadByUsers = true;
            try
            {
                for (var i = 0; i < ids.Count; i++)
                {
                    var id = ids[i];
                    List<CommentAvatarViewModel> viewers = null;
                    try
                    {
                        viewers = await TelegramService.Instance.GetMessageViewersAsync(_chat, id, 20);
                    }
                    catch
                    {
                    }

                    if (_chat == null || _chat.MessageViewersUnavailable) break;
                    if (viewers == null || viewers.Count == 0) continue;
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
                    {
                        var msg = FindMessageById(id);
                        if (msg != null)
                        {
                            msg.IsRead = true;
                            msg.SetReadByUsers(viewers);
                        }
                    });
                }
            }
            finally
            {
                _loadingReadByUsers = false;
            }
        }

        /// <summary>
        /// Visible range straight from the virtualizing panel, the way Unigram's
        /// ViewVisibleMessages does it. The previous implementation resolved a container and ran
        /// TransformToVisual for every candidate message, which is an order of magnitude more
        /// work for an answer the panel already knows.
        /// </summary>
        private bool TryGetVisibleMessageRange(out int firstIndex, out int lastIndex)
        {
            firstIndex = -1;
            lastIndex = -1;
            if (MessageList == null) return false;

            var panel = MessageList.ItemsPanelRoot as ItemsStackPanel;
            if (panel == null || panel.FirstVisibleIndex < 0 || panel.LastVisibleIndex < panel.FirstVisibleIndex) return false;

            firstIndex = panel.FirstVisibleIndex;
            lastIndex = panel.LastVisibleIndex;
            return true;
        }

        private bool IsMessageVisible(ChatMessageViewModel msg)
        {
            if (msg == null || _messages == null) return false;

            int firstIndex;
            int lastIndex;
            if (!TryGetVisibleMessageRange(out firstIndex, out lastIndex)) return false;

            if (lastIndex >= _messages.Count) lastIndex = _messages.Count - 1;
            for (var i = firstIndex; i <= lastIndex; i++)
            {
                if (object.ReferenceEquals(_messages[i], msg)) return true;
            }

            return false;
        }

        private bool ShouldAutoDownloadMedia(ChatMessageViewModel msg)
        {
            if (msg == null) return false;
            if (ShouldAutoDownloadMediaKind(msg.MediaKind) && msg.HasMedia && string.IsNullOrEmpty(msg.MediaFileUri) && !IsAutoMediaDownloadFailed(msg)) return true;
            if (msg.MediaItems == null) return false;
            for (var i = 0; i < msg.MediaItems.Count; i++)
            {
                var item = msg.MediaItems[i];
                if (ShouldAutoDownloadMediaItem(item)) return true;
            }
            return false;
        }

        private bool ShouldAutoDownloadMediaItem(ChatMediaItemViewModel item)
        {
            if (item == null) return false;
            return ShouldAutoDownloadMediaKind(item.MediaKind) && string.IsNullOrEmpty(item.MediaFileUri) && !IsAutoMediaDownloadFailed(item);
        }

        private bool ShouldAutoDownloadMediaKind(string mediaKind)
        {
            if (string.IsNullOrEmpty(mediaKind)) return false;
            if (mediaKind == "photo") return TelegramAppSettings.ChatAutoDownloadPhotosEnabled;
            if (mediaKind == "gif") return TelegramAppSettings.ChatAutoDownloadGifsEnabled;
            if (mediaKind == "sticker") return TelegramAppSettings.ChatAutoDownloadStickersEnabled;
            if (IsVideoMediaKind(mediaKind)) return TelegramAppSettings.ChatAutoDownloadVideosEnabled;
            if (mediaKind == "file" || mediaKind == "document") return false;
            return TelegramAppSettings.ChatAutoDownloadOtherEnabled;
        }

        private bool IsAutoMediaDownloadFailed(ChatMessageViewModel msg)
        {
            var key = BuildAutoMediaDownloadKey(msg);
            return !string.IsNullOrEmpty(key) && _autoMediaDownloadFailedKeys.Contains(key);
        }

        private bool IsAutoMediaDownloadFailed(ChatMediaItemViewModel item)
        {
            var key = BuildAutoMediaDownloadKey(item);
            return !string.IsNullOrEmpty(key) && _autoMediaDownloadFailedKeys.Contains(key);
        }

        private void RememberAutoMediaFailure(ChatMessageViewModel msg)
        {
            var key = BuildAutoMediaDownloadKey(msg);
            if (!string.IsNullOrEmpty(key)) _autoMediaDownloadFailedKeys.Add(key);
        }

        private void RememberAutoMediaFailure(ChatMediaItemViewModel item)
        {
            var key = BuildAutoMediaDownloadKey(item);
            if (!string.IsNullOrEmpty(key)) _autoMediaDownloadFailedKeys.Add(key);
        }

        private string BuildAutoMediaDownloadKey(ChatMessageViewModel msg)
        {
            if (msg == null) return string.Empty;
            if (msg.MediaId != 0) return "m:" + msg.MediaId.ToString();
            if (msg.Id != 0) return "msg:" + msg.Id.ToString() + ":" + (msg.MediaKind ?? string.Empty);
            return string.Empty;
        }

        private string BuildAutoMediaDownloadKey(ChatMediaItemViewModel item)
        {
            if (item == null) return string.Empty;
            if (item.MediaId != 0) return "i:" + item.MediaId.ToString();
            var owner = item.OwnerMessage;
            if (owner != null && owner.Id != 0) return "item:" + owner.Id.ToString() + ":" + (item.MediaKind ?? string.Empty) + ":" + (item.MediaFileName ?? string.Empty);
            return string.Empty;
        }

        private void ApplyAutoMediaFloodBackoff(Exception ex)
        {
            var seconds = ParseFloodWaitSeconds(ex == null ? null : ex.Message);
            if (seconds <= 0) return;
            if (seconds > 600) seconds = 600;
            _autoMediaBackoffUntilTicks = DateTime.UtcNow.AddSeconds(seconds + 2).Ticks;
            _autoMediaDownloadAgain = false;
            _autoMediaDownloadQueued = false;
        }

        private static int ParseFloodWaitSeconds(string message)
        {
            if (string.IsNullOrEmpty(message)) return 0;
            const string prefix = "FLOOD_WAIT_";
            var index = message.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return 0;
            index += prefix.Length;
            var end = index;
            while (end < message.Length && message[end] >= '0' && message[end] <= '9') end++;
            int seconds;
            if (int.TryParse(message.Substring(index, end - index), out seconds)) return seconds;
            return 0;
        }

        private async System.Threading.Tasks.Task LoadVisibleVideoPreviewsAsync()
        {
            if (_videoPreviewLoading)
            {
                _videoPreviewLoadAgain = true;
                return;
            }
            if (_messages == null) return;

            _videoPreviewLoading = true;
            try
            {
                var targets = new List<KeyValuePair<string, object>>();
                for (var i = 0; i < _messages.Count && targets.Count < 16; i++)
                {
                    var msg = _messages[i] as ChatMessageViewModel;
                    if (msg == null || !IsMessageVisible(msg)) continue;

                    if (ShouldLoadVideoPreview(msg))
                    {
                        var key = BuildVideoPreviewKey(msg);
                        if (CanRequestVideoPreview(key))
                        {
                            _videoPreviewRequestedKeys.Add(key);
                            targets.Add(new KeyValuePair<string, object>(key, msg));
                        }
                    }

                    if (msg.MediaItems == null) continue;
                    for (var j = 0; j < msg.MediaItems.Count && targets.Count < 16; j++)
                    {
                        var item = msg.MediaItems[j];
                        if (!ShouldLoadVideoPreview(item)) continue;
                        var key = BuildVideoPreviewKey(item);
                        if (!CanRequestVideoPreview(key)) continue;
                        _videoPreviewRequestedKeys.Add(key);
                        targets.Add(new KeyValuePair<string, object>(key, item));
                    }
                }

                for (var i = 0; i < targets.Count; i++)
                {
                    var key = targets[i].Key;
                    var msg = targets[i].Value as ChatMessageViewModel;
                    if (msg != null)
                    {
                        var loaded = false;
                        try
                        {
                            await TelegramService.Instance.DownloadMessageVideoPreviewAsync(_chat, msg);
                            ReplaceMessage(msg);
                            loaded = !string.IsNullOrEmpty(msg.MediaPreviewUri);
                        }
                        catch
                        {
                        }

                        if (loaded) CompleteVideoPreviewRequest(key);
                        else ScheduleVideoPreviewRetry(key);
                        continue;
                    }

                    var item = targets[i].Value as ChatMediaItemViewModel;
                    if (item == null)
                    {
                        ScheduleVideoPreviewRetry(key);
                        continue;
                    }

                    var itemLoaded = false;
                    try
                    {
                        await TelegramService.Instance.DownloadMessageVideoPreviewAsync(item);
                        var owner = item.OwnerMessage;
                        if (owner != null)
                        {
                            NormalizeGroupedMessageContainer(owner);
                            ReplaceMessage(owner);
                        }
                        itemLoaded = !string.IsNullOrEmpty(item.MediaPreviewUri);
                    }
                    catch
                    {
                    }

                    if (itemLoaded) CompleteVideoPreviewRequest(key);
                    else ScheduleVideoPreviewRetry(key);
                }
            }
            finally
            {
                _videoPreviewLoading = false;
                if (_videoPreviewLoadAgain)
                {
                    _videoPreviewLoadAgain = false;
                    var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, delegate
                    {
                        QueueVisibleVideoPreviews();
                    });
                }
            }
        }

        private bool CanRequestVideoPreview(string key)
        {
            if (string.IsNullOrEmpty(key) || _videoPreviewRequestedKeys.Contains(key)) return false;
            int retries;
            return !_videoPreviewRetryCounts.TryGetValue(key, out retries) || retries < 5;
        }

        private void CompleteVideoPreviewRequest(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _videoPreviewRetryCounts.Remove(key);
            _videoPreviewRetryQueuedKeys.Remove(key);
        }

        private void ScheduleVideoPreviewRetry(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _videoPreviewRequestedKeys.Remove(key);

            int retries;
            _videoPreviewRetryCounts.TryGetValue(key, out retries);
            if (retries >= 5) return;
            retries++;
            _videoPreviewRetryCounts[key] = retries;

            if (!_videoPreviewRetryQueuedKeys.Add(key)) return;
            var delay = 450 * retries;
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, async delegate
            {
                await System.Threading.Tasks.Task.Delay(delay);
                _videoPreviewRetryQueuedKeys.Remove(key);
                QueueVisibleVideoPreviews();
            });
        }

        private bool HasPendingVideoPreview(ChatMessageViewModel msg)
        {
            if (msg == null) return false;
            if (IsVideoMediaKind(msg.MediaKind) &&
                string.IsNullOrEmpty(msg.MediaPreviewUri) && msg.MediaPreviewId != 0)
                return true;
            if (msg.MediaItems == null) return false;
            for (var i = 0; i < msg.MediaItems.Count; i++)
            {
                var item = msg.MediaItems[i];
                if (item != null && IsVideoMediaKind(item.MediaKind) &&
                    string.IsNullOrEmpty(item.MediaPreviewUri) && item.MediaPreviewId != 0)
                    return true;
            }
            return false;
        }

        private bool ShouldLoadVideoPreview(ChatMessageViewModel msg)
        {
            if (msg == null || !IsPreviewMediaKind(msg.MediaKind)) return false;
            if (!ShouldLoadPreviewMediaKind(msg.MediaKind)) return false;
            if (!string.IsNullOrEmpty(msg.MediaPreviewUri)) return false;
            // Keep the inline minithumbnail visible while the real thumbnail downloads.
            // A non-null BitmapImage isn't proof that the remote thumbnail was requested.
            return msg.MediaPreviewId != 0;
        }

        private bool ShouldLoadVideoPreview(ChatMediaItemViewModel item)
        {
            if (item == null || !IsPreviewMediaKind(item.MediaKind)) return false;
            if (!ShouldLoadPreviewMediaKind(item.MediaKind)) return false;
            if (!string.IsNullOrEmpty(item.MediaPreviewUri)) return false;
            return item.MediaPreviewId != 0;
        }

        private bool ShouldLoadPreviewMediaKind(string mediaKind)
        {
            if (IsVideoMediaKind(mediaKind)) return true;
            if (mediaKind == "audio") return TelegramAppSettings.ChatAutoDownloadOtherEnabled;
            return false;
        }

        private string BuildVideoPreviewKey(ChatMessageViewModel msg)
        {
            if (msg == null) return string.Empty;
            var chatId = _chat == null ? 0 : _chat.PeerId;
            return "m:" + chatId.ToString() + ":" + msg.Id.ToString() + ":" + msg.MediaId.ToString() + ":" + msg.MediaPreviewId.ToString() + ":" + (msg.MediaThumbSize ?? string.Empty);
        }

        private string BuildVideoPreviewKey(ChatMediaItemViewModel item)
        {
            if (item == null) return string.Empty;
            var ownerId = item.OwnerMessage == null ? 0 : item.OwnerMessage.Id;
            var chatId = _chat == null ? 0 : _chat.PeerId;
            return "i:" + chatId.ToString() + ":" + ownerId.ToString() + ":" + item.SourceMessageId.ToString() + ":" + item.MediaId.ToString() + ":" + item.MediaPreviewId.ToString() + ":" + (item.MediaThumbSize ?? string.Empty);
        }

        private async System.Threading.Tasks.Task<int> AutoDownloadGroupedSpecialMediaAsync(ChatMessageViewModel msg, int limit)
        {
            var downloaded = 0;
            if (msg == null || msg.MediaItems == null || limit <= 0) return downloaded;
            for (var i = 0; i < msg.MediaItems.Count; i++)
            {
                if (downloaded >= limit) break;
                if (_autoMediaBackoffUntilTicks > DateTime.UtcNow.Ticks) break;
                var item = msg.MediaItems[i];
                if (!ShouldAutoDownloadMediaItem(item)) continue;
                try
                {
                    SetMediaItemDownloadingPreservingViewport(item, true);
                    if (IsVideoMediaKind(item.MediaKind)) BeginVideoLoadingDisplayRequest();
                    try
                    {
                        await TelegramService.Instance.DownloadMessageMediaAsync(item);
                        downloaded++;
                    }
                    finally
                    {
                        if (IsVideoMediaKind(item.MediaKind)) EndVideoLoadingDisplayRequest();
                    }
                    item.MediaTitle = string.Empty;
                    item.MediaErrorText = string.Empty;
                    item.HasPlaybackError = false;
                }
                catch (Exception ex)
                {
                    RememberAutoMediaFailure(item);
                    ApplyAutoMediaFloodBackoff(ex);
                    item.MediaTitle = string.Empty;
                    item.MediaErrorText = string.Empty;
                    item.HasPlaybackError = false;
                }
                finally
                {
                    SetMediaItemDownloadingPreservingViewport(item, false);
                }
            }
            return downloaded;
        }


        private async void DownloadMediaItemButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button == null ? null : button.Tag as ChatMediaItemViewModel;
            if (item == null || item.IsMediaDownloading) return;

            try
            {
                if (item.MediaKind == "photo")
                {
                    await DownloadViewedPhotoAsync(item, FindFirstImageSource(button), true);
                    return;
                }

                item.IsMediaDownloading = true;
                if (item.MediaKind == "document")
                {
                    item.IsFileDownloadOperationActive = true;
                    await AllowFileDownloadVisualStateToRenderAsync();
                }
                if (IsVideoMediaKind(item.MediaKind))
                {
                    await DownloadVideoItemForPlaybackAsync(item);
                }
                else
                {
                    await TelegramService.Instance.DownloadMessageMediaAsync(item);

                    if (item.MediaKind == "document")
                    {
                        item.MediaDownloadBytes = 0;
                        item.MediaDownloadTotalBytes = 0;
                        item.IsMediaDownloading = true;
                        Debug.WriteLine("TG_GROUPED_FILE_MEDIA_READY sourceMessageId=" + item.SourceMessageId.ToString() +
                                        " name=" + (item.MediaFileName ?? "-") +
                                        " uri=" + (item.MediaFileUri ?? "-"));

                        var sourceFile = await GetDownloadedMediaStorageFileAsync(item.MediaFileUri);
                        if (sourceFile == null)
                            throw new InvalidOperationException("Downloaded TDLib file could not be opened.");

                        var targetName = SanitizeDownloadFileName(item.MediaFileName, sourceFile.Name);
                        item.MediaDownloadBytes = 0;
                        item.MediaDownloadTotalBytes = await GetStorageFileSizeAsync(sourceFile);

                        var targetFile = await CopyStorageFileToDownloadsAsync(sourceFile, targetName, null);
                        if (targetFile == null)
                            throw new InvalidOperationException("The file was not saved.");

                        Debug.WriteLine("TG_GROUPED_FILE_DOWNLOAD_DONE sourceMessageId=" + item.SourceMessageId.ToString() +
                                        " source=" + sourceFile.Name +
                                        " target=" + targetFile.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_GROUPED_FILE_DOWNLOAD_ERROR sourceMessageId=" +
                                (item == null ? 0 : item.SourceMessageId).ToString() +
                                " error=" + ex.GetType().Name + ": " + (ex.Message ?? string.Empty));
                item.MediaErrorText = string.Empty;
                item.HasPlaybackError = false;

                if (item.MediaKind == "document")
                    await ShowChatAlertAsync("File download error", AlertErrorMessage(ex, "Could not download this file."));
            }
            finally
            {
                item.IsMediaDownloading = false;
                if (item != null && item.MediaKind == "document")
                {
                    item.IsFileDownloadOperationActive = false;
                    item.MediaDownloadBytes = 0;
                    item.MediaDownloadTotalBytes = 0;
                }
            }
        }

        private async void DownloadMediaButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var msg = button == null ? null : button.Tag as ChatMessageViewModel;
            if (msg == null || msg.IsMediaDownloading) return;

            try
            {
                if (msg.MediaKind == "photo")
                {
                    await DownloadViewedPhotoAsync(msg, FindFirstImageSource(button), true);
                    return;
                }

                msg.IsMediaDownloading = true;

                if (IsVideoMediaKind(msg.MediaKind))
                {
                    await DownloadVideoMessageForPlaybackAsync(msg);
                    return;
                }

                if (msg.GroupedId != 0 && msg.MediaItems != null && msg.MediaItems.Count > 0)
                {
                    await DownloadGroupedMessageMediaAsync(msg);
                    NormalizeGroupedMessageContainer(msg);
                    ReplaceMessage(msg);
                    return;
                }

                await TelegramService.Instance.DownloadMessageMediaAsync(_chat, msg);
            }
            catch
            {
                msg.MediaTitle = string.Empty;
                msg.MediaErrorText = string.Empty;
                msg.HasPlaybackError = false;
                ReplaceMessage(msg);
            }
            finally
            {
                msg.IsMediaDownloading = false;
                if (msg.GroupedId != 0) NormalizeGroupedMessageContainer(msg);
                ReplaceMessage(msg);
            }
        }

        private bool IsVideoMediaKind(string mediaKind)
        {
            return mediaKind == "video" || mediaKind == "roundvideo";
        }

        private bool IsPreviewMediaKind(string mediaKind)
        {
            return IsVideoMediaKind(mediaKind) || mediaKind == "audio";
        }

        private void QueueViewedAutoDownload(object source)
        {
            if (!_historyLoaded || source == null) return;
            if (_autoMediaBackoffUntilTicks > DateTime.UtcNow.Ticks) return;
            if (!ShouldAutoDownloadViewedSource(source)) return;

            var key = BuildViewedPhotoDownloadKey(source);
            if (string.IsNullOrEmpty(key) || _viewedPhotoDownloadQueuedKeys.Contains(key)) return;
            _viewedPhotoDownloadQueuedKeys.Add(key);
            _viewedPhotoDownloadQueue.Enqueue(source);

            if (!_viewedPhotoDownloadRunning)
                ProcessViewedPhotoDownloadQueue();
        }

        private void QueueVisibleViewedAutoDownloads()
        {
            if (!TelegramAppSettings.AnyChatAutoDownloadEnabled) return;
            if (!_historyLoaded || _messages == null) return;
            var queued = 0;
            for (var i = 0; i < _messages.Count && queued < 3; i++)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg == null || !IsMessageVisible(msg)) continue;

                if (ShouldAutoDownloadViewedSource(msg))
                {
                    QueueViewedAutoDownload(msg);
                    queued++;
                    continue;
                }

                if (msg.MediaItems == null) continue;
                for (var j = 0; j < msg.MediaItems.Count && queued < 3; j++)
                {
                    var item = msg.MediaItems[j];
                    if (!ShouldAutoDownloadViewedSource(item)) continue;
                    QueueViewedAutoDownload(item);
                    queued++;
                }
            }
        }

        private async void ProcessViewedPhotoDownloadQueue()
        {
            if (_viewedPhotoDownloadRunning) return;
            _viewedPhotoDownloadRunning = true;
            try
            {
                var processed = 0;
                while (_viewedPhotoDownloadQueue.Count > 0 && processed < 2)
                {
                    if (_autoMediaBackoffUntilTicks > DateTime.UtcNow.Ticks) break;
                    var source = _viewedPhotoDownloadQueue.Dequeue();
                    if (!ShouldAutoDownloadViewedSource(source)) continue;
                    await DownloadViewedAutoMediaAsync(source);
                    processed++;
                }
            }
            finally
            {
                _viewedPhotoDownloadRunning = false;
            }

            if (_viewedPhotoDownloadQueue.Count > 0 && _autoMediaBackoffUntilTicks <= DateTime.UtcNow.Ticks)
            {
                await System.Threading.Tasks.Task.Delay(1200);
                if (!_viewedPhotoDownloadRunning)
                    ProcessViewedPhotoDownloadQueue();
            }
        }

        private bool IsUnloadedPhotoSource(object source)
        {
            var msg = source as ChatMessageViewModel;
            if (msg != null)
                return msg.MediaKind == "photo" && msg.HasMedia && string.IsNullOrEmpty(msg.MediaFileUri) && !msg.IsMediaDownloading;

            var item = source as ChatMediaItemViewModel;
            return item != null && item.MediaKind == "photo" && string.IsNullOrEmpty(item.MediaFileUri) && !item.IsMediaDownloading;
        }

        private bool ShouldAutoDownloadViewedSource(object source)
        {
            var msg = source as ChatMessageViewModel;
            if (msg != null)
                return ShouldAutoDownloadMediaKind(msg.MediaKind) &&
                    msg.HasMedia &&
                    string.IsNullOrEmpty(msg.MediaFileUri) &&
                    !msg.IsMediaDownloading &&
                    !IsAutoMediaDownloadFailed(msg);

            var item = source as ChatMediaItemViewModel;
            return ShouldAutoDownloadMediaItem(item);
        }

        private async System.Threading.Tasks.Task DownloadViewedAutoMediaAsync(object source)
        {
            var msg = source as ChatMessageViewModel;
            if (msg != null)
            {
                if (!ShouldAutoDownloadViewedSource(msg)) return;
                if (msg.MediaKind == "photo")
                {
                    await DownloadViewedPhotoAsync(msg, null, false);
                    return;
                }

                try
                {
                    msg.IsMediaDownloading = true;
                    if (IsVideoMediaKind(msg.MediaKind)) BeginVideoLoadingDisplayRequest();
                    try
                    {
                        await TelegramService.Instance.DownloadMessageMediaAsync(_chat, msg);
                    }
                    finally
                    {
                        if (IsVideoMediaKind(msg.MediaKind)) EndVideoLoadingDisplayRequest();
                    }
                    msg.MediaTitle = string.Empty;
                    msg.MediaErrorText = string.Empty;
                    msg.HasPlaybackError = false;
                }
                catch (Exception ex)
                {
                    RememberAutoMediaFailure(msg);
                    ApplyAutoMediaFloodBackoff(ex);
                    msg.MediaTitle = string.Empty;
                    msg.MediaErrorText = string.Empty;
                    msg.HasPlaybackError = false;
                }
                finally
                {
                    msg.IsMediaDownloading = false;
                    ReplaceMessage(msg);
                }
                return;
            }

            var item = source as ChatMediaItemViewModel;
            if (item == null || !ShouldAutoDownloadViewedSource(item)) return;
            if (item.MediaKind == "photo")
            {
                await DownloadViewedPhotoAsync(item, null, false);
                return;
            }

            try
            {
                item.IsMediaDownloading = true;
                if (IsVideoMediaKind(item.MediaKind)) BeginVideoLoadingDisplayRequest();
                try
                {
                    await TelegramService.Instance.DownloadMessageMediaAsync(item);
                }
                finally
                {
                    if (IsVideoMediaKind(item.MediaKind)) EndVideoLoadingDisplayRequest();
                }
                item.MediaTitle = string.Empty;
                item.MediaErrorText = string.Empty;
                item.HasPlaybackError = false;
            }
            catch (Exception ex)
            {
                RememberAutoMediaFailure(item);
                ApplyAutoMediaFloodBackoff(ex);
                item.MediaTitle = string.Empty;
                item.MediaErrorText = string.Empty;
                item.HasPlaybackError = false;
            }
            finally
            {
                item.IsMediaDownloading = false;
                var owner = item.OwnerMessage;
                if (owner != null)
                {
                    NormalizeGroupedMessageContainer(owner);
                    ReplaceMessage(owner);
                }
            }
        }

        private string BuildViewedPhotoDownloadKey(object source)
        {
            var msg = source as ChatMessageViewModel;
            if (msg != null) return BuildAutoMediaDownloadKey(msg);
            var item = source as ChatMediaItemViewModel;
            if (item != null) return BuildAutoMediaDownloadKey(item);
            return string.Empty;
        }

        private async System.Threading.Tasks.Task DownloadViewedPhotoAsync(object source, ImageSource previewSource, bool showOverlay)
        {
            var msg = source as ChatMessageViewModel;
            var item = source as ChatMediaItemViewModel;
            if (msg == null && item == null) return;

            if (msg != null)
            {
                if (msg.MediaKind != "photo") return;
                if (showOverlay) ShowPhotoOverlay(msg, previewSource);

                if (showOverlay)
                {
                    if (!string.IsNullOrEmpty(msg.MediaFullUri) || msg.IsMediaDownloading) return;

                    try
                    {
                        msg.IsMediaDownloading = true;
                        var original = await TelegramService.Instance.DownloadOriginalPhotoAsync(_chat, msg);
                        var fullUri = ToFileUri(original);
                        if (!string.IsNullOrEmpty(fullUri)) msg.MediaFullUri = fullUri;
                        msg.MediaTitle = string.Empty;
                        msg.MediaErrorText = string.Empty;
                        msg.HasPlaybackError = false;
                    }
                    catch (Exception ex)
                    {
                        ApplyAutoMediaFloodBackoff(ex);
                        msg.MediaTitle = string.Empty;
                        msg.MediaErrorText = string.Empty;
                        msg.HasPlaybackError = false;
                    }
                    finally
                    {
                        msg.IsMediaDownloading = false;
                        ReplaceMessage(msg);
                    }

                    if (!string.IsNullOrEmpty(msg.MediaFullUri) && PhotoOverlay.Visibility == Visibility.Visible)
                        ShowPhotoOverlay(msg, null);
                    return;
                }

                if (!string.IsNullOrEmpty(msg.MediaFileUri) || msg.IsMediaDownloading) return;

                try
                {
                    msg.IsMediaDownloading = true;
                    await TelegramService.Instance.DownloadMessageMediaAsync(_chat, msg);
                    msg.MediaTitle = string.Empty;
                    msg.MediaErrorText = string.Empty;
                    msg.HasPlaybackError = false;
                }
                catch (Exception ex)
                {
                    ApplyAutoMediaFloodBackoff(ex);
                    msg.MediaTitle = string.Empty;
                    msg.MediaErrorText = string.Empty;
                    msg.HasPlaybackError = false;
                }
                finally
                {
                    msg.IsMediaDownloading = false;
                    ReplaceMessage(msg);
                }

                return;
            }

            if (item.MediaKind != "photo") return;
            if (showOverlay) ShowPhotoOverlay(item, previewSource);

            if (showOverlay)
            {
                if (!string.IsNullOrEmpty(item.MediaFullUri) || item.IsMediaDownloading) return;

                try
                {
                    item.IsMediaDownloading = true;
                    var original = await TelegramService.Instance.DownloadOriginalPhotoAsync(item);
                    var fullUri = ToFileUri(original);
                    if (!string.IsNullOrEmpty(fullUri)) item.MediaFullUri = fullUri;
                    item.MediaTitle = string.Empty;
                    item.MediaErrorText = string.Empty;
                    item.HasPlaybackError = false;
                }
                catch (Exception ex)
                {
                    ApplyAutoMediaFloodBackoff(ex);
                    item.MediaTitle = string.Empty;
                    item.MediaErrorText = string.Empty;
                    item.HasPlaybackError = false;
                }
                finally
                {
                    item.IsMediaDownloading = false;
                    var owner = item.OwnerMessage;
                    if (owner != null)
                    {
                        NormalizeGroupedMessageContainer(owner);
                        ReplaceMessage(owner);
                    }
                }

                if (!string.IsNullOrEmpty(item.MediaFullUri) && PhotoOverlay.Visibility == Visibility.Visible)
                    ShowPhotoOverlay(item, null);
                return;
            }

            if (!string.IsNullOrEmpty(item.MediaFileUri) || item.IsMediaDownloading) return;

            try
            {
                item.IsMediaDownloading = true;
                await TelegramService.Instance.DownloadMessageMediaAsync(item);
                item.MediaTitle = string.Empty;
                item.MediaErrorText = string.Empty;
                item.HasPlaybackError = false;
            }
            catch (Exception ex)
            {
                ApplyAutoMediaFloodBackoff(ex);
                item.MediaTitle = string.Empty;
                item.MediaErrorText = string.Empty;
                item.HasPlaybackError = false;
            }
            finally
            {
                item.IsMediaDownloading = false;
                var owner = item.OwnerMessage;
                if (owner != null)
                {
                    NormalizeGroupedMessageContainer(owner);
                    ReplaceMessage(owner);
                }
            }

        }

        private ImageSource FindFirstImageSource(DependencyObject root)
        {
            if (root == null) return null;
            var image = root as Image;
            if (image != null && image.Source != null) return image.Source;

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindFirstImageSource(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        private void BeginVideoLoadingDisplayRequest()
        {
            try
            {
                _videoLoadingDisplayRequest.RequestActive();
                _videoLoadingDisplayRequestCount++;
            }
            catch
            {
            }
        }

        private void EndVideoLoadingDisplayRequest()
        {
            if (_videoLoadingDisplayRequestCount <= 0) return;
            try
            {
                _videoLoadingDisplayRequest.RequestRelease();
                _videoLoadingDisplayRequestCount--;
            }
            catch
            {
                _videoLoadingDisplayRequestCount = 0;
            }
        }

        private void ReleaseAllVideoLoadingDisplayRequests()
        {
            while (_videoLoadingDisplayRequestCount > 0)
            {
                EndVideoLoadingDisplayRequest();
            }
        }

        private async System.Threading.Tasks.Task DownloadVideoMessageForPlaybackAsync(ChatMessageViewModel msg)
        {
            if (msg == null) return;
            BeginVideoLoadingDisplayRequest();
            try
            {
                msg.MediaDownloadBytes = 0;
                msg.MediaDownloadTotalBytes = msg.MediaSize;
                await TelegramService.Instance.DownloadMessageVideoForPlaybackAsync(_chat, msg, delegate(string uri)
                {
                    msg.MediaFileUri = uri;
                    msg.HasPlaybackError = false;
                    ReplaceMessage(msg);
                    QueuePlayMediaElement(msg);
                }, delegate(long bytes, long total)
                {
                    msg.MediaDownloadBytes = bytes;
                    msg.MediaDownloadTotalBytes = total > 0 ? total : msg.MediaSize;
                });
                CompleteMediaDownloadProgress(msg);
                msg.HasPlaybackError = false;
                ReplaceMessage(msg);
                QueuePlayMediaElement(msg);
            }
            finally
            {
                EndVideoLoadingDisplayRequest();
            }
        }

        private async System.Threading.Tasks.Task DownloadVideoItemForPlaybackAsync(ChatMediaItemViewModel item)
        {
            if (item == null) return;
            BeginVideoLoadingDisplayRequest();
            try
            {
                item.MediaDownloadBytes = 0;
                item.MediaDownloadTotalBytes = item.MediaSize;
                await TelegramService.Instance.DownloadMessageVideoForPlaybackAsync(item, delegate(string uri)
                {
                    item.MediaFileUri = uri;
                    item.HasPlaybackError = false;
                    QueuePlayMediaElement(item);
                }, delegate(long bytes, long total)
                {
                    item.MediaDownloadBytes = bytes;
                    item.MediaDownloadTotalBytes = total > 0 ? total : item.MediaSize;
                });
                CompleteMediaDownloadProgress(item);
                item.HasPlaybackError = false;
                QueuePlayMediaElement(item);
            }
            finally
            {
                EndVideoLoadingDisplayRequest();
            }
        }

        private void CompleteMediaDownloadProgress(ChatMessageViewModel msg)
        {
            if (msg == null) return;
            var total = msg.MediaDownloadTotalBytes > 0 ? msg.MediaDownloadTotalBytes : msg.MediaSize;
            if (total <= 0 && msg.MediaDownloadBytes > 0) total = msg.MediaDownloadBytes;
            if (total <= 0) return;
            msg.MediaDownloadTotalBytes = total;
            msg.MediaDownloadBytes = total;
        }

        private void CompleteMediaDownloadProgress(ChatMediaItemViewModel item)
        {
            if (item == null) return;
            var total = item.MediaDownloadTotalBytes > 0 ? item.MediaDownloadTotalBytes : item.MediaSize;
            if (total <= 0 && item.MediaDownloadBytes > 0) total = item.MediaDownloadBytes;
            if (total <= 0) return;
            item.MediaDownloadTotalBytes = total;
            item.MediaDownloadBytes = total;
        }

        private void QueuePlayMediaElement(object dataContext)
        {
            if (dataContext == null) return;
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, delegate
            {
                PlayMediaElementForDataContext(dataContext);
                var ignoredLow = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, delegate
                {
                    PlayMediaElementForDataContext(dataContext);
                });
            });
        }

        private void PlayMediaElementForDataContext(object dataContext)
        {
            var ffmpegPlayer = FindFfmpegVideoPlayerForDataContext(MessageList, dataContext);
            if (ffmpegPlayer != null)
            {
                ffmpegPlayer.PlayWhenReady();
            }
        }

        private void MaybeRetryFfmpegVideoPlayback(object dataContext, long bytes, long total)
        {
            if (dataContext == null || bytes < 256 * 1024) return;
            QueuePlayMediaElement(dataContext);
        }

        private async void FfmpegVideoMediaElement_Loaded(object sender, RoutedEventArgs e)
        {
            var player = sender as MediaElement;
            if (player == null) return;
            await PrepareFfmpegVideoElementAsync(player, player.DataContext);
        }

        private void FfmpegVideoMediaElement_MediaOpened(object sender, RoutedEventArgs e)
        {
            var player = sender as MediaElement;
            if (player == null) return;
            if (!IsVideoDataContext(player.DataContext)) return;
            if (player.NaturalVideoWidth > 0 && player.NaturalVideoHeight > 0) return;

            var uri = GetVideoMediaFileUri(player.DataContext);
            var key = BuildFfmpegVideoKey(player.DataContext, uri);
            if (string.IsNullOrEmpty(key)) return;

            int retries;
            _ffmpegBlankVideoRetryCounts.TryGetValue(key, out retries);
            Debug.WriteLine("TG_VIDEO_FFMPEG opened-without-video key=" + key +
                " retries=" + retries.ToString() +
                " downloading=" + IsVideoDownloadInProgress(player.DataContext).ToString() +
                " forceDecode=" + _ffmpegForceVideoDecodeKeys.Contains(key).ToString());

            if (retries >= 2) return;
            _ffmpegBlankVideoRetryCounts[key] = retries + 1;

            ToggleFfmpegVideoDecodeMode(key);
            ResetFfmpegVideoKey(key);
            QueuePlayMediaElement(player.DataContext);
        }

        private void FfmpegVideoMediaElement_MediaEnded(object sender, RoutedEventArgs e)
        {
            var player = sender as MediaElement;
            if (player == null || !IsVideoDownloadInProgress(player.DataContext)) return;

            var uri = GetVideoMediaFileUri(player.DataContext);
            var key = BuildFfmpegVideoKey(player.DataContext, uri);
            ResetFfmpegVideoKey(key);
            QueuePlayMediaElement(player.DataContext);
        }


        private void GifMediaElement_MediaEnded(object sender, RoutedEventArgs e)
        {
            var player = sender as MediaElement;
            if (player == null) return;

            try
            {
                player.Position = TimeSpan.Zero;
                player.Play();
            }
            catch
            {
            }
        }

        private async System.Threading.Tasks.Task<bool> PrepareFfmpegVideoElementAsync(MediaElement player, object dataContext)
        {
            if (player == null || dataContext == null) return false;
            var uri = GetVideoMediaFileUri(dataContext);
            if (string.IsNullOrEmpty(uri)) return false;
            if (IsPosterImageUri(uri)) return false;

            var key = BuildFfmpegVideoKey(dataContext, uri);
            if (string.IsNullOrEmpty(key) || _ffmpegFailedVideoKeys.Contains(key)) return false;
            var currentKey = player.Tag as string;
            if (string.Equals(currentKey, key, StringComparison.OrdinalIgnoreCase) && _ffmpegPreparedVideoKeys.Contains(key))
                return true;

            try
            {
                var file = await GetDownloadedMediaStorageFileAsync(uri);
                if (file == null) return false;

                var forceVideoDecode = true;
                var ffmpegSource = await CreateFfmpegMediaStreamSourceAsync(file, forceVideoDecode);
                if (ffmpegSource == null || ffmpegSource.Source == null || ffmpegSource.Interop == null)
                {
                    if (!IsVideoDownloadInProgress(dataContext)) _ffmpegFailedVideoKeys.Add(key);
                    return false;
                }

                player.Stop();
                player.Source = null;
                player.Tag = key;
                player.SetMediaStreamSource(ffmpegSource.Source);
                _ffmpegPreparedVideoKeys.Add(key);
                _ffmpegInteropObjects[key] = ffmpegSource.Interop;
                Debug.WriteLine("TG_VIDEO_FFMPEG prepared key=" + key +
                    " forceDecode=" + forceVideoDecode.ToString() +
                    " downloading=" + IsVideoDownloadInProgress(dataContext).ToString());
                return true;
            }
            catch
            {
                if (!IsVideoDownloadInProgress(dataContext)) _ffmpegFailedVideoKeys.Add(key);
                return false;
            }
        }

        private bool IsVideoDownloadInProgress(object dataContext)
        {
            var msg = dataContext as ChatMessageViewModel;
            if (msg != null && IsVideoMediaKind(msg.MediaKind)) return msg.IsMediaDownloading;
            var item = dataContext as ChatMediaItemViewModel;
            if (item != null && IsVideoMediaKind(item.MediaKind)) return item.IsMediaDownloading;
            return false;
        }

        private bool IsVideoDataContext(object dataContext)
        {
            var msg = dataContext as ChatMessageViewModel;
            if (msg != null) return IsVideoMediaKind(msg.MediaKind);
            var item = dataContext as ChatMediaItemViewModel;
            if (item != null) return IsVideoMediaKind(item.MediaKind);
            return false;
        }

        private string GetVideoMediaFileUri(object dataContext)
        {
            var msg = dataContext as ChatMessageViewModel;
            if (msg != null && IsVideoMediaKind(msg.MediaKind) && !IsPosterImageUri(msg.MediaFileUri)) return msg.MediaFileUri;
            var item = dataContext as ChatMediaItemViewModel;
            if (item != null && IsVideoMediaKind(item.MediaKind) && !IsPosterImageUri(item.MediaFileUri)) return item.MediaFileUri;
            return null;
        }

        private string BuildFfmpegVideoKey(object dataContext, string uri)
        {
            var msg = dataContext as ChatMessageViewModel;
            if (msg != null) return "m:" + msg.Id.ToString() + ":" + msg.MediaId.ToString() + ":" + (uri ?? string.Empty);
            var item = dataContext as ChatMediaItemViewModel;
            if (item != null) return "i:" + item.MediaId.ToString() + ":" + (uri ?? string.Empty);
            return uri;
        }

        private async System.Threading.Tasks.Task<FfmpegMediaStreamSourceResult> CreateFfmpegMediaStreamSourceAsync(StorageFile file, bool forceVideoDecode)
        {
            if (file == null) return null;

            try
            {
                var stream = await file.OpenReadAsync();
                var interop = FFmpegInteropMSS.CreateFFmpegInteropMSSFromStream(stream, true, forceVideoDecode);
                if (interop == null) return null;
                var source = interop.GetMediaStreamSource();
                if (source == null) return null;
                return new FfmpegMediaStreamSourceResult { Source = source, Interop = interop };
            }
            catch
            {
                return null;
            }
        }

        private sealed class FfmpegMediaStreamSourceResult
        {
            public Windows.Media.Core.MediaStreamSource Source { get; set; }
            public object Interop { get; set; }
        }

        private void ClearFfmpegVideoCache()
        {
            _ffmpegPreparedVideoKeys.Clear();
            _ffmpegFailedVideoKeys.Clear();
            _ffmpegInteropObjects.Clear();
            _ffmpegRetryProgressBytes.Clear();
            _ffmpegForceVideoDecodeKeys.Clear();
            _ffmpegBlankVideoRetryCounts.Clear();
        }

        private void ResetFfmpegVideoKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _ffmpegPreparedVideoKeys.Remove(key);
            _ffmpegFailedVideoKeys.Remove(key);
            _ffmpegInteropObjects.Remove(key);
        }

        private void ToggleFfmpegVideoDecodeMode(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _ffmpegForceVideoDecodeKeys.Add(key);
        }

        private bool IsPosterImageUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            var value = uri.Trim();
            var cut = value.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) value = value.Substring(0, cut);
            return value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
        }

        private MediaElement FindMediaElementForDataContext(DependencyObject root, object dataContext)
        {
            if (root == null || dataContext == null) return null;
            var fe = root as FrameworkElement;
            var player = root as MediaElement;
            if (player != null && object.ReferenceEquals(fe == null ? null : fe.DataContext, dataContext))
                return player;

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var result = FindMediaElementForDataContext(VisualTreeHelper.GetChild(root, i), dataContext);
                if (result != null) return result;
            }
            return null;
        }

        private FfmpegVideoPlayerControl FindFfmpegVideoPlayerForDataContext(DependencyObject root, object dataContext)
        {
            if (root == null || dataContext == null) return null;
            var fe = root as FrameworkElement;
            var player = root as FfmpegVideoPlayerControl;
            if (player != null && object.ReferenceEquals(fe == null ? null : fe.DataContext, dataContext))
                return player;

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var result = FindFfmpegVideoPlayerForDataContext(VisualTreeHelper.GetChild(root, i), dataContext);
                if (result != null) return result;
            }
            return null;
        }

        private async void DownloadFileButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var msg = button == null ? null : button.Tag as ChatMessageViewModel;
            if (msg == null || msg.IsMediaDownloading) return;

            try
            {
                Debug.WriteLine("TG_FILE_DOWNLOAD_START id=" + msg.Id.ToString() +
                                " kind=" + (msg.MediaKind ?? "-") +
                                " name=" + (msg.MediaFileName ?? "-") +
                                " uri=" + (msg.MediaFileUri ?? "-") +
                                " dc=" + msg.MediaDcId.ToString());
                msg.MediaDownloadBytes = 0;
                msg.MediaDownloadTotalBytes = 0;
                msg.IsFileDownloadOperationActive = true;
                msg.IsMediaDownloading = true;
                await AllowFileDownloadVisualStateToRenderAsync();

                if (string.IsNullOrEmpty(msg.MediaFileUri))
                {
                    await DownloadMessageMediaWithRetryAsync(msg);
                    msg.MediaDownloadBytes = 0;
                    msg.MediaDownloadTotalBytes = 0;
                    msg.IsMediaDownloading = true;
                    await AllowFileDownloadVisualStateToRenderAsync();
                    Debug.WriteLine("TG_FILE_DOWNLOAD_MEDIA_READY id=" + msg.Id.ToString() +
                                    " uri=" + (msg.MediaFileUri ?? "-") +
                                    " title=" + (msg.MediaTitle ?? "-"));
                }

                var sourceFile = await GetDownloadedMediaStorageFileAsync(msg.MediaFileUri);
                if (sourceFile != null)
                {
                    Debug.WriteLine("TG_FILE_SOURCE_RESOLVED id=" + msg.Id.ToString() +
                                    " path=" + sourceFile.Path);
                }
                if (sourceFile == null)
                {
                    Debug.WriteLine("TG_FILE_DOWNLOAD_NO_SOURCE id=" + msg.Id.ToString() +
                                    " uri=" + (msg.MediaFileUri ?? "-") +
                                    " title=" + (msg.MediaTitle ?? "-"));
                    await ShowChatAlertAsync("File download error", string.IsNullOrEmpty(msg.MediaTitle) ? "File is not available." : msg.MediaTitle);
                    return;
                }

                var targetName = BuildDownloadFileName(msg, sourceFile);
                msg.MediaDownloadBytes = 0;
                msg.MediaDownloadTotalBytes = await GetStorageFileSizeAsync(sourceFile);
                var targetFile = await CopyStorageFileToDownloadsAsync(sourceFile, targetName, msg);
                if (targetFile == null)
                    throw new InvalidOperationException("The file was not saved.");

                Debug.WriteLine("TG_FILE_DOWNLOAD_DONE id=" + msg.Id.ToString() +
                                " source=" + sourceFile.Name +
                                " target=" + targetFile.Name);
            }
            catch (COMException ex)
            {
                Debug.WriteLine("TG_FILE_DOWNLOAD_COM_ERROR id=" + msg.Id.ToString() +
                                " error=" + ex.GetType().Name + ": " + (ex.Message ?? string.Empty));
                if (!IsAbortedIoException(ex))
                    await ShowChatAlertAsync("File download error", AlertErrorMessage(ex, "Could not download this file."));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_FILE_DOWNLOAD_ERROR id=" + msg.Id.ToString() +
                                " error=" + ex.GetType().Name + ": " + (ex.Message ?? string.Empty));
                await ShowChatAlertAsync("File download error", AlertErrorMessage(ex, "Could not download this file."));
            }
            finally
            {
                msg.IsMediaDownloading = false;
                msg.IsFileDownloadOperationActive = false;
                msg.MediaDownloadBytes = 0;
                msg.MediaDownloadTotalBytes = 0;
            }
        }

        private async System.Threading.Tasks.Task AllowFileDownloadVisualStateToRenderAsync()
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate { });
            await System.Threading.Tasks.Task.Delay(50);
        }

        private async System.Threading.Tasks.Task<StorageFile> CopyStorageFileToDownloadsAsync(StorageFile sourceFile, string targetName, ChatMessageViewModel msg)
        {
            var safeName = SanitizeDownloadFileName(targetName, sourceFile == null ? null : sourceFile.Name);
            var targetFile = await CreateDownloadTargetFileAsync(safeName);
            if (targetFile == null) return null;
            await CopyStorageFileToFileAsync(sourceFile, targetFile, msg);
            return targetFile;
        }

        private async System.Threading.Tasks.Task<StorageFile> CreateDownloadTargetFileAsync(string safeName)
        {
            // UWP apps don't have unrestricted filesystem access to the user's
            // Downloads directory. FileSavePicker with Downloads as the start
            // location is the supported way to obtain a StorageFile there.
            return await PickDownloadTargetFileAsync(safeName);
        }

        private async System.Threading.Tasks.Task<StorageFile> PickDownloadTargetFileAsync(string safeName)
        {
            try
            {
                var extension = GetDownloadFileExtension(safeName);
                var picker = new FileSavePicker();
                picker.SuggestedStartLocation = PickerLocationId.Downloads;
                picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(safeName);
                picker.FileTypeChoices.Add("File", new List<string> { extension });
                return await picker.PickSaveFileAsync();
            }
            catch (COMException ex)
            {
                if (!IsAbortedIoException(ex))
                    await ShowChatAlertAsync("File download error", AlertErrorMessage(ex, "Could not open Downloads."));
                return null;
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("File download error", AlertErrorMessage(ex, "Could not open Downloads."));
                return null;
            }
        }

        private string GetDownloadFileExtension(string safeName)
        {
            var extension = System.IO.Path.GetExtension(safeName);
            return string.IsNullOrEmpty(extension) ? ".bin" : extension;
        }

        private async System.Threading.Tasks.Task CopyStorageFileToFileAsync(StorageFile sourceFile, StorageFile targetFile, ChatMessageViewModel msg)
        {
            using (var input = await sourceFile.OpenStreamForReadAsync())
            using (var output = await targetFile.OpenStreamForWriteAsync())
            {
                output.SetLength(0);
                var copied = 0L;
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, 0, buffer.Length);
                    if (read <= 0) break;

                    await output.WriteAsync(buffer, 0, read);
                    copied += read;
                    if (msg != null) msg.MediaDownloadBytes = copied;
                }
                await output.FlushAsync();
            }
        }

        private async System.Threading.Tasks.Task<long> GetStorageFileSizeAsync(StorageFile file)
        {
            if (file == null) return 0;

            try
            {
                var props = await file.GetBasicPropertiesAsync();
                if (props == null) return 0;
                return (long)props.Size;
            }
            catch
            {
                return 0;
            }
        }

        private async System.Threading.Tasks.Task DownloadMessageMediaWithRetryAsync(ChatMessageViewModel msg)
        {
            Exception lastError = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Debug.WriteLine("TG_FILE_DOWNLOAD_ATTEMPT id=" + (msg == null ? 0 : msg.Id).ToString() +
                                    " attempt=" + (attempt + 1).ToString() +
                                    " dc=" + (msg == null ? 0 : msg.MediaDcId).ToString());
                    await TelegramService.Instance.DownloadMessageMediaAsync(_chat, msg);
                    if (!string.IsNullOrEmpty(msg.MediaFileUri))
                    {
                        Debug.WriteLine("TG_FILE_DOWNLOAD_ATTEMPT_OK id=" + msg.Id.ToString() +
                                        " attempt=" + (attempt + 1).ToString() +
                                        " uri=" + msg.MediaFileUri);
                        return;
                    }
                    lastError = new TimeoutException(string.IsNullOrEmpty(msg.MediaTitle) ? "Telegram did not return media file data." : msg.MediaTitle);
                    Debug.WriteLine("TG_FILE_DOWNLOAD_ATTEMPT_EMPTY id=" + (msg == null ? 0 : msg.Id).ToString() +
                                    " attempt=" + (attempt + 1).ToString() +
                                    " title=" + (msg == null ? "-" : (msg.MediaTitle ?? "-")));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("TG_FILE_DOWNLOAD_ATTEMPT_ERROR id=" + (msg == null ? 0 : msg.Id).ToString() +
                                    " attempt=" + (attempt + 1).ToString() +
                                    " error=" + ex.GetType().Name + ": " + (ex.Message ?? string.Empty));
                    if (!IsRetryableFileDownloadException(ex)) throw;
                    lastError = ex;
                }

                if (attempt < 2)
                    await System.Threading.Tasks.Task.Delay(700 + attempt * 900);
            }

            if (lastError != null) throw lastError;
        }

        private async System.Threading.Tasks.Task<StorageFile> GetDownloadedMediaStorageFileAsync(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return null;

            // 1) Legacy/app-owned chat_media URI.
            var fileName = ExtractLocalMediaFileName(uri);
            if (!string.IsNullOrEmpty(fileName))
            {
                var chatMediaFile = await GetChatMediaStorageFileAsync(fileName);
                if (chatMediaFile != null) return chatMediaFile;
            }

            // 2) TDLib returns completed documents as file:///C:/... paths from
            // its own files_directory. GetFileFromApplicationUriAsync can't
            // open those and throws ArgumentException on Windows 10 Mobile.
            try
            {
                Uri parsed;
                if (Uri.TryCreate(uri, UriKind.Absolute, out parsed) &&
                    string.Equals(parsed.Scheme, "file", StringComparison.OrdinalIgnoreCase))
                {
                    var localPath = parsed.LocalPath;
                    if (!string.IsNullOrEmpty(localPath))
                    {
                        // Uri.LocalPath may contain escaped characters.
                        try { localPath = Uri.UnescapeDataString(localPath); }
                        catch { }

                        var file = await StorageFile.GetFileFromPathAsync(localPath);
                        if (file != null) return file;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_FILE_RESOLVE_FILEURI_ERROR uri=" + uri +
                                " error=" + ex.GetType().Name + ": " + (ex.Message ?? string.Empty));
            }

            // 3) Raw absolute Windows path, useful if a future TDLib mapping
            // returns C:\... instead of file:///C:/...
            try
            {
                var candidatePath = uri;
                if (candidatePath.Length > 3 &&
                    candidatePath[1] == ':' &&
                    (candidatePath[2] == '\\' || candidatePath[2] == '/'))
                {
                    var file = await StorageFile.GetFileFromPathAsync(candidatePath.Replace('/', '\\'));
                    if (file != null) return file;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_FILE_RESOLVE_PATH_ERROR uri=" + uri +
                                " error=" + ex.GetType().Name + ": " + (ex.Message ?? string.Empty));
            }

            // 4) ms-appx/ms-appdata and any other application URI supported by UWP.
            try
            {
                Uri appUri;
                if (Uri.TryCreate(uri, UriKind.Absolute, out appUri) &&
                    (string.Equals(appUri.Scheme, "ms-appx", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(appUri.Scheme, "ms-appdata", StringComparison.OrdinalIgnoreCase)))
                {
                    return await StorageFile.GetFileFromApplicationUriAsync(appUri);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_FILE_RESOLVE_APPURI_ERROR uri=" + uri +
                                " error=" + ex.GetType().Name + ": " + (ex.Message ?? string.Empty));
            }

            return null;
        }

        private async System.Threading.Tasks.Task<StorageFile> GetChatMediaStorageFileAsync(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            try
            {
                var folderItem = await ApplicationData.Current.LocalFolder.TryGetItemAsync("chat_media");
                var folder = folderItem as StorageFolder;
                if (folder == null) return null;

                return await folder.GetFileAsync(fileName);
            }
            catch
            {
                return null;
            }
        }

        private bool IsRetryableFileDownloadException(Exception ex)
        {
            return ex is TimeoutException || ex is System.IO.InvalidDataException || IsAbortedIoException(ex);
        }

        private bool IsAbortedIoException(Exception ex)
        {
            var com = ex as COMException;
            if (com == null) return false;

            var message = com.Message ?? string.Empty;
            return message.IndexOf("I/O operation has been aborted", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("operation has been aborted", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("thread exit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("application request", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string BuildDownloadFileName(ChatMessageViewModel msg, StorageFile sourceFile)
        {
            var name = msg == null ? null : msg.MediaFileName;
            if (string.IsNullOrEmpty(name) && sourceFile != null) name = sourceFile.Name;
            return SanitizeDownloadFileName(name, "file");
        }

        private string SanitizeDownloadFileName(string name, string fallback)
        {
            if (string.IsNullOrEmpty(name)) name = fallback;
            if (string.IsNullOrEmpty(name)) name = "file";

            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return name;
        }

        private async void StructuredMediaItem_Click(object sender, RoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var item = fe == null ? null : fe.DataContext as StructuredMediaItemViewModel;
            await HandleStructuredMediaItemClickAsync(item);
        }

        private async void PollControl_VoteRequested(object sender, PollVoteRequestedEventArgs e)
        {
            await HandleStructuredMediaItemClickAsync(e == null ? null : e.Option);
        }

        private async void PollControl_AddOptionRequested(object sender, EventArgs e)
        {
            var fe = sender as FrameworkElement;
            var msg = fe == null ? null : fe.DataContext as ChatMessageViewModel;
            if (msg == null || _chat == null || msg.Id <= 0 || !msg.PollCanAddOption || msg.StructuredMediaIsClosed) return;

            var input = new TextBox
            {
                MaxLength = 100,
                PlaceholderText = "Option",
                AcceptsReturn = false
            };

            var dialog = new ContentDialog
            {
                Title = "Add option",
                Content = input,
                PrimaryButtonText = "Add",
                SecondaryButtonText = "Cancel",
                FullSizeDesired = false
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var text = input.Text == null ? "" : input.Text.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            try
            {
                await TelegramService.Instance.AddPollOptionAsync(_chat, msg, text);
                await RefreshStructuredMessageAsync(msg);
            }
            catch
            {
                await RefreshStructuredMessageAsync(msg);
            }
        }

        private async System.Threading.Tasks.Task HandleStructuredMediaItemClickAsync(StructuredMediaItemViewModel item)
        {
            if (item == null || item.IsBusy) return;

            var msg = item.OwnerMessage;
            if (msg == null || _chat == null || msg.Id <= 0) return;

            item.IsBusy = true;
            try
            {
                if (msg.MediaKind == "poll")
                {
                    var options = BuildPollVoteOptions(msg, item);
                    if (!msg.StructuredMediaAllowsMultiple && options.Count == 0 && !msg.PollAllowsRevoting) return;

                    ApplyPollSelection(msg, item);
                    MarkPendingPollLocalSelection(msg);
                    await TelegramService.Instance.SendPollVoteAsync(_chat, msg, options);
                    await System.Threading.Tasks.Task.Delay(350);
                    await RefreshStructuredMessageAsync(msg);
                }
                else if (msg.MediaKind == "todo")
                {
                    var completed = !item.IsCompleted;
                    item.IsCompleted = completed;
                    await TelegramService.Instance.ToggleTodoCompletedAsync(_chat, msg, item.TodoId, completed);
                    await RefreshStructuredMessageAsync(msg);
                }
            }
            catch
            {
                await RefreshStructuredMessageAsync(msg);
            }
            finally
            {
                item.IsBusy = false;
            }
        }

        private async System.Threading.Tasks.Task RefreshStructuredMessageAsync(ChatMessageViewModel msg)
        {
            if (msg == null || _chat == null || msg.Id <= 0) return;
            try
            {
                var updates = await TelegramService.Instance.GetMessagesByIdAsync(_chat, msg.Id);
                if (updates == null || updates.Count == 0 || updates[0] == null) return;
                UpdateExistingMessageState(updates[0]);
            }
            catch
            {
            }
        }

        private List<int> BuildPollVoteOptions(ChatMessageViewModel msg, StructuredMediaItemViewModel clicked)
        {
            var result = new List<int>();
            if (msg == null || clicked == null) return result;

            if (!msg.StructuredMediaAllowsMultiple)
            {
                if (clicked.IsSelected && msg.PollAllowsRevoting) return result;
                result.Add(clicked.PollOptionId);
                return result;
            }

            var willSelect = !clicked.IsSelected;
            if (msg.StructuredMediaItems != null)
            {
                for (var i = 0; i < msg.StructuredMediaItems.Count; i++)
                {
                    var item = msg.StructuredMediaItems[i];
                    if (item == null) continue;
                    if (item == clicked)
                    {
                        if (willSelect) result.Add(item.PollOptionId);
                    }
                    else if (item.IsSelected)
                    {
                        result.Add(item.PollOptionId);
                    }
                }
            }
            return result;
        }

        private void ApplyPollSelection(ChatMessageViewModel msg, StructuredMediaItemViewModel clicked)
        {
            if (msg == null || clicked == null) return;
            ClearPollVotePercentages(msg);

            var hadSelectedBefore = false;
            if (msg.StructuredMediaItems != null)
            {
                for (var i = 0; i < msg.StructuredMediaItems.Count; i++)
                {
                    var item = msg.StructuredMediaItems[i];
                    if (item != null && item.IsSelected) hadSelectedBefore = true;
                }
            }

            if (!msg.StructuredMediaAllowsMultiple && msg.StructuredMediaItems != null)
            {
                var clickedWasSelected = clicked.IsSelected;
                for (var i = 0; i < msg.StructuredMediaItems.Count; i++)
                {
                    var item = msg.StructuredMediaItems[i];
                    if (item == null) continue;
                    if (item != clicked && item.IsSelected && item.Voters > 0) item.Voters--;
                    item.IsSelected = clickedWasSelected && msg.PollAllowsRevoting ? false : item == clicked;
                }

                if (clickedWasSelected && msg.PollAllowsRevoting)
                {
                    if (clicked.Voters > 0) clicked.Voters--;
                    if (msg.StructuredMediaTotalVoters > 0) msg.StructuredMediaTotalVoters--;
                }
                else
                {
                    if (!clickedWasSelected && clicked.Voters >= 0) clicked.Voters++;
                    if (!hadSelectedBefore && !clickedWasSelected) msg.StructuredMediaTotalVoters++;
                }
                UpdatePollItemsTotal(msg);
                msg.NotifyPollDataChanged();
                return;
            }

            clicked.IsSelected = !clicked.IsSelected;
            if (clicked.Voters >= 0) clicked.Voters += clicked.IsSelected ? 1 : -1;

            var hasSelectedAfter = false;
            if (msg.StructuredMediaItems != null)
            {
                for (var i = 0; i < msg.StructuredMediaItems.Count; i++)
                {
                    var item = msg.StructuredMediaItems[i];
                    if (item != null && item.IsSelected) hasSelectedAfter = true;
                }
            }

            if (!hadSelectedBefore && hasSelectedAfter) msg.StructuredMediaTotalVoters++;
            else if (hadSelectedBefore && !hasSelectedAfter && msg.StructuredMediaTotalVoters > 0) msg.StructuredMediaTotalVoters--;

            UpdatePollItemsTotal(msg);
            msg.NotifyPollDataChanged();
        }

        private void ClearPollVotePercentages(ChatMessageViewModel msg)
        {
            if (msg == null || msg.StructuredMediaItems == null) return;
            for (var i = 0; i < msg.StructuredMediaItems.Count; i++)
            {
                var item = msg.StructuredMediaItems[i];
                if (item != null) item.VotePercentage = -1;
            }
        }

        private void MarkPendingPollLocalSelection(ChatMessageViewModel msg)
        {
            if (msg == null || msg.Id <= 0) return;
            _pendingPollLocalSelectionUntil[msg.Id] = DateTime.UtcNow.AddSeconds(2);
        }

        private void RemovePendingPollLocalSelection(int messageId)
        {
            if (messageId <= 0) return;
            _pendingPollLocalSelectionUntil.Remove(messageId);
        }

        private bool ShouldIgnoreStalePollRefresh(ChatMessageViewModel existing, ChatMessageViewModel incoming)
        {
            if (existing == null || incoming == null || existing.Id <= 0) return false;
            if (!string.Equals(existing.MediaKind, "poll", StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(incoming.MediaKind, "poll", StringComparison.OrdinalIgnoreCase)) return false;

            DateTime until;
            if (!_pendingPollLocalSelectionUntil.TryGetValue(existing.Id, out until)) return false;
            if (until <= DateTime.UtcNow)
            {
                _pendingPollLocalSelectionUntil.Remove(existing.Id);
                return false;
            }

            return !string.Equals(GetPollSelectionSignature(existing), GetPollSelectionSignature(incoming), StringComparison.Ordinal);
        }

        private static string GetPollSelectionSignature(ChatMessageViewModel msg)
        {
            if (msg == null || msg.StructuredMediaItems == null || msg.StructuredMediaItems.Count == 0) return string.Empty;
            var selected = new List<int>();
            for (var i = 0; i < msg.StructuredMediaItems.Count; i++)
            {
                var item = msg.StructuredMediaItems[i];
                if (item != null && item.IsSelected)
                    selected.Add(item.PollOptionId);
            }
            selected.Sort();
            return string.Join(",", selected);
        }

        private void UpdatePollItemsTotal(ChatMessageViewModel msg)
        {
            if (msg == null || msg.StructuredMediaItems == null) return;
            for (var i = 0; i < msg.StructuredMediaItems.Count; i++)
            {
                var item = msg.StructuredMediaItems[i];
                if (item != null) item.TotalVoters = msg.StructuredMediaTotalVoters;
            }
        }

        private async System.Threading.Tasks.Task DownloadGroupedMessageMediaAsync(ChatMessageViewModel msg)
        {
            if (msg == null || msg.MediaItems == null || msg.MediaItems.Count == 0) return;

            for (var i = 0; i < msg.MediaItems.Count; i++)
            {
                var item = msg.MediaItems[i];
                if (item == null) continue;

                if (!string.IsNullOrEmpty(item.MediaFileUri))
                {
                    item.IsMediaDownloading = false;
                    item.MediaErrorText = string.Empty;
                    item.HasPlaybackError = false;
                    continue;
                }

                try
                {
                    item.IsMediaDownloading = true;
                    if (IsVideoMediaKind(item.MediaKind)) BeginVideoLoadingDisplayRequest();
                    try
                    {
                        await TelegramService.Instance.DownloadMessageMediaAsync(item);
                    }
                    finally
                    {
                        if (IsVideoMediaKind(item.MediaKind)) EndVideoLoadingDisplayRequest();
                    }
                    item.MediaTitle = string.Empty;
                    item.MediaErrorText = string.Empty;
                    item.HasPlaybackError = false;
                }
                catch
                {
                    item.MediaTitle = string.Empty;
                    item.MediaErrorText = string.Empty;
                    item.HasPlaybackError = false;
                }
                finally
                {
                    item.IsMediaDownloading = false;
                    NormalizeGroupedMessageContainer(msg);
                    msg.NotifyContentChanged();
                }
            }
        }

        private void FfmpegAudioPlayer_SourceRequested(object sender, FfmpegAudioSourceRequestedEventArgs e)
        {
            if (e == null) return;
            e.ReloadTask = ReloadAudioSourceForPlaybackAsync(e.DataContext);
        }

        private async void FfmpegAudioPlayer_PlaybackEnded(object sender, FfmpegAudioPlaybackEndedEventArgs e)
        {
            if (e == null) return;
            await TryPlayNextSequentialAudioAsync(e.DataContext);
        }

        private void ChatMusicPlayer_PlaybackStarted(object sender, FfmpegAudioPlaybackEndedEventArgs e)
        {
            RefreshChatAudioTracks();
            _currentChatAudioTrackIndex = FindChatAudioTrackIndex(e == null ? null : e.DataContext);
        }

        private async void ChatMusicPlayer_PlaybackEnded(object sender, FfmpegAudioPlaybackEndedEventArgs e)
        {
            await TryPlayAdjacentChatAudioAsync(e == null ? null : e.DataContext, 1);
        }

        private async void ChatMusicPlayer_NextRequested(object sender, FfmpegAudioPlaybackEndedEventArgs e)
        {
            await TryPlayAdjacentChatAudioAsync(e == null ? null : e.DataContext, 1);
        }

        private async void ChatMusicPlayer_PreviousRequested(object sender, FfmpegAudioPlaybackEndedEventArgs e)
        {
            await TryPlayAdjacentChatAudioAsync(e == null ? null : e.DataContext, -1);
        }

        private async System.Threading.Tasks.Task TryPlayAdjacentChatAudioAsync(object currentDataContext, int direction)
        {
            RefreshChatAudioTracks();
            if (_currentChatAudioTracks == null || _currentChatAudioTracks.Count == 0) return;

            var index = FindChatAudioTrackIndex(currentDataContext);
            if (index < 0) index = _currentChatAudioTrackIndex;
            if (index < 0) return;

            await PlayChatAudioAtAsync(index + direction);
        }

        private void RefreshChatAudioTracks()
        {
            var tracks = new List<object>();
            if (_messages != null)
            {
                for (var i = 0; i < _messages.Count; i++)
                {
                    var message = _messages[i] as ChatMessageViewModel;
                    if (IsChatAudioTrack(message))
                        tracks.Add(message);
                }
            }

            _currentChatAudioTracks = tracks;
        }

        private bool IsChatAudioTrack(ChatMessageViewModel message)
        {
            if (message == null) return false;
            if (!message.HasMedia) return false;
            if (message.MediaItems != null && message.MediaItems.Count > 0) return false;
            if (message.HasPlaybackError) return false;

            return string.Equals(message.MediaKind, "audio", StringComparison.OrdinalIgnoreCase);
        }

        private int FindChatAudioTrackIndex(object track)
        {
            if (track == null || _currentChatAudioTracks == null) return -1;

            var trackMessage = GetMessageFromAudioDataContext(track);
            for (var i = 0; i < _currentChatAudioTracks.Count; i++)
            {
                var candidate = _currentChatAudioTracks[i] as ChatMessageViewModel;
                if (candidate == null) continue;
                if (object.ReferenceEquals(candidate, track)) return i;
                if (trackMessage != null)
                {
                    if (object.ReferenceEquals(candidate, trackMessage)) return i;
                    if (trackMessage.Id != 0 && candidate.Id == trackMessage.Id) return i;
                }
            }

            return -1;
        }

        private async System.Threading.Tasks.Task PlayChatAudioAtAsync(int index)
        {
            if (_currentChatAudioTracks == null || _currentChatAudioTracks.Count == 0) return;

            if (index >= _currentChatAudioTracks.Count) index = 0;
            if (index < 0) index = _currentChatAudioTracks.Count - 1;

            _currentChatAudioTrackIndex = index;
            var track = _currentChatAudioTracks[index];
            if (track == null || MessageList == null) return;

            try
            {
                MessageList.ScrollIntoView(track);

                FfmpegMusicPlayerControl player = null;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    await System.Threading.Tasks.Task.Delay(attempt == 0 ? 80 : 140);
                    TryUpdateMessageListLayout("PlayChatAudioAtAsync");
                    player = FindMusicPlayerForDataContext(track);
                    if (player != null) break;
                }

                if (player != null)
                    await player.PlayAsync();
            }
            catch
            {
            }
        }

        private async System.Threading.Tasks.Task TryPlayNextSequentialAudioAsync(object currentDataContext)
        {
            var nextMessage = FindNextSequentialAudioMessage(currentDataContext);
            if (nextMessage == null) return;

            try
            {
                MessageList.ScrollIntoView(nextMessage);

                FfmpegAudioPlayerControl player = null;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    await System.Threading.Tasks.Task.Delay(attempt == 0 ? 80 : 140);
                    TryUpdateMessageListLayout("TryPlayNextSequentialAudioAsync");
                    player = FindAudioPlayerForMessage(nextMessage);
                    if (player != null) break;
                }

                if (player != null)
                    player.StartPlaybackFromExternal();
            }
            catch
            {
            }
        }

        private ChatMessageViewModel FindNextSequentialAudioMessage(object currentDataContext)
        {
            if (_messages == null || _messages.Count == 0) return null;

            var currentMessage = GetMessageFromAudioDataContext(currentDataContext);
            if (currentMessage == null) return null;

            var currentIndex = IndexOfMessage(currentMessage);
            if (currentIndex < 0) return null;
            if (!IsSequentialVoiceMessage(currentMessage)) return null;

            for (var i = currentIndex + 1; i < _messages.Count; i++)
            {
                var candidate = _messages[i] as ChatMessageViewModel;
                if (candidate == null) continue;

                if (IsSequentialVoiceMessage(candidate))
                    return candidate;

                break;
            }

            return null;
        }

        private ChatMessageViewModel GetMessageFromAudioDataContext(object dataContext)
        {
            var message = dataContext as ChatMessageViewModel;
            if (message != null) return message;

            var item = dataContext as ChatMediaItemViewModel;
            if (item != null) return item.OwnerMessage;

            return null;
        }

        private int IndexOfMessage(ChatMessageViewModel message)
        {
            if (message == null || _messages == null) return -1;

            for (var i = 0; i < _messages.Count; i++)
            {
                var candidate = _messages[i] as ChatMessageViewModel;
                if (candidate == null) continue;
                if (object.ReferenceEquals(candidate, message)) return i;
                if (message.Id != 0 && candidate.Id == message.Id) return i;
            }

            return -1;
        }

        private bool IsSequentialVoiceMessage(ChatMessageViewModel message)
        {
            if (message == null) return false;
            if (!message.HasMedia) return false;
            if (message.MediaItems != null && message.MediaItems.Count > 0) return false;

            return string.Equals(message.MediaKind, "voice", StringComparison.OrdinalIgnoreCase);
        }

        private FfmpegAudioPlayerControl FindAudioPlayerForMessage(ChatMessageViewModel message)
        {
            if (message == null || MessageList == null) return null;

            var container = MessageList.ContainerFromItem(message) as DependencyObject;
            if (container == null) return null;

            return FindVisualChildForDataContext<FfmpegAudioPlayerControl>(container, message);
        }

        private FfmpegMusicPlayerControl FindMusicPlayerForDataContext(object dataContext)
        {
            if (dataContext == null || MessageList == null) return null;

            var container = MessageList.ContainerFromItem(dataContext) as DependencyObject;
            if (container == null) return null;

            return FindVisualChildForDataContext<FfmpegMusicPlayerControl>(container, dataContext);
        }

        private T FindVisualChildForDataContext<T>(DependencyObject root, object dataContext) where T : FrameworkElement
        {
            if (root == null) return null;

            var element = root as T;
            if (element != null && object.ReferenceEquals(element.DataContext, dataContext)) return element;

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var result = FindVisualChildForDataContext<T>(child, dataContext);
                if (result != null) return result;
            }

            return null;
        }

        private async System.Threading.Tasks.Task<bool> ReloadAudioSourceForPlaybackAsync(object dataContext)
        {
            try
            {
                var msg = dataContext as ChatMessageViewModel;
                if (msg != null)
                {
                    msg.IsMediaDownloading = true;
                    await TelegramService.Instance.DownloadMessageMediaAsync(_chat, msg);
                    await TryLoadAudioPreviewAsync(msg);
                    msg.IsMediaDownloading = false;
                    msg.HasPlaybackError = false;
                    msg.MediaErrorText = string.Empty;
                    return !string.IsNullOrEmpty(msg.MediaFileUri);
                }

                var item = dataContext as ChatMediaItemViewModel;
                if (item != null)
                {
                    item.IsMediaDownloading = true;
                    await TelegramService.Instance.DownloadMessageMediaAsync(item);
                    await TryLoadAudioPreviewAsync(item);
                    item.IsMediaDownloading = false;
                    item.HasPlaybackError = false;
                    item.MediaErrorText = string.Empty;
                    return !string.IsNullOrEmpty(item.MediaFileUri);
                }
            }
            catch
            {
                var msg = dataContext as ChatMessageViewModel;
                if (msg != null) msg.IsMediaDownloading = false;
                var item = dataContext as ChatMediaItemViewModel;
                if (item != null) item.IsMediaDownloading = false;
            }
            return false;
        }

        private async System.Threading.Tasks.Task TryLoadAudioPreviewAsync(object dataContext)
        {
            try
            {
                var msg = dataContext as ChatMessageViewModel;
                if (msg != null)
                {
                    if (msg.MediaKind == "audio" && ShouldLoadVideoPreview(msg))
                        await TelegramService.Instance.DownloadMessageVideoPreviewAsync(_chat, msg);
                    return;
                }

                var item = dataContext as ChatMediaItemViewModel;
                if (item != null && item.MediaKind == "audio" && ShouldLoadVideoPreview(item))
                    await TelegramService.Instance.DownloadMessageVideoPreviewAsync(item);
            }
            catch
            {
            }
        }

        private string ExtractLocalMediaFileName(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            const string prefix = "ms-appdata:///local/chat_media/";
            if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var name = uri.Substring(prefix.Length);
            try { return Uri.UnescapeDataString(name); }
            catch { return name; }
        }

        private void MediaElement_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            if (fe == null) return;
            var item = fe.DataContext as ChatMediaItemViewModel;
            if (item != null)
            {
                if (IsVideoMediaKind(item.MediaKind))
                {
                    item.HasPlaybackError = false;
                    item.MediaErrorText = string.Empty;
                    return;
                }
                if (item.MediaKind == "audio" || item.MediaKind == "voice")
                {
                    item.HasPlaybackError = false;
                    item.MediaErrorText = string.Empty;
                    return;
                }
                item.HasPlaybackError = true;
                item.MediaErrorText = string.Empty;
                return;
            }

            var msg = fe.DataContext as ChatMessageViewModel;
            if (msg == null) return;

            if (IsVideoMediaKind(msg.MediaKind))
            {
                msg.HasPlaybackError = false;
                msg.MediaErrorText = string.Empty;
                return;
            }
            if (msg.MediaKind == "audio" || msg.MediaKind == "voice")
            {
                msg.HasPlaybackError = false;
                msg.MediaErrorText = string.Empty;
                return;
            }

            msg.HasPlaybackError = true;
            msg.MediaErrorText = string.Empty;
        }

        private void RetryFailedFfmpegVideo(FrameworkElement playerElement, object dataContext)
        {
            var uri = GetVideoMediaFileUri(dataContext);
            var key = BuildFfmpegVideoKey(dataContext, uri);
            if (string.IsNullOrEmpty(key)) return;

            int retries;
            _ffmpegBlankVideoRetryCounts.TryGetValue(key, out retries);
            if (retries >= 3) return;
            _ffmpegBlankVideoRetryCounts[key] = retries + 1;

            Debug.WriteLine("TG_VIDEO_FFMPEG failed key=" + key +
                " retries=" + retries.ToString() +
                " downloading=" + IsVideoDownloadInProgress(dataContext).ToString() +
                " forceDecode=" + _ffmpegForceVideoDecodeKeys.Contains(key).ToString());

            ToggleFfmpegVideoDecodeMode(key);
            ResetFfmpegVideoKey(key);
            QueuePlayMediaElement(dataContext);
        }

        private void PhotoImage_Opened(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            if (image == null) return;
            if (ShouldKeepBottomDuringLayoutChange())
            {
                _stickToBottom = true;
                QueueBottomPinBurst();
            }
            QueueViewedAutoDownload(image.DataContext);
        }

        private void MediaPreviewImage_Failed(object sender, ExceptionRoutedEventArgs e)
        {
            var image = sender as Image;
            if (image == null) return;

            var msg = image.DataContext as ChatMessageViewModel;
            if (msg != null)
            {
                var key = BuildVideoPreviewKey(msg);
                msg.MediaPreviewUri = string.Empty;
                ScheduleVideoPreviewRetry(key);
                return;
            }

            var item = image.DataContext as ChatMediaItemViewModel;
            if (item == null) return;
            var itemKey = BuildVideoPreviewKey(item);
            item.MediaPreviewUri = string.Empty;
            ScheduleVideoPreviewRetry(itemKey);
        }

        private void MediaPreviewImage_Opened(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            if (image == null) return;

            var bitmap = image.Source as BitmapImage;
            if (bitmap == null || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return;

            var aspectRatio = (double)bitmap.PixelWidth / (double)bitmap.PixelHeight;
            if (aspectRatio <= 0.1 || double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio)) return;

            var keepBottom = ShouldKeepBottomDuringLayoutChange();
            var anchor = keepBottom ? null : CaptureScrollViewportAnchor();

            var msg = image.DataContext as ChatMessageViewModel;
            if (msg != null)
            {
                msg.SetMediaPreviewAspectRatio(aspectRatio);
                QueueViewportCorrectionAfterLayout(keepBottom, anchor);
                QueueViewedAutoDownload(msg);
                return;
            }

            var item = image.DataContext as ChatMediaItemViewModel;
            if (item != null)
            {
                item.SetMediaPreviewAspectRatio(aspectRatio);
                QueueViewportCorrectionAfterLayout(keepBottom, anchor);
                QueueViewedAutoDownload(item);
            }
        }

        private void ChatPhoto_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var image = sender as Image;
            if (image == null) return;

            var ignored = DownloadViewedPhotoAsync(image.DataContext, image.Source, true);
            e.Handled = true;
        }

        private void ShowPhotoOverlay(object dataContext, ImageSource tappedSource)
        {
            _photoOverlayImages.Clear();
            _photoOverlayUris.Clear();
            _photoOverlaySources.Clear();
            _photoOverlayIndicators.Clear();
            PhotoOverlayIndicatorPanel.Children.Clear();
            PhotoOverlayFlipView.SelectionChanged -= PhotoOverlayFlipView_SelectionChanged;

            var selectedUri = string.Empty;
            var item = dataContext as ChatMediaItemViewModel;
            var message = dataContext as ChatMessageViewModel;
            if (item != null)
            {
                selectedUri = GetPhotoDisplayUri(item);
                message = item.OwnerMessage;
            }
            else if (message != null)
            {
                selectedUri = GetPhotoDisplayUri(message);
            }

            var selectedIndex = 0;
            if (message != null && message.MediaItems != null && message.MediaItems.Count > 0)
            {
                for (var i = 0; i < message.MediaItems.Count; i++)
                {
                    var mediaItem = message.MediaItems[i];
                    if (mediaItem == null || mediaItem.MediaKind != "photo" || string.IsNullOrEmpty(mediaItem.MediaFileUri)) continue;
                    var displayUri = GetPhotoDisplayUri(mediaItem);

                    if (AddPhotoOverlayImage(displayUri, mediaItem) &&
                        !string.IsNullOrEmpty(selectedUri) &&
                        string.Equals(displayUri, selectedUri, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = _photoOverlayImages.Count - 1;
                    }
                }
            }
            else if (item != null && item.MediaKind == "photo")
            {
                AddPhotoOverlayImage(GetPhotoDisplayUri(item), item);
            }
            else if (message != null && message.MediaKind == "photo")
            {
                AddPhotoOverlayImage(GetPhotoDisplayUri(message), message);
            }

            if (_photoOverlayImages.Count == 0 && tappedSource != null)
            {
                _photoOverlayImages.Add(tappedSource);
                _photoOverlayUris.Add(string.Empty);
                _photoOverlaySources.Add(null);
            }

            if (_photoOverlayImages.Count == 0) return;

            if (selectedIndex < 0 || selectedIndex >= _photoOverlayImages.Count)
                selectedIndex = 0;

            PhotoOverlayFlipView.ItemsSource = null;
            PhotoOverlayFlipView.ItemsSource = _photoOverlayImages;
            PhotoOverlayFlipView.SelectedIndex = selectedIndex;

            for (var i = 0; i < _photoOverlayImages.Count; i++)
            {
                var ellipse = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Margin = new Thickness(5, 0, 5, 0),
                    Fill = i == selectedIndex
                        ? new SolidColorBrush(Colors.White)
                        : new SolidColorBrush(Color.FromArgb(255, 120, 120, 120))
                };
                _photoOverlayIndicators.Add(ellipse);
                PhotoOverlayIndicatorPanel.Children.Add(ellipse);
            }

            UpdatePhotoOverlayCounter(selectedIndex);
            PhotoOverlayFlipView.SelectionChanged += PhotoOverlayFlipView_SelectionChanged;
            PhotoOverlay.Visibility = Visibility.Visible;
        }

        private bool AddPhotoOverlayImage(string uri, object source)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            try
            {
                _photoOverlayImages.Add(new BitmapImage(new Uri(uri)));
                _photoOverlayUris.Add(uri);
                _photoOverlaySources.Add(source);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetPhotoDisplayUri(object source)
        {
            var item = source as ChatMediaItemViewModel;
            if (item != null) return string.IsNullOrEmpty(item.MediaFullUri) ? item.MediaFileUri : item.MediaFullUri;

            var message = source as ChatMessageViewModel;
            if (message != null) return string.IsNullOrEmpty(message.MediaFullUri) ? message.MediaFileUri : message.MediaFullUri;

            return string.Empty;
        }

        private static string ToFileUri(StorageFile file)
        {
            if (file == null || string.IsNullOrEmpty(file.Path)) return string.Empty;
            try { return new Uri(file.Path).AbsoluteUri; }
            catch { return "file:///" + file.Path.Replace("\\", "/"); }
        }

        private void PhotoOverlayScrollViewer_Loaded(object sender, RoutedEventArgs e)
        {
            UpdatePhotoOverlayScrollViewerViewport(sender as ScrollViewer, true);
        }

        private void PhotoOverlayScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdatePhotoOverlayScrollViewerViewport(sender as ScrollViewer, false);
        }

        private void UpdatePhotoOverlayScrollViewerViewport(ScrollViewer viewer, bool resetZoom)
        {
            if (viewer == null) return;

            var width = viewer.ActualWidth;
            var height = viewer.ActualHeight;
            if (width <= 0 && PhotoOverlay != null) width = PhotoOverlay.ActualWidth;
            if (height <= 0 && PhotoOverlay != null) height = PhotoOverlay.ActualHeight;
            if (width <= 0 || height <= 0) return;

            var root = viewer.Content as Grid;
            if (root == null) return;

            root.Width = width;
            root.Height = height;

            for (var i = 0; i < root.Children.Count; i++)
            {
                var image = root.Children[i] as Image;
                if (image == null) continue;

                image.MaxWidth = width;
                image.MaxHeight = height;
                image.ClearValue(FrameworkElement.WidthProperty);
                image.ClearValue(FrameworkElement.HeightProperty);
            }

            if (resetZoom)
                viewer.ChangeView(0.0, 0.0, 1.0f, true);
        }

        private void PhotoOverlayFlipView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedIndex = PhotoOverlayFlipView.SelectedIndex;
            if (selectedIndex < 0 || selectedIndex >= _photoOverlayIndicators.Count) return;

            for (var i = 0; i < _photoOverlayIndicators.Count; i++)
            {
                _photoOverlayIndicators[i].Fill = i == selectedIndex
                    ? new SolidColorBrush(Colors.White)
                    : new SolidColorBrush(Color.FromArgb(255, 120, 120, 120));
            }

            UpdatePhotoOverlayCounter(selectedIndex);
        }

        private void UpdatePhotoOverlayCounter(int index)
        {
            if (_photoOverlayImages.Count <= 0)
            {
                PhotoOverlayCounter.Text = string.Empty;
                return;
            }

            PhotoOverlayCounter.Text = (index + 1).ToString() + " / " + _photoOverlayImages.Count.ToString();
        }

        private void ClosePhotoOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            ClosePhotoOverlay();
        }

        private void PhotoOverlayBackground_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ClosePhotoOverlay();
            e.Handled = true;
        }

        private async void DownloadPhotoOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            var index = PhotoOverlayFlipView.SelectedIndex;
            if (index < 0 || index >= _photoOverlayUris.Count) return;

            var uri = _photoOverlayUris[index];
            if (string.IsNullOrEmpty(uri)) return;

            try
            {
                var source = index >= 0 && index < _photoOverlaySources.Count ? _photoOverlaySources[index] : null;
                var sourceFile = await GetPhotoOverlayOriginalStorageFileAsync(source);
                if (sourceFile == null)
                    sourceFile = await GetPhotoOverlayStorageFileAsync(uri);
                if (sourceFile == null) return;

                var targetName = GetPhotoOverlaySuggestedFileName(sourceFile, uri) + GetPhotoOverlayFileExtension(sourceFile, uri);
                await CopyStorageFileToDownloadsAsync(sourceFile, targetName, null);
            }
            catch (COMException ex)
            {
                if (!IsAbortedIoException(ex))
                    await ShowChatAlertAsync("Photo save error", AlertErrorMessage(ex, "Could not save this photo."));
            }
            catch
            {
                await ShowChatAlertAsync("Photo save error", "Could not save this photo.");
            }
        }

        private async System.Threading.Tasks.Task<StorageFile> GetPhotoOverlayOriginalStorageFileAsync(object source)
        {
            try
            {
                var item = source as ChatMediaItemViewModel;
                if (item != null && item.MediaKind == "photo")
                    return await TelegramService.Instance.DownloadOriginalPhotoAsync(item);

                var message = source as ChatMessageViewModel;
                if (message != null && message.MediaKind == "photo")
                    return await TelegramService.Instance.DownloadOriginalPhotoAsync(_chat, message);
            }
            catch
            {
            }
            return null;
        }

        private async System.Threading.Tasks.Task<StorageFile> GetPhotoOverlayStorageFileAsync(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;

            var fileName = ExtractLocalMediaFileName(uri);
            if (!string.IsNullOrEmpty(fileName))
                return await GetChatMediaStorageFileAsync(fileName);

            try
            {
                return await StorageFile.GetFileFromApplicationUriAsync(new Uri(uri));
            }
            catch
            {
            }

            try
            {
                fileName = ExtractLocalMediaFileName(uri);
                if (string.IsNullOrEmpty(fileName)) return null;
                return await GetChatMediaStorageFileAsync(fileName);
            }
            catch
            {
                return null;
            }
        }

        private string GetPhotoOverlaySuggestedFileName(StorageFile sourceFile, string uri)
        {
            var name = sourceFile == null ? null : sourceFile.Name;
            if (string.IsNullOrEmpty(name)) name = ExtractLocalMediaFileName(uri);
            if (string.IsNullOrEmpty(name))
            {
                try
                {
                    var parsed = new Uri(uri);
                    name = System.IO.Path.GetFileName(parsed.LocalPath);
                }
                catch
                {
                    name = string.Empty;
                }
            }

            if (string.IsNullOrEmpty(name)) name = "photo";
            var withoutExtension = System.IO.Path.GetFileNameWithoutExtension(name);
            return string.IsNullOrEmpty(withoutExtension) ? "photo" : withoutExtension;
        }

        private string GetPhotoOverlayFileExtension(StorageFile sourceFile, string uri)
        {
            var name = sourceFile == null ? null : sourceFile.Name;
            if (string.IsNullOrEmpty(name)) name = ExtractLocalMediaFileName(uri);
            if (string.IsNullOrEmpty(name))
            {
                try
                {
                    var parsed = new Uri(uri);
                    name = parsed.LocalPath;
                }
                catch
                {
                    name = string.Empty;
                }
            }

            var extension = System.IO.Path.GetExtension(name);
            if (string.IsNullOrEmpty(extension)) extension = ".jpg";
            return extension;
        }

        private void ClosePhotoOverlay()
        {
            PhotoOverlay.Visibility = Visibility.Collapsed;
            PhotoOverlayFlipView.SelectionChanged -= PhotoOverlayFlipView_SelectionChanged;
            PhotoOverlayFlipView.ItemsSource = null;
            PhotoOverlayIndicatorPanel.Children.Clear();
            _photoOverlayImages.Clear();
            _photoOverlayUris.Clear();
            _photoOverlaySources.Clear();
            _photoOverlayIndicators.Clear();
            PhotoOverlayCounter.Text = string.Empty;
        }

        private void MessageRoot_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState == HoldingState.Started)
            {
                e.Handled = true;
                return;
            }
            if (e.HoldingState != HoldingState.Completed) return;

            var fe = sender as FrameworkElement;
            if (fe == null) return;
            if (ShowMessageActions(fe, e.GetPosition(fe)))
                _suppressMessageRightTappedUntilTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
            e.Handled = true;
        }

        private void MessageRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var msg = element == null ? null : element.Tag as ChatMessageViewModel;
            if (msg == null || !msg.CanReply || _chat == null || !_chat.CanSendMessages)
            {
                ResetMessageSwipe(false, e == null ? null : e.Pointer);
                return;
            }

            _messageSwipeTracking = true;
            _messageSwipeActive = false;
            _messageSwipeElement = element;
            _messageSwipeMessage = msg;
            _messageSwipeStartPoint = e.GetCurrentPoint(element).Position;

            var transform = GetMessageSwipeTransform(element);
            if (transform != null) transform.X = 0;
        }

        private void MessageRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_messageSwipeTracking || _messageSwipeElement == null || e == null) return;

            var point = e.GetCurrentPoint(_messageSwipeElement).Position;
            var deltaX = point.X - _messageSwipeStartPoint.X;
            var deltaY = point.Y - _messageSwipeStartPoint.Y;
            var absX = Math.Abs(deltaX);
            var absY = Math.Abs(deltaY);

            if (!_messageSwipeActive)
            {
                if (absY > 14 && absY > absX)
                {
                    ResetMessageSwipe(false, e.Pointer);
                    return;
                }

                if (deltaX >= -18 || absX <= absY * 1.35)
                    return;

                _messageSwipeActive = true;
                try { _messageSwipeElement.CapturePointer(e.Pointer); }
                catch { }
            }

            var transform = GetMessageSwipeTransform(_messageSwipeElement);
            if (transform != null)
                transform.X = Math.Max(-72.0, Math.Min(0.0, deltaX));

            e.Handled = true;
        }

        private void MessageRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_messageSwipeTracking)
            {
                ResetMessageSwipe(false, e == null ? null : e.Pointer);
                return;
            }

            var wasActive = _messageSwipeActive;
            var shouldReply = false;
            if (_messageSwipeActive && _messageSwipeElement != null && e != null)
            {
                var point = e.GetCurrentPoint(_messageSwipeElement).Position;
                shouldReply = point.X - _messageSwipeStartPoint.X <= -54;
            }

            var msg = _messageSwipeMessage;
            ResetMessageSwipe(false, e == null ? null : e.Pointer);

            if (shouldReply && msg != null)
                BeginReplyToMessage(msg);

            if (e != null && wasActive) e.Handled = true;
        }

        private void MessageRoot_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            ResetMessageSwipe(false, e == null ? null : e.Pointer);
        }

        private TranslateTransform GetMessageSwipeTransform(FrameworkElement element)
        {
            if (element == null) return null;

            var transform = element.RenderTransform as TranslateTransform;
            if (transform == null)
            {
                transform = new TranslateTransform();
                element.RenderTransform = transform;
            }

            return transform;
        }

        private void ResetMessageSwipe(bool keepOffset, Pointer pointer)
        {
            if (_messageSwipeElement != null)
            {
                if (!keepOffset)
                {
                    var transform = GetMessageSwipeTransform(_messageSwipeElement);
                    if (transform != null) transform.X = 0;
                }

                if (pointer != null)
                {
                    try { _messageSwipeElement.ReleasePointerCapture(pointer); }
                    catch { }
                }
            }

            _messageSwipeTracking = false;
            _messageSwipeActive = false;
            _messageSwipeElement = null;
            _messageSwipeMessage = null;
        }

        private void MessageRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (IsMessageRightTappedSuppressed())
            {
                e.Handled = true;
                return;
            }

            var fe = sender as FrameworkElement;
            if (fe == null) return;
            ShowMessageActions(fe, e.GetPosition(fe));
            e.Handled = true;
        }

        private bool IsMessageRightTappedSuppressed()
        {
            if (_messageActionsFlyoutOpen) return true;
            if (_suppressMessageRightTappedUntilTicks <= 0) return false;

            var now = Stopwatch.GetTimestamp();
            if (now < _suppressMessageRightTappedUntilTicks) return true;

            _suppressMessageRightTappedUntilTicks = 0;
            return false;
        }

        private bool ShowMessageActions(FrameworkElement fe, Point showAt)
        {
            if (_messageActionsFlyoutOpen) return false;

            var msg = fe == null ? null : fe.Tag as ChatMessageViewModel;
            if (msg == null) return false;

            var flyout = new Flyout();
            var panel = new StackPanel
            {
                MinWidth = 220,
                MaxWidth = 300,
                Padding = new Thickness(6, 4, 6, 4)
            };

            ScrollViewer reactionStrip = null;
            if (CanShowReactAction(msg))
            {
                reactionStrip = CreateReactionStrip(msg, flyout);
                panel.Children.Add(reactionStrip);
            }

            if (CanShowReplyAction(msg))
                panel.Children.Add(CreateActionButton("Reply", msg, ReplyMenu_Click, flyout));
            if (CanShowPinAction(msg))
                panel.Children.Add(CreateActionButton(msg.IsPinned ? "Unpin" : "Pin", msg, PinMenu_Click, flyout));
            if (msg.CanCopyText)
                panel.Children.Add(CreateActionButton("Copy text", msg, CopyTextMenu_Click, flyout));
            if (CanShowForwardAction(msg))
                panel.Children.Add(CreateActionButton("Forward", msg, ForwardMenu_Click, flyout));
            if (CanShowDeleteAction(msg))
                panel.Children.Add(CreateActionButton("Delete", msg, DeleteMenu_Click, flyout));
            if (CanShowReadByAction(msg))
                panel.Children.Add(CreateActionButton("Read by", msg, ReadByMenu_Click, flyout));

            if (panel.Children.Count == 0) return false;

            var placementTarget = fe;
            Border placementAnchor = null;
            if (PageRoot != null)
            {
                try
                {
                    var pagePoint = fe.TransformToVisual(PageRoot).TransformPoint(showAt);
                    placementAnchor = new Border
                    {
                        Width = 1,
                        Height = 1,
                        Background = new SolidColorBrush(Colors.Transparent),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(pagePoint.X, pagePoint.Y, 0, 0),
                        IsHitTestVisible = false
                    };
                    Grid.SetRow(placementAnchor, 0);
                    Grid.SetRowSpan(placementAnchor, 3);
                    Canvas.SetZIndex(placementAnchor, 1000);
                    PageRoot.Children.Add(placementAnchor);
                    placementTarget = placementAnchor;
                }
                catch
                {
                    placementAnchor = null;
                    placementTarget = fe;
                }
            }

            flyout.Content = panel;
            flyout.Closed += delegate
            {
                _messageActionsFlyoutOpen = false;
                if (placementAnchor != null && PageRoot != null)
                    PageRoot.Children.Remove(placementAnchor);
            };
            _messageActionsFlyoutOpen = true;
            flyout.ShowAt(placementTarget);
            if (reactionStrip != null)
                LoadAvailableMessageReactionsAsync(msg, flyout, reactionStrip);
            return true;
        }

        private bool CanShowReplyAction(ChatMessageViewModel msg)
        {
            return msg != null && msg.CanReply && _chat != null && _chat.CanSendMessages && !msg.IsServiceMessage && msg.Id > 0;
        }

        private bool CanShowPinAction(ChatMessageViewModel msg)
        {
            return msg != null && msg.CanPin && _chat != null && !msg.IsServiceMessage && msg.Id > 0;
        }

        private bool CanShowForwardAction(ChatMessageViewModel msg)
        {
            return msg != null && msg.CanForward && !msg.IsServiceMessage && msg.Id > 0 && _chat != null && !_chat.NoForwards;
        }

        private bool CanShowDeleteAction(ChatMessageViewModel msg)
        {
            return msg != null && msg.CanDelete && !msg.IsServiceMessage && msg.Id > 0 && _chat != null;
        }

        private bool CanShowReactAction(ChatMessageViewModel msg)
        {
            return msg != null && msg.CanReact && !msg.IsServiceMessage && msg.Id > 0;
        }

        private bool CanShowReadByAction(ChatMessageViewModel msg)
        {
            if (msg == null || _chat == null) return false;
            if (msg.Id <= 0 || !msg.IsOutgoing || msg.IsServiceMessage) return false;
            if (_chat.IsBroadcast || _chat.IsCommentsThread) return false;
            if (!_chat.IsGroup && !_chat.IsForumTopic) return false;
            if (_chat.MessageViewersUnavailable) return false;
            if (_chat.SubscriberCount > 100) return false;
            if (msg.HasCanGetViewersFlag && !msg.CanGetViewers) return false;
            return true;
        }

        private async void ReplyPreview_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var msg = fe == null ? null : fe.Tag as ChatMessageViewModel;
            if (msg == null || msg.ReplyToMessageId <= 0) return;

            var target = FindMessageById(msg.ReplyToMessageId);
            for (var i = 0; target == null && i < 5 && !_noMoreOlderMessages; i++)
            {
                await LoadOlderMessagesAsync();
                target = FindMessageById(msg.ReplyToMessageId);
            }

            if (target != null)
            {
                TryUpdateMessageListLayout("ReplyPreview_Tapped");
                MessageList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
            }

            e.Handled = true;
        }

        private async void PinnedMessageBar_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_chat != null && _chat.PinnedMessageCount > 1)
                await MovePinnedMessageAsync(1);
            else
            {
                var messageId = CurrentPinnedMessageId();
                if (messageId > 0)
                    await OpenPinnedMessageAsync(messageId);
            }
            if (e != null) e.Handled = true;
        }

        private async void PinnedPreviousButton_Click(object sender, RoutedEventArgs e)
        {
            await MovePinnedMessageAsync(-1);
        }

        private async void PinnedNextButton_Click(object sender, RoutedEventArgs e)
        {
            await MovePinnedMessageAsync(1);
        }

        private async System.Threading.Tasks.Task MovePinnedMessageAsync(int delta)
        {
            if (_chat == null || _chat.PinnedMessageCount <= 1) return;
            var ids = _chat.PinnedMessageIds;
            if (ids == null || ids.Count == 0) return;

            var next = _chat.CurrentPinnedMessageIndex + delta;
            if (next < 0) next = ids.Count - 1;
            if (next >= ids.Count) next = 0;

            _chat.CurrentPinnedMessageIndex = next;
            _chat.PinnedMessageId = ids[next];

            var local = FindMessageById(_chat.PinnedMessageId);
            var preview = BuildPinnedMessagePreview(local);
            if (HasVisibleText(preview))
            {
                _pinnedPreviewCache[_chat.PinnedMessageId] = preview;
                _chat.PinnedMessagePreview = preview;
            }

            UpdatePinnedMessageBar();
            await OpenPinnedMessageAsync(_chat.PinnedMessageId);
        }

        private int CurrentPinnedMessageId()
        {
            if (_chat == null) return 0;
            var ids = _chat.PinnedMessageIds;
            if (ids != null && ids.Count > 0)
            {
                var index = _chat.CurrentPinnedMessageIndex;
                if (index < 0 || index >= ids.Count)
                {
                    index = 0;
                    _chat.CurrentPinnedMessageIndex = 0;
                }
                var id = ids[index];
                if (id > 0) return id;
            }
            return _chat.PinnedMessageId;
        }

        private async System.Threading.Tasks.Task<ChatMessageViewModel> LoadOlderHistoryUntilMessageAsync(int messageId)
        {
            var target = FindMessageById(messageId);
            if (target != null) return target;
            if (_chat == null || _messages == null || _messages.Count == 0) return null;

            for (var page = 0; page < PinnedJumpMaxPageLoads && target == null && !_noMoreOlderMessages; page++)
            {
                var oldestId = GetOldestMessageId();
                var previousOldestSortId = GetOldestMessageSortId();
                if (oldestId <= 0 || previousOldestSortId == 0) break;

                var older = await TelegramService.Instance.GetHistoryBeforeAsync(_chat, oldestId, PinnedJumpHistoryPageLimit);
                if (older == null) break;
                if (older.Count == 0)
                {
                    _olderEmptyResponseCount++;
                    _noMoreOlderMessages = _olderEmptyResponseCount >= 2;
                    break;
                }

                var added = MergeMessages(older, false);
                if (added > 0)
                    _olderEmptyResponseCount = 0;

                target = FindMessageById(messageId);
                if (target != null) break;

                var currentOldestSortId = GetOldestMessageSortId();
                if (currentOldestSortId == 0 || currentOldestSortId >= previousOldestSortId)
                    break;

                if ((page & 7) == 7)
                    await System.Threading.Tasks.Task.Delay(1);
            }

            return target;
        }

        private async System.Threading.Tasks.Task LoadNewerHistoryUntilAsync(int anchorId, long targetNewestSortId)
        {
            if (_chat == null || anchorId <= 0 || targetNewestSortId <= 0) return;

            var anchorSortId = GetMessageSortIdById(anchorId);
            if (anchorSortId <= 0)
                anchorSortId = anchorId;

            for (var page = 0; page < PinnedJumpMaxPageLoads && anchorSortId < targetNewestSortId; page++)
            {
                var newer = await TelegramService.Instance.GetHistoryForwardAsync(_chat, anchorId, PinnedJumpHistoryPageLimit);
                if (newer == null || newer.Count == 0)
                    break;

                var newestId = GetNewestMessageId(newer);
                var newestSortId = GetNewestMessageSortId(newer);
                MergeMessages(newer, false);

                if (newestId <= 0 || newestSortId <= anchorSortId)
                    break;

                anchorId = newestId;
                anchorSortId = newestSortId;

                if ((page & 7) == 7)
                    await System.Threading.Tasks.Task.Delay(1);
            }
        }

        private async System.Threading.Tasks.Task<ChatMessageViewModel> LoadPinnedFallbackWindowAsync(int messageId, int previousNewestId)
        {
            var around = await TelegramService.Instance.GetHistoryAroundAsync(_chat, messageId, PinnedJumpHistoryPageLimit);
            if (around == null)
                around = new List<ChatMessageViewModel>();

            var hasPinned = false;
            for (var i = 0; i < around.Count; i++)
            {
                if (around[i] != null && around[i].Id == messageId)
                {
                    hasPinned = true;
                    break;
                }
            }

            if (!hasPinned)
            {
                var exact = await TelegramService.Instance.GetMessagesByIdAsync(_chat, messageId);
                if (exact != null)
                {
                    for (var i = 0; i < exact.Count; i++)
                    {
                        var candidate = exact[i];
                        if (candidate != null)
                            around.Add(candidate);
                    }
                }
            }

            MergeMessages(around, false);

            var target = FindMessageById(messageId);
            var anchorId = GetNewestMessageId(around);
            if (anchorId <= 0 && target != null)
                anchorId = target.Id;

            var previousNewestSortId = GetMessageSortIdById(previousNewestId);
            if (anchorId > 0 && previousNewestSortId > 0)
                await LoadNewerHistoryUntilAsync(anchorId, previousNewestSortId);

            return FindMessageById(messageId);
        }

        private async System.Threading.Tasks.Task OpenPinnedMessageAsync(int messageId)
        {
            if (_chat == null || messageId <= 0) return;

            _stickToBottom = false;
            IgnoreScrollTrackingBriefly();
            var previousNewestId = GetNewestMessageId();

            try
            {
                SetTopLoading(true);

                var target = await LoadOlderHistoryUntilMessageAsync(messageId);
                if (target == null)
                    target = await LoadPinnedFallbackWindowAsync(messageId, previousNewestId);

                await CompleteTopBoundaryAlbumAsync();
                await CompleteVisibleGroupedAlbumsAsync();
                UpdateOutgoingMessageStates();

                if (target != null)
                {
                    IgnoreScrollTrackingBriefly();
                    TryUpdateMessageListLayout("OpenPinnedMessageAsync-initial");
                    MessageList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);

                    await System.Threading.Tasks.Task.Delay(40);
                    IgnoreScrollTrackingBriefly();
                    TryUpdateMessageListLayout("OpenPinnedMessageAsync-deferred");
                    MessageList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
                }

                if (target != null)
                {
                    var preview = BuildPinnedMessagePreview(target);
                    if (HasVisibleText(preview))
                    {
                        _pinnedPreviewCache[messageId] = preview;
                        _chat.PinnedMessagePreview = preview;
                    }
                }

                BeginAutoDownloadMedia();
                StartBackgroundReactionLoad();
                UpdatePinnedMessageBar();
                UpdateScrollDownButton();
            }
            catch
            {
                // Keep the current reconstructed range usable even when a
                // remote page fails partway through the jump.
            }
            finally
            {
                SetTopLoading(false);
            }
        }

        private void QueuePinnedMessagesLoad(bool force)
        {
            if (_chat == null) return;
            if (_pinnedMessagesLoading && !force) return;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                await LoadPinnedMessagesAsync(force);
            });
        }

        private async System.Threading.Tasks.Task LoadPinnedMessagesAsync(bool force)
        {
            if (_chat == null || (_pinnedMessagesLoading && !force)) return;
            _pinnedMessagesLoading = true;
            try
            {
                var pinned = await TelegramService.Instance.GetPinnedMessagesAsync(_chat, 50);
                if (pinned == null || pinned.Count == 0)
                {
                    UpdatePinnedMessageBar();
                    return;
                }

                // Pinned messages are metadata for the header only.
                // Never merge them into the visible chat collection here:
                // a very old pinned message would otherwise appear as a
                // disconnected row at the top of the currently loaded history.
                var ids = new List<int>();
                for (var i = 0; i < pinned.Count; i++)
                {
                    var msg = pinned[i];
                    if (msg == null || msg.Id <= 0 || ids.Contains(msg.Id)) continue;
                    ids.Add(msg.Id);
                    var itemPreview = BuildPinnedMessagePreview(msg);
                    if (HasVisibleText(itemPreview))
                        _pinnedPreviewCache[msg.Id] = itemPreview;
                }

                if (ids.Count == 0) return;

                var selectedId = _chat.PinnedMessageId;
                if (selectedId <= 0 || !ids.Contains(selectedId))
                    selectedId = ids[0];

                _chat.PinnedMessageIds = ids;
                _chat.PinnedMessageId = selectedId;
                _chat.CurrentPinnedMessageIndex = Math.Max(0, ids.IndexOf(selectedId));

                ChatMessageViewModel selected = null;
                for (var i = 0; i < pinned.Count; i++)
                {
                    var candidate = pinned[i];
                    if (candidate != null && candidate.Id == selectedId)
                    {
                        selected = candidate;
                        break;
                    }
                }
                var preview = BuildPinnedMessagePreview(selected);
                if (!HasVisibleText(preview))
                    _pinnedPreviewCache.TryGetValue(selectedId, out preview);
                if (HasVisibleText(preview))
                    _chat.PinnedMessagePreview = preview;
                UpdatePinnedMessageBar();
            }
            catch
            {
                UpdatePinnedMessageBar();
            }
            finally
            {
                _pinnedMessagesLoading = false;
            }
        }

        private void UpdatePinnedMessageBar()
        {
            if (PinnedMessageBar == null) return;

            var messageId = CurrentPinnedMessageId();
            if (_chat == null || messageId <= 0)
            {
                PinnedMessageBar.Visibility = Visibility.Collapsed;
                ApplyTopChromeGlassSetting();
                UpdateMessageListChromePadding();
                return;
            }

            var local = FindMessageById(messageId);
            var preview = BuildPinnedMessagePreview(local);
            if (HasVisibleText(preview))
                _pinnedPreviewCache[messageId] = preview;
            if (!HasVisibleText(preview))
                _pinnedPreviewCache.TryGetValue(messageId, out preview);
            if (!HasVisibleText(preview))
            {
                var hasOnlyOnePinned = _chat.PinnedMessageCount <= 1;
                preview = hasOnlyOnePinned ? _chat.PinnedMessagePreview : string.Empty;
            }
            if (!HasVisibleText(preview))
            {
                preview = "Loading pinned message...";
                QueuePinnedMessagePreviewLoad(messageId);
            }

            _chat.PinnedMessagePreview = preview;
            PinnedMessageText.Text = preview;
            UpdatePinnedMessageStripes();
            PinnedMessageBar.Visibility = Visibility.Visible;
            ApplyTopChromeGlassSetting();
            UpdateMessageListChromePadding();
        }

        private void UpdatePinnedMessageStripes()
        {
            if (PinnedMessageIndicator == null || PinnedMessageIndicatorTrack == null || PinnedMessageIndicatorThumb == null) return;

            var count = _chat == null ? 0 : _chat.PinnedMessageCount;
            if (count <= 0) count = 1;

            var active = _chat == null ? 0 : _chat.CurrentPinnedMessageIndex;
            if (active < 0) active = 0;
            if (active >= count) active = count - 1;

            const double railHeight = 34.0;
            PinnedMessageIndicator.Height = railHeight;
            PinnedMessageIndicatorTrack.Height = railHeight;

            var thumbHeight = count <= 1 ? railHeight : Math.Max(7.0, railHeight / Math.Min(count, 5));
            var top = count <= 1 ? 0.0 : (railHeight - thumbHeight) * (count - 1 - active) / Math.Max(1, count - 1);

            PinnedMessageIndicatorThumb.Height = thumbHeight;
            PinnedMessageIndicatorThumb.Margin = new Thickness(0, top, 0, 0);
            PinnedMessageIndicatorTrack.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void QueuePinnedMessagePreviewLoad(int messageId)
        {
            if (_chat == null || messageId <= 0) return;
            if (_pinnedPreviewLoading && _pinnedPreviewRequestedId == messageId) return;
            if (_pinnedPreviewLoadAttemptedIds.Contains(messageId)) return;

            _pinnedPreviewLoadAttemptedIds.Add(messageId);
            _pinnedPreviewLoading = true;
            _pinnedPreviewRequestedId = messageId;

            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                try
                {
                    var messages = await TelegramService.Instance.GetMessagesByIdAsync(_chat, messageId);
                    ChatMessageViewModel msg = null;
                    if (messages != null)
                    {
                        for (var i = 0; i < messages.Count; i++)
                        {
                            var candidate = messages[i];
                            if (candidate != null && candidate.Id == messageId)
                            {
                                msg = candidate;
                                break;
                            }
                        }
                    }
                    var preview = BuildPinnedMessagePreview(msg);
                    if (HasVisibleText(preview))
                    {
                        _pinnedPreviewCache[messageId] = preview;
                        _chat.PinnedMessagePreview = preview;
                    }
                }
                catch
                {
                }
                finally
                {
                    _pinnedPreviewLoading = false;
                    _pinnedPreviewRequestedId = 0;
                    UpdatePinnedMessageBar();
                }
            });
        }

        private string BuildPinnedMessagePreview(ChatMessageViewModel msg)
        {
            if (msg == null) return string.Empty;

            var text = msg.VisibleText;
            if (!HasVisibleText(text)) text = msg.Text;
            if (!HasVisibleText(text)) text = msg.MediaTitle;
            if (!HasVisibleText(text)) text = msg.MediaFileName;
            if (!HasVisibleText(text)) text = msg.PendingMediaFallbackText;
            if (!HasVisibleText(text)) text = BuildPinnedMediaKindLabel(msg);

            text = SanitizeRichTextRunText(text);
            if (!HasVisibleText(text)) return string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 140 ? text.Substring(0, 140) + "..." : text;
        }

        private string BuildPinnedMediaKindLabel(ChatMessageViewModel msg)
        {
            if (msg == null) return string.Empty;
            var kind = string.IsNullOrEmpty(msg.MediaKind) ? string.Empty : msg.MediaKind.ToLowerInvariant();
            if (kind == "photo") return "Photo";
            if (kind == "video") return "Video";
            if (kind == "roundvideo") return "Round video";
            if (kind == "gif") return "GIF";
            if (kind == "sticker") return "Sticker";
            if (kind == "voice") return "Voice message";
            if (kind == "audio") return "Audio";
            if (kind == "poll") return "Poll";
            if (kind == "grouped") return "Album";
            if (msg.MediaItems != null && msg.MediaItems.Count > 0) return "Album";
            return msg.HasMedia ? "Media" : "Message";
        }

        private ChatMessageViewModel FindMessageById(int id)
        {
            if (_messages == null || id <= 0) return null;
            for (var i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i] as ChatMessageViewModel;
                if (msg != null && msg.Id == id) return msg;
            }
            return null;
        }

        private ScrollViewer CreateReactionStrip(ChatMessageViewModel msg, Flyout ownerFlyout)
        {
            var loading = new TextBlock
            {
                Text = "…",
                FontSize = 22,
                Width = 42,
                Height = 40,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return new ScrollViewer
            {
                Content = loading,
                MaxWidth = 288,
                MinHeight = 40,
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
        }

        private async void LoadAvailableMessageReactionsAsync(ChatMessageViewModel msg, Flyout ownerFlyout, ScrollViewer host)
        {
            if (msg == null || ownerFlyout == null || host == null || _chat == null) return;
            List<MessageReactionViewModel> reactions = null;
            try
            {
                reactions = await TelegramService.Instance.GetMessageAvailableReactionsAsync(_chat, msg.Id);
            }
            catch
            {
            }

            if (!_messageActionsFlyoutOpen || host == null) return;
            if (reactions == null || reactions.Count == 0)
            {
                RemoveMessageActionFlyoutSection(ownerFlyout, host);
                return;
            }

            // Only render the exact list returned by TDLib for this message. Never mix in a
            // local fallback, otherwise restricted reactions look clickable even when the
            // server forbids them.
            host.Content = CreateReactionButtons(msg, ownerFlyout, reactions);

            for (var i = 0; i < reactions.Count; i++)
            {
                var reaction = reactions[i];
                if (reaction == null || reaction.CustomEmojiDocumentId == 0 || !string.IsNullOrEmpty(reaction.CustomEmojiUri)) continue;
                try
                {
                    reaction.CustomEmojiUri = await TelegramService.Instance.GetCustomEmojiStickerUriAsync(reaction.CustomEmojiDocumentId);
                }
                catch
                {
                }
                if (!_messageActionsFlyoutOpen) return;
                if (!string.IsNullOrEmpty(reaction.CustomEmojiUri))
                    host.Content = CreateReactionButtons(msg, ownerFlyout, reactions);
            }
        }

        private void RemoveMessageActionFlyoutSection(Flyout ownerFlyout, FrameworkElement section)
        {
            var parent = section == null ? null : section.Parent as Windows.UI.Xaml.Controls.Panel;
            if (parent == null) return;
            parent.Children.Remove(section);
            if (parent.Children.Count == 0 && ownerFlyout != null)
                ownerFlyout.Hide();
        }

        private StackPanel CreateReactionButtons(ChatMessageViewModel msg, Flyout ownerFlyout, IList<MessageReactionViewModel> reactions)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (reactions == null) return row;

            for (var i = 0; i < reactions.Count; i++)
            {
                var reaction = reactions[i];
                if (reaction == null) continue;
                object content;
                if (reaction.CustomEmojiDocumentId != 0 && !string.IsNullOrEmpty(reaction.CustomEmojiUri))
                {
                    content = new FfmpegStickerImageControl
                    {
                        SourceUri = reaction.CustomEmojiUri,
                        MediaKind = "sticker",
                        Width = 24,
                        Height = 24,
                        ImageStretch = Stretch.Uniform
                    };
                }
                else
                {
                    var localEmojiUri = reaction.CustomEmojiDocumentId == 0 ? ResolveLocalEmojiAssetUri(reaction.Emoticon) : string.Empty;
                    if (!string.IsNullOrEmpty(localEmojiUri))
                    {
                        content = new Image
                        {
                            Source = new BitmapImage(new Uri(localEmojiUri)),
                            Width = 24,
                            Height = 24,
                            Stretch = Stretch.Uniform,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                    }
                    else
                    {
                        content = new TextBlock
                        {
                            Text = reaction.CustomEmojiDocumentId != 0 ? "\u2726" : (reaction.Emoticon ?? ""),
                            FontSize = 22,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                    }
                }

                var button = new Button
                {
                    Content = content,
                    Width = 42,
                    Height = 40,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 4, 0),
                    Tag = new ReactionActionContext
                    {
                        Message = msg,
                        Emoticon = reaction.Emoticon,
                        CustomEmojiDocumentId = reaction.CustomEmojiDocumentId,
                        OwnerFlyout = ownerFlyout
                    }
                };
                button.Style = Resources["TelegramBareButtonStyle"] as Style;
                button.Click += ReactionMenu_Click;
                row.Children.Add(button);
            }
            return row;
        }

        private async void ReadByMenu_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var msg = element == null ? null : element.Tag as ChatMessageViewModel;
            if (!CanShowReadByAction(msg)) return;

            var sheet = new ReadBySheet();
            var cached = msg.ReadByUsers == null || msg.ReadByUsers.Count == 0 ? null : new List<CommentAvatarViewModel>(msg.ReadByUsers);
            var user = await sheet.ShowAsync(_chat, msg.Id, "Read by", cached);
            if (user == null || user.PeerId == 0) return;

            try
            {
                var chat = await TelegramService.Instance.GetPrivateChatAsync(user.PeerId);
                if (chat == null) return;

                if (AdaptiveShellNavigationService.NavigateLeft(typeof(UserProfilePage), chat))
                    return;
                Frame.Navigate(typeof(UserProfilePage), chat);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("READ_BY_OPEN_PROFILE_FAIL message=" + ex.Message);
            }
        }

        private async void ReadByPreview_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var msg = element == null ? null : element.Tag as ChatMessageViewModel;
            if (msg == null) return;
            await ShowReadByUsersDialogAsync(msg);
        }

        private async System.Threading.Tasks.Task ShowReadByUsersDialogAsync(ChatMessageViewModel msg)
        {
            if (msg == null) return;

            var panel = new StackPanel
            {
                MinWidth = 230,
                MaxWidth = 320,
                Padding = new Thickness(10, 8, 10, 8)
            };

            panel.Children.Add(new TextBlock
            {
                Text = msg.ReadByPreviewText,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 4, 8)
            });

            if (msg.ReadByUsers == null || msg.ReadByUsers.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "No viewers yet",
                    FontSize = 14,
                    Margin = new Thickness(4, 0, 4, 2)
                });
            }
            else
            {
                for (var i = 0; i < msg.ReadByUsers.Count; i++)
                {
                    var user = msg.ReadByUsers[i];
                    if (user == null) continue;
                    panel.Children.Add(CreateReadByUserRow(user));
                }
            }

            var dialog = new ContentDialog
            {
                Title = "Read by",
                Content = panel,
                PrimaryButtonText = "Close"
            };

            try { await dialog.ShowAsync(); }
            catch (Exception ex) { Debug.WriteLine("READ_BY_DIALOG_FAIL message=" + ex.Message); }
        }

        private FrameworkElement CreateReadByUserRow(CommentAvatarViewModel user)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                MinHeight = 42,
                Padding = new Thickness(4, 3, 4, 3)
            };

            row.Children.Add(CreateReadByAvatar(user, 32));
            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(user.Title) ? "User" : user.Title,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
                Margin = new Thickness(10, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            return row;
        }

        private FrameworkElement CreateReadByAvatar(CommentAvatarViewModel user, double size)
        {
            var grid = new Grid
            {
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Center
            };

            grid.Children.Add(new Ellipse { Fill = ResolveTextBrush("TelegramAvatarPlaceholderBrush") });
            if (user != null && user.AvatarImageSource != null)
            {
                grid.Children.Add(new Image
                {
                    Width = size,
                    Height = size,
                    Source = user.AvatarImageSource,
                    Stretch = Stretch.UniformToFill
                });
            }

            grid.Children.Add(new TextBlock
            {
                Text = user == null || string.IsNullOrWhiteSpace(user.Initials) ? "?" : user.Initials,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResolveTextBrush("TelegramAvatarTextBrush"),
                Visibility = user == null || string.IsNullOrEmpty(user.AvatarUri) ? Visibility.Visible : Visibility.Collapsed
            });
            return grid;
        }

        private Button CreateActionButton(string text, ChatMessageViewModel msg, RoutedEventHandler handler, Flyout ownerFlyout)
        {
            var button = new Button
            {
                Content = text,
                Tag = msg,
                Height = 40,
                MinWidth = 220,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Foreground = ResolveTextBrush("TelegramPrimaryTextBrush")
            };
            button.Click += delegate(object sender, RoutedEventArgs e)
            {
                if (ownerFlyout != null) ownerFlyout.Hide();
                handler(sender, e);
            };
            return button;
        }

        private async void ReactionButton_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var reaction = element == null ? null : element.DataContext as MessageReactionViewModel;
            if (reaction == null || reaction.OwnerMessage == null) return;
            await ToggleReactionAsync(reaction.OwnerMessage, reaction.Emoticon, reaction.CustomEmojiDocumentId, reaction.IsChosen);
        }

        private async void CommentsPreview_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var msg = element == null ? null : element.Tag as ChatMessageViewModel;
            if (_chat == null || msg == null || msg.Id <= 0 || !msg.CanOpenComments || Frame == null) return;
            e.Handled = true;

            var commentsChat = BuildCommentsChat(msg);
            if (commentsChat == null) return;
            try
            {
                var resolved = await TelegramService.Instance.ResolveDiscussionChatAsync(_chat, msg);
                if (resolved != null)
                    commentsChat = resolved;
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Comments error", AlertErrorMessage(ex, "Could not open comments."));
                return;
            }
            var navigationTarget = new ChatNavigationTarget { Chat = commentsChat };
            if (!AdaptiveShellNavigationService.NavigateChat(navigationTarget))
                Frame.Navigate(typeof(ChatPage), navigationTarget);
        }

        private ChatViewModel BuildCommentsChat(ChatMessageViewModel msg)
        {
            if (_chat == null || msg == null || msg.Id <= 0) return null;

            var discussionTitle = string.IsNullOrEmpty(msg.CommentsDiscussionTitle) ? "Comments" : msg.CommentsDiscussionTitle;
            return new ChatViewModel
            {
                PeerId = _chat.PeerId,
                PeerType = _chat.PeerType,
                PeerKey = _chat.PeerKey,
                AccessHash = _chat.AccessHash,
                Title = "Comments",
                LastMessage = msg.Text,
                LastMessageDate = msg.Date,
                TopMessageId = msg.CommentsMaxId > 0 ? msg.CommentsMaxId : msg.Id,
                ReadOutboxMaxId = msg.CommentsReadMaxId,
                IsGroup = true,
                IsChannel = true,
                IsForumTopic = true,
                IsCommentsThread = true,
                TopicId = msg.Id,
                TopicRootMessageId = msg.Id,
                ParentPeerType = _chat.PeerType,
                ParentPeerId = _chat.PeerId,
                ParentPeerKey = _chat.PeerKey,
                ParentAccessHash = _chat.AccessHash,
                ParentTitle = string.IsNullOrEmpty(_chat.Title) ? discussionTitle : _chat.Title,
                CanSendMessages = msg.CommentsDiscussionCanSend || _chat.CanSendMessages,
                CanPinMessages = false,
                CanDeleteMessages = _chat.CanDeleteMessages,
                NoForwards = _chat.NoForwards,
                IconText = BuildSimpleIconText(discussionTitle),
                AvatarUri = _chat.AvatarUri,
                AvatarIsPreview = _chat.AvatarIsPreview,
                AvatarPhotoId = _chat.AvatarPhotoId,
                AvatarDcId = _chat.AvatarDcId,
                AvatarStrippedThumb = _chat.AvatarStrippedThumb
            };
        }

        private async void ReactionMenu_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var context = element == null ? null : element.Tag as ReactionActionContext;
            if (context == null || context.Message == null) return;
            if (context.OwnerFlyout != null) context.OwnerFlyout.Hide();
            var existing = context.Message.FindReaction(context.Emoticon, context.CustomEmojiDocumentId);
            await ToggleReactionAsync(context.Message, context.Emoticon, context.CustomEmojiDocumentId, existing != null && existing.IsChosen);
        }

        private async System.Threading.Tasks.Task ToggleReactionAsync(ChatMessageViewModel msg, string emoticon, bool remove)
        {
            await ToggleReactionAsync(msg, emoticon, 0, remove);
        }

        private async System.Threading.Tasks.Task ToggleReactionAsync(ChatMessageViewModel msg, string emoticon, long customEmojiDocumentId, bool remove)
        {
            if (_chat == null || !CanShowReactAction(msg) || (string.IsNullOrEmpty(emoticon) && customEmojiDocumentId == 0)) return;
            try
            {
                await TelegramService.Instance.SendReactionAsync(_chat, msg.Id, emoticon, customEmojiDocumentId, remove);
                msg.ApplyLocalReaction(emoticon, customEmojiDocumentId);
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Reaction error", AlertErrorMessage(ex, "Could not update reaction."));
            }
        }

        private sealed class ReactionActionContext
        {
            public ChatMessageViewModel Message;
            public string Emoticon;
            public long CustomEmojiDocumentId;
            public Flyout OwnerFlyout;
        }

        private ChatMessageViewModel GetMessageFromMenuSender(object sender)
        {
            var element = sender as FrameworkElement;
            return element == null ? null : element.Tag as ChatMessageViewModel;
        }

        private void ReplyMenu_Click(object sender, RoutedEventArgs e)
        {
            var msg = GetMessageFromMenuSender(sender);
            if (!CanShowReplyAction(msg)) return;
            BeginReplyToMessage(msg);
        }

        private void BeginReplyToMessage(ChatMessageViewModel msg)
        {
            if (msg == null || _chat == null || !_chat.CanSendMessages) return;

            _replyToMessageId = msg.Id;
            var text = msg.Text;
            if (string.IsNullOrEmpty(text)) text = msg.MediaTitle;
            if (string.IsNullOrEmpty(text)) text = "message";
            if (text.Length > 80) text = text.Substring(0, 80) + "…";
            ReplyPreviewText.Text = "Reply to: " + text;
            ReplyPanel.Visibility = Visibility.Visible;
            MessageText.Focus(FocusState.Programmatic);
        }

        private async void PinMenu_Click(object sender, RoutedEventArgs e)
        {
            var msg = GetMessageFromMenuSender(sender);
            if (!CanShowPinAction(msg)) return;
            try
            {
                if (msg.IsPinned)
                {
                    await TelegramService.Instance.UnpinMessageAsync(_chat, msg);
                    if (_chat != null)
                    {
                        var ids = _chat.PinnedMessageIds == null ? new List<int>() : new List<int>(_chat.PinnedMessageIds);
                        ids.Remove(msg.Id);
                        _chat.PinnedMessageIds = ids;
                        if (_chat.PinnedMessageId == msg.Id)
                        {
                            _chat.PinnedMessageId = ids.Count > 0 ? ids[0] : 0;
                            _chat.CurrentPinnedMessageIndex = 0;
                            _chat.PinnedMessagePreview = "";
                        }
                        _pinnedPreviewCache.Remove(msg.Id);
                        UpdatePinnedMessageBar();
                        QueuePinnedMessagesLoad(true);
                    }
                    await ShowChatAlertAsync("Message unpinned", "Message unpinned.");
                }
                else
                {
                    await TelegramService.Instance.PinMessageAsync(_chat, msg);
                    if (_chat != null)
                    {
                        var ids = _chat.PinnedMessageIds == null ? new List<int>() : new List<int>(_chat.PinnedMessageIds);
                        ids.Remove(msg.Id);
                        ids.Insert(0, msg.Id);
                        _chat.PinnedMessageIds = ids;
                        _chat.PinnedMessageId = msg.Id;
                        _chat.CurrentPinnedMessageIndex = 0;
                        var preview = BuildPinnedMessagePreview(msg);
                        if (HasVisibleText(preview))
                            _pinnedPreviewCache[msg.Id] = preview;
                        _chat.PinnedMessagePreview = preview;
                        UpdatePinnedMessageBar();
                        QueuePinnedMessagesLoad(true);
                    }
                    await ShowChatAlertAsync("Message pinned", "Message pinned.");
                }
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Pin error", AlertErrorMessage(ex, msg.IsPinned ? "Could not unpin this message." : "Could not pin this message."));
            }
        }

        private void CopyTextMenu_Click(object sender, RoutedEventArgs e)
        {
            var msg = GetMessageFromMenuSender(sender);
            if (msg == null || string.IsNullOrEmpty(msg.Text)) return;
            var data = new DataPackage();
            data.SetText(msg.Text);
            Clipboard.SetContent(data);
            ShowChatAlert("Text copied", "Text copied.");
        }

        private async void ForwardMenu_Click(object sender, RoutedEventArgs e)
        {
            var msg = GetMessageFromMenuSender(sender);
            if (!CanShowForwardAction(msg)) return;
            try
            {
                var picker = new ChatPickerSheet();
                var target = await picker.ShowAsync("Forward to");
                if (target == null) return;

                await TelegramService.Instance.ForwardMessageAsync(_chat, msg, target);
                await ShowChatAlertAsync("Message forwarded", "Forwarded to " + (string.IsNullOrEmpty(target.Title) ? "chat" : target.Title) + ".");
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Forward error", AlertErrorMessage(ex, "Could not forward this message."));
            }
        }

        private async System.Threading.Tasks.Task<ChatViewModel> ShowForwardTargetDialogAsync()
        {
            var targets = await LoadForwardTargetsAsync();
            ChatViewModel selected = null;

            var panel = new StackPanel();
            if (targets.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "No writable chats found.",
                    Margin = new Thickness(12),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ResolveTextBrush("TelegramSecondaryTextBrush")
                });
            }
            else
            {
                for (var i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    var button = CreateForwardTargetButton(target);
                    button.Click += delegate
                    {
                        selected = target;
                        var owner = button.Tag as ContentDialog;
                        if (owner != null) owner.Hide();
                    };
                    panel.Children.Add(button);
                }
            }

            var scroll = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 420,
                VerticalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var dialog = new ContentDialog
            {
                Title = "Forward to",
                Content = scroll,
                SecondaryButtonText = "Cancel"
            };

            for (var i = 0; i < panel.Children.Count; i++)
            {
                var button = panel.Children[i] as Button;
                if (button != null) button.Tag = dialog;
            }

            await dialog.ShowAsync();
            return selected;
        }

        private async System.Threading.Tasks.Task<List<ChatViewModel>> LoadForwardTargetsAsync()
        {
            var result = new List<ChatViewModel>();
            var keys = new HashSet<string>();

            ChatViewModel saved = null;
            try
            {
                saved = await TelegramService.Instance.GetSavedMessagesChatAsync();
            }
            catch
            {
            }

            if (saved == null)
                saved = CreateSavedMessagesForwardTarget();

            AddForwardTarget(result, keys, saved);
            await AddForwardTargetsFromFolderAsync(result, keys, -1);
            await AddForwardTargetsFromFolderAsync(result, keys, 1);

            return result;
        }

        private async System.Threading.Tasks.Task AddForwardTargetsFromFolderAsync(List<ChatViewModel> result, HashSet<string> keys, int folderId)
        {
            Tuple<List<ChatViewModel>, bool> page = null;
            try
            {
                page = await TelegramService.Instance.GetChatsPageAsync(folderId, 0, 0);
            }
            catch
            {
            }

            var chats = page == null ? null : page.Item1;
            if (chats != null)
            {
                for (var i = 0; i < chats.Count; i++)
                {
                    var chat = chats[i];
                    if (!CanForwardToChat(chat)) continue;
                    AddForwardTarget(result, keys, chat);
                }
            }
        }

        private void AddForwardTarget(List<ChatViewModel> result, HashSet<string> keys, ChatViewModel chat)
        {
            if (result == null || keys == null || chat == null) return;
            var key = GetForwardTargetKey(chat);
            if (string.IsNullOrEmpty(key) || keys.Contains(key)) return;
            keys.Add(key);
            result.Add(chat);
        }

        private bool CanForwardToChat(ChatViewModel chat)
        {
            if (chat == null || chat.IsArchiveEntry) return false;
            if (IsSavedMessagesForwardTarget(chat)) return true;
            return chat.CanSendMessages;
        }

        private string GetForwardTargetKey(ChatViewModel chat)
        {
            if (chat == null) return null;
            if (IsSavedMessagesForwardTarget(chat)) return "self";
            if (!string.IsNullOrEmpty(chat.PeerKey)) return chat.PeerKey;
            return (chat.PeerType ?? string.Empty) + ":" + chat.PeerId.ToString();
        }

        private bool IsSavedMessagesForwardTarget(ChatViewModel chat)
        {
            if (chat == null) return false;
            var peerType = (chat.PeerType ?? string.Empty).ToLowerInvariant();
            if (peerType == "self" || peerType == "saved") return true;
            var key = (chat.PeerKey ?? string.Empty).ToLowerInvariant();
            if (key == "self" || key == "saved" || key == "savedmessages" || key == "saved_messages") return true;
            var title = (chat.Title ?? string.Empty).Trim().ToLowerInvariant();
            return title == "saved messages" || title == "saved message";
        }

        private ChatViewModel CreateSavedMessagesForwardTarget()
        {
            return new ChatViewModel
            {
                PeerType = "self",
                PeerKey = "self",
                Title = "Saved Messages",
                LastMessage = string.Empty,
                IconText = "S",
                CanSendMessages = true,
                CanPinMessages = true,
                CanDeleteMessages = true
            };
        }

        private Button CreateForwardTargetButton(ChatViewModel chat)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 2)
            };

            var row = new Grid
            {
                MinHeight = 58,
                Padding = new Thickness(10, 6, 10, 6),
                Background = new SolidColorBrush(Colors.Transparent)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var avatar = CreateForwardTargetAvatar(chat);
            Grid.SetColumn(avatar, 0);
            row.Children.Add(avatar);

            var textPanel = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(textPanel, 1);
            textPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(chat.Title) ? "Chat" : chat.Title,
                FontSize = 17,
                FontWeight = Windows.UI.Text.FontWeights.SemiLight,
                Foreground = ResolveTextBrush("TelegramPrimaryTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });
            var subtitle = chat.SubtitleText;
            textPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(subtitle) ? " " : subtitle,
                FontSize = 12,
                Foreground = ResolveTextBrush("TelegramSecondaryTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                Margin = new Thickness(0, 1, 0, 0)
            });
            row.Children.Add(textPanel);

            button.Content = row;
            return button;
        }

        private FrameworkElement CreateForwardTargetAvatar(ChatViewModel chat)
        {
            var root = new Grid
            {
                Width = 42,
                Height = 42,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };

            var ellipse = new Ellipse
            {
                Width = 42,
                Height = 42,
                Fill = ResolveTextBrush("TelegramAvatarPlaceholderBrush")
            };

            if (!string.IsNullOrEmpty(chat.AvatarUri))
            {
                try
                {
                    ellipse.Fill = new ImageBrush
                    {
                        ImageSource = new BitmapImage(new Uri(chat.AvatarUri)),
                        Stretch = Stretch.UniformToFill
                    };
                }
                catch
                {
                }
            }

            root.Children.Add(ellipse);

            if (string.IsNullOrEmpty(chat.AvatarUri))
            {
                root.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(chat.IconText) ? "?" : chat.IconText,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 15,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            return root;
        }

        private Brush ResolveTextBrush(string key)
        {
            object value;
            if (Resources != null && Resources.TryGetValue(key, out value))
            {
                var brush = value as Brush;
                if (brush != null) return brush;
            }

            if (Application.Current != null && Application.Current.Resources != null &&
                Application.Current.Resources.TryGetValue(key, out value))
            {
                var brush = value as Brush;
                if (brush != null) return brush;
            }

            if (key == "TelegramSecondaryTextBrush")
                return new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));
            if (key == "TelegramAvatarPlaceholderBrush")
                return new SolidColorBrush(Color.FromArgb(255, 46, 46, 46));
            return new SolidColorBrush(Colors.White);
        }

        private async void DeleteMenu_Click(object sender, RoutedEventArgs e)
        {
            var msg = GetMessageFromMenuSender(sender);
            if (!CanShowDeleteAction(msg)) return;
            try
            {
                var revoke = await AskDeleteMessageRevokeAsync(msg);
                if (!revoke.HasValue) return;

                await TelegramService.Instance.DeleteMessageAsync(_chat, msg, revoke.Value);
                RemoveMessagesById(new List<int> { msg.Id });
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Delete error", AlertErrorMessage(ex, "Could not delete this message."));
            }
        }

        private async System.Threading.Tasks.Task<bool?> AskDeleteMessageRevokeAsync(ChatMessageViewModel msg)
        {
            var canDeleteForEveryone = CanDeleteMessageForEveryone(msg);
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = canDeleteForEveryone
                    ? "Delete this message?"
                    : "Delete this message for you?",
                TextWrapping = TextWrapping.WrapWholeWords
            });

            CheckBox revokeCheckBox = null;
            if (canDeleteForEveryone)
            {
                revokeCheckBox = new CheckBox
                {
                    Content = "Delete for everyone",
                    Margin = new Thickness(0, 12, 0, 0)
                };
                panel.Children.Add(revokeCheckBox);
            }

            var dialog = new ContentDialog
            {
                Title = "Delete message?",
                Content = panel,
                PrimaryButtonText = "Delete",
                SecondaryButtonText = "Cancel",
                FullSizeDesired = false
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;
            return revokeCheckBox != null && revokeCheckBox.IsChecked == true;
        }

        private bool CanDeleteMessageForEveryone(ChatMessageViewModel msg)
        {
            if (msg == null || _chat == null) return false;
            if (_chat.PeerType == "self") return false;
            if (_chat.CanDeleteMessages) return true;
            return msg.IsOutgoing;
        }

        private void CancelReplyButton_Click(object sender, RoutedEventArgs e)
        {
            ClearReply();
        }

        private void ClearReply()
        {
            _replyToMessageId = 0;
            ReplyPreviewText.Text = string.Empty;
            ReplyPanel.Visibility = Visibility.Collapsed;
        }

        private void MessageText_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateComposerTextHeight();
            UpdateComposerState();
            if (_suppressComposerTextChanged) return;
            QueueTypingAction();
        }

        private void MessageText_GotFocus(object sender, RoutedEventArgs e)
        {
            SetEmojiKeyboardVisible(false);
        }

        private void MessageText_Tapped(object sender, TappedRoutedEventArgs e)
        {
            SetEmojiKeyboardVisible(false);
        }

        private void TypingCancelTimer_Tick(object sender, object e)
        {
            if (_typingCancelTimer != null) _typingCancelTimer.Stop();
            CancelTypingAction();
        }

        private void QueueTypingAction()
        {
            if (_chat == null || !_chat.CanSendMessages || MessageText == null) return;
            if (string.IsNullOrWhiteSpace(MessageText.Text))
            {
                CancelTypingAction();
                return;
            }

            var now = DateTime.UtcNow;
            if (_typingActionActive && (now - _lastTypingActionSentUtc).TotalSeconds < 4)
            {
                RestartTypingCancelTimer();
                return;
            }

            _typingActionActive = true;
            _lastTypingActionSentUtc = now;
            RestartTypingCancelTimer();
            SendChatActionFireAndForget("typing");
        }

        private void CancelTypingAction()
        {
            if (!_typingActionActive) return;
            _typingActionActive = false;
            _lastTypingActionSentUtc = DateTime.MinValue;
            SendChatActionFireAndForget("cancel");
        }

        private void RestartTypingCancelTimer()
        {
            if (_typingCancelTimer == null) return;
            _typingCancelTimer.Stop();
            _typingCancelTimer.Start();
        }

        private void SendChatActionFireAndForget(string actionKind)
        {
            if (_chat == null || !_chat.CanSendMessages) return;
            var chat = _chat;
            var ignored = System.Threading.Tasks.Task.Run(async delegate
            {
                await SendChatActionSafeAsync(chat, actionKind).ConfigureAwait(false);
            });
        }

        private async System.Threading.Tasks.Task SendChatActionSafeAsync(ChatViewModel chat, string actionKind)
        {
            try
            {
                await TelegramService.Instance.SendChatActionAsync(chat, actionKind).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private void EmojiButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsAnyRecording()) return;
            var open = EmojiKeyboardPanel == null || EmojiKeyboardPanel.Visibility != Visibility.Visible;
            SetEmojiKeyboardVisible(open);
        }

        private void StickerSetTabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_stickerSetSelectionChanging) return;
            ApplySelectedStickerSet(StickerSetTabsList == null ? null : StickerSetTabsList.SelectedItem as StickerSetViewModel);
        }

        private void StickerItem_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var sticker = button == null ? null : button.Tag as StickerItemViewModel;
            if (sticker == null || _chat == null || !_chat.CanSendMessages || IsAnyRecording()) return;

            var chat = _chat;
            var replyToMessageId = _replyToMessageId;
            var maxIdBeforeSend = GetNewestMessageId();
            var pending = CreatePendingOutgoingMediaMessage(new PendingPhotoAttachment { FileName = "Sticker", Kind = "sticker" }, string.Empty);

            CancelTypingAction();
            DismissUnreadSeparatorAfterOutgoing();
            AddPendingOutgoingMessage(pending);
            ScrollToBottomSoon();
            ClearReply();

            StartOutgoingSend(
                delegate { return TelegramService.Instance.SendStickerAsync(chat, sticker, replyToMessageId); },
                new List<ChatMessageViewModel> { pending },
                maxIdBeforeSend,
                "Send error",
                "Could not send sticker.");
        }

        private void SetEmojiKeyboardVisible(bool visible)
        {
            if (visible)
            {
                UpdateEmojiKeyboardHeight();
                var ignored = EnsureStickerPanelLoadedAsync();
            }

            if (EmojiKeyboardPanel != null)
                EmojiKeyboardPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible && BotReplyKeyboardPanel != null)
                BotReplyKeyboardPanel.Visibility = Visibility.Collapsed;
            else if (!visible)
                UpdateBotReplyKeyboardPanel();

            if (EmojiButtonIcon != null)
                EmojiButtonIcon.Opacity = visible ? 1.0 : 0.82;
        }

        private async System.Threading.Tasks.Task EnsureStickerPanelLoadedAsync()
        {
            if (_stickerPanelLoaded || _stickerPanelLoading) return;
            _stickerPanelLoading = true;
            SetStickerPanelStatus("Loading stickers...", true);
            try
            {
                var sets = await TelegramService.Instance.GetStickerPanelSetsAsync();
                _stickerSets.Clear();
                if (sets != null)
                {
                    for (var i = 0; i < sets.Count; i++)
                    {
                        var set = sets[i];
                        if (set != null) _stickerSets.Add(set);
                    }
                }

                _stickerPanelLoaded = true;
                if (_stickerSets.Count > 0)
                {
                    _stickerSetSelectionChanging = true;
                    StickerSetTabsList.SelectedIndex = 0;
                    _stickerSetSelectionChanging = false;
                    ApplySelectedStickerSet(_stickerSets[0]);
                }
                else
                {
                    _stickerItems.Clear();
                    SetStickerPanelStatus("No stickers", true);
                }
            }
            catch (Exception ex)
            {
                SetStickerPanelStatus(AlertErrorMessage(ex, "Could not load stickers."), true);
            }
            finally
            {
                _stickerPanelLoading = false;
                if (_stickerItems.Count > 0)
                    SetStickerPanelStatus("", false);
            }
        }

        private void ApplySelectedStickerSet(StickerSetViewModel set)
        {
            _stickerItems.Clear();
            if (set == null || set.Stickers == null || set.Stickers.Count == 0)
            {
                var title = set == null ? "No stickers" : "No stickers in " + set.DisplayTitle;
                SetStickerPanelStatus(title, true);
                return;
            }

            for (var i = 0; i < set.Stickers.Count; i++)
                _stickerItems.Add(set.Stickers[i]);
            SetStickerPanelStatus("", false);
        }

        private void SetStickerPanelStatus(string text, bool visible)
        {
            if (StickerPanelStatusText == null) return;
            StickerPanelStatusText.Text = text ?? string.Empty;
            StickerPanelStatusText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateEmojiKeyboardHeight()
        {
            if (EmojiKeyboardPanel == null) return;

            var width = ActualWidth;
            var height = ActualHeight;
            if ((width <= 0 || height <= 0) && Window.Current != null)
            {
                width = Window.Current.Bounds.Width;
                height = Window.Current.Bounds.Height;
            }

            EmojiKeyboardPanel.Height = width > height ? 176 : 260;
        }

        private void UpdateComposerState()
        {
            UpdateComposerTextHeight();
            var isRecording = IsAnyRecording();
            if (MessageWatermark != null)
                MessageWatermark.Visibility = !isRecording && string.IsNullOrEmpty(MessageText.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (VoiceCancelHint != null)
                VoiceCancelHint.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
            if (MessageText != null)
                MessageText.Visibility = isRecording ? Visibility.Collapsed : Visibility.Visible;
            if (VideoNotePreviewPanel != null)
                VideoNotePreviewPanel.Visibility = _isVideoNoteRecording ? Visibility.Visible : Visibility.Collapsed;

            var hasText = !string.IsNullOrWhiteSpace(MessageText.Text);
            var hasPayload = HasComposerPayload();
            var hideInputButtons = hasPayload || isRecording;

            if (hideInputButtons)
                SetEmojiKeyboardVisible(false);

            if (EmojiButton != null)
                EmojiButton.Visibility = hideInputButtons ? Visibility.Collapsed : Visibility.Visible;

            var textLeft = hideInputButtons ? 0 : 42;
            if (MessageText != null)
                MessageText.Margin = new Thickness(textLeft, 0, 0, 0);
            if (MessageWatermark != null)
                MessageWatermark.Margin = new Thickness(textLeft, 0, 0, 18);
            if (VoiceCancelHint != null)
                VoiceCancelHint.Margin = new Thickness(textLeft, 0, 0, 18);

            if (SendIcon != null)
            {
                SendIcon.Text = hasPayload ? "\uE724" : (_isVideoNoteMode ? "\uE722" : "\uE720");
                if (hasPayload || isRecording)
                    SendIcon.Foreground = new SolidColorBrush(Windows.UI.Colors.White);
                else
                    SendIcon.ClearValue(TextBlock.ForegroundProperty);
                SendIcon.Opacity = hasPayload || isRecording ? 1.0 : 0.9;
            }

            if (AttachIcon != null)
                AttachIcon.Text = "\uE723";

            if (AttachButton != null)
                AttachButton.Visibility = hideInputButtons ? Visibility.Collapsed : Visibility.Visible;

            if (AttachColumn != null)
                AttachColumn.Width = hideInputButtons ? new GridLength(0) : new GridLength(44);

            if (AttachmentPreviewScroll != null)
                AttachmentPreviewScroll.Visibility = _pendingPhotoAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (SendButtonAccentBackground != null)
                SendButtonAccentBackground.Visibility = hasPayload || isRecording ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool IsAnyRecording()
        {
            return _isVoiceRecording || _isVideoNoteRecording;
        }

        private void UpdateComposerTextHeight()
        {
            if (MessageText == null) return;

            var maxHeight = GetComposerTextMaxHeight();
            MessageText.MaxHeight = maxHeight;

            var textHeight = MeasureComposerTextHeight(MessageText.Text, MessageText.ActualWidth);
            if (textHeight < 56) textHeight = 56;
            if (textHeight > maxHeight) textHeight = maxHeight;

            MessageText.Height = textHeight;
            if (ComposerPanel != null)
                ComposerPanel.MinHeight = textHeight;
            MessageText.VerticalContentAlignment = VerticalAlignment.Top;
        }

        private double GetComposerTextMaxHeight()
        {
            var height = ActualHeight;
            if (height <= 0 && Window.Current != null)
                height = Window.Current.Bounds.Height;

            if (height <= 0)
                return 260;

            var max = height * 0.42;
            if (max < 120) max = 120;
            if (max > 260) max = 260;
            return max;
        }

        private double MeasureComposerTextHeight(string text, double width)
        {
            if (width <= 0) width = 180;

            var measureWidth = width - MessageText.Margin.Left - MessageText.Margin.Right - 8;
            if (measureWidth < 80) measureWidth = 80;

            var probe = new TextBlock();
            probe.Text = string.IsNullOrEmpty(text) ? " " : text;
            probe.FontSize = MessageText.FontSize;
            probe.FontWeight = MessageText.FontWeight;
            probe.FontFamily = MessageText.FontFamily;
            probe.TextWrapping = TextWrapping.Wrap;
            probe.Width = measureWidth;
            probe.Measure(new Size(measureWidth, double.PositiveInfinity));

            return Math.Ceiling(probe.DesiredSize.Height + 20);
        }

        private int EstimateComposerLineCount(string text, double width)
        {
            if (string.IsNullOrEmpty(text)) return 1;
            var charsPerLine = (int)((width <= 0 ? 180 : width) / 8.5);
            if (charsPerLine < 12) charsPerLine = 12;

            var lines = 1;
            var current = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '\r') continue;
                if (ch == '\n')
                {
                    lines++;
                    current = 0;
                    continue;
                }

                current++;
                if (current >= charsPerLine)
                {
                    lines++;
                    current = 0;
                }
            }
            return lines;
        }

        private bool HasComposerPayload()
        {
            return !string.IsNullOrWhiteSpace(MessageText.Text) || _pendingPhotoAttachments.Count > 0;
        }

        private void SendButton_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_chat == null || !_chat.CanSendMessages || HasComposerPayload() || IsAnyRecording() || _recordPressPending || _voiceStartTask != null || _voiceFinishInProgress) return;
            _voiceRecordStartX = e.GetCurrentPoint(ComposerPanel).Position.X;
            _voiceRecordCanceled = false;
            _recordPressPending = true;
            var button = sender as UIElement;
            _recordCapturedElement = button;
            _recordCapturedPointer = e.Pointer;
            if (button != null) button.CapturePointer(e.Pointer);
            _recordPressTimer.Stop();
            _recordPressTimer.Start();
            e.Handled = true;
        }

        private void SendButton_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!IsAnyRecording() || _isVideoNoteRecording) return;
            var x = e.GetCurrentPoint(ComposerPanel).Position.X;
            _voiceRecordCanceled = _voiceRecordStartX - x > 96;
            if (VoiceCancelHint != null)
            {
                VoiceCancelHint.Text = _voiceRecordCanceled ? "Release to cancel" : "< Cancel";
                VoiceCancelHint.Foreground = new SolidColorBrush(_voiceRecordCanceled ? Windows.UI.Color.FromArgb(255, 41, 182, 246) : Windows.UI.Color.FromArgb(255, 179, 179, 179));
            }
            e.Handled = true;
        }

        private async void SendButton_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_recordPressPending)
            {
                _recordPressTimer.Stop();
                _recordPressPending = false;
                _suppressNextEmptySendButtonClick = true;
                ToggleVideoNoteMode();
                var pendingButton = sender as UIElement;
                if (pendingButton != null) pendingButton.ReleasePointerCapture(e.Pointer);
                ClearRecordPointerCapture();
                e.Handled = true;
                return;
            }

            if (!IsAnyRecording() || _voiceFinishInProgress) return;
            if (_isVideoNoteRecording)
            {
                e.Handled = true;
                var videoButton = sender as UIElement;
                if (videoButton != null) videoButton.ReleasePointerCapture(e.Pointer);
                ClearRecordPointerCapture();
                return;
            }

            e.Handled = true;
            await FinishVoiceRecordingAsync(_voiceRecordCanceled);
            var button = sender as UIElement;
            if (button != null) button.ReleasePointerCapture(e.Pointer);
            ClearRecordPointerCapture();
        }

        private async void SendButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (_isVideoNoteRecording)
            {
                e.Handled = true;
                ClearRecordPointerCapture();
                return;
            }

            if (_recordPressPending)
            {
                _recordPressTimer.Stop();
                _recordPressPending = false;
                ClearRecordPointerCapture();
                e.Handled = true;
                return;
            }

            if (!IsAnyRecording() || _voiceFinishInProgress) return;
            e.Handled = true;
            await FinishVoiceRecordingAsync(true);
            ClearRecordPointerCapture();
        }

        private void RecordPressTimer_Tick(object sender, object e)
        {
            _recordPressTimer.Stop();
            if (!_recordPressPending) return;
            _recordPressPending = false;
            if (_chat == null || !_chat.CanSendMessages || HasComposerPayload() || IsAnyRecording() || _voiceStartTask != null || _voiceFinishInProgress) return;
            if (_isVideoNoteMode)
            {
                SuppressNextEmptySendButtonClickBriefly();
                _voiceStartTask = StartVideoNoteRecordingAsync();
            }
            else
            {
                _voiceStartTask = StartVoiceRecordingAsync();
            }
        }

        private void ClearRecordPointerCapture()
        {
            try
            {
                if (_recordCapturedElement != null && _recordCapturedPointer != null)
                    _recordCapturedElement.ReleasePointerCapture(_recordCapturedPointer);
            }
            catch
            {
            }

            _recordCapturedElement = null;
            _recordCapturedPointer = null;
        }

        private void SuppressNextEmptySendButtonClickBriefly()
        {
            _suppressNextEmptySendButtonClick = true;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                await System.Threading.Tasks.Task.Delay(450);
                _suppressNextEmptySendButtonClick = false;
            });
        }

        private void ToggleVideoNoteMode()
        {
            if (_chat == null || !_chat.CanSendMessages || HasComposerPayload() || IsAnyRecording()) return;
            _isVideoNoteMode = !_isVideoNoteMode;
            SetEmojiKeyboardVisible(false);
            UpdateComposerState();
        }

        private async System.Threading.Tasks.Task<bool> EnsureRecordingAccessAsync(bool includeVideo)
        {
            var audioStatus = await RequestAudioCaptureAccessOnceAsync();
            if (audioStatus != DeviceAccessStatus.Allowed)
            {
                await ShowChatAlertAsync("Microphone access", "Allow microphone access in system settings to record voice messages.");
                return false;
            }

            if (!includeVideo)
                return true;

            var videoStatus = await RequestVideoCaptureAccessOnceAsync();
            if (videoStatus != DeviceAccessStatus.Allowed)
            {
                await ShowChatAlertAsync("Camera access", "Allow camera access in system settings to record video messages.");
                return false;
            }

            return true;
        }

        private static System.Threading.Tasks.Task<DeviceAccessStatus> RequestAudioCaptureAccessOnceAsync()
        {
            if (_audioCaptureAccessStatus.HasValue)
                return System.Threading.Tasks.Task.FromResult(_audioCaptureAccessStatus.Value);

            if (_audioCaptureAccessTask == null)
                _audioCaptureAccessTask = RequestCaptureAccessOnceAsync(DeviceClass.AudioCapture, delegate(DeviceAccessStatus status)
                {
                    _audioCaptureAccessStatus = status;
                    _audioCaptureAccessTask = null;
                });

            return _audioCaptureAccessTask;
        }

        private static System.Threading.Tasks.Task<DeviceAccessStatus> RequestVideoCaptureAccessOnceAsync()
        {
            if (_videoCaptureAccessStatus.HasValue)
                return System.Threading.Tasks.Task.FromResult(_videoCaptureAccessStatus.Value);

            if (_videoCaptureAccessTask == null)
                _videoCaptureAccessTask = RequestCaptureAccessOnceAsync(DeviceClass.VideoCapture, delegate(DeviceAccessStatus status)
                {
                    _videoCaptureAccessStatus = status;
                    _videoCaptureAccessTask = null;
                });

            return _videoCaptureAccessTask;
        }

        private static async System.Threading.Tasks.Task<DeviceAccessStatus> RequestCaptureAccessOnceAsync(DeviceClass deviceClass, Action<DeviceAccessStatus> completed)
        {
            var status = DeviceAccessStatus.Unspecified;
            try
            {
                var access = DeviceAccessInformation.CreateFromDeviceClass(deviceClass);
                if (access != null)
                    status = access.CurrentStatus;

                if (status == DeviceAccessStatus.Unspecified)
                    status = await ProbeCaptureAccessAsync(deviceClass);
            }
            catch
            {
                status = DeviceAccessStatus.DeniedBySystem;
            }

            if (completed != null)
                completed(status);
            return status;
        }

        private static async System.Threading.Tasks.Task<DeviceAccessStatus> ProbeCaptureAccessAsync(DeviceClass deviceClass)
        {
            MediaCapture probe = null;
            try
            {
                probe = new MediaCapture();
                var settings = new MediaCaptureInitializationSettings();
                settings.StreamingCaptureMode = deviceClass == DeviceClass.VideoCapture
                    ? StreamingCaptureMode.Video
                    : StreamingCaptureMode.Audio;
                await probe.InitializeAsync(settings);
                return DeviceAccessStatus.Allowed;
            }
            catch (UnauthorizedAccessException)
            {
                return DeviceAccessStatus.DeniedByUser;
            }
            catch
            {
                return DeviceAccessStatus.DeniedBySystem;
            }
            finally
            {
                try
                {
                    if (probe != null)
                        probe.Dispose();
                }
                catch
                {
                }
            }
        }

        private async System.Threading.Tasks.Task StartVoiceRecordingAsync()
        {
            if (_voiceFinishInProgress || _voiceStartTask != null && _voiceStartTask.Status == System.Threading.Tasks.TaskStatus.Running) return;
            try
            {
                if (!await EnsureRecordingAccessAsync(false))
                {
                    _voiceStartTask = null;
                    return;
                }

                _voiceRecordCanceled = false;
                _isVoiceRecording = true;
                if (VoiceCancelHint != null)
                {
                    VoiceCancelHint.Text = "< Cancel";
                    VoiceCancelHint.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 179, 179, 179));
                }
                UpdateComposerState();

                _voiceDisplayRequest.RequestActive();
                _voiceCapture = new MediaCapture();
                var settings = new MediaCaptureInitializationSettings();
                settings.StreamingCaptureMode = StreamingCaptureMode.Audio;
                await _voiceCapture.InitializeAsync(settings);

                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("voice", CreationCollisionOption.OpenIfExists);
                _voiceRecordFile = await folder.CreateFileAsync("voice_" + DateTime.UtcNow.Ticks + ".m4a", CreationCollisionOption.GenerateUniqueName);
                await _voiceCapture.StartRecordToStorageFileAsync(MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Auto), _voiceRecordFile);
                _voiceRecordStarted = true;
                _voiceRecordStartedAt = DateTime.UtcNow;
                SendChatActionFireAndForget("recording_voice");
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Audio recording error", AlertErrorMessage(ex, "Could not start audio recording."));
                _isVoiceRecording = false;
                _voiceRecordStarted = false;
                _voiceStartTask = null;
                UpdateComposerState();
            }
        }

        private async System.Threading.Tasks.Task StartVideoNoteRecordingAsync()
        {
            if (_voiceFinishInProgress || _voiceStartTask != null && _voiceStartTask.Status == System.Threading.Tasks.TaskStatus.Running) return;
            try
            {
                if (!await EnsureRecordingAccessAsync(true))
                {
                    _voiceStartTask = null;
                    return;
                }

                _voiceRecordCanceled = false;
                _isVideoNoteRecording = true;
                if (VoiceCancelHint != null)
                {
                    VoiceCancelHint.Text = "< Cancel";
                    VoiceCancelHint.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 179, 179, 179));
                }
                UpdateComposerState();

                _voiceDisplayRequest.RequestActive();
                _voiceCapture = new MediaCapture();
                var settings = new MediaCaptureInitializationSettings();
                settings.StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo;
                var videoDeviceId = await SelectVideoNoteCameraIdAsync();
                if (!string.IsNullOrEmpty(videoDeviceId))
                    settings.VideoDeviceId = videoDeviceId;
                await _voiceCapture.InitializeAsync(settings);

                var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
                TryConfigureVideoNoteProfile(profile);
                await ApplyVideoNoteCaptureSettingsAsync(profile);

                if (VideoNoteCapturePreview != null)
                {
                    VideoNoteCapturePreview.Source = _voiceCapture;
                    await _voiceCapture.StartPreviewAsync();
                }

                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("video_notes", CreationCollisionOption.OpenIfExists);
                _voiceRecordFile = await folder.CreateFileAsync("video_note_" + DateTime.UtcNow.Ticks + ".mp4", CreationCollisionOption.GenerateUniqueName);
                await _voiceCapture.StartRecordToStorageFileAsync(profile, _voiceRecordFile);
                _voiceRecordStarted = true;
                _voiceRecordStartedAt = DateTime.UtcNow;
                StartRecordDurationTimer();
                SendChatActionFireAndForget("recording_video_note");
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Video message recording error", AlertErrorMessage(ex, "Could not start video message recording."));
                StopRecordDurationTimer();
                try
                {
                    if (_voiceCapture != null)
                    {
                        try { await _voiceCapture.StopPreviewAsync(); }
                        catch { }
                        _voiceCapture.Dispose();
                    }
                }
                catch { }
                try { _voiceDisplayRequest.RequestRelease(); }
                catch { }
                _voiceCapture = null;
                _voiceRecordFile = null;
                _isVideoNoteRecording = false;
                _voiceRecordStarted = false;
                _voiceStartTask = null;
                UpdateComposerState();
            }
        }

        private async System.Threading.Tasks.Task FinishVoiceRecordingAsync(bool cancel)
        {
            if (_voiceFinishInProgress) return;
            _voiceFinishInProgress = true;
            var wasVideoNote = _isVideoNoteRecording;
            var startTask = _voiceStartTask;
            if (startTask != null)
            {
                try { await startTask; }
                catch { cancel = true; }
            }

            if (wasVideoNote)
            {
                await FinishVideoNoteRecordingAsync(cancel);
                return;
            }

            var file = _voiceRecordFile;
            var startedAt = _voiceRecordStartedAt;
            var started = _voiceRecordStarted;
            Exception stopRecordException = null;
            try
            {
                if (_voiceCapture != null && started)
                    await _voiceCapture.StopRecordAsync();
            }
            catch (Exception ex)
            {
                stopRecordException = ex;
            }

            if (wasVideoNote)
            {
                try
                {
                    if (_voiceCapture != null)
                        await _voiceCapture.StopPreviewAsync();
                }
                catch
                {
                }
                if (VideoNoteCapturePreview != null)
                    VideoNoteCapturePreview.Source = null;
            }

            try
            {
                if (_voiceCapture != null) _voiceCapture.Dispose();
            }
            catch { }

            try { _voiceDisplayRequest.RequestRelease(); }
            catch { }

            _voiceCapture = null;
            _voiceRecordFile = null;
            _voiceStartTask = null;
            _isVoiceRecording = false;
            _isVideoNoteRecording = false;
            _voiceRecordStarted = false;
            _voiceRecordCanceled = false;
            _voiceFinishInProgress = false;
            UpdateComposerState();
            SendChatActionFireAndForget("cancel");

            if (cancel)
            {
                await SafeDeleteStorageFileAsync(file);
                return;
            }

            if (stopRecordException != null)
            {
                await ShowChatAlertAsync(wasVideoNote ? "Video message recording error" : "Audio recording error",
                    AlertErrorMessage(stopRecordException, wasVideoNote ? "Could not finish video message recording." : "Could not finish audio recording."));
                await SafeDeleteStorageFileAsync(file);
                return;
            }

            if (file != null)
            {
                var duration = 1;
                if (startedAt != default(DateTime))
                    duration = Math.Max(1, (int)Math.Round((DateTime.UtcNow - startedAt).TotalSeconds));

                if (!await WaitForRecordedAudioFileReadyAsync(file))
                {
                    await ShowChatAlertAsync(wasVideoNote ? "Video message recording error" : "Audio recording error",
                        wasVideoNote ? "Video message was not recorded." : "Audio was not recorded.");
                    await SafeDeleteStorageFileAsync(file);
                    return;
                }

                if (wasVideoNote)
                    await SendRecordedVideoNoteAsync(file, duration);
                else
                    await SendRecordedAudioAsync(file, duration);
            }
        }

        private async System.Threading.Tasks.Task FinishVideoNoteRecordingAsync(bool cancel)
        {
            StorageFile file = null;
            Exception stopRecordException = null;
            var startedAt = _voiceRecordStartedAt;
            try
            {
                file = await StopCurrentVideoNoteSegmentAsync(true);
            }
            catch (Exception ex)
            {
                stopRecordException = ex;
            }

            _voiceCapture = null;
            _voiceRecordFile = null;
            _voiceStartTask = null;
            _isVideoNoteRecording = false;
            _voiceRecordStarted = false;
            _voiceRecordCanceled = false;
            _voiceFinishInProgress = false;
            StopRecordDurationTimer();
            UpdateComposerState();
            SendChatActionFireAndForget("cancel");

            if (cancel)
            {
                await SafeDeleteStorageFileAsync(file);
                return;
            }

            if (stopRecordException != null)
            {
                await ShowChatAlertAsync("Video message recording error",
                    AlertErrorMessage(stopRecordException, "Could not finish video message recording."));
                await SafeDeleteStorageFileAsync(file);
                return;
            }

            if (file == null)
            {
                await ShowChatAlertAsync("Video message recording error", "Video message was not recorded.");
                return;
            }

            var duration = 1;
            if (startedAt != default(DateTime))
                duration = Math.Max(1, (int)Math.Round((DateTime.UtcNow - startedAt).TotalSeconds));

            await SendRecordedVideoNoteAsync(file, duration);
        }

        private async System.Threading.Tasks.Task<string> SelectVideoNoteCameraIdAsync()
        {
            try
            {
                var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
                if (devices == null || devices.Count == 0) return null;

                DeviceInformation fallback = null;
                for (var i = 0; i < devices.Count; i++)
                {
                    var device = devices[i];
                    if (device == null) continue;
                    if (fallback == null) fallback = device;

                    var location = device.EnclosureLocation;
                    if (location == null) continue;
                    if (location.Panel == Windows.Devices.Enumeration.Panel.Front)
                        return device.Id;
                }

                return fallback == null ? null : fallback.Id;
            }
            catch
            {
                return null;
            }
        }

        private static void TryConfigureVideoNoteProfile(MediaEncodingProfile profile)
        {
            try
            {
                if (profile == null || profile.Video == null) return;
                profile.Video.Width = 480;
                profile.Video.Height = 480;
                profile.Video.Bitrate = 900000;
                profile.Video.FrameRate.Numerator = 30;
                profile.Video.FrameRate.Denominator = 1;
                if (profile.Video.PixelAspectRatio != null)
                {
                    profile.Video.PixelAspectRatio.Numerator = 1;
                    profile.Video.PixelAspectRatio.Denominator = 1;
                }
            }
            catch
            {
            }
        }

        private async System.Threading.Tasks.Task ApplyVideoNoteCaptureSettingsAsync(MediaEncodingProfile profile)
        {
            if (_voiceCapture == null) return;

            try { _voiceCapture.SetPreviewRotation(VideoRotation.Clockwise270Degrees); }
            catch { }

            try { _voiceCapture.SetRecordRotation(VideoRotation.Clockwise270Degrees); }
            catch { }

            try
            {
                if (profile != null && profile.Video != null)
                    await _voiceCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoRecord, profile.Video);
            }
            catch
            {
            }
        }

        private async System.Threading.Tasks.Task<StorageFile> StopCurrentVideoNoteSegmentAsync(bool releaseDisplayRequest)
        {
            var file = _voiceRecordFile;
            var started = _voiceRecordStarted;
            Exception stopRecordException = null;

            try
            {
                if (_voiceCapture != null && started)
                    await _voiceCapture.StopRecordAsync();
            }
            catch (Exception ex)
            {
                stopRecordException = ex;
            }

            try
            {
                if (_voiceCapture != null)
                    await _voiceCapture.StopPreviewAsync();
            }
            catch
            {
            }

            if (VideoNoteCapturePreview != null)
                VideoNoteCapturePreview.Source = null;

            try
            {
                if (_voiceCapture != null)
                    _voiceCapture.Dispose();
            }
            catch
            {
            }

            if (releaseDisplayRequest)
            {
                try { _voiceDisplayRequest.RequestRelease(); }
                catch { }
            }

            _voiceCapture = null;
            _voiceRecordFile = null;
            _voiceRecordStarted = false;

            if (stopRecordException != null)
                throw stopRecordException;

            if (file == null) return null;
            if (!await WaitForRecordedAudioFileReadyAsync(file))
            {
                await SafeDeleteStorageFileAsync(file);
                return null;
            }

            return file;
        }

        private async System.Threading.Tasks.Task<bool> WaitForRecordedAudioFileReadyAsync(StorageFile file)
        {
            if (file == null) return false;
            ulong lastSize = 0;
            var stableReads = 0;
            for (var i = 0; i < 16; i++)
            {
                try
                {
                    if (!string.IsNullOrEmpty(file.Path))
                        await StorageFile.GetFileFromPathAsync(file.Path);
                    var props = await file.GetBasicPropertiesAsync();
                    if (props != null && props.Size > 0)
                    {
                        if (props.Size == lastSize)
                            stableReads++;
                        else
                        {
                            lastSize = props.Size;
                            stableReads = 0;
                        }

                        if (stableReads >= 1) return true;
                    }
                }
                catch
                {
                }

                await System.Threading.Tasks.Task.Delay(120);
            }

            return false;
        }

        private async System.Threading.Tasks.Task SafeDeleteStorageFileAsync(StorageFile file)
        {
            if (file == null) return;
            try
            {
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
            }
        }

        private System.Threading.Tasks.Task SendRecordedAudioAsync(StorageFile file, int duration)
        {
            if (_chat == null || file == null || !_chat.CanSendMessages) return System.Threading.Tasks.Task.FromResult<object>(null);
            var maxIdBeforeSend = GetNewestMessageId();
            var sendKind = "voice";
            var attachment = new PendingPhotoAttachment { File = file, FileName = file.Name, Kind = sendKind };
            var pending = CreatePendingOutgoingMediaMessage(attachment, string.Empty);
            var chat = _chat;
            var replyToMessageId = _replyToMessageId;

            AddPendingOutgoingMessage(pending);
            ScrollToBottomSoon();
            DismissUnreadSeparatorAfterOutgoing();
            ClearReply();
            SendChatActionFireAndForget(sendKind == "voice" ? "uploading_voice" : "uploading_document");

            StartOutgoingSend(
                delegate { return TelegramService.Instance.SendMediaAsync(chat, file, sendKind, string.Empty, replyToMessageId, duration); },
                new List<ChatMessageViewModel> { pending },
                maxIdBeforeSend,
                "Audio sending error",
                "Could not send audio.");

            return System.Threading.Tasks.Task.FromResult<object>(null);
        }

        private System.Threading.Tasks.Task SendRecordedVideoNoteAsync(StorageFile file, int duration)
        {
            if (_chat == null || file == null || !_chat.CanSendMessages) return System.Threading.Tasks.Task.FromResult<object>(null);
            var maxIdBeforeSend = GetNewestMessageId();
            var sendKind = "roundvideo";
            var attachment = new PendingPhotoAttachment { File = file, FileName = file.Name, Kind = sendKind };
            var pending = CreatePendingOutgoingMediaMessage(attachment, string.Empty);
            var chat = _chat;
            var replyToMessageId = _replyToMessageId;

            AddPendingOutgoingMessage(pending);
            ScrollToBottomSoon();
            DismissUnreadSeparatorAfterOutgoing();
            ClearReply();
            SendChatActionFireAndForget("uploading_video_note");

            StartOutgoingSend(
                delegate { return TelegramService.Instance.SendMediaAsync(chat, file, sendKind, string.Empty, replyToMessageId, duration); },
                new List<ChatMessageViewModel> { pending },
                maxIdBeforeSend,
                "Video message sending error",
                "Could not send video message.");

            return System.Threading.Tasks.Task.FromResult<object>(null);
        }

        private void VideoNotePreviewHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateVideoNotePreviewMask();
        }

        private void StartRecordDurationTimer()
        {
            UpdateRecordDurationText();
            if (VideoNoteDurationBadge != null)
                VideoNoteDurationBadge.Visibility = Visibility.Visible;
            if (_recordDurationTimer != null && !_recordDurationTimer.IsEnabled)
                _recordDurationTimer.Start();
        }

        private void StopRecordDurationTimer()
        {
            if (_recordDurationTimer != null)
                _recordDurationTimer.Stop();
            if (VideoNoteDurationText != null)
                VideoNoteDurationText.Text = "0:00";
            if (VideoNoteDurationBadge != null)
                VideoNoteDurationBadge.Visibility = Visibility.Collapsed;
        }

        private void RecordDurationTimer_Tick(object sender, object e)
        {
            UpdateRecordDurationText();
        }

        private void UpdateRecordDurationText()
        {
            if (VideoNoteDurationText == null) return;

            var elapsed = 0;
            if (_voiceRecordStartedAt != default(DateTime))
                elapsed = Math.Max(0, (int)Math.Floor((DateTime.UtcNow - _voiceRecordStartedAt).TotalSeconds));

            var minutes = elapsed / 60;
            var seconds = elapsed % 60;
            VideoNoteDurationText.Text = minutes.ToString() + ":" + seconds.ToString("00");
        }

        private void UpdateVideoNotePreviewMask()
        {
            if (VideoNotePreviewMask == null || VideoNotePreviewHost == null) return;
            var width = VideoNotePreviewHost.ActualWidth > 0 ? VideoNotePreviewHost.ActualWidth : 196;
            var height = VideoNotePreviewHost.ActualHeight > 0 ? VideoNotePreviewHost.ActualHeight : 196;
            VideoNotePreviewMask.Data = BuildCircularMaskGeometry(width, height);
        }

        private static Geometry BuildCircularMaskGeometry(double width, double height)
        {
            if (width <= 0) width = 196;
            if (height <= 0) height = 196;

            var radius = Math.Min(width, height) / 2;
            var center = new Point(width / 2, height / 2);

            var outer = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true };
            outer.Segments.Add(new LineSegment { Point = new Point(width, 0) });
            outer.Segments.Add(new LineSegment { Point = new Point(width, height) });
            outer.Segments.Add(new LineSegment { Point = new Point(0, height) });

            var inner = new PathFigure { StartPoint = new Point(center.X, center.Y - radius), IsClosed = true };
            inner.Segments.Add(new ArcSegment { Point = new Point(center.X, center.Y + radius), Size = new Size(radius, radius), IsLargeArc = true, SweepDirection = SweepDirection.Clockwise });
            inner.Segments.Add(new ArcSegment { Point = new Point(center.X, center.Y - radius), Size = new Size(radius, radius), IsLargeArc = true, SweepDirection = SweepDirection.Clockwise });

            var geometry = new PathGeometry();
            geometry.FillRule = FillRule.EvenOdd;
            geometry.Figures.Add(outer);
            geometry.Figures.Add(inner);
            return geometry;
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chat == null || !_chat.CanSendMessages) return;
            if (_suppressNextEmptySendButtonClick && !HasComposerPayload())
            {
                _suppressNextEmptySendButtonClick = false;
                return;
            }
            if (_isVideoNoteRecording)
            {
                await FinishVoiceRecordingAsync(false);
                return;
            }
            if (_recordPressPending)
            {
                _recordPressTimer.Stop();
                _recordPressPending = false;
                ToggleVideoNoteMode();
                return;
            }
            if (!HasComposerPayload())
            {
                if (!IsAnyRecording() && _voiceStartTask == null && !_voiceFinishInProgress)
                    ToggleVideoNoteMode();
                return;
            }

            var chat = _chat;
            var text = MessageText.Text == null ? string.Empty : MessageText.Text.Trim();
            if (_pendingPhotoAttachments.Count == 0)
            {
                SendTextMessageFast(chat, text, _replyToMessageId);
                return;
            }

            var pendingPhotos = new List<PendingPhotoAttachment>();
            for (var i = 0; i < _pendingPhotoAttachments.Count; i++)
                pendingPhotos.Add(_pendingPhotoAttachments[i]);

            var maxIdBeforeSend = GetNewestMessageId();
            var replyToMessageId = _replyToMessageId;
            var pendingMediaMessages = new List<ChatMessageViewModel>();

            try
            {
                CancelTypingAction();
                MessageText.Text = string.Empty;
                _pendingPhotoAttachments.Clear();
                ClearReply();
                DismissUnreadSeparatorAfterOutgoing();
                UpdateComposerState();

                if (pendingPhotos.Count == 0)
                {
                    var pendingTextMessage = CreatePendingOutgoingTextMessage(text);
                    AddPendingOutgoingMessage(pendingTextMessage);
                    ScrollToBottomSoon();
                    StartOutgoingSend(
                        delegate { return TelegramService.Instance.SendTextAsync(chat, text, replyToMessageId); },
                        new List<ChatMessageViewModel> { pendingTextMessage },
                        maxIdBeforeSend,
                        "Send error",
                        "Could not send message.");
                }
                else
                {
                    for (var i = 0; i < pendingPhotos.Count; i++)
                    {
                        var caption = i == 0 ? text : string.Empty;
                        var pendingMediaMessage = CreatePendingOutgoingMediaMessage(pendingPhotos[i], caption);
                        pendingMediaMessages.Add(pendingMediaMessage);
                        AddPendingOutgoingMessage(pendingMediaMessage);
                        ScrollToBottomSoon();
                    }

                    if (pendingPhotos.Count == 1)
                    {
                        SendChatActionFireAndForget(GuessUploadChatAction(pendingPhotos[0].Kind));
                        StartOutgoingSend(
                            delegate { return TelegramService.Instance.SendMediaAsync(chat, pendingPhotos[0].File, pendingPhotos[0].Kind, text, replyToMessageId); },
                            pendingMediaMessages,
                            maxIdBeforeSend,
                            "Send error",
                            "Could not send media.");
                    }
                    else
                    {
                        var files = new List<StorageFile>();
                        var kinds = new List<string>();
                        for (var i = 0; i < pendingPhotos.Count; i++)
                        {
                            files.Add(pendingPhotos[i].File);
                            kinds.Add(pendingPhotos[i].Kind);
                        }
                        SendChatActionFireAndForget(GuessUploadChatAction(kinds));
                        StartOutgoingSend(
                            delegate { return TelegramService.Instance.SendMediaAlbumAsync(chat, files, kinds, text, replyToMessageId); },
                            pendingMediaMessages,
                            maxIdBeforeSend,
                            "Send error",
                            "Could not send media album.");
                    }
                }
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Send error", AlertErrorMessage(ex, "Could not send message."));
                for (var i = 0; i < pendingMediaMessages.Count; i++)
                    if (pendingMediaMessages[i] != null) pendingMediaMessages[i].IsSending = false;
            }
        }

        private void SendTextMessageFast(ChatViewModel chat, string text, int replyToMessageId)
        {
            if (chat == null || string.IsNullOrWhiteSpace(text)) return;

            var maxIdBeforeSend = GetNewestMessageId();
            var pendingTextMessage = CreatePendingOutgoingTextMessage(text);

            try
            {
                _suppressComposerTextChanged = true;
                if (MessageText != null) MessageText.Text = string.Empty;
            }
            finally
            {
                _suppressComposerTextChanged = false;
            }

            ClearReply();
            DismissUnreadSeparatorAfterOutgoing();

            AddPendingOutgoingMessage(pendingTextMessage);
            ScrollMessageIntoViewSoon(pendingTextMessage);

            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
            {
                if (!object.ReferenceEquals(chat, _chat)) return;
                CancelTypingAction();
                StartOutgoingSend(
                    delegate { return TelegramService.Instance.SendTextAsync(chat, text, replyToMessageId); },
                    new List<ChatMessageViewModel> { pendingTextMessage },
                    maxIdBeforeSend,
                    "Send error",
                    "Could not send message.",
                    700);
            });
        }

        private void StartOutgoingSend(Func<System.Threading.Tasks.Task> sendAction, IList<ChatMessageViewModel> pendingMessages, int maxIdBeforeSend, string errorTitle, string errorFallback)
        {
            StartOutgoingSend(sendAction, pendingMessages, maxIdBeforeSend, errorTitle, errorFallback, 0, false);
        }

        private void StartOutgoingSend(Func<System.Threading.Tasks.Task> sendAction, IList<ChatMessageViewModel> pendingMessages, int maxIdBeforeSend, string errorTitle, string errorFallback, int delayBeforeSendMs)
        {
            StartOutgoingSend(sendAction, pendingMessages, maxIdBeforeSend, errorTitle, errorFallback, delayBeforeSendMs, false);
        }

        private void StartOutgoingSend(Func<System.Threading.Tasks.Task> sendAction, IList<ChatMessageViewModel> pendingMessages, int maxIdBeforeSend, string errorTitle, string errorFallback, int delayBeforeSendMs, bool refreshAfterSuccess)
        {
            if (sendAction == null) return;
            var chatAtSend = _chat;
            var pending = new List<ChatMessageViewModel>();
            if (pendingMessages != null)
            {
                for (var i = 0; i < pendingMessages.Count; i++)
                    if (pendingMessages[i] != null) pending.Add(pendingMessages[i]);
            }

            var ignored = System.Threading.Tasks.Task.Run(async delegate
            {
                Exception error = null;
                try
                {
                    if (delayBeforeSendMs > 0)
                        await System.Threading.Tasks.Task.Delay(delayBeforeSendMs).ConfigureAwait(false);
                    await sendAction().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
                {
                    if (!object.ReferenceEquals(chatAtSend, _chat)) return;

                    SendChatActionFireAndForget("cancel");
                    if (error != null)
                    {
                        for (var i = 0; i < pending.Count; i++)
                            pending[i].IsSending = false;
                        ShowChatAlert(errorTitle, AlertErrorMessage(error, errorFallback));
                    }
                    else
                    {
                        if (refreshAfterSuccess)
                        {
                            var ignoredRefresh = RefreshMessagesAfterSendAsync(maxIdBeforeSend);
                        }
                    }
                    UpdateComposerState();
                });
            });
        }

        private ChatMessageViewModel CreatePendingOutgoingTextMessage(string text)
        {
            return new ChatMessageViewModel
            {
                Id = NextPendingMessageId(),
                Date = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds,
                IsOutgoing = true,
                IsGroupChat = _chat != null && (_chat.IsGroup || _chat.PeerType == "chat" || (_chat.PeerType == "channel" && !_chat.IsBroadcast)),
                IsChannelPost = IsCurrentChatChannelPost(),
                Text = text,
                IsSending = true,
                IsRead = false
            };
        }

        private ChatMessageViewModel CreatePendingOutgoingMediaMessage(PendingPhotoAttachment attachment, string caption)
        {
            var kind = attachment == null ? "photo" : attachment.Kind;
            var text = caption;
            if (string.IsNullOrWhiteSpace(text))
            {
                if (kind == "video") text = "Sending video...";
                else if (kind == "roundvideo") text = "Sending round video...";
                else if (kind == "sticker") text = "Sending sticker...";
                else if (kind == "voice") text = "Sending voice message...";
                else if (kind == "audio") text = "Sending audio...";
                else if (kind == "document") text = "Sending file...";
                else text = "Sending photo...";
            }

            return new ChatMessageViewModel
            {
                Id = NextPendingMessageId(),
                Date = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds,
                IsOutgoing = true,
                IsGroupChat = _chat != null && (_chat.IsGroup || _chat.PeerType == "chat" || (_chat.PeerType == "channel" && !_chat.IsBroadcast)),
                IsChannelPost = IsCurrentChatChannelPost(),
                Text = text,
                MediaKind = kind,
                IsSending = true,
                IsRead = false
            };
        }

        private int NextPendingMessageId()
        {
            if (_pendingMessageIdSeed >= -1) _pendingMessageIdSeed = -1;
            return _pendingMessageIdSeed--;
        }

        private bool IsCurrentChatChannelPost()
        {
            return _chat != null && (_chat.IsBroadcast || (_chat.IsChannel && !_chat.IsGroup && _chat.PeerType == "channel"));
        }

        private void AddPendingOutgoingMessage(ChatMessageViewModel message)
        {
            if (message == null) return;
            ChatMessageViewModel previous = null;
            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                previous = _messages[i] as ChatMessageViewModel;
                if (previous != null) break;
            }

            message.IsFirstInSenderGroup = !IsSameSenderAsPrevious(previous, message);
            _messages.Add(message);
            _messageKeys.Add(MessageKey(message));
        }

        private void RemovePendingOutgoingMessage(ChatMessageViewModel message)
        {
            if (message == null) return;
            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                var current = _messages[i] as ChatMessageViewModel;
                if (current == null) continue;
                if (!object.ReferenceEquals(current, message) && current.Id != message.Id) continue;
                _messageKeys.Remove(MessageKey(current));
                _messages.RemoveAt(i);
                UpdateMessageGrouping();
                UpdateOutgoingMessageStates();
                return;
            }
        }

        private void RemovePendingOutgoingMessages(IList<ChatMessageViewModel> messages)
        {
            if (messages == null || messages.Count == 0) return;
            var removed = false;
            for (var m = 0; m < messages.Count; m++)
            {
                var message = messages[m];
                if (message == null) continue;
                for (var i = _messages.Count - 1; i >= 0; i--)
                {
                    var current = _messages[i] as ChatMessageViewModel;
                    if (current == null) continue;
                    if (!object.ReferenceEquals(current, message) && current.Id != message.Id) continue;
                    _messageKeys.Remove(MessageKey(current));
                    _messages.RemoveAt(i);
                    removed = true;
                    break;
                }
            }

            if (!removed) return;
            UpdateMessageGrouping();
            UpdateOutgoingMessageStates();
        }

        private async System.Threading.Tasks.Task RefreshMessagesAfterSendAsync(int maxIdBeforeSend)
        {
            if (_chat == null) return;
            try
            {
                await System.Threading.Tasks.Task.Delay(350);
                RemoveMessagesById(await TelegramService.Instance.TakeDeletedMessageIdsAsync(_chat));
                var added = MergeMessages(await TelegramService.Instance.TakeMessageUpdatesAsync(_chat), false);
                if (added == 0)
                {
                    var fresh = await TelegramService.Instance.GetHistorySinceAsync(_chat, maxIdBeforeSend, FreshHistoryLimit);
                    added = MergeMessages(fresh, false);
                    if (added == 0)
                    {
                        await System.Threading.Tasks.Task.Delay(650);
                        RemoveMessagesById(await TelegramService.Instance.TakeDeletedMessageIdsAsync(_chat));
                        added = MergeMessages(await TelegramService.Instance.TakeMessageUpdatesAsync(_chat), false);
                        if (added == 0)
                        {
                            fresh = await TelegramService.Instance.GetHistorySinceAsync(_chat, maxIdBeforeSend, FreshHistoryLimit);
                            added = MergeMessages(fresh, false);
                        }
                    }
                }

                await RefreshFullChatInfoAsync();
                if (added > 0)
                {
                    BeginAutoDownloadMedia();
                    StartBackgroundReactionLoad();
                }
                UpdateOutgoingMessageStates();
                ScrollToBottom(false);
            }
            catch
            {
                // Do not reload the entire list after sending; the next poll will pick up the message.
            }
        }

        private void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chat == null || !_chat.CanSendMessages) return;
            ShowAttachMenu(sender as FrameworkElement);
        }

        private void ShowAttachMenu(FrameworkElement target)
        {
            if (target == null) return;

            var flyout = new MenuFlyout();

            var mediaItem = new MenuFlyoutItem { Text = "Photo or video" };
            mediaItem.Click += async delegate { await PickPhotoOrVideoAttachmentsAsync(); };
            flyout.Items.Add(mediaItem);

            var fileItem = new MenuFlyoutItem { Text = "File" };
            fileItem.Click += async delegate { await PickAndSendFileAsync(); };
            flyout.Items.Add(fileItem);

            var locationItem = new MenuFlyoutItem { Text = "Location" };
            locationItem.Click += async delegate { await SendCurrentLocationAsync(true); };
            flyout.Items.Add(locationItem);

            flyout.ShowAt(target);
        }

        private async System.Threading.Tasks.Task PickPhotoOrVideoAttachmentsAsync()
        {
            if (_chat == null || !_chat.CanSendMessages) return;
            if (!string.IsNullOrWhiteSpace(MessageText.Text)) return;

            try
            {
                var picker = CreatePhotoVideoPicker();
                var files = await picker.PickMultipleFilesAsync();
                if (files == null || files.Count == 0) return;

                for (var i = 0; i < files.Count; i++)
                    await AddPhotoAttachmentAsync(files[i]);

                UpdateComposerState();
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Media picker error", AlertErrorMessage(ex, "Could not open media picker."));
            }
        }

        private FileOpenPicker CreatePhotoVideoPicker()
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".m4v");
            picker.FileTypeFilter.Add(".webm");
            return picker;
        }

        private async System.Threading.Tasks.Task PickAndSendFileAsync()
        {
            if (_chat == null || !_chat.CanSendMessages) return;

            try
            {
                var picker = new FileOpenPicker();
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.ViewMode = PickerViewMode.List;
                picker.FileTypeFilter.Add("*");
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                await SendMediaAsync(file, "document");
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("File picker error", AlertErrorMessage(ex, "Could not open file picker."));
            }
        }

        private async System.Threading.Tasks.Task SendCurrentLocationAsync(bool confirm)
        {
            if (_chat == null || !_chat.CanSendMessages) return;

            try
            {
                if (confirm)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Share location?",
                        Content = "Send your current phone location to this chat.",
                        PrimaryButtonText = "Share",
                        SecondaryButtonText = "Cancel"
                    };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                }

                var access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync();
                if (access != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed)
                {
                    await ShowChatAlertAsync("Location error", "Location access is disabled. Enable location access for Telegram in system settings.");
                    return;
                }

                SetComposerEnabled(false);
                var geolocator = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.Default };
                var position = await geolocator.GetGeopositionAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(12));
                if (position == null || position.Coordinate == null)
                {
                    await ShowChatAlertAsync("Location error", "Could not get current location.");
                    return;
                }
                var coordinate = position.Coordinate.Point.Position;
                var accuracy = position.Coordinate.Accuracy;
                var maxId = GetNewestMessageId();
                await TelegramService.Instance.SendLocationAsync(_chat, coordinate.Latitude, coordinate.Longitude, accuracy);
                await RefreshMessagesAfterSendAsync(maxId);
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Location error", AlertErrorMessage(ex, "Could not send location."));
            }
            finally
            {
                SetComposerEnabled(true);
            }
        }

        private async System.Threading.Tasks.Task AddPhotoAttachmentAsync(StorageFile file)
        {
            if (file == null) return;

            var preview = new BitmapImage();
            try
            {
                using (var stream = await file.OpenAsync(FileAccessMode.Read))
                {
                    await preview.SetSourceAsync(stream);
                }
            }
            catch
            {
                preview = null;
            }

            _pendingPhotoAttachments.Add(new PendingPhotoAttachment
            {
                File = file,
                Preview = preview,
                FileName = file.Name,
                Kind = GuessAttachmentKind(file)
            });
        }

        private string GuessAttachmentKind(StorageFile file)
        {
            var type = file == null || file.FileType == null ? string.Empty : file.FileType.ToLowerInvariant();
            if (type == ".mp4" || type == ".mov" || type == ".m4v" || type == ".webm") return "video";
            return "photo";
        }

        private static bool IsTelegramVoiceNoteFile(StorageFile file)
        {
            var type = file == null || file.FileType == null ? string.Empty : file.FileType.ToLowerInvariant();
            return type == ".ogg" || type == ".oga" || type == ".opus";
        }

        private static string GuessUploadChatAction(string kind)
        {
            if (string.Equals(kind, "photo", StringComparison.OrdinalIgnoreCase))
                return "uploading_photo";
            if (string.Equals(kind, "video", StringComparison.OrdinalIgnoreCase))
                return "uploading_video";
            if (string.Equals(kind, "roundvideo", StringComparison.OrdinalIgnoreCase))
                return "uploading_video_note";
            if (string.Equals(kind, "voice", StringComparison.OrdinalIgnoreCase))
                return "uploading_voice";
            return "uploading_document";
        }

        private static string GuessUploadChatAction(IList<string> kinds)
        {
            if (kinds == null || kinds.Count == 0) return "uploading_document";
            for (var i = 0; i < kinds.Count; i++)
                if (string.Equals(kinds[i], "roundvideo", StringComparison.OrdinalIgnoreCase))
                    return "uploading_video_note";
            for (var i = 0; i < kinds.Count; i++)
                if (string.Equals(kinds[i], "video", StringComparison.OrdinalIgnoreCase))
                    return "uploading_video";
            for (var i = 0; i < kinds.Count; i++)
                if (string.Equals(kinds[i], "photo", StringComparison.OrdinalIgnoreCase))
                    return "uploading_photo";
            return GuessUploadChatAction(kinds[0]);
        }

        private void RemoveAttachmentButton_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var item = element == null ? null : element.DataContext as PendingPhotoAttachment;
            if (item != null) _pendingPhotoAttachments.Remove(item);
            UpdateComposerState();
        }

        private async void VideoPlaceholder_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var msg = element == null ? null : element.DataContext as ChatMessageViewModel;
            if (msg == null || msg.IsMediaDownloading) return;

            try
            {
                msg.IsMediaDownloading = true;
                if (IsVideoMediaKind(msg.MediaKind))
                    await DownloadVideoMessageForPlaybackAsync(msg);
                else
                    await TelegramService.Instance.DownloadMessageMediaAsync(_chat, msg);
            }
            catch
            {
                msg.MediaTitle = string.Empty;
                msg.MediaErrorText = string.Empty;
                msg.HasPlaybackError = false;
                ReplaceMessage(msg);
            }
            finally
            {
                msg.IsMediaDownloading = false;
                ReplaceMessage(msg);
            }
        }

        private async void PhotoButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".webp");
            var file = await picker.PickSingleFileAsync();
            if (file != null) await SendMediaAsync(file, "photo");
        }

        private async void VideoButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".m4v");
            picker.FileTypeFilter.Add(".webm");
            var file = await picker.PickSingleFileAsync();
            if (file != null) await SendMediaAsync(file, "video");
        }

        private async void AudioButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".m4a");
            picker.FileTypeFilter.Add(".ogg");
            picker.FileTypeFilter.Add(".oga");
            picker.FileTypeFilter.Add(".opus");
            picker.FileTypeFilter.Add(".wav");
            var file = await picker.PickSingleFileAsync();
            if (file != null) await SendMediaAsync(file, "audio");
        }

        private async System.Threading.Tasks.Task SendMediaAsync(StorageFile file, string kind)
        {
            if (_chat == null || file == null || !_chat.CanSendMessages) return;
            var maxIdBeforeSend = GetNewestMessageId();
            var chat = _chat;
            var replyToMessageId = _replyToMessageId;
            var caption = MessageText.Text == null ? string.Empty : MessageText.Text.Trim();
            var pending = CreatePendingOutgoingMediaMessage(new PendingPhotoAttachment { File = file, FileName = file.Name, Kind = kind }, caption);
            try
            {
                CancelTypingAction();
                MessageText.Text = string.Empty;
                ClearReply();
                DismissUnreadSeparatorAfterOutgoing();
                UpdateComposerState();
                AddPendingOutgoingMessage(pending);
                ScrollToBottomSoon();
                SendChatActionFireAndForget(GuessUploadChatAction(kind));
                StartOutgoingSend(
                    delegate { return TelegramService.Instance.SendMediaAsync(chat, file, kind, caption, replyToMessageId); },
                    new List<ChatMessageViewModel> { pending },
                    maxIdBeforeSend,
                    "File sending error",
                    "Could not send this file.");
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("File sending error", AlertErrorMessage(ex, "Could not send this file."));
                if (pending != null) pending.IsSending = false;
            }
        }

        private async void ToggleNotificationsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chat != null && _chat.IsBot && _botNeedsStart)
            {
                await StartBotAsync();
                return;
            }
            if (_chat == null) return;
            var joinAction = CanJoinCurrentChat();
            try
            {
                SetComposerEnabled(false);
                if (joinAction)
                {
                    await JoinCurrentChatAsync();
                    return;
                }

                var newMuted = !_chat.IsMuted;
                await TelegramService.Instance.SetNotificationsMutedAsync(_chat, newMuted);
                _chat.IsMuted = newMuted;
                ApplyPermissionsToUi(_historyLoaded && !_loading);
                UpdateOutgoingMessageStates();
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync(joinAction ? "Join error" : "Notifications error", AlertErrorMessage(ex, joinAction ? "Could not join this chat." : "Could not update notification settings."));
            }
            finally
            {
                SetComposerEnabled(true);
            }
        }

        private void ApplyPermissionsToUi(bool enabled)
        {
            if (_chat == null) return;
            var canSend = _chat.CanSendMessages;
            var showBotStart = _chat.IsBot && _botNeedsStart;
            var canInteractWithComposer = enabled && canSend && !showBotStart;

            ComposerPanel.Visibility = canSend && !showBotStart ? Visibility.Visible : Visibility.Collapsed;
            ReplyPanel.Visibility = canSend && !showBotStart && _replyToMessageId != 0 ? Visibility.Visible : Visibility.Collapsed;
            ReadOnlyPanel.Visibility = (!canSend || showBotStart) ? Visibility.Visible : Visibility.Collapsed;
            if (!canSend || !enabled)
                SetEmojiKeyboardVisible(false);
            var readOnlyChannel = !canSend && (_chat.IsBroadcast || _chat.IsChannel);
            ToggleNotificationsButton.Content = showBotStart ? "START" : (CanJoinCurrentChat() ? "Join" : (_chat.IsMuted ? "Turn on notifications" : "Turn off notifications"));
            if (showBotStart)
            {
                ReadOnlyText.Text = string.Empty;
                ReadOnlyText.Visibility = Visibility.Collapsed;
                Grid.SetColumn(ToggleNotificationsButton, 0);
                Grid.SetColumnSpan(ToggleNotificationsButton, 2);
                ToggleNotificationsButton.Visibility = Visibility.Visible;
            }
            else if (!canSend)
            {
                if (readOnlyChannel)
                {
                    ReadOnlyText.Text = string.Empty;
                    ReadOnlyText.Visibility = Visibility.Collapsed;
                    Grid.SetColumn(ToggleNotificationsButton, 0);
                    Grid.SetColumnSpan(ToggleNotificationsButton, 2);
                    ToggleNotificationsButton.Visibility = Visibility.Visible;
                }
                else
                {
                    ReadOnlyText.Text = "Sending messages is not available in this chat. Only notification settings are available.";
                    ReadOnlyText.Visibility = Visibility.Visible;
                    Grid.SetColumn(ToggleNotificationsButton, 1);
                    Grid.SetColumnSpan(ToggleNotificationsButton, 1);
                    ToggleNotificationsButton.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ReadOnlyText.Visibility = Visibility.Visible;
                Grid.SetColumn(ToggleNotificationsButton, 1);
                Grid.SetColumnSpan(ToggleNotificationsButton, 1);
                ToggleNotificationsButton.Visibility = Visibility.Visible;
            }

            SetElementInteractive(SendButton, canInteractWithComposer);
            SetElementInteractive(AttachButton, canInteractWithComposer);
            SetElementInteractive(MessageText, canInteractWithComposer);
            SetElementInteractive(ToggleNotificationsButton, enabled);

            if (BotMenuButton != null) BotMenuButton.Visibility = (_chat.IsBot && !showBotStart && canSend) ? Visibility.Visible : Visibility.Collapsed;
            if (EmojiButton != null) EmojiButton.Visibility = (_chat.IsBot && !showBotStart && canSend) ? Visibility.Collapsed : Visibility.Visible;

            if (MessageText != null)
                MessageText.IsReadOnly = !canInteractWithComposer;

            if (SendButton != null) SendButton.IsEnabled = true;
            if (AttachButton != null) AttachButton.IsEnabled = true;
            if (MessageText != null) MessageText.IsEnabled = true;
            if (ToggleNotificationsButton != null) ToggleNotificationsButton.IsEnabled = true;
        }

        private void UpdateBotInterfaceState()
        {
            if (_chat == null || _messages == null) return;

            var hasRealConversation = false;
            for (var i = 0; i < _messages.Count; i++)
            {
                var m = _messages[i] as ChatMessageViewModel;
                if (m == null || m.Id <= 0) continue;
                // Any ordinary message means this isn't a pristine bot chat anymore.
                hasRealConversation = true;
                if (m.IsOutgoing) break;
            }
            _botNeedsStart = _chat.IsBot && _chat.PeerType == "user" && !hasRealConversation;

            ChatMessageViewModel localMarkup = null;
            if (!_botReplyKeyboardExplicitlyRemoved)
            {
                for (var i = _messages.Count - 1; i >= 0; i--)
                {
                    var message = _messages[i] as ChatMessageViewModel;
                    if (message == null) continue;
                    if (message.RemovesReplyKeyboard) break;
                    if (message.HasReplyKeyboard)
                    {
                        localMarkup = message;
                        break;
                    }
                }
            }
            if (localMarkup != null) _activeBotReplyMarkupMessage = localMarkup;

            UpdateBotReplyKeyboardPanel();
            if (_historyLoaded) ApplyPermissionsToUi(!_loading);
        }

        private async System.Threading.Tasks.Task RefreshBotReplyMarkupFromChatAsync()
        {
            if (_chat == null || !_chat.IsBot || _botReplyMarkupLoading) return;
            if (_botReplyKeyboardExplicitlyRemoved && _chat.ReplyMarkupMessageId == 0) return;
            _botReplyMarkupLoading = true;
            try
            {
                var markup = await TelegramService.Instance.GetChatReplyMarkupMessageAsync(_chat);
                if (markup == null)
                {
                    if (_chat.ReplyMarkupMessageId == 0)
                    {
                        _activeBotReplyMarkupMessage = null;
                        _loadedBotReplyMarkupMessageId = 0;
                    }
                }
                else if (markup.HasReplyKeyboard)
                {
                    _botReplyKeyboardExplicitlyRemoved = false;
                    _activeBotReplyMarkupMessage = markup;
                    _loadedBotReplyMarkupMessageId = _chat.ReplyMarkupMessageId;
                }
                else if (markup.RemovesReplyKeyboard)
                {
                    _botReplyKeyboardExplicitlyRemoved = true;
                    _activeBotReplyMarkupMessage = null;
                    _loadedBotReplyMarkupMessageId = _chat.ReplyMarkupMessageId;
                }
                UpdateBotReplyKeyboardPanel();
            }
            catch { }
            finally { _botReplyMarkupLoading = false; }
        }

        private void UpdateBotReplyKeyboardPanel()
        {
            if (BotReplyKeyboardPanel == null || BotReplyKeyboardRows == null) return;
            var visible = !_botNeedsStart && _activeBotReplyMarkupMessage != null &&
                _activeBotReplyMarkupMessage.HasReplyKeyboard &&
                (EmojiKeyboardPanel == null || EmojiKeyboardPanel.Visibility != Visibility.Visible);
            BotReplyKeyboardRows.ItemsSource = visible ? _activeBotReplyMarkupMessage.ReplyKeyboardRows : null;
            BotReplyKeyboardPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible && MessageWatermark != null && !string.IsNullOrWhiteSpace(_activeBotReplyMarkupMessage.ReplyKeyboardPlaceholder) && string.IsNullOrEmpty(MessageText.Text))
                MessageWatermark.Text = _activeBotReplyMarkupMessage.ReplyKeyboardPlaceholder;
            else if (MessageWatermark != null)
                MessageWatermark.Text = "Type a message";
        }

        private async System.Threading.Tasks.Task StartBotAsync()
        {
            if (_chat == null || !_chat.IsBot) return;
            SetComposerEnabled(false);
            try
            {
                var maxId = GetNewestMessageId();
                await TelegramService.Instance.SendBotStartMessageAsync(_chat, string.Empty);
                _botNeedsStart = false;
                await RefreshMessagesAfterSendAsync(maxId);
                UpdateBotInterfaceState();
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Bot error", AlertErrorMessage(ex, "Could not start this bot."));
            }
            finally
            {
                SetComposerEnabled(true);
            }
        }

        private async void BotMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (_chat == null || !_chat.IsBot) return;

            // A custom Web App menu button replaces the ordinary command menu.
            if (!string.IsNullOrWhiteSpace(_chat.BotMenuButtonUrl) &&
                (_chat.BotMenuButtonType == "botMenuButtonWebApp" || _chat.BotMenuButtonType == "botMenuButton"))
            {
                try { await Windows.System.Launcher.LaunchUriAsync(new Uri(_chat.BotMenuButtonUrl)); } catch { }
                return;
            }

            var flyout = new MenuFlyout();
            var restart = new MenuFlyoutItem { Text = "Restart bot", Tag = "/start" };
            restart.Click += BotCommandMenuItem_Click;
            flyout.Items.Add(restart);

            if (_chat.BotCommands != null)
            {
                for (var i = 0; i < _chat.BotCommands.Count; i++)
                {
                    var command = _chat.BotCommands[i];
                    if (command == null || string.IsNullOrWhiteSpace(command.Command)) continue;
                    var normalized = command.Command[0] == '/' ? command.Command : "/" + command.Command;
                    if (string.Equals(normalized, "/start", StringComparison.OrdinalIgnoreCase)) continue;
                    var item = new MenuFlyoutItem { Text = command.DisplayText, Tag = normalized };
                    item.Click += BotCommandMenuItem_Click;
                    flyout.Items.Add(item);
                }
            }
            flyout.ShowAt(BotMenuButton);
        }

        private async void BotCommandMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            var command = item == null ? null : item.Tag as string;
            if (_chat == null || string.IsNullOrWhiteSpace(command)) return;
            try
            {
                var maxId = GetNewestMessageId();
                if (string.Equals(command, "/start", StringComparison.OrdinalIgnoreCase))
                    await TelegramService.Instance.SendBotStartMessageAsync(_chat, string.Empty);
                else
                    await TelegramService.Instance.SendTextAsync(_chat, command);
                await RefreshMessagesAfterSendAsync(maxId);
                await RefreshBotReplyMarkupFromChatAsync();
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Bot command", AlertErrorMessage(ex, "Could not send the bot command."));
            }
        }

        private async void BotReplyKeyboardButton_Click(object sender, RoutedEventArgs e)
        {
            var buttonControl = sender as Button;
            var button = buttonControl == null ? null : buttonControl.Tag as BotKeyboardButtonViewModel;
            if (button == null || _chat == null) return;
            await HandleBotButtonAsync(button, false);
            if (_activeBotReplyMarkupMessage != null && _activeBotReplyMarkupMessage.ReplyKeyboardOneTime)
            {
                _activeBotReplyMarkupMessage = null;
                UpdateBotReplyKeyboardPanel();
            }
        }

        private async void BotInlineButton_Click(object sender, RoutedEventArgs e)
        {
            var buttonControl = sender as Button;
            var button = buttonControl == null ? null : buttonControl.Tag as BotKeyboardButtonViewModel;
            if (button == null || _chat == null) return;
            await HandleBotButtonAsync(button, true);
        }

        private async System.Threading.Tasks.Task HandleBotButtonAsync(BotKeyboardButtonViewModel button, bool inline)
        {
            if (button == null || _chat == null) return;
            var type = button.Type ?? string.Empty;
            try
            {
                if (type == "keyboardButtonTypeText" || string.IsNullOrEmpty(type))
                {
                    var maxId = GetNewestMessageId();
                    await TelegramService.Instance.SendTextAsync(_chat, button.Text ?? string.Empty);
                    await RefreshMessagesAfterSendAsync(maxId);
                    return;
                }

                if (type == "inlineKeyboardButtonTypeCallback" || type == "inlineKeyboardButtonTypeCallbackGame")
                {
                    var answer = await TelegramService.Instance.AnswerBotCallbackAsync(_chat, button);
                    if (answer != null)
                    {
                        if (!string.IsNullOrWhiteSpace(answer.Url))
                            await Windows.System.Launcher.LaunchUriAsync(new Uri(answer.Url));
                        if (!string.IsNullOrWhiteSpace(answer.Text))
                            await ShowChatAlertAsync(answer.ShowAlert ? "Telegram" : "Bot", answer.Text);
                    }
                    return;
                }

                if (type == "inlineKeyboardButtonTypeUrl" || type == "inlineKeyboardButtonTypeLoginUrl" ||
                    type == "inlineKeyboardButtonTypeWebApp" || type == "keyboardButtonTypeWebApp")
                {
                    if (!string.IsNullOrWhiteSpace(button.Url))
                        await Windows.System.Launcher.LaunchUriAsync(new Uri(button.Url));
                    return;
                }

                if (type == "inlineKeyboardButtonTypeSwitchInline")
                {
                    SetEmojiKeyboardVisible(false);
                    MessageText.Text = button.Query ?? string.Empty;
                    MessageText.Focus(FocusState.Programmatic);
                    return;
                }

                if (type == "inlineKeyboardButtonTypeCopyText")
                {
                    var package = new DataPackage();
                    package.SetText(string.IsNullOrEmpty(button.Query) ? (button.Text ?? string.Empty) : button.Query);
                    Clipboard.SetContent(package);
                    return;
                }

                if (type == "keyboardButtonTypeRequestPhoneNumber")
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Share phone number?",
                        Content = "This bot requested the phone number connected to your Telegram account.",
                        PrimaryButtonText = "Share",
                        SecondaryButtonText = "Cancel"
                    };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        var maxId = GetNewestMessageId();
                        await TelegramService.Instance.SendOwnContactAsync(_chat);
                        await RefreshMessagesAfterSendAsync(maxId);
                    }
                    return;
                }

                if (type == "keyboardButtonTypeRequestLocation")
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Share location?",
                        Content = "This bot requested your current location.",
                        PrimaryButtonText = "Share",
                        SecondaryButtonText = "Cancel"
                    };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        await SendCurrentLocationAsync(false);
                    }
                    return;
                }

                await ShowChatAlertAsync("Bot button", "This bot button type is not supported by this client yet.");
            }
            catch (Exception ex)
            {
                await ShowChatAlertAsync("Bot button error", AlertErrorMessage(ex, "Could not process the bot button."));
            }
        }

        private void SetElementInteractive(UIElement element, bool enabled)
        {
            if (element == null) return;
            element.IsHitTestVisible = enabled;
            element.Opacity = 1.0;
        }

        private void SetComposerEnabled(bool enabled)
        {
            if (HeaderMoreButton != null)
            {
                HeaderMoreButton.IsEnabled = true;
                HeaderMoreButton.IsHitTestVisible = true;
                HeaderMoreButton.Opacity = 1.0;
            }
            ApplyPermissionsToUi(enabled);
        }

        private void StartPolling()
        {
            // Real-time events from TdLib handle message delivery.
            // No polling needed.
        }

        private void StopPolling()
        {
            // No-op.
        }

        private void SubscribeRealtimeEvents()
        {
            if (_realtimeEventsSubscribed) return;
            _realtimeEventsSubscribed = true;
            var svc = TelegramService.Instance;
            svc.NewMessageArrived += OnNewMessageArrived;
            svc.MessageContentUpdated += OnMessageContentUpdated;
            svc.MessagesDeleted += OnMessagesDeleted;
            svc.UserStatusChanged += OnUserStatusChanged;
        }

        private void UnsubscribeRealtimeEvents()
        {
            if (!_realtimeEventsSubscribed) return;
            _realtimeEventsSubscribed = false;
            _realtimeDrainAgain = false;
            var svc = TelegramService.Instance;
            svc.NewMessageArrived -= OnNewMessageArrived;
            svc.MessageContentUpdated -= OnMessageContentUpdated;
            svc.MessagesDeleted -= OnMessagesDeleted;
            svc.UserStatusChanged -= OnUserStatusChanged;
        }

        private void OnNewMessageArrived(object sender, long chatId)
        {
            QueueRealtimeMessageDrain(chatId);
        }

        private void OnMessageContentUpdated(object sender, long chatId)
        {
            QueueRealtimeMessageDrain(chatId);
        }

        private void OnMessagesDeleted(object sender, long chatId)
        {
            QueueRealtimeMessageDrain(chatId);
        }

        private void QueueRealtimeMessageDrain(long chatId)
        {
            if (_chat == null || _chat.PeerId != chatId) return;

            // While the initial history request is running, TDLib updates stay queued in
            // TelegramService/TdLibTelegramClient. LoadHistoryAsync drains them once after
            // _historyLoaded becomes true, which avoids losing updates or racing the first render.
            if (!_historyLoaded || _loading) return;

            if (_realtimeDrainRunning)
            {
                _realtimeDrainAgain = true;
                return;
            }

            _realtimeDrainRunning = true;
            var ignored = DrainRealtimeMessageUpdatesAsync(chatId);
        }

        private async System.Threading.Tasks.Task DrainRealtimeMessageUpdatesAsync(long chatId)
        {
            try
            {
                do
                {
                    _realtimeDrainAgain = false;
                    var chat = _chat;
                    if (chat == null || chat.PeerId != chatId) break;

                    List<ChatMessageViewModel> updates = null;
                    List<int> deleted = null;
                    var resetReplyMarkup = false;
                    try
                    {
                        updates = await TelegramService.Instance.TakeMessageUpdatesAsync(chat);
                        deleted = await TelegramService.Instance.TakeDeletedMessageIdsAsync(chat);
                        resetReplyMarkup = TelegramService.Instance.ConsumeReplyMarkupReset(chat);
                    }
                    catch
                    {
                        // Keep real-time delivery alive; the next TDLib update will retry the drain.
                    }

                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
                    {
                        if (_chat == null || !object.ReferenceEquals(_chat, chat) || _chat.PeerId != chatId || !_historyLoaded || _loading) return;
                        _polling = true;
                        try
                        {
                            var wasAtBottom = ShouldStickToBottom();
                            var removed = deleted == null ? 0 : RemoveMessagesById(deleted);

                            if (resetReplyMarkup)
                            {
                                _botReplyKeyboardExplicitlyRemoved = true;
                                _activeBotReplyMarkupMessage = null;
                                UpdateBotReplyKeyboardPanel();
                            }

                            var added = MergeMessages(updates, false);
                            if (added > 0)
                            {
                                BeginAutoDownloadMedia();
                                StartBackgroundReactionLoad();
                                StartFastReactionRefresh();
                            }

                            UpdateOutgoingMessageStates();
                            if (added > 0 && wasAtBottom && ShouldStickToBottom())
                            {
                                ScrollToBottom(false);
                                QueueMarkVisibleMessagesRead();
                                QueueBottomPinBurst();
                            }
                            if (removed > 0 && wasAtBottom && ShouldStickToBottom())
                                KeepBottomIfStillRequested();
                            UpdateScrollDownButton();
                        }
                        catch
                        {
                        }
                        finally
                        {
                            _polling = false;
                        }
                    });
                }
                while (_realtimeDrainAgain);
            }
            finally
            {
                _realtimeDrainRunning = false;
                if (_realtimeDrainAgain)
                    QueueRealtimeMessageDrain(chatId);
            }
        }

        private async void OnUserStatusChanged(object sender, long userId)
        {
            if (_chat == null) return;
            // For user chats, match against UserId (not PeerId which is chat ID)
            long targetId = _chat.PeerType == "user" ? _chat.UserId : _chat.PeerId;
            if (targetId != userId) return;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                if (_chat == null) return;
                TelegramService.Instance.ApplyCachedUserStatus(userId, _chat);
                HeaderSubtitle.Text = _chat.SubtitleText;
            });
        }

        public sealed class PendingPhotoAttachment
        {
            public StorageFile File { get; set; }
            public BitmapImage Preview { get; set; }
            public string FileName { get; set; }
            public string Kind { get; set; }
            public string KindLabel
            {
                get
                {
                    if (Kind == "video") return "Video";
                    if (Kind == "document") return "File";
                    if (Kind == "audio") return "Audio";
                    if (Kind == "voice") return "Voice";
                    return "Photo";
                }
            }
        }

        private class MessageTemplateSelector : DataTemplateSelector
        {
            public DataTemplate MessageTemplate { get; set; }
            public DataTemplate DateSeparatorTemplate { get; set; }
            public DataTemplate ServiceMessageTemplate { get; set; }

            protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
            {
                if (item != null && item.GetType() == typeof(DateSeparatorItem)) return DateSeparatorTemplate;
                var msg = item as ChatMessageViewModel;
                if (msg != null && msg.IsServiceMessage && !string.IsNullOrEmpty(msg.ServiceActionText))
                    return ServiceMessageTemplate;
                return MessageTemplate;
            }
        }
    }

    internal class DateSeparatorVisibilityConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value != null && value.GetType() == typeof(DateSeparatorItem) ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    internal class UnreadSeparatorVisibilityConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is UnreadSeparatorItem ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    internal class ServiceMessageVisibilityConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var msg = value as ChatMessageViewModel;
            if (msg != null && msg.IsServiceMessage && !string.IsNullOrEmpty(msg.ServiceActionText))
                return Visibility.Visible;
            return Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    internal class NormalMessageVisibilityConverter : Windows.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is DateSeparatorItem) return Visibility.Collapsed;
            var msg = value as ChatMessageViewModel;
            if (msg != null && msg.IsServiceMessage && !string.IsNullOrEmpty(msg.ServiceActionText))
                return Visibility.Collapsed;
            return Visibility.Visible;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    internal sealed class DownloadProgressPieSlice : Windows.UI.Xaml.Shapes.Path
    {
        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            "Progress", typeof(double), typeof(DownloadProgressPieSlice), new PropertyMetadata(0.0, OnProgressChanged));

        public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
            "Radius", typeof(double), typeof(DownloadProgressPieSlice), new PropertyMetadata(28.0, OnGeometryChanged));

        public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
            "IsIndeterminate", typeof(bool), typeof(DownloadProgressPieSlice), new PropertyMetadata(false, OnProgressChanged));

        private readonly RotateTransform _rotation;
        private readonly Storyboard _foreverStoryboard;
        private bool _loaded;

        public DownloadProgressPieSlice()
        {
            StrokeStartLineCap = PenLineCap.Flat;
            StrokeEndLineCap = PenLineCap.Flat;
            _rotation = new RotateTransform();
            RenderTransform = _rotation;
            RenderTransformOrigin = new Point(0.5, 0.5);

            _foreverStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var animation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(3.0),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(animation, _rotation);
            Storyboard.SetTargetProperty(animation, "Angle");
            _foreverStoryboard.Children.Add(animation);

            Loaded += delegate
            {
                _loaded = true;
                UpdatePath();
            };
            Unloaded += delegate
            {
                _loaded = false;
                _foreverStoryboard.Stop();
            };
        }

        public double Progress
        {
            get { return (double)GetValue(ProgressProperty); }
            set { SetValue(ProgressProperty, value); }
        }

        public double Radius
        {
            get { return (double)GetValue(RadiusProperty); }
            set { SetValue(RadiusProperty, value); }
        }

        public bool IsIndeterminate
        {
            get { return (bool)GetValue(IsIndeterminateProperty); }
            set { SetValue(IsIndeterminateProperty, value); }
        }

        private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var pieSlice = d as DownloadProgressPieSlice;
            if (pieSlice != null) pieSlice.UpdatePath();
        }

        private static void OnGeometryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var pieSlice = d as DownloadProgressPieSlice;
            if (pieSlice != null) pieSlice.UpdatePath();
        }

        private void UpdatePath()
        {
            if (!_loaded) return;

            var radius = Radius;
            if (radius <= 0) radius = 1;

            // Do not draw a fake minimum segment at zero. On older phone GPUs a tiny
            // anti-aliased arc is rendered as a grey dash before the real white progress starts.
            if (Progress <= 0.0)
            {
                Data = null;
                _foreverStoryboard.Stop();
                _rotation.Angle = 0;
                return;
            }

            var angle = IsIndeterminate ? 42.0 : Progress * 359.0 / 100.0;
            if (angle < 0.0) angle = 0.0;
            if (!IsIndeterminate && Progress >= 99.5)
            {
                _foreverStoryboard.Stop();
                _rotation.Angle = 0;
                Width = Height = 2.0 * radius + StrokeThickness;
                var fullCenter = radius + StrokeThickness / 2.0;
                Data = new EllipseGeometry
                {
                    Center = new Point(fullCenter, fullCenter),
                    RadiusX = radius,
                    RadiusY = radius
                };
                return;
            }
            if (angle > 359.0) angle = 359.0;
            if (angle <= 0.0)
            {
                Data = null;
                _foreverStoryboard.Stop();
                _rotation.Angle = 0;
                return;
            }

            if (IsIndeterminate)
                _foreverStoryboard.Begin();
            else
            {
                _foreverStoryboard.Stop();
                _rotation.Angle = 0;
            }

            Width = Height = 2.0 * radius + StrokeThickness;
            var centerOffset = StrokeThickness / 2.0;
            var endAngle = angle;

            var figure = new PathFigure
            {
                StartPoint = new Point(radius + centerOffset, centerOffset),
                IsClosed = false
            };

            var arcX = radius + Math.Sin(endAngle * Math.PI / 180.0) * radius + centerOffset;
            var arcY = radius - Math.Cos(endAngle * Math.PI / 180.0) * radius + centerOffset;
            var arc = new ArcSegment
            {
                IsLargeArc = angle >= 180.0,
                Point = new Point(arcX, arcY),
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise
            };
            figure.Segments.Add(arc);

            Data = new PathGeometry { Figures = { figure } };
        }
    }
}
