using Microsoft.Graphics.Canvas.Effects;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Telegram.Models;
using Telegram.Services;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Core;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Telegram
{
    public sealed partial class Chats : Page
    {
        private const double ChatListBaseTopPadding = 2.0;
        private const double ChatListBaseBottomPadding = 2.0;
        private const double DefaultChatsTopBarHeight = 56.0;
        private const double DefaultBottomMenuAppBarHeight = 48.0;
        private const double DefaultBottomMenuSecondaryCommandHeight = 44.0;
        private const double BottomMenuAppBarClosingDurationMs = 167.0;

        private int _folderLoadVersion;
        private int _chatLoadVersion;
        private bool _archiveMode;
        private bool _backRequestedAttached;
        private bool _accountInfoStarted;
        private ChatViewModel _accountChat;
        private int _currentFolderId = -1;
        private bool _loadingMoreChats;
        private int _loadingFolderId = int.MinValue;
        private int _loadingVersion;
        private int _mainLoadingCount;
        private bool _chatMenuOpen;
        private DateTime _ignoreChatRightTapUntilUtc = DateTime.MinValue;
        private bool _updatingFolderItems;
        private bool _syncingSelection;
        private ListView _currentChatList;
        private DateTime _suppressChatItemClickUntilUtc = DateTime.MinValue;
        private ScrollViewer _chatScrollViewer;
        private ListView _chatLayoutList;
        private bool _visibleAppendQueued;
        private int _appliedChatsInitialDisplayCount;
        private int _appliedChatsIncrementalDisplayCount;
        private bool _appliedChatsShowAllImmediately;
        private List<ChatViewModel> _currentChats = new List<ChatViewModel>();
        private readonly Dictionary<int, ObservableCollection<ChatViewModel>> _folderChats = new Dictionary<int, ObservableCollection<ChatViewModel>>();
        private readonly Dictionary<int, ObservableCollection<ChatViewModel>> _folderVisibleChats = new Dictionary<int, ObservableCollection<ChatViewModel>>();
        private readonly Dictionary<int, ListView> _folderChatLists = new Dictionary<int, ListView>();
        private readonly Dictionary<int, HashSet<string>> _folderChatKeys = new Dictionary<int, HashSet<string>>();
        private readonly Dictionary<int, bool> _folderHasMore = new Dictionary<int, bool>();
        private readonly HashSet<int> _foldersPendingVisibleAppend = new HashSet<int>();
        private const int ChatsPageSize = 20;
        private const int RetainedInactiveServerChats = 40;
        private const double ChatAppendNearBottomThreshold = 180.0;
        private static int _cacheResetVersion;
        private static int _chatDisplaySettingsVersion;
        private static event EventHandler ChatDisplaySettingsChanged;
        private int _appliedCacheResetVersion;
        private int _appliedChatDisplaySettingsVersion;
        private bool _authorizationRefreshRunning;
        private bool _authorizationRefreshAgain;
        private int _emptyChatRetryVersion;
        private int _delayedChatRefreshVersion;
        private int _selectedFolderLoadRequestVersion;
        private bool _postTdLibRefreshPending;
        private bool _postTdLibRefreshRunning;
        private Brush _chatsTopBarBackground;
        private Brush _bottomMenuAppBarBackground;
        private Brush _bottomMenuAppBarOverflowBackground;
        private bool _chatsTopBarBackgroundCaptured;
        private bool _bottomMenuAppBarBackgroundCaptured;
        private bool _bottomMenuAppBarOverflowBackgroundCaptured;
        private bool _bottomMenuAppBarIsOpenOrOpening;
        private bool _bottomMenuAppBarIsClosing;
        private bool _bottomMenuAppBarGlassRenderLoopAttached;
        private int _bottomMenuAppBarGlassRenderFramesRemaining;
        private double _bottomMenuAppBarCompactHeight = DefaultBottomMenuAppBarHeight;
        private double _bottomMenuAppBarClosingStartHeight = DefaultBottomMenuAppBarHeight;
        private DateTime _bottomMenuAppBarClosingStartedUtc = DateTime.MinValue;

        private void ChatLastMessageText_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyChatLastMessageText(sender as TextBlock);
        }

        private void ChatLastMessageText_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var textBlock = sender as TextBlock;
            if (textBlock == null) return;
            textBlock.Text = string.Empty;
            textBlock.Inlines.Clear();

            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
            {
                ApplyChatLastMessageText(textBlock);
            });
        }

        private void ApplyChatLastMessageText(TextBlock textBlock)
        {
            if (textBlock == null) return;
            textBlock.Text = string.Empty;
            textBlock.Inlines.Clear();

            var chat = textBlock.DataContext as ChatViewModel;
            var text = chat == null ? string.Empty : (chat.DisplayLastMessage ?? string.Empty);
            if (string.IsNullOrEmpty(text)) return;

            // Fast path: the overwhelming majority of previews are plain text with no
            // local-emoji glyphs. Skip building inlines (and the emoji table probing)
            // entirely - this is the hot path while the chat list loads.
            if (!HasPossibleLocalEmoji(text))
            {
                textBlock.Text = text;
                return;
            }

            AddLocalEmojiInlines(textBlock.Inlines, text, 16, 16, new Thickness(1, 0, 1, -2));
        }

        private static bool HasPossibleLocalEmoji(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                // Emoji and their modifiers all live at or above U+2000 (symbols,
                // dingbats, high surrogates for U+1Fxxx, VS16 and the keycap combiner).
                var c = text[i];
                if (c >= (char)0x2000 || c == '©' || c == '®')
                    return true;
            }
            return false;
        }

        private void AddLocalEmojiInlines(InlineCollection inlines, string text, double width, double height, Thickness margin)
        {
            if (inlines == null || string.IsNullOrEmpty(text)) return;

            var segmentStart = 0;
            var index = 0;
            while (index < text.Length)
            {
                string uri;
                int length;
                if (!TryReadLocalEmojiUri(text, index, out uri, out length))
                {
                    index++;
                    continue;
                }

                if (index > segmentStart)
                    inlines.Add(new Run { Text = text.Substring(segmentStart, index - segmentStart) });

                inlines.Add(new InlineUIContainer
                {
                    Child = new Image
                    {
                        Width = width,
                        Height = height,
                        Stretch = Stretch.Uniform,
                        Margin = margin,
                        Source = new BitmapImage(new Uri(uri))
                    }
                });

                index += length;
                segmentStart = index;
            }

            if (segmentStart < text.Length)
                inlines.Add(new Run { Text = text.Substring(segmentStart) });
        }

        private bool TryReadLocalEmojiUri(string text, int index, out string uri, out int length)
        {
            uri = null;
            length = 0;
            if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length) return false;

            // Plain letters/spaces/punctuation below U+2000 can never begin a local emoji
            // asset, so skip probing the emoji table (up to 16 lookups) for them. Digits,
            // '#' and '*' are kept because they can start keycap emoji (e.g. 1 + VS16 + keycap).
            var first = text[index];
            if (first < (char)0x2000 &&
                !((first >= '0' && first <= '9') || first == '#' || first == '*' || first == '©' || first == '®'))
                return false;

            var maxLength = Math.Min(16, text.Length - index);
            for (var candidateLength = maxLength; candidateLength > 0; candidateLength--)
            {
                var candidate = text.Substring(index, candidateLength);
                var candidateUri = ChatPage.ResolveLocalEmojiAssetUri(candidate);
                if (string.IsNullOrEmpty(candidateUri)) continue;

                uri = candidateUri;
                length = candidateLength;
                return true;
            }

            return false;
        }

        public static async System.Threading.Tasks.Task ClearCacheAsync()
        {
            _cacheResetVersion++;
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var files = await folder.GetFilesAsync();
                for (var i = 0; i < files.Count; i++)
                {
                    var name = files[i].Name;
                    if (string.Equals(name, "chats_folders_cache.txt", StringComparison.OrdinalIgnoreCase) ||
                        (name.StartsWith("chats_cache_", StringComparison.OrdinalIgnoreCase) &&
                         name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                    {
                        try
                        {
                            await files[i].DeleteAsync(StorageDeleteOption.PermanentDelete);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public Chats()
        {
            InitializeComponent();
            _appliedChatsInitialDisplayCount = TelegramAppSettings.ChatsInitialDisplayCount;
            _appliedChatsIncrementalDisplayCount = TelegramAppSettings.ChatsIncrementalDisplayCount;
            _appliedChatsShowAllImmediately = TelegramAppSettings.ChatsShowAllImmediately;
            // Each folder is a real page inside FolderFlip (a headerless FlipView bound
            // to the folder list). Every page hosts its own chat ListView bound to that
            // folder's chats, so horizontal swipes slide the real next folder in while
            // the list still scrolls vertically. The per-page ListViews wire their own
            // Loaded/SizeChanged/ContainerContentChanging via the item template.
            Loaded += Chats_Loaded;
            Loaded += Chats_DisplaySettingsLoaded;
            Loaded += Chats_GlassLoaded;
            Unloaded += Chats_DisplaySettingsUnloaded;
        }

        public static void NotifyChatDisplaySettingsChanged()
        {
            _chatDisplaySettingsVersion++;
            var handler = ChatDisplaySettingsChanged;
            if (handler != null)
                handler(null, EventArgs.Empty);
        }

        private sealed class IncrementalChatCollection : ObservableCollection<ChatViewModel>, ISupportIncrementalLoading
        {
            private readonly Chats _owner;
            private readonly int _folderId;
            private bool _loading;

            public IncrementalChatCollection(Chats owner, int folderId)
            {
                _owner = owner;
                _folderId = folderId;
            }

            public bool HasMoreItems
            {
                get { return _owner != null && _owner.HasMoreVisibleChats(_folderId); }
            }

            public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
            {
                return AsyncInfo.Run<LoadMoreItemsResult>(async cancellationToken =>
                {
                    if (_loading || _owner == null)
                        return new LoadMoreItemsResult { Count = 0 };

                    _loading = true;
                    try
                    {
                        var added = await _owner.LoadMoreVisibleChatsForIncrementalLoadingAsync(_folderId);
                        return new LoadMoreItemsResult { Count = added };
                    }
                    finally
                    {
                        _loading = false;
                    }
                });
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _archiveMode = e.Parameter is string && (string)e.Parameter == "archive";
            ConfigureSystemBackButton(_archiveMode);
            ApplyGlassSetting();
            ApplyExternalCacheReset();
            ApplyChatDisplaySettingsChange();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StatusBarLoadingIndicator.Hide();
            DetachGlass();
            ConfigureSystemBackButton(false);
            base.OnNavigatedFrom(e);
        }

        private async void Chats_Loaded(object sender, RoutedEventArgs e)
        {
            TelegramService.Instance.ChatRemoved -= TelegramService_ChatRemoved;
            TelegramService.Instance.ChatRemoved += TelegramService_ChatRemoved;
            TelegramService.Instance.AuthorizationReady -= TelegramService_AuthorizationReady;
            TelegramService.Instance.AuthorizationReady += TelegramService_AuthorizationReady;
            ApplyExternalCacheReset();
            ApplyChatDisplaySettingsChange();
            LoadCachedAccountInfo();
            StartAccountInfoLoad();
            await LoadFoldersAsync();
            QueueDelayedCurrentFolderRefreshes();
            ApplyPendingRemovedChats();
        }

        private void Chats_DisplaySettingsLoaded(object sender, RoutedEventArgs e)
        {
            ChatDisplaySettingsChanged -= Chats_ChatDisplaySettingsChanged;
            ChatDisplaySettingsChanged += Chats_ChatDisplaySettingsChanged;
        }

        private void Chats_DisplaySettingsUnloaded(object sender, RoutedEventArgs e)
        {
            ChatDisplaySettingsChanged -= Chats_ChatDisplaySettingsChanged;
            TelegramService.Instance.ChatRemoved -= TelegramService_ChatRemoved;
            TelegramService.Instance.AuthorizationReady -= TelegramService_AuthorizationReady;
            DetachGlass();
        }

        private void Chats_GlassLoaded(object sender, RoutedEventArgs e)
        {
            ApplyGlassSetting();
        }

        private void ApplyGlassSetting()
        {
            if (ChatsTopBar != null && !_chatsTopBarBackgroundCaptured)
            {
                _chatsTopBarBackground = ChatsTopBar.Background;
                _chatsTopBarBackgroundCaptured = true;
            }
            if (BottomMenuAppBar != null && !_bottomMenuAppBarBackgroundCaptured)
            {
                _bottomMenuAppBarBackground = BottomMenuAppBar.Background;
                _bottomMenuAppBarBackgroundCaptured = true;
            }

            if (TelegramAppSettings.GlassEffectEnabled)
            {
                if (ChatsTopBar != null && ChatsTopBarGlass != null)
                {
                    ChatsTopBar.Background = new SolidColorBrush(Colors.Transparent);
                    ChatsTopBarGlass.Background = new SolidColorBrush(Colors.Transparent);
                    FluentGlassEffectHelper.AttachTopBar(ChatsTopBarGlass, _chatsTopBarBackground);
                }
                if (BottomMenuAppBar != null && BottomMenuAppBarGlass != null)
                {
                    UpdateBottomMenuAppBarGlassBounds();
                    BottomMenuAppBar.Background = new SolidColorBrush(Colors.Transparent);
                    BottomMenuAppBarGlass.Background = new SolidColorBrush(Colors.Transparent);
                    FluentGlassEffectHelper.AttachBottomBar(BottomMenuAppBarGlass, _bottomMenuAppBarBackground);
                }
                ApplyBottomMenuAppBarOverflowBackground(false);
            }
            else
            {
                DetachGlass();
            }

            ApplyChatListOverlapPadding(_currentChatList);
        }

        private void DetachGlass()
        {
            StopBottomMenuAppBarGlassRenderLoop();
            _bottomMenuAppBarIsClosing = false;
            _bottomMenuAppBarClosingStartedUtc = DateTime.MinValue;
            if (ChatsTopBarGlass != null)
                FluentGlassEffectHelper.Detach(ChatsTopBarGlass);
            if (ChatsTopBar != null && _chatsTopBarBackgroundCaptured)
                ChatsTopBar.Background = _chatsTopBarBackground;
            if (BottomMenuAppBarGlass != null)
                FluentGlassEffectHelper.Detach(BottomMenuAppBarGlass);
            if (BottomMenuAppBar != null && _bottomMenuAppBarBackgroundCaptured)
                BottomMenuAppBar.Background = _bottomMenuAppBarBackground;
            ApplyBottomMenuAppBarOverflowBackground(true);
        }

        private void ApplyBottomMenuAppBarOverflowBackground(bool forceRestore)
        {
            if (BottomMenuAppBar == null) return;

            var overflow = FindVisualChild<CommandBarOverflowPresenter>(BottomMenuAppBar);
            if (overflow == null) return;

            if (!_bottomMenuAppBarOverflowBackgroundCaptured)
            {
                _bottomMenuAppBarOverflowBackground = overflow.Background;
                _bottomMenuAppBarOverflowBackgroundCaptured = true;
            }

            overflow.Background = !forceRestore && TelegramAppSettings.GlassEffectEnabled
                ? new SolidColorBrush(Colors.Transparent)
                : _bottomMenuAppBarOverflowBackground;

            if (!forceRestore && TelegramAppSettings.GlassEffectEnabled)
                FluentGlassEffectHelper.AttachBottomBar(overflow, _bottomMenuAppBarBackground);
            else
                FluentGlassEffectHelper.Detach(overflow);
        }

        private void UpdateBottomMenuAppBarGlassBounds()
        {
            if (BottomMenuAppBarGlass == null) return;

            var height = GetBottomMenuAppBarOverlapHeight();
            if (height <= 0)
                height = DefaultBottomMenuAppBarHeight;

            BottomMenuAppBarGlass.Height = height;
            BottomMenuAppBarGlass.MinHeight = height;
            BottomMenuAppBarGlass.VerticalAlignment = VerticalAlignment.Bottom;
        }

        private double GetBottomMenuAppBarOverlapHeight()
        {
            var appBarHeight = BottomMenuAppBar == null ? DefaultBottomMenuAppBarHeight : BottomMenuAppBar.ActualHeight;
            if (appBarHeight <= 0)
                appBarHeight = DefaultBottomMenuAppBarHeight;
            var reserveExpandedHeight = ShouldReserveExpandedBottomMenuAppBarHeight();
            if (!reserveExpandedHeight && !_bottomMenuAppBarIsClosing)
                CaptureBottomMenuAppBarCompactHeight();

            if (reserveExpandedHeight)
                appBarHeight = Math.Max(appBarHeight, GetExpandedBottomMenuAppBarHeight());

            var root = RootGrid as FrameworkElement;
            if (root == null || root.ActualHeight <= 0 || BottomMenuAppBar == null)
                return appBarHeight;

            if (_bottomMenuAppBarIsClosing)
                return GetBottomMenuAppBarClosingHeight();

            var minimumHeight = _bottomMenuAppBarIsClosing ? GetBottomMenuAppBarCompactHeight() : appBarHeight;
            var top = root.ActualHeight - minimumHeight;
            Rect bounds;
            if (TryGetElementBounds(BottomMenuAppBar, root, out bounds))
                top = Math.Min(top, bounds.Top);

            var overflow = FindVisualChild<CommandBarOverflowPresenter>(BottomMenuAppBar);
            if (overflow != null && overflow.ActualHeight > 0 && TryGetElementBounds(overflow, root, out bounds))
                top = Math.Min(top, bounds.Top);

            var height = root.ActualHeight - top;
            return height > minimumHeight ? height : minimumHeight;
        }

        private double GetBottomMenuAppBarClosingHeight()
        {
            var compactHeight = GetBottomMenuAppBarCompactHeight();
            var startHeight = _bottomMenuAppBarClosingStartHeight;
            if (startHeight <= compactHeight)
                startHeight = Math.Max(compactHeight, GetExpandedBottomMenuAppBarHeight());

            if (_bottomMenuAppBarClosingStartedUtc == DateTime.MinValue)
                return startHeight;

            var elapsed = (DateTime.UtcNow - _bottomMenuAppBarClosingStartedUtc).TotalMilliseconds;
            var progress = elapsed / BottomMenuAppBarClosingDurationMs;
            if (progress <= 0)
                return startHeight;
            if (progress >= 1)
                return compactHeight;

            var eased = EaseCommandBarClose(progress);
            return startHeight + ((compactHeight - startHeight) * eased);
        }

        private static double EaseCommandBarClose(double progress)
        {
            if (progress <= 0) return 0;
            if (progress >= 1) return 1;

            const double x1 = 0.2;
            const double y1 = 0.0;
            const double x2 = 0.0;
            const double y2 = 1.0;
            var low = 0.0;
            var high = 1.0;
            var t = progress;

            for (var i = 0; i < 8; i++)
            {
                t = (low + high) * 0.5;
                var x = CubicBezier(t, 0.0, x1, x2, 1.0);
                if (x < progress)
                    low = t;
                else
                    high = t;
            }

            return CubicBezier(t, 0.0, y1, y2, 1.0);
        }

        private static double CubicBezier(double t, double p0, double p1, double p2, double p3)
        {
            var u = 1.0 - t;
            return (u * u * u * p0) +
                   (3.0 * u * u * t * p1) +
                   (3.0 * u * t * t * p2) +
                   (t * t * t * p3);
        }

        private bool ShouldReserveExpandedBottomMenuAppBarHeight()
        {
            if (_bottomMenuAppBarIsClosing)
                return false;

            return IsBottomMenuAppBarOpenOrOpening();
        }

        private bool IsBottomMenuAppBarOpenOrOpening()
        {
            if (_bottomMenuAppBarIsOpenOrOpening)
                return true;

            try
            {
                return BottomMenuAppBar != null && BottomMenuAppBar.IsOpen;
            }
            catch
            {
                return false;
            }
        }

        private double GetExpandedBottomMenuAppBarHeight()
        {
            var compactHeight = _bottomMenuAppBarCompactHeight;
            if (compactHeight <= 0)
                compactHeight = DefaultBottomMenuAppBarHeight;

            var overflowHeight = 0.0;
            if (BottomMenuAppBar != null && BottomMenuAppBar.SecondaryCommands != null)
            {
                for (var i = 0; i < BottomMenuAppBar.SecondaryCommands.Count; i++)
                {
                    var element = BottomMenuAppBar.SecondaryCommands[i] as FrameworkElement;
                    var itemHeight = element == null ? 0 : Math.Max(element.ActualHeight, element.MinHeight);
                    if (itemHeight <= 0)
                        itemHeight = DefaultBottomMenuSecondaryCommandHeight;
                    overflowHeight += itemHeight;
                }
            }

            return compactHeight + overflowHeight;
        }

        private double GetBottomMenuAppBarCompactHeight()
        {
            var compactHeight = _bottomMenuAppBarCompactHeight;
            if (compactHeight <= 0)
                compactHeight = DefaultBottomMenuAppBarHeight;
            return compactHeight;
        }

        private void CaptureBottomMenuAppBarCompactHeight()
        {
            if (BottomMenuAppBar == null || _bottomMenuAppBarIsOpenOrOpening || _bottomMenuAppBarIsClosing)
                return;

            var height = BottomMenuAppBar.ActualHeight;
            if (height > 0)
                _bottomMenuAppBarCompactHeight = height;
        }

        private static bool TryGetElementBounds(FrameworkElement element, FrameworkElement relativeTo, out Rect bounds)
        {
            bounds = new Rect();
            if (element == null || relativeTo == null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                return false;

            try
            {
                var transform = element.TransformToVisual(relativeTo);
                bounds = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async void QueueApplyBottomMenuAppBarGlass()
        {
            ApplyGlassSetting();
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
            {
                ApplyGlassSetting();
            });
        }

        private void StartBottomMenuAppBarGlassRenderLoop(int frameCount)
        {
            if (frameCount <= 0)
                frameCount = 1;

            _bottomMenuAppBarGlassRenderFramesRemaining = Math.Max(_bottomMenuAppBarGlassRenderFramesRemaining, frameCount);
            if (_bottomMenuAppBarGlassRenderLoopAttached)
                return;

            Windows.UI.Xaml.Media.CompositionTarget.Rendering += BottomMenuAppBarGlass_Rendering;
            _bottomMenuAppBarGlassRenderLoopAttached = true;
        }

        private void StopBottomMenuAppBarGlassRenderLoop()
        {
            if (!_bottomMenuAppBarGlassRenderLoopAttached)
                return;

            Windows.UI.Xaml.Media.CompositionTarget.Rendering -= BottomMenuAppBarGlass_Rendering;
            _bottomMenuAppBarGlassRenderLoopAttached = false;
            _bottomMenuAppBarGlassRenderFramesRemaining = 0;
        }

        private void BottomMenuAppBarGlass_Rendering(object sender, object e)
        {
            if (!TelegramAppSettings.GlassEffectEnabled || BottomMenuAppBar == null || BottomMenuAppBarGlass == null)
            {
                StopBottomMenuAppBarGlassRenderLoop();
                return;
            }

            UpdateBottomMenuAppBarGlassBounds();

            _bottomMenuAppBarGlassRenderFramesRemaining--;
            if (_bottomMenuAppBarGlassRenderFramesRemaining <= 0)
                StopBottomMenuAppBarGlassRenderLoop();
        }

        private void ApplyChatListOverlapPadding(ListView list)
        {
            if (list == null) return;

            var topBarHeight = ChatsTopBar == null ? DefaultChatsTopBarHeight : ChatsTopBar.ActualHeight;
            if (topBarHeight <= 0)
                topBarHeight = DefaultChatsTopBarHeight;
            var bottomBarHeight = GetBottomMenuAppBarOverlapHeight();
            if (bottomBarHeight <= 0)
                bottomBarHeight = DefaultBottomMenuAppBarHeight;

            list.Padding = new Thickness(
                list.Padding.Left,
                ChatListBaseTopPadding + topBarHeight,
                list.Padding.Right,
                ChatListBaseBottomPadding + bottomBarHeight);
        }

        private void Chats_ChatDisplaySettingsChanged(object sender, EventArgs e)
        {
            ApplyChatDisplaySettingsChange();
        }

        private async void TelegramService_AuthorizationReady(object sender, EventArgs e)
        {
            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async delegate
                {
                    await ReloadAfterAuthorizationReadyAsync();
                });
            }
            catch
            {
            }
        }

        private async System.Threading.Tasks.Task ReloadAfterAuthorizationReadyAsync()
        {
            if (_authorizationRefreshRunning)
            {
                _authorizationRefreshAgain = true;
                return;
            }

            _authorizationRefreshRunning = true;
            try
            {
                do
                {
                    _authorizationRefreshAgain = false;
                    if (!_archiveMode)
                        _postTdLibRefreshPending = true;
                    ApplyExternalCacheReset();
                    LoadCachedAccountInfo();
                    StartAccountInfoLoad();
                    await LoadFoldersAsync();
                    QueueDelayedCurrentFolderRefreshes();
                    ApplyPendingRemovedChats();
                }
                while (_authorizationRefreshAgain);
            }
            finally
            {
                _authorizationRefreshRunning = false;
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _postTdLibRefreshPending = false;
            await RefreshChatsPageAsync();
        }

        private async System.Threading.Tasks.Task RefreshChatsPageAsync()
        {
            ResetFolderPaging();
            await LoadFoldersAsync();
        }

        private async void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            foreach (var removedItem in e.RemovedItems)
            {
                var removedFolder = removedItem as FolderViewModel;
                if (removedFolder != null)
                    removedFolder.IsSelected = false;
            }

            foreach (var addedItem in e.AddedItems)
            {
                var addedFolder = addedItem as FolderViewModel;
                if (addedFolder != null)
                    addedFolder.IsSelected = true;
            }

            if (_updatingFolderItems) return;
            if (_syncingSelection) return; // driven by the FlipView; it performs the load

            var index = FolderList.SelectedIndex;
            _syncingSelection = true;
            try
            {
                if (FolderFlip != null && FolderFlip.SelectedIndex != index)
                    FolderFlip.SelectedIndex = index;
            }
            finally
            {
                _syncingSelection = false;
            }

            var folder = FolderList.SelectedItem as FolderViewModel;
            if (folder != null)
            {
                UpdateCurrentChatList(index);
                await LoadChatsAsync(folder.Id);
            }
        }

        private void SetFolderItemsSource(IList<FolderViewModel> folders)
        {
            SetFolderItemsSource(folders, int.MinValue);
        }

        private void SetFolderItemsSource(IList<FolderViewModel> folders, int preferredFolderId)
        {
            BindFolderVisibleChats(folders);
            var selectedIndex = FindFolderIndex(folders, preferredFolderId);
            if (selectedIndex < 0 && folders != null && folders.Count > 0)
                selectedIndex = 0;

            _updatingFolderItems = true;
            try
            {
                if (FolderList != null)
                    FolderList.ItemsSource = folders;
                if (FolderFlip != null)
                    FolderFlip.ItemsSource = folders;
                if (FolderList != null && selectedIndex >= 0 && selectedIndex < FolderList.Items.Count)
                {
                    FolderList.SelectedIndex = selectedIndex;
                    FolderList.ScrollIntoView(FolderList.SelectedItem);
                    if (FolderFlip != null)
                        FolderFlip.SelectedIndex = selectedIndex;
                }
                ApplyFolderSelectionFlags();
            }
            finally
            {
                _updatingFolderItems = false;
            }

            EnsureSelectedFolderChatsLoaded();
        }

        private void BindFolderVisibleChats(IList<FolderViewModel> folders)
        {
            if (folders == null) return;
            for (var i = 0; i < folders.Count; i++)
                BindFolderViewModelVisibleChats(folders[i]);
        }

        private void BindFolderViewModelVisibleChats(FolderViewModel folder)
        {
            if (folder == null) return;
            folder.VisibleChats = EnsureFolderVisibleChats(folder.Id);
        }

        private void BindSelectedFolderVisibleChats()
        {
            var folder = FolderList == null ? null : FolderList.SelectedItem as FolderViewModel;
            BindFolderViewModelVisibleChats(folder);
        }

        private void RebindFolderVisibleChats()
        {
            var folders = FolderList == null ? null : FolderList.ItemsSource as IList<FolderViewModel>;
            BindFolderVisibleChats(folders);
        }

        private static int FindFolderIndex(IList<FolderViewModel> folders, int folderId)
        {
            if (folders == null || folders.Count == 0) return -1;
            if (folderId != int.MinValue)
            {
                for (var i = 0; i < folders.Count; i++)
                {
                    if (folders[i] != null && folders[i].Id == folderId)
                        return i;
                }
            }
            return 0;
        }

        private void ApplyFolderSelectionFlags()
        {
            if (FolderList == null || FolderList.Items == null) return;
            for (var i = 0; i < FolderList.Items.Count; i++)
            {
                var folder = FolderList.Items[i] as FolderViewModel;
                if (folder != null)
                    folder.IsSelected = i == FolderList.SelectedIndex;
            }
        }

        // A real swipe on the FlipView lands on the adjacent folder's own page (its own
        // chat list), so the content slides in naturally. Mirror the selection onto the
        // folder tabs and load that folder.
        private async void FolderFlip_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FolderFlip == null || _updatingFolderItems) return;
            if (_syncingSelection) return; // driven by the folder tabs

            var index = FolderFlip.SelectedIndex;
            if (index < 0) return;

            _syncingSelection = true;
            try
            {
                if (FolderList != null && FolderList.SelectedIndex != index)
                {
                    FolderList.SelectedIndex = index;
                    FolderList.ScrollIntoView(FolderList.SelectedItem);
                }
            }
            finally
            {
                _syncingSelection = false;
            }

            ApplyFolderSelectionFlags();

            var folder = FolderList == null ? null : FolderList.SelectedItem as FolderViewModel;
            if (folder != null)
            {
                UpdateCurrentChatList(index);
                await LoadChatsAsync(folder.Id);
            }
        }

        // Points _currentChatList at the ListView hosted by the selected folder page so
        // scroll tracking and incremental loading target the visible list.
        private void UpdateCurrentChatList(int index)
        {
            if (FolderFlip == null || index < 0) return;

            var container = FolderFlip.ContainerFromIndex(index) as FlipViewItem;
            if (container == null) return;

            var list = FindVisualChild<ListView>(container);
            if (list != null)
            {
                var folder = list.DataContext as FolderViewModel;
                if (folder != null)
                    BindChatListToFolder(list, folder.Id);
                else
                {
                    _currentChatList = list;
                    AttachChatScrollViewer();
                }
            }
        }

        private void ChatList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (DateTime.UtcNow < _suppressChatItemClickUntilUtc) return;

            var chat = e.ClickedItem as ChatViewModel;
            if (chat == null) return;

            if (chat.IsArchiveEntry)
            {
                if (AdaptiveShellNavigationService.NavigateLeft(typeof(ArchivePage)))
                    return;
                Frame.Navigate(typeof(ArchivePage));
                return;
            }

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

        private void ChatItem_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started) return;
            _ignoreChatRightTapUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
            ShowChatMenu(sender as FrameworkElement);
            e.Handled = true;
        }

        private void ChatItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (DateTime.UtcNow < _ignoreChatRightTapUntilUtc)
            {
                e.Handled = true;
                return;
            }

            ShowChatMenu(sender as FrameworkElement);
            e.Handled = true;
        }

        private void ShowChatMenu(FrameworkElement target)
        {
            if (_chatMenuOpen) return;
            if (target == null) return;
            var chat = target.DataContext as ChatViewModel;
            if (chat == null || chat.IsArchiveEntry) return;

            var menu = new MenuFlyout();

            var pinItem = new MenuFlyoutItem();
            pinItem.Text = chat.IsPinned ? "Unpin chat" : "Pin chat";
            pinItem.Tag = chat;
            pinItem.Click += ChatPinMenuItem_Click;
            menu.Items.Add(pinItem);

            var archiveItem = new MenuFlyoutItem();
            archiveItem.Text = IsChatArchived(chat) ? "Unarchive" : "Archive";
            archiveItem.Tag = chat;
            archiveItem.Click += ChatArchiveMenuItem_Click;
            menu.Items.Add(archiveItem);

            if (chat.UnreadCount > 0)
            {
                var readItem = new MenuFlyoutItem();
                readItem.Text = "Mark as read";
                readItem.Tag = chat;
                readItem.Click += ChatReadMenuItem_Click;
                menu.Items.Add(readItem);
            }

            menu.Items.Add(new MenuFlyoutSeparator());
            var removeItem = new MenuFlyoutItem();
            removeItem.Text = GetChatRemoveActionText(chat);
            removeItem.Tag = chat;
            removeItem.Click += ChatRemoveMenuItem_Click;
            menu.Items.Add(removeItem);

            _chatMenuOpen = true;
            menu.Closed += delegate { _chatMenuOpen = false; };
            try
            {
                menu.ShowAt(target);
            }
            catch
            {
                _chatMenuOpen = false;
            }
        }

        private string GetChatRemoveActionText(ChatViewModel chat)
        {
            if (chat == null) return "Delete chat";
            if (chat.IsBroadcast || (chat.IsChannel && !chat.IsGroup)) return "Leave channel";
            if (chat.IsGroup || chat.PeerType == "chat" || chat.PeerType == "channel") return "Leave group";
            return "Delete chat";
        }

        private async void ChatRemoveMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            var chat = item == null ? null : item.Tag as ChatViewModel;
            if (chat == null) return;

            try
            {
                if (chat.IsBroadcast || chat.IsGroup || chat.PeerType == "chat" || chat.PeerType == "channel")
                    await TelegramService.Instance.LeaveChatAsync(chat);
                else
                    await TelegramService.Instance.DeleteChatAsync(chat);

                RemoveChatFromCurrentList(chat);
                RefreshVisibleChats(_currentFolderId, EnsureFolderVisibleChats(_currentFolderId).Count);
                UpdateCurrentFolderUi(_currentFolderId);
                await SaveFolderCacheAsync(_currentFolderId, EnsureFolderChats(_currentFolderId));
            }
            catch (Exception ex)
            {
                ShowOperationError(ex);
            }
        }

        private async void ChatPinMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            var chat = item == null ? null : item.Tag as ChatViewModel;
            if (chat == null) return;

            try
            {
                var pinned = !chat.IsPinned;
                var updated = await TelegramService.Instance.SetChatPinnedAsync(chat, pinned);
                if (updated != null)
                    chat = updated;
                chat.IsPinned = pinned;
                RefreshChatInCurrentList(chat);
                var chats = EnsureFolderChats(_currentFolderId);
                SortChatsForDisplay(chats);
                RefreshVisibleChats(_currentFolderId, EnsureFolderVisibleChats(_currentFolderId).Count);
                UpdateCurrentFolderUi(_currentFolderId);
                await SaveFolderCacheAsync(_currentFolderId, chats);
            }
            catch (Exception ex)
            {
                ShowOperationError(ex);
            }
        }

        private async void ChatArchiveMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            var chat = item == null ? null : item.Tag as ChatViewModel;
            if (chat == null) return;

            try
            {
                var archive = !IsChatArchived(chat);
                var updated = await TelegramService.Instance.SetChatArchivedAsync(chat, archive);
                if (updated != null)
                    chat = updated;
                chat.FolderId = archive ? 1 : 0;
                chat.IsArchived = archive;

                RemoveChatFromCurrentList(chat);
                RefreshVisibleChats(_currentFolderId, EnsureFolderVisibleChats(_currentFolderId).Count);
                UpdateCurrentFolderUi(_currentFolderId);
                await SaveFolderCacheAsync(_currentFolderId, EnsureFolderChats(_currentFolderId));

                if (!_archiveMode && _currentFolderId != 1 && FolderList.SelectedIndex == 0)
                {
                    var chats = EnsureFolderChats(_currentFolderId);
                    await UpdateArchiveEntryAsync(chats, EnsureFolderKeys(_currentFolderId));
                    SortChatsForDisplay(chats);
                    RefreshVisibleChats(_currentFolderId, EnsureFolderVisibleChats(_currentFolderId).Count);
                    await SaveFolderCacheAsync(_currentFolderId, chats);
                }
            }
            catch (Exception ex)
            {
                ShowOperationError(ex);
            }
        }

        private async void ChatReadMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            var chat = item == null ? null : item.Tag as ChatViewModel;
            if (chat == null || chat.UnreadCount <= 0) return;

            try
            {
                var updated = await TelegramService.Instance.MarkChatReadAsync(chat);
                if (updated != null)
                    chat = updated;
                chat.UnreadCount = 0;
                RefreshChatInCurrentList(chat);
                RefreshVisibleChats(_currentFolderId, EnsureFolderVisibleChats(_currentFolderId).Count);
                UpdateCurrentFolderUi(_currentFolderId);
                await SaveFolderCacheAsync(_currentFolderId, EnsureFolderChats(_currentFolderId));
            }
            catch (Exception ex)
            {
                ShowOperationError(ex);
            }
        }

        private async void SavedMessagesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var chat = await TelegramService.Instance.GetSavedMessagesChatAsync();
                if (chat == null)
                {
                    chat = CreateSavedMessagesChat();
                }

                if (!AdaptiveShellNavigationService.NavigateChat(chat))
                    Frame.Navigate(typeof(ChatPage), chat);
            }
            catch
            {
                var fallback = CreateSavedMessagesChat();
                if (!AdaptiveShellNavigationService.NavigateChat(fallback))
                    Frame.Navigate(typeof(ChatPage), fallback);
            }
        }

        private ChatViewModel CreateSavedMessagesChat()
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
            if (!_archiveMode || Frame == null) return;
            e.Handled = true;
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(Chats));
        }

        private async System.Threading.Tasks.Task LoadFoldersAsync()
        {
            var version = ++_folderLoadVersion;
            ++_chatLoadVersion;
            BeginMainLoading();
            try
            {
                RefreshButton.IsEnabled = false;
                FolderList.IsEnabled = false;
                HideStatusText();

                var cachedFolders = _archiveMode ? CreateArchiveFolders() : await LoadCachedFoldersAsync();
                if (version == _folderLoadVersion && cachedFolders.Count > 0 && FolderList.ItemsSource == null)
                {
                    SetFolderItemsSource(cachedFolders);
                    EnsureSelectedFolderChatsLoaded();
                    var refreshIgnore = RefreshFoldersFromServerAsync(version);
                    return;
                }

                var folders = _archiveMode ? CreateArchiveFolders() : await TelegramService.Instance.GetFoldersAsync();
                if (version != _folderLoadVersion) return;

                if (_archiveMode)
                {
                    folders = CreateArchiveFolders();
                }
                else
                {
                    for (var i = folders.Count - 1; i >= 0; i--)
                    {
                        if (folders[i] != null && folders[i].Id == 1)
                            folders.RemoveAt(i);
                    }
                }

                SetFolderItemsSource(folders);
                if (folders.Count > 0)
                {
                    EnsureSelectedFolderChatsLoaded();
                }

                if (!_archiveMode)
                    await SaveFoldersCacheAsync(folders);
            }
            catch (Exception ex)
            {
                if (version == _folderLoadVersion)
                {
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Text = "Failed to load folders: " + ex.Message;
                }
            }
            finally
            {
                if (version == _folderLoadVersion)
                {
                    RefreshButton.IsEnabled = true;
                    FolderList.IsEnabled = true;
                }
                EndMainLoading();
            }
        }

        private async System.Threading.Tasks.Task RefreshFoldersFromServerAsync(int version)
        {
            BeginMainLoading();
            try
            {
                var folders = _archiveMode ? CreateArchiveFolders() : await TelegramService.Instance.GetFoldersAsync();
                if (version != _folderLoadVersion) return;

                if (_archiveMode)
                {
                    folders = CreateArchiveFolders();
                }
                else
                {
                    for (var i = folders.Count - 1; i >= 0; i--)
                    {
                        if (folders[i] != null && folders[i].Id == 1)
                            folders.RemoveAt(i);
                    }
                }

                var selectedFolder = FolderList.SelectedItem as FolderViewModel;
                var selectedId = selectedFolder == null ? int.MinValue : selectedFolder.Id;
                SetFolderItemsSource(folders, selectedId);
                if (folders.Count > 0)
                {
                    EnsureSelectedFolderChatsLoaded();
                }

                if (!_archiveMode)
                    await SaveFoldersCacheAsync(folders);
            }
            catch
            {
                // Cached folders are already visible; refresh can wait for the next open.
            }
            finally
            {
                EndMainLoading();
            }
        }

        private void EnsureSelectedFolderChatsLoaded()
        {
            BindSelectedFolderVisibleChats();
            QueueSelectedFolderChatsLoad();
        }

        private void QueueSelectedFolderChatsLoad()
        {
            var requestVersion = ++_selectedFolderLoadRequestVersion;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async delegate
            {
                try
                {
                    if (requestVersion != _selectedFolderLoadRequestVersion) return;

                    var folder = FolderList == null ? null : FolderList.SelectedItem as FolderViewModel;
                    if (folder == null) return;

                    BindFolderViewModelVisibleChats(folder);
                    if (FolderFlip != null)
                        UpdateCurrentChatList(FolderFlip.SelectedIndex);

                    var chats = EnsureFolderChats(folder.Id);
                    var visibleChats = EnsureFolderVisibleChats(folder.Id);
                    if (_currentFolderId == folder.Id)
                    {
                        if (chats.Count > 0 && visibleChats.Count == 0)
                        {
                            SortChatsForDisplay(chats);
                            RefreshVisibleChats(folder.Id, GetInitialVisibleChatsCount(chats));
                            UpdateCurrentFolderUi(folder.Id);
                        }
                        if (chats.Count > 0 || visibleChats.Count > 0) return;
                    }

                    await LoadChatsAsync(folder.Id);
                }
                catch
                {
                }
            });
        }

        private async System.Threading.Tasks.Task LoadChatsAsync(int folderId)
        {
            var version = ++_chatLoadVersion;
            _currentFolderId = folderId;
            ReleaseInactiveFolderMemory(folderId);
            BeginMainLoading();
            try
            {
                RefreshButton.IsEnabled = false;
                FolderList.IsEnabled = false;
                LoadMoreButton.Visibility = Visibility.Collapsed;
                HideStatusText();

                var chats = EnsureFolderChats(folderId);
                var visibleChats = EnsureFolderVisibleChats(folderId);
                SetChatListItemsSource(visibleChats);
                AttachChatScrollViewer();
                if (version != _chatLoadVersion) return;

                var keys = EnsureFolderKeys(folderId);
                if (chats.Count == 0)
                {
                    await LoadFolderCacheAsync(folderId, chats, keys);
                    SortChatsForDisplay(chats);
                    RefreshVisibleChats(folderId, GetInitialVisibleChatsCount(chats));
                }

                var isFirstFolder = FolderList.SelectedIndex == 0;
                if (!_archiveMode && isFirstFolder && folderId != 1)
                {
                    InsertArchivePlaceholder(chats, keys);
                    SortChatsForDisplay(chats);
                    RefreshVisibleChats(folderId, Math.Max(EnsureFolderVisibleChats(folderId).Count, GetInitialVisibleChatsCount(chats)));
                    var archiveIgnore = UpdateArchiveEntryAsync(chats, keys);
                }

                if (chats.Count == 0)
                {
                    await LoadChatsUntilCompleteAsync(folderId, true, version, true, 1, true);
                    QueueAppendVisibleChatsIfNearBottomDelayed();
                }
                else
                {
                    UpdateCurrentFolderUi(folderId);
                    HideStatusText();
                    var ignore = LoadChatsUntilCompleteAsync(folderId, false, version, true, 1, true);
                }

                QueueEmptyChatRetryIfNeeded(folderId, version);
            }
            catch (Exception ex)
            {
                if (version == _chatLoadVersion)
                {
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Text = "Failed to load chats: " + ex.Message;
                }
            }
            finally
            {
                if (version == _chatLoadVersion)
                {
                    RefreshButton.IsEnabled = true;
                    FolderList.IsEnabled = true;
                }
                EndMainLoading();
            }
        }

        private async void LoadMoreButton_Click(object sender, RoutedEventArgs e)
        {
            var version = _chatLoadVersion;
            if (AppendVisibleChats(_currentFolderId, TelegramAppSettings.ChatsIncrementalDisplayCount) > 0)
                return;
            await LoadChatsUntilCompleteAsync(_currentFolderId, false, version);
        }

        private async System.Threading.Tasks.Task LoadChatsUntilCompleteAsync(int folderId, bool reset, int version)
        {
            await LoadChatsUntilCompleteAsync(folderId, reset, version, reset);
        }

        private async System.Threading.Tasks.Task LoadChatsUntilCompleteAsync(int folderId, bool reset, int version, bool forceRefresh)
        {
            await LoadChatsUntilCompleteAsync(folderId, reset, version, forceRefresh, 1, true);
        }

        private async System.Threading.Tasks.Task LoadChatsUntilCompleteAsync(int folderId, bool reset, int version, bool forceRefresh, int maxPages, bool showLoading)
        {
            if (_loadingMoreChats && _loadingFolderId == folderId && _loadingVersion == version) return;
            _loadingMoreChats = true;
            _loadingFolderId = folderId;
            _loadingVersion = version;
            if (showLoading) BeginMainLoading();
            try
            {
                if (showLoading) LoadMoreButton.IsEnabled = false;
                if (showLoading) LoadMoreButton.Visibility = Visibility.Collapsed;
                HideStatusText();

                var chats = EnsureFolderChats(folderId);
                var visibleChats = EnsureFolderVisibleChats(folderId);
                var keys = EnsureFolderKeys(folderId);
                SetChatListItemsSource(visibleChats);
                AttachChatScrollViewer();

                if (reset)
                {
                    chats.Clear();
                    visibleChats.Clear();
                    keys.Clear();
                }

                var nextReset = reset;
                var emptyPages = 0;
                var loadedPages = 0;
                while (version == _chatLoadVersion && folderId == _currentFolderId)
                {
                    HideStatusText();

                    var before = CountServerChats(chats);
                    var refreshTopPage = forceRefresh && !nextReset;
                    var requestOffset = refreshTopPage ? 0 : before;
                    var page = await TelegramService.Instance.GetChatsPageAsync(folderId, requestOffset, ChatsPageSize, forceRefresh || nextReset);
                    forceRefresh = false;
                    if (version != _chatLoadVersion || folderId != _currentFolderId) return;

                    AddOrUpdateChats(chats, keys, page.Item1);

                    var isFirstFolder = FolderList.SelectedIndex == 0;
                    if ((nextReset || refreshTopPage) && !_archiveMode && isFirstFolder && folderId != 1)
                    {
                        if (maxPages == 1 && showLoading)
                        {
                            InsertArchivePlaceholder(chats, keys);
                            var archiveIgnore = UpdateArchiveEntryAsync(chats, keys);
                        }
                        else
                        {
                            await UpdateArchiveEntryAsync(chats, keys);
                        }
                    }

                    SortChatsForDisplay(chats);
                    if (visibleChats.Count == 0)
                        RefreshVisibleChats(folderId, GetInitialVisibleChatsCount(chats));
                    else if (TelegramAppSettings.ChatsShowAllImmediately)
                        RefreshVisibleChats(folderId, chats.Count);
                    else
                        RefreshVisibleChats(folderId, visibleChats.Count);
                    if (_foldersPendingVisibleAppend.Contains(folderId) && visibleChats.Count < chats.Count)
                    {
                        _foldersPendingVisibleAppend.Remove(folderId);
                        AppendVisibleChats(folderId, TelegramAppSettings.ChatsIncrementalDisplayCount);
                    }

                    nextReset = false;
                    _folderHasMore[folderId] = page.Item2;
                    UpdateCurrentFolderUi(folderId);
                    QueueAppendVisibleChatsIfNearBottom();
                    await SaveFolderCacheAsync(folderId, chats);
                    QueuePostTdLibRefreshIfNeeded(folderId, version);

                    if (CountServerChats(chats) == before) emptyPages++;
                    else emptyPages = 0;

                    if (!page.Item2 || emptyPages >= 5)
                        break;
                    loadedPages++;
                    if (maxPages > 0 && loadedPages >= maxPages)
                        break;
                }

                SortChatsForDisplay(chats);
                UpdateCurrentFolderUi(folderId);
                await SaveFolderCacheAsync(folderId, chats);
                HideStatusText();
                QueueEmptyChatRetryIfNeeded(folderId, version);
            }
            catch (Exception ex)
            {
                if (showLoading && version == _chatLoadVersion)
                {
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Text = "Failed to load chats: " + ex.Message;
                }
            }
            finally
            {
                if (_loadingFolderId == folderId && _loadingVersion == version)
                    _loadingMoreChats = false;
                if (version == _chatLoadVersion)
                    LoadMoreButton.IsEnabled = true;
                if (showLoading) EndMainLoading();
            }
        }


        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (AdaptiveShellNavigationService.NavigateLeft(typeof(SearchPage)))
                return;
            Frame.Navigate(typeof(SearchPage));
        }

        private void ContactsButton_Click(object sender, RoutedEventArgs e)
        {
            if (AdaptiveShellNavigationService.NavigateLeft(typeof(ContactsPage)))
                return;
            Frame.Navigate(typeof(ContactsPage));
        }

        private void SettingsMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (AdaptiveShellNavigationService.NavigateLeft(typeof(SettingsPage)))
                return;
            Frame.Navigate(typeof(SettingsPage));
        }

        private async void AccountMenuButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var account = _accountChat;
                if (account == null)
                    account = await TelegramService.Instance.GetSelfUserAsync();
                if (account == null) return;
                _accountChat = account;
                if (AdaptiveShellNavigationService.NavigateLeft(typeof(UserProfilePage), account))
                    return;
                Frame.Navigate(typeof(UserProfilePage), account);
            }
            catch (Exception ex)
            {
                ShowOperationError(ex);
            }
        }

        private void BottomMenuAppBar_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateBottomMenuAppBarWidth();
            QueueApplyBottomMenuAppBarGlass();
        }

        private void BottomMenuAppBar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateBottomMenuAppBarWidth();
            QueueApplyBottomMenuAppBarGlass();
            if (_bottomMenuAppBarIsOpenOrOpening || _bottomMenuAppBarIsClosing)
                StartBottomMenuAppBarGlassRenderLoop(12);
        }

        private void BottomMenuAppBar_Opening(object sender, object e)
        {
            _bottomMenuAppBarIsClosing = false;
            _bottomMenuAppBarClosingStartedUtc = DateTime.MinValue;
            _bottomMenuAppBarIsOpenOrOpening = true;
            UpdateBottomMenuAppBarWidth();
            StartBottomMenuAppBarGlassRenderLoop(24);
            QueueApplyBottomMenuAppBarGlass();
        }

        private void BottomMenuAppBar_Opened(object sender, object e)
        {
            _bottomMenuAppBarIsClosing = false;
            _bottomMenuAppBarClosingStartedUtc = DateTime.MinValue;
            _bottomMenuAppBarIsOpenOrOpening = true;
            UpdateBottomMenuAppBarWidth();
            StartBottomMenuAppBarGlassRenderLoop(8);
            QueueApplyBottomMenuAppBarGlass();
        }

        private void BottomMenuAppBar_Closing(object sender, object e)
        {
            _bottomMenuAppBarClosingStartHeight = Math.Max(GetBottomMenuAppBarOverlapHeight(), GetExpandedBottomMenuAppBarHeight());
            _bottomMenuAppBarClosingStartedUtc = DateTime.UtcNow;
            _bottomMenuAppBarIsOpenOrOpening = false;
            _bottomMenuAppBarIsClosing = true;
            UpdateBottomMenuAppBarWidth();
            StartBottomMenuAppBarGlassRenderLoop(18);
            QueueApplyBottomMenuAppBarGlass();
        }

        private void BottomMenuAppBar_Closed(object sender, object e)
        {
            _bottomMenuAppBarIsOpenOrOpening = false;
            _bottomMenuAppBarIsClosing = false;
            _bottomMenuAppBarClosingStartedUtc = DateTime.MinValue;
            CaptureBottomMenuAppBarCompactHeight();
            UpdateBottomMenuAppBarWidth();
            StartBottomMenuAppBarGlassRenderLoop(4);
            QueueApplyBottomMenuAppBarGlass();
        }

        private void UpdateBottomMenuAppBarWidth()
        {
            var appBar = FindName("BottomMenuAppBar") as FrameworkElement;
            if (appBar == null) appBar = FindName("BottomCommandBar") as FrameworkElement;
            if (appBar == null) return;

            var root = FindName("RootGrid") as FrameworkElement;
            var width = root == null ? 0 : root.ActualWidth;
            if (width <= 0)
                width = ActualWidth;
            if (width <= 0) return;

            appBar.Width = width;
            appBar.MinWidth = width;
        }

        private void HideStatusText()
        {
            if (StatusText == null) return;
            StatusText.Text = string.Empty;
            StatusText.Visibility = Visibility.Collapsed;
        }

        private void BeginMainLoading()
        {
            _mainLoadingCount++;
            SetMainLoading(true);
        }

        private void EndMainLoading()
        {
            if (_mainLoadingCount > 0) _mainLoadingCount--;
            SetMainLoading(_mainLoadingCount > 0);
        }

        private void SetMainLoading(bool active)
        {
            StatusBarLoadingIndicator.SetActive(active, TopLoadingBar);
        }

        private List<FolderViewModel> CreateArchiveFolders()
        {
            var folders = new List<FolderViewModel>();
            folders.Add(new FolderViewModel { Id = 1, Title = "Archive" });
            return folders;
        }

        private void LoadCachedAccountInfo()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                var account = new ChatViewModel();
                object title;
                if (values.TryGetValue("chats_account_title", out title) && title != null)
                {
                    account.Title = title.ToString();
                    AccountMenuButton.Label = account.Title;
                }
                else
                {
                    account.Title = "Account";
                    AccountMenuButton.Label = "Account";
                }

                object avatar;
                if (values.TryGetValue("chats_account_avatar", out avatar) && avatar != null)
                {
                    account.AvatarUri = avatar.ToString();
                    AccountMenuButton.Tag = avatar.ToString();
                }

                account.IconText = BuildAccountInitials(account.Title);
                AccountMenuButton.DataContext = account;
            }
            catch
            {
                if (AccountMenuButton != null) AccountMenuButton.Label = "Account";
            }
        }

        private async void StartAccountInfoLoad()
        {
            if (_accountInfoStarted) return;
            _accountInfoStarted = true;
            try
            {
                var account = await TelegramService.Instance.GetSelfUserAsync();
                if (account == null) return;
                _accountChat = account;
                if (!string.IsNullOrEmpty(account.Title))
                    AccountMenuButton.Label = account.Title;
                if (!string.IsNullOrEmpty(account.AvatarUri))
                    AccountMenuButton.Tag = account.AvatarUri;
                AccountMenuButton.DataContext = account;

                var values = ApplicationData.Current.LocalSettings.Values;
                values["chats_account_title"] = AccountMenuButton.Label;
                values["chats_account_avatar"] = AccountMenuButton.Tag == null ? string.Empty : AccountMenuButton.Tag.ToString();
            }
            catch
            {
                // Cached account info is enough for first paint.
            }
        }

        private static string BuildAccountInitials(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            var parts = title.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "";
            if (parts.Length == 1) return parts[0].Substring(0, 1).ToUpperInvariant();
            return (parts[0].Substring(0, 1) + parts[1].Substring(0, 1)).ToUpperInvariant();
        }

        private async System.Threading.Tasks.Task<List<FolderViewModel>> LoadCachedFoldersAsync()
        {
            var result = new List<FolderViewModel>();
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync("chats_folders_cache.txt");
                var text = await FileIO.ReadTextAsync(file);
                var lines = SplitLines(text);
                for (var i = 0; i < lines.Length; i++)
                {
                    var parts = lines[i].Split('\t');
                    if (parts.Length < 2) continue;
                    int id;
                    if (!int.TryParse(parts[0], out id)) continue;
                    if (id == 1) continue;
                    result.Add(new FolderViewModel { Id = id, Title = DecodeCacheString(parts[1]) });
                }
            }
            catch { }
            return result;
        }

        private async System.Threading.Tasks.Task SaveFoldersCacheAsync(IList<FolderViewModel> folders)
        {
            try
            {
                if (folders == null) return;
                var sb = new StringBuilder();
                for (var i = 0; i < folders.Count; i++)
                {
                    var folder = folders[i];
                    if (folder == null || folder.Id == 1) continue;
                    sb.Append(folder.Id).Append('\t').Append(EncodeCacheString(folder.Title)).Append('\n');
                }
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("chats_folders_cache.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, sb.ToString());
            }
            catch { }
        }

        private async System.Threading.Tasks.Task LoadFolderCacheAsync(int folderId, ObservableCollection<ChatViewModel> chats, HashSet<string> keys)
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(GetChatsCacheFileName(folderId));
                var text = await FileIO.ReadTextAsync(file);
                var lines = SplitLines(text);
                for (var i = 0; i < lines.Length; i++)
                {
                    var chat = DeserializeChat(lines[i]);
                    var key = GetChatKey(chat);
                    if (chat == null || string.IsNullOrEmpty(key) || keys.Contains(key)) continue;
                    keys.Add(key);
                    chats.Add(chat);
                }
            }
            catch { }
        }

        private async System.Threading.Tasks.Task SaveFolderCacheAsync(int folderId, IList<ChatViewModel> chats)
        {
            try
            {
                if (chats == null) return;
                var sb = new StringBuilder();
                for (var i = 0; i < chats.Count; i++)
                {
                    var line = SerializeChat(chats[i]);
                    if (!string.IsNullOrEmpty(line)) sb.Append(line).Append('\n');
                }
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(GetChatsCacheFileName(folderId), CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, sb.ToString());
            }
            catch { }
        }

        private static string GetChatsCacheFileName(int folderId)
        {
            return "chats_cache_" + folderId + ".txt";
        }

        private static string[] SplitLines(string text)
        {
            return (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string SerializeChat(ChatViewModel chat)
        {
            if (chat == null) return null;
            var fields = new List<string>();
            fields.Add(chat.IsArchiveEntry ? "1" : "0");
            fields.Add(EncodeCacheString(chat.PeerType));
            fields.Add(chat.PeerId.ToString());
            fields.Add(EncodeCacheString(chat.PeerKey));
            fields.Add(chat.AccessHash.ToString());
            fields.Add(EncodeCacheString(chat.Title));
            fields.Add(EncodeCacheString(chat.LastMessage));
            fields.Add(chat.LastMessageDate.ToString());
            fields.Add(chat.LastMessageIsOutgoing ? "1" : "0");
            fields.Add(chat.UnreadCount.ToString());
            fields.Add(chat.TopMessageId.ToString());
            fields.Add(chat.ReadOutboxMaxId.ToString());
            fields.Add(chat.FolderId.ToString());
            fields.Add(chat.IsMuted ? "1" : "0");
            fields.Add(chat.IsContact ? "1" : "0");
            fields.Add(chat.IsBot ? "1" : "0");
            fields.Add(chat.IsGroup ? "1" : "0");
            fields.Add(chat.IsBroadcast ? "1" : "0");
            fields.Add(chat.IsChannel ? "1" : "0");
            fields.Add(chat.CanSendMessages ? "1" : "0");
            fields.Add(chat.CanPinMessages ? "1" : "0");
            fields.Add(chat.CanDeleteMessages ? "1" : "0");
            fields.Add(chat.NoForwards ? "1" : "0");
            fields.Add(chat.SubscriberCount.ToString());
            fields.Add(chat.OnlineCount.ToString());
            fields.Add(chat.LastSeenUnixTime.ToString());
            fields.Add(EncodeCacheString(chat.LastSeenText));
            fields.Add(EncodeCacheString(chat.UserStatusKind));
            fields.Add(EncodeCacheString(chat.IconText));
            fields.Add(EncodeCacheString(chat.TypeIcon));
            fields.Add(EncodeCacheString(chat.TypeGlyph));
            fields.Add(EncodeCacheString(chat.AvatarUri));
            fields.Add(chat.AvatarPhotoId.ToString());
            fields.Add(chat.AvatarDcId.ToString());
            fields.Add(chat.IsPinned ? "1" : "0");
            fields.Add(chat.IsForum ? "1" : "0");
            fields.Add(chat.IsForumTopic ? "1" : "0");
            fields.Add(chat.TopicId.ToString());
            fields.Add(chat.TopicRootMessageId.ToString());
            fields.Add(chat.TopicIconColor.ToString());
            fields.Add(chat.TopicIconEmojiId.ToString());
            fields.Add(chat.IsTopicClosed ? "1" : "0");
            fields.Add(EncodeCacheString(chat.ParentPeerType));
            fields.Add(chat.ParentPeerId.ToString());
            fields.Add(EncodeCacheString(chat.ParentPeerKey));
            fields.Add(chat.ParentAccessHash.ToString());
            fields.Add(EncodeCacheString(chat.ParentTitle));
            fields.Add(EncodeCacheString(chat.LastMessageSenderName));
            return string.Join("\t", fields);
        }

        private static ChatViewModel DeserializeChat(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            var p = line.Split('\t');
            if (p.Length < 34) return null;
            var chat = new ChatViewModel();
            chat.IsArchiveEntry = p[0] == "1";
            chat.PeerType = DecodeCacheString(p[1]);
            chat.PeerId = ReadLong(p[2]);
            chat.PeerKey = DecodeCacheString(p[3]);
            chat.AccessHash = ReadLong(p[4]);
            chat.Title = DecodeCacheString(p[5]);
            chat.LastMessage = DecodeCacheString(p[6]);
            chat.LastMessageDate = ReadInt(p[7]);
            chat.LastMessageIsOutgoing = p[8] == "1";
            chat.UnreadCount = ReadInt(p[9]);
            chat.TopMessageId = ReadInt(p[10]);
            chat.ReadOutboxMaxId = ReadInt(p[11]);
            chat.FolderId = ReadInt(p[12]);
            chat.IsMuted = p[13] == "1";
            chat.IsContact = p[14] == "1";
            chat.IsBot = p[15] == "1";
            chat.IsGroup = p[16] == "1";
            chat.IsBroadcast = p[17] == "1";
            chat.IsChannel = p[18] == "1";
            chat.CanSendMessages = p[19] == "1";
            chat.CanPinMessages = p[20] == "1";
            chat.CanDeleteMessages = p[21] == "1";
            chat.NoForwards = p[22] == "1";
            chat.SubscriberCount = ReadInt(p[23]);
            chat.OnlineCount = ReadInt(p[24]);
            chat.LastSeenUnixTime = ReadInt(p[25]);
            chat.LastSeenText = DecodeCacheString(p[26]);
            chat.UserStatusKind = DecodeCacheString(p[27]);
            chat.IconText = DecodeCacheString(p[28]);
            chat.TypeIcon = DecodeCacheString(p[29]);
            chat.TypeGlyph = DecodeCacheString(p[30]);
            chat.AvatarUri = DecodeCacheString(p[31]);
            chat.AvatarPhotoId = ReadLong(p[32]);
            chat.AvatarDcId = ReadInt(p[33]);
            chat.IsPinned = p.Length > 34 && p[34] == "1";
            chat.IsForum = p.Length > 35 && p[35] == "1";
            chat.IsForumTopic = p.Length > 36 && p[36] == "1";
            chat.TopicId = p.Length > 37 ? ReadInt(p[37]) : 0;
            chat.TopicRootMessageId = p.Length > 38 ? ReadInt(p[38]) : 0;
            if (p.Length > 46)
            {
                chat.TopicIconColor = ReadInt(p[39]);
                chat.TopicIconEmojiId = ReadLong(p[40]);
                chat.IsTopicClosed = p[41] == "1";
                chat.ParentPeerType = DecodeCacheString(p[42]);
                chat.ParentPeerId = ReadLong(p[43]);
                chat.ParentPeerKey = DecodeCacheString(p[44]);
                chat.ParentAccessHash = ReadLong(p[45]);
                chat.ParentTitle = DecodeCacheString(p[46]);
            }
            else
            {
                chat.IsTopicClosed = p.Length > 39 && p[39] == "1";
                chat.ParentPeerType = p.Length > 40 ? DecodeCacheString(p[40]) : null;
                chat.ParentPeerId = p.Length > 41 ? ReadLong(p[41]) : 0;
                chat.ParentPeerKey = p.Length > 42 ? DecodeCacheString(p[42]) : null;
                chat.ParentAccessHash = p.Length > 43 ? ReadLong(p[43]) : 0;
                chat.ParentTitle = p.Length > 44 ? DecodeCacheString(p[44]) : null;
            }
            chat.LastMessageSenderName = p.Length > 47 ? DecodeCacheString(p[47]) : null;
            return chat;
        }

        private static string EncodeCacheString(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string DecodeCacheString(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
            catch { return string.Empty; }
        }

        private static int ReadInt(string value)
        {
            int result;
            return int.TryParse(value, out result) ? result : 0;
        }

        private static long ReadLong(string value)
        {
            long result;
            return long.TryParse(value, out result) ? result : 0;
        }

        private ObservableCollection<ChatViewModel> EnsureFolderChats(int folderId)
        {
            ObservableCollection<ChatViewModel> chats;
            if (!_folderChats.TryGetValue(folderId, out chats))
            {
                chats = new ObservableCollection<ChatViewModel>();
                _folderChats[folderId] = chats;
            }
            return chats;
        }

        private ObservableCollection<ChatViewModel> EnsureFolderVisibleChats(int folderId)
        {
            ObservableCollection<ChatViewModel> chats;
            if (!_folderVisibleChats.TryGetValue(folderId, out chats))
            {
                chats = new IncrementalChatCollection(this, folderId);
                _folderVisibleChats[folderId] = chats;
            }
            return chats;
        }

        private int GetInitialVisibleChatsCount(ICollection<ChatViewModel> chats)
        {
            if (TelegramAppSettings.ChatsShowAllImmediately)
                return chats == null ? 0 : chats.Count;

            return TelegramAppSettings.ChatsInitialDisplayCount;
        }

        private bool HasMoreVisibleChats(int folderId)
        {
            var allChats = EnsureFolderChats(folderId);
            var visibleChats = EnsureFolderVisibleChats(folderId);
            bool hasMore;
            return visibleChats.Count < allChats.Count ||
                (_folderHasMore.TryGetValue(folderId, out hasMore) && hasMore);
        }

        private async System.Threading.Tasks.Task<uint> LoadMoreVisibleChatsForIncrementalLoadingAsync(int folderId)
        {
            if (folderId != _currentFolderId)
                return 0;

            var added = AppendVisibleChats(folderId, TelegramAppSettings.ChatsIncrementalDisplayCount);
            if (added > 0)
                return (uint)added;

            bool hasMore;
            if (!_folderHasMore.TryGetValue(folderId, out hasMore) || !hasMore)
                return 0;

            _foldersPendingVisibleAppend.Add(folderId);
            if (!_loadingMoreChats)
            {
                await LoadChatsUntilCompleteAsync(folderId, false, _chatLoadVersion);
                _foldersPendingVisibleAppend.Remove(folderId);
                added = AppendVisibleChats(folderId, TelegramAppSettings.ChatsIncrementalDisplayCount);
                if (added > 0)
                    return (uint)added;
            }

            return 0;
        }

        private HashSet<string> EnsureFolderKeys(int folderId)
        {
            HashSet<string> keys;
            if (!_folderChatKeys.TryGetValue(folderId, out keys))
            {
                keys = new HashSet<string>();
                _folderChatKeys[folderId] = keys;
            }
            return keys;
        }

        private void ReleaseInactiveFolderMemory(int activeFolderId)
        {
            var visibleFolderIds = new List<int>(_folderVisibleChats.Keys);
            for (var i = 0; i < visibleFolderIds.Count; i++)
            {
                var folderId = visibleFolderIds[i];
                if (folderId == activeFolderId) continue;

                ObservableCollection<ChatViewModel> visibleChats;
                if (_folderVisibleChats.TryGetValue(folderId, out visibleChats) && visibleChats != null)
                    visibleChats.Clear();
            }

            var folderIds = new List<int>(_folderChats.Keys);
            for (var i = 0; i < folderIds.Count; i++)
            {
                var folderId = folderIds[i];
                if (folderId == activeFolderId) continue;
                TrimFolderChatsForMemory(folderId, RetainedInactiveServerChats);
            }
        }

        private void TrimFolderChatsForMemory(int folderId, int maxServerChats)
        {
            var chats = EnsureFolderChats(folderId);
            if (chats.Count <= maxServerChats) return;

            var keys = EnsureFolderKeys(folderId);
            var removed = false;
            while (CountServerChats(chats) > maxServerChats && chats.Count > 0)
            {
                var index = chats.Count - 1;
                var chat = chats[index];
                if (chat != null && chat.IsArchiveEntry)
                    break;

                var key = GetChatKey(chat);
                chats.RemoveAt(index);
                if (!string.IsNullOrEmpty(key)) keys.Remove(key);
                removed = true;
            }

            if (removed)
                _folderHasMore[folderId] = true;
        }

        private void UpdateCurrentFolderUi(int folderId)
        {
            var chats = EnsureFolderChats(folderId);
            var visibleChats = EnsureFolderVisibleChats(folderId);
            _currentChats = CopySearchableChats(chats);
            bool hasMore;
            LoadMoreButton.Visibility = visibleChats.Count < chats.Count || (_folderHasMore.TryGetValue(folderId, out hasMore) && hasMore)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void SetChatListItemsSource(ObservableCollection<ChatViewModel> visibleChats)
        {
            // Each folder page binds its own list in ChatList_Loaded; here we just make
            // sure the currently visible list points at the right collection.
            if (_currentChatList == null && FolderFlip != null)
                UpdateCurrentChatList(FolderFlip.SelectedIndex);
            if (_currentChatList != null && !object.ReferenceEquals(_currentChatList.ItemsSource, visibleChats))
                _currentChatList.ItemsSource = visibleChats;
        }

        private void ResetFolderPaging()
        {
            _emptyChatRetryVersion++;
            _delayedChatRefreshVersion++;
            _folderChats.Clear();
            _folderVisibleChats.Clear();
            _folderChatKeys.Clear();
            _folderHasMore.Clear();
            _foldersPendingVisibleAppend.Clear();
            _currentChats.Clear();
            RebindFolderVisibleChats();
            RebindLoadedChatLists();
            if (LoadMoreButton != null)
                LoadMoreButton.Visibility = Visibility.Collapsed;
            if (_chatScrollViewer != null)
            {
                _chatScrollViewer.ViewChanged -= ChatScrollViewer_ViewChanged;
                _chatScrollViewer.ViewChanging -= ChatScrollViewer_ViewChanging;
                _chatScrollViewer = null;
            }
            if (_chatLayoutList != null)
            {
                _chatLayoutList.LayoutUpdated -= ChatList_LayoutUpdated;
                _chatLayoutList = null;
            }
        }

        private void QueueDelayedCurrentFolderRefreshes()
        {
            var refreshVersion = ++_delayedChatRefreshVersion;
            QueueDelayedCurrentFolderRefresh(refreshVersion, TimeSpan.FromMilliseconds(250));
            QueueDelayedCurrentFolderRefresh(refreshVersion, TimeSpan.FromSeconds(1));
            QueueDelayedCurrentFolderRefresh(refreshVersion, TimeSpan.FromSeconds(3));
        }

        private void QueueDelayedCurrentFolderRefresh(int refreshVersion, TimeSpan delay)
        {
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(delay);
                    if (refreshVersion != _delayedChatRefreshVersion) return;
                    await EnsureCurrentFolderVisibleAfterFirstLoginAsync();
                }
                catch
                {
                }
            });
        }

        private async System.Threading.Tasks.Task EnsureCurrentFolderVisibleAfterFirstLoginAsync()
        {
            if (_archiveMode) return;

            var folder = FolderList == null ? null : FolderList.SelectedItem as FolderViewModel;
            if (folder == null) return;

            if (_currentFolderId != folder.Id)
            {
                await LoadChatsAsync(folder.Id);
                return;
            }

            EnsureCurrentChatListBound();

            var chats = EnsureFolderChats(folder.Id);
            var visibleChats = EnsureFolderVisibleChats(folder.Id);
            if (CountServerChats(chats) > 0)
            {
                if (visibleChats.Count == 0)
                    RefreshVisibleChats(folder.Id, GetInitialVisibleChatsCount(chats));
                EnsureCurrentChatListBound();
                UpdateCurrentFolderUi(folder.Id);
                return;
            }

            bool hasMore;
            if (_loadingMoreChats || (_folderHasMore.TryGetValue(folder.Id, out hasMore) && hasMore))
                return;

            var authorized = false;
            try
            {
                authorized = await TelegramService.Instance.IsAuthorizedAsync();
            }
            catch
            {
            }
            if (!authorized) return;

            await LoadChatsUntilCompleteAsync(folder.Id, true, _chatLoadVersion, true, 3, true);
            EnsureCurrentChatListBound();
        }

        private void EnsureCurrentChatListBound()
        {
            if (_currentFolderId == -1) return;

            if (_currentChatList == null && FolderFlip != null)
                UpdateCurrentChatList(FolderFlip.SelectedIndex);

            var visibleChats = EnsureFolderVisibleChats(_currentFolderId);
            if (_currentChatList != null && !object.ReferenceEquals(_currentChatList.ItemsSource, visibleChats))
                _currentChatList.ItemsSource = visibleChats;
            RebindLoadedChatLists();

            var allChats = EnsureFolderChats(_currentFolderId);
            if (visibleChats.Count == 0 && allChats.Count > 0)
                RefreshVisibleChats(_currentFolderId, GetInitialVisibleChatsCount(allChats));
        }

        private void QueueEmptyChatRetryIfNeeded(int folderId, int version)
        {
            if (_archiveMode) return;
            if (folderId != _currentFolderId || version != _chatLoadVersion) return;

            var chats = EnsureFolderChats(folderId);
            if (CountServerChats(chats) > 0) return;

            bool hasMore;
            if (_folderHasMore.TryGetValue(folderId, out hasMore) && hasMore)
                return;

            var retryVersion = ++_emptyChatRetryVersion;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2));
                    if (retryVersion != _emptyChatRetryVersion) return;
                    if (folderId != _currentFolderId || version != _chatLoadVersion) return;
                    if (CountServerChats(EnsureFolderChats(folderId)) > 0) return;

                    var authorized = false;
                    try
                    {
                        authorized = await TelegramService.Instance.IsAuthorizedAsync();
                    }
                    catch
                    {
                    }
                    if (!authorized) return;

                    var currentVersion = _chatLoadVersion;
                    await LoadChatsUntilCompleteAsync(folderId, true, currentVersion, true, 2, true);
                }
                catch
                {
                }
            });
        }

        private void QueuePostTdLibRefreshIfNeeded(int folderId, int version)
        {
            if (_archiveMode) return;
            if (!_postTdLibRefreshPending || _postTdLibRefreshRunning) return;
            if (folderId != _currentFolderId || version != _chatLoadVersion) return;

            var chats = EnsureFolderChats(folderId);
            if (CountServerChats(chats) == 0) return;

            _postTdLibRefreshPending = false;
            _postTdLibRefreshRunning = true;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(250));
                    await RefreshChatsPageAsync();
                }
                catch
                {
                }
                finally
                {
                    _postTdLibRefreshRunning = false;
                }
            });
        }

        private void ApplyExternalCacheReset()
        {
            if (_appliedCacheResetVersion == _cacheResetVersion) return;
            _appliedCacheResetVersion = _cacheResetVersion;
            if (!_archiveMode)
                _postTdLibRefreshPending = true;
            ++_folderLoadVersion;
            ++_chatLoadVersion;
            ResetFolderPaging();
            _currentFolderId = -1;
            _currentChatList = null;
            if (FolderList != null)
                FolderList.ItemsSource = null;
            if (FolderFlip != null)
                FolderFlip.ItemsSource = null;
            HideStatusText();
        }

        private void ApplyChatDisplaySettingsChange()
        {
            var initialCount = TelegramAppSettings.ChatsInitialDisplayCount;
            var incrementalCount = TelegramAppSettings.ChatsIncrementalDisplayCount;
            var showAllImmediately = TelegramAppSettings.ChatsShowAllImmediately;
            if (_appliedChatDisplaySettingsVersion == _chatDisplaySettingsVersion &&
                _appliedChatsInitialDisplayCount == initialCount &&
                _appliedChatsIncrementalDisplayCount == incrementalCount &&
                _appliedChatsShowAllImmediately == showAllImmediately)
                return;

            var oldInitialCount = _appliedChatsInitialDisplayCount;
            var oldShowAllImmediately = _appliedChatsShowAllImmediately;
            _appliedChatDisplaySettingsVersion = _chatDisplaySettingsVersion;
            _appliedChatsInitialDisplayCount = initialCount;
            _appliedChatsIncrementalDisplayCount = incrementalCount;
            _appliedChatsShowAllImmediately = showAllImmediately;

            if (_currentFolderId == -1) return;

            var visibleChats = EnsureFolderVisibleChats(_currentFolderId);
            var allChats = EnsureFolderChats(_currentFolderId);
            if (allChats.Count == 0) return;

            if (showAllImmediately)
                RefreshVisibleChats(_currentFolderId, allChats.Count);
            else if (oldShowAllImmediately || (oldInitialCount != initialCount &&
                (visibleChats.Count <= oldInitialCount || visibleChats.Count > initialCount))
            )
                RefreshVisibleChats(_currentFolderId, initialCount);
            else
                UpdateCurrentFolderUi(_currentFolderId);
        }

        private void ChatList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args == null || args.InRecycleQueue) return;
            if (_currentFolderId == -1) return;
            var visibleChats = EnsureFolderVisibleChats(_currentFolderId);
            if (visibleChats.Count == 0) return;
            if (args.ItemIndex >= visibleChats.Count - 1)
                QueueAppendVisibleChats();
        }

        private void ChatList_Loaded(object sender, RoutedEventArgs e)
        {
            var list = sender as ListView;
            if (list == null) return;

            // Bind this folder page's list to that folder's chat collection.
            var folder = list.DataContext as FolderViewModel;
            if (folder != null)
                BindChatListToFolder(list, folder.Id);
            else
                list.Unloaded -= ChatList_Unloaded;

            if (folder == null || folder.Id == _currentFolderId || _currentChatList == null)
            {
                _currentChatList = list;
                ApplyChatListOverlapPadding(list);
                AttachChatScrollViewer();
                EnsureCurrentChatListBound();
                QueueAppendVisibleChatsIfNearBottom();
            }
        }

        private void ChatList_Unloaded(object sender, RoutedEventArgs e)
        {
            var list = sender as ListView;
            if (list == null) return;

            var removeKeys = new List<int>();
            foreach (var item in _folderChatLists)
            {
                if (object.ReferenceEquals(item.Value, list))
                    removeKeys.Add(item.Key);
            }

            for (var i = 0; i < removeKeys.Count; i++)
                _folderChatLists.Remove(removeKeys[i]);

            if (object.ReferenceEquals(_currentChatList, list))
            {
                _currentChatList = null;
                if (_chatScrollViewer != null)
                {
                    _chatScrollViewer.ViewChanged -= ChatScrollViewer_ViewChanged;
                    _chatScrollViewer.ViewChanging -= ChatScrollViewer_ViewChanging;
                    _chatScrollViewer = null;
                }
                if (_chatLayoutList != null)
                {
                    _chatLayoutList.LayoutUpdated -= ChatList_LayoutUpdated;
                    _chatLayoutList = null;
                }
            }
        }

        private void ChatList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var list = sender as ListView;
            var folder = list == null ? null : list.DataContext as FolderViewModel;
            if (list != null && (folder == null || folder.Id == _currentFolderId))
            {
                if (folder != null)
                    BindChatListToFolder(list, folder.Id);
                else
                {
                    _currentChatList = list;
                    ApplyChatListOverlapPadding(list);
                    AttachChatScrollViewer();
                }
            }
            ApplyChatListOverlapPadding(list);
            QueueAppendVisibleChatsIfNearBottom();
        }

        private void BindChatListToFolder(ListView list, int folderId)
        {
            if (list == null) return;

            _folderChatLists[folderId] = list;
            list.Unloaded -= ChatList_Unloaded;
            list.Unloaded += ChatList_Unloaded;

            var visibleChats = EnsureFolderVisibleChats(folderId);
            var folder = list.DataContext as FolderViewModel;
            if (folder != null && folder.Id == folderId)
                folder.VisibleChats = visibleChats;
            if (!object.ReferenceEquals(list.ItemsSource, visibleChats))
                list.ItemsSource = visibleChats;

            if (folderId == _currentFolderId || _currentChatList == null)
            {
                _currentChatList = list;
                ApplyChatListOverlapPadding(list);
                AttachChatScrollViewer();
            }
        }

        private void BindLoadedChatListForFolder(int folderId)
        {
            ListView list;
            if (_folderChatLists.TryGetValue(folderId, out list) && list != null)
                BindChatListToFolder(list, folderId);
        }

        private void RebindLoadedChatLists()
        {
            if (_folderChatLists.Count == 0) return;

            var items = new List<KeyValuePair<int, ListView>>(_folderChatLists);
            for (var i = 0; i < items.Count; i++)
            {
                var list = items[i].Value;
                if (list == null)
                {
                    _folderChatLists.Remove(items[i].Key);
                    continue;
                }
                BindChatListToFolder(list, items[i].Key);
            }
        }

        private void AttachChatScrollViewer()
        {
            if (_currentChatList == null) return;
            AttachChatLayoutWatcher();
            var scrollViewer = FindVisualChild<ScrollViewer>(_currentChatList);
            if (scrollViewer == null || object.ReferenceEquals(scrollViewer, _chatScrollViewer)) return;

            if (_chatScrollViewer != null)
            {
                _chatScrollViewer.ViewChanged -= ChatScrollViewer_ViewChanged;
                _chatScrollViewer.ViewChanging -= ChatScrollViewer_ViewChanging;
            }

            _chatScrollViewer = scrollViewer;
            _chatScrollViewer.ViewChanged += ChatScrollViewer_ViewChanged;
            _chatScrollViewer.ViewChanging += ChatScrollViewer_ViewChanging;
        }

        private void AttachChatLayoutWatcher()
        {
            if (object.ReferenceEquals(_chatLayoutList, _currentChatList)) return;

            if (_chatLayoutList != null)
                _chatLayoutList.LayoutUpdated -= ChatList_LayoutUpdated;

            _chatLayoutList = _currentChatList;
            if (_chatLayoutList != null)
                _chatLayoutList.LayoutUpdated += ChatList_LayoutUpdated;
        }

        private void ChatScrollViewer_ViewChanging(object sender, ScrollViewerViewChangingEventArgs e)
        {
            if (_currentFolderId == -1) return;
            QueueAppendVisibleChatsIfNearBottom();
        }

        private void ChatList_LayoutUpdated(object sender, object e)
        {
            QueueAppendVisibleChatsIfNearBottom();
        }

        private void ChatScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null || _currentFolderId == -1) return;
            QueueAppendVisibleChatsIfNearBottom();
            if (!e.IsIntermediate)
                QueueAppendVisibleChatsIfNearBottomDelayed();
        }

        private void QueueAppendVisibleChatsIfNearBottom()
        {
            if (_currentFolderId == -1) return;

            var allChats = EnsureFolderChats(_currentFolderId);
            var visibleChats = EnsureFolderVisibleChats(_currentFolderId);
            bool hasMore;
            var canAppend = visibleChats.Count < allChats.Count ||
                (_folderHasMore.TryGetValue(_currentFolderId, out hasMore) && hasMore);
            if (!canAppend) return;

            AttachChatScrollViewer();
            if (_chatScrollViewer == null)
                return;

            if (IsChatScrollAtBottom())
                QueueAppendVisibleChats();
        }

        private void QueueAppendVisibleChatsIfNearBottomDelayed()
        {
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
            {
                QueueAppendVisibleChatsIfNearBottom();
            });
        }

        private bool IsChatScrollAtBottom()
        {
            AttachChatScrollViewer();
            if (_chatScrollViewer == null) return false;
            if (_chatScrollViewer.ScrollableHeight <= 0) return true;

            var remaining = _chatScrollViewer.ScrollableHeight - _chatScrollViewer.VerticalOffset;
            return remaining <= ChatAppendNearBottomThreshold;
        }

        private void QueueAppendVisibleChats()
        {
            if (_visibleAppendQueued) return;
            _visibleAppendQueued = true;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, async delegate
            {
                _visibleAppendQueued = false;
                if (_currentFolderId == -1) return;
                if (AppendVisibleChats(_currentFolderId, TelegramAppSettings.ChatsIncrementalDisplayCount) > 0)
                    return;

                bool hasMore;
                if (_folderHasMore.TryGetValue(_currentFolderId, out hasMore) && hasMore)
                {
                    var folderId = _currentFolderId;
                    _foldersPendingVisibleAppend.Add(folderId);
                    if (!_loadingMoreChats)
                    {
                        await LoadChatsUntilCompleteAsync(folderId, false, _chatLoadVersion);
                        _foldersPendingVisibleAppend.Remove(folderId);
                        AppendVisibleChats(folderId, TelegramAppSettings.ChatsIncrementalDisplayCount);
                    }
                }
            });
        }

        private void RefreshVisibleChats(int folderId, int count)
        {
            var allChats = EnsureFolderChats(folderId);
            var visibleChats = EnsureFolderVisibleChats(folderId);
            if (count < 0) count = 0;
            if (count > allChats.Count) count = allChats.Count;

            var preserveOffset = folderId == _currentFolderId && visibleChats.Count > 0;
            var oldVerticalOffset = 0.0;
            if (preserveOffset)
            {
                AttachChatScrollViewer();
                if (_chatScrollViewer != null)
                    oldVerticalOffset = _chatScrollViewer.VerticalOffset;
            }

            while (visibleChats.Count > count)
                visibleChats.RemoveAt(visibleChats.Count - 1);

            var replaceCount = Math.Min(visibleChats.Count, count);
            for (var i = 0; i < replaceCount; i++)
            {
                if (!object.ReferenceEquals(visibleChats[i], allChats[i]))
                    visibleChats[i] = allChats[i];
            }

            for (var i = visibleChats.Count; i < count; i++)
                visibleChats.Add(allChats[i]);

            if (folderId == _currentFolderId)
            {
                UpdateCurrentFolderUi(folderId);
                BindLoadedChatListForFolder(folderId);
                AttachChatScrollViewer();
                if (preserveOffset)
                    RestoreChatScrollOffset(oldVerticalOffset);
            }
            else
                BindLoadedChatListForFolder(folderId);
        }

        private void RestoreChatScrollOffset(double verticalOffset)
        {
            AttachChatScrollViewer();
            if (_chatScrollViewer == null) return;

            var target = Math.Max(0.0, Math.Min(verticalOffset, _chatScrollViewer.ScrollableHeight));
            _chatScrollViewer.ChangeView(null, target, null, true);

            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
            {
                AttachChatScrollViewer();
                if (_chatScrollViewer == null) return;
                var delayedTarget = Math.Max(0.0, Math.Min(verticalOffset, _chatScrollViewer.ScrollableHeight));
                _chatScrollViewer.ChangeView(null, delayedTarget, null, true);
            });
        }

        private int AppendVisibleChats(int folderId, int count)
        {
            var allChats = EnsureFolderChats(folderId);
            var visibleChats = EnsureFolderVisibleChats(folderId);
            if (TelegramAppSettings.ChatsShowAllImmediately)
                count = allChats.Count - visibleChats.Count;
            if (count <= 0) count = 1;
            if (visibleChats.Count >= allChats.Count) return 0;

            var start = visibleChats.Count;
            var end = Math.Min(allChats.Count, start + count);
            for (var i = start; i < end; i++)
                visibleChats.Add(allChats[i]);

            if (folderId == _currentFolderId)
            {
                UpdateCurrentFolderUi(folderId);
                BindLoadedChatListForFolder(folderId);
                AttachChatScrollViewer();
            }
            else
                BindLoadedChatListForFolder(folderId);
            return end - start;
        }

        private static int CountServerChats(IList<ChatViewModel> chats)
        {
            if (chats == null) return 0;
            var count = 0;
            for (var i = 0; i < chats.Count; i++)
            {
                var chat = chats[i];
                if (chat != null && !chat.IsArchiveEntry) count++;
            }
            return count;
        }

        private static List<ChatViewModel> CopySearchableChats(IList<ChatViewModel> chats)
        {
            var result = new List<ChatViewModel>();
            if (chats == null) return result;
            for (var i = 0; i < chats.Count; i++)
            {
                var chat = chats[i];
                if (chat != null && !chat.IsArchiveEntry) result.Add(chat);
            }
            return result;
        }

        private static void AddUniqueChats(IList<ChatViewModel> target, HashSet<string> seen, IList<ChatViewModel> source)
        {
            if (target == null || seen == null || source == null) return;
            for (var i = 0; i < source.Count; i++)
            {
                var chat = source[i];
                var key = GetChatKey(chat);
                if (string.IsNullOrEmpty(key) || seen.Contains(key)) continue;
                seen.Add(key);
                target.Add(chat);
            }
        }

        private static void AddOrUpdateChats(IList<ChatViewModel> target, HashSet<string> seen, IList<ChatViewModel> source)
        {
            if (target == null || seen == null || source == null) return;
            for (var i = 0; i < source.Count; i++)
            {
                var chat = source[i];
                var key = GetChatKey(chat);
                if (string.IsNullOrEmpty(key)) continue;
                var index = IndexOfChat(target, key);
                if (index >= 0)
                {
                    target[index] = chat;
                }
                else
                {
                    seen.Add(key);
                    target.Add(chat);
                }
            }
        }

        private static void SortChatsForDisplay(IList<ChatViewModel> chats)
        {
            if (chats == null || chats.Count < 2) return;

            var sorted = new List<ChatViewModel>();
            var originalIndexes = new Dictionary<string, int>();
            for (var i = 0; i < chats.Count; i++)
            {
                if (chats[i] != null)
                {
                    sorted.Add(chats[i]);
                    var key = GetChatKey(chats[i]);
                    if (!string.IsNullOrEmpty(key) && !originalIndexes.ContainsKey(key))
                        originalIndexes.Add(key, i);
                }
            }

            sorted.Sort(delegate(ChatViewModel a, ChatViewModel b)
            {
                var aa = a != null && a.IsArchiveEntry;
                var ba = b != null && b.IsArchiveEntry;
                if (aa && !ba) return -1;
                if (!aa && ba) return 1;

                var ap = a != null && a.IsPinned;
                var bp = b != null && b.IsPinned;
                if (ap && !bp) return -1;
                if (!ap && bp) return 1;
                if (ap && bp)
                {
                    int ai;
                    int bi;
                    if (!originalIndexes.TryGetValue(GetChatKey(a), out ai)) ai = 0;
                    if (!originalIndexes.TryGetValue(GetChatKey(b), out bi)) bi = 0;
                    var indexCompare = ai.CompareTo(bi);
                    if (indexCompare != 0) return indexCompare;
                }

                var ad = a == null ? 0 : a.LastMessageDate;
                var bd = b == null ? 0 : b.LastMessageDate;
                var dateCompare = bd.CompareTo(ad);
                if (dateCompare != 0) return dateCompare;

                var am = a == null ? 0 : a.TopMessageId;
                var bm = b == null ? 0 : b.TopMessageId;
                var messageCompare = bm.CompareTo(am);
                if (messageCompare != 0) return messageCompare;

                var at = a == null ? string.Empty : (a.Title ?? string.Empty);
                var bt = b == null ? string.Empty : (b.Title ?? string.Empty);
                return string.Compare(at, bt, StringComparison.OrdinalIgnoreCase);
            });

            for (var i = 0; i < sorted.Count; i++)
            {
                if (!object.ReferenceEquals(chats[i], sorted[i]))
                    chats[i] = sorted[i];
            }
        }

        private static int IndexOfChat(IList<ChatViewModel> chats, string key)
        {
            if (chats == null || string.IsNullOrEmpty(key)) return -1;
            for (var i = 0; i < chats.Count; i++)
            {
                if (GetChatKey(chats[i]) == key) return i;
            }
            return -1;
        }

        private static string GetChatKey(ChatViewModel chat)
        {
            if (chat == null) return null;
            if (chat.IsArchiveEntry) return "archive";
            if (!string.IsNullOrEmpty(chat.PeerKey)) return chat.PeerKey;
            return (chat.PeerType ?? string.Empty) + ":" + chat.PeerId;
        }

        private bool IsChatArchived(ChatViewModel chat)
        {
            if (chat == null) return false;
            return _archiveMode || chat.FolderId == 1;
        }

        private void RefreshChatInCurrentList(ChatViewModel chat)
        {
            var chats = EnsureFolderChats(_currentFolderId);
            var key = GetChatKey(chat);
            var index = IndexOfChat(chats, key);
            if (index >= 0) chats[index] = chat;
        }

        private void RemoveChatFromCurrentList(ChatViewModel chat)
        {
            var chats = EnsureFolderChats(_currentFolderId);
            var keys = EnsureFolderKeys(_currentFolderId);
            var key = GetChatKey(chat);
            var index = IndexOfChat(chats, key);
            if (index >= 0) chats.RemoveAt(index);
            if (!string.IsNullOrEmpty(key)) keys.Remove(key);
        }

        private void TelegramService_ChatRemoved(object sender, ChatViewModel chat)
        {
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                ApplyRemovedChat(chat);
            });
        }

        private void ApplyPendingRemovedChats()
        {
            var removedChats = TelegramService.Instance.TakeRemovedChats();
            for (var i = 0; i < removedChats.Count; i++)
                ApplyRemovedChat(removedChats[i]);
        }

        private void ApplyRemovedChat(ChatViewModel chat)
        {
            var key = GetChatKey(chat);
            if (string.IsNullOrEmpty(key)) return;

            var folderIds = new List<int>(_folderChats.Keys);
            for (var i = 0; i < folderIds.Count; i++)
            {
                var folderId = folderIds[i];
                var changed = false;
                var chats = EnsureFolderChats(folderId);
                var index = IndexOfChat(chats, key);
                if (index >= 0)
                {
                    chats.RemoveAt(index);
                    changed = true;
                }

                var keys = EnsureFolderKeys(folderId);
                if (keys.Remove(key))
                    changed = true;

                ObservableCollection<ChatViewModel> visibleChats;
                if (_folderVisibleChats.TryGetValue(folderId, out visibleChats))
                {
                    for (var j = visibleChats.Count - 1; j >= 0; j--)
                    {
                        if (GetChatKey(visibleChats[j]) == key)
                            visibleChats.RemoveAt(j);
                    }
                }

                if (changed)
                {
                    if (folderId == _currentFolderId)
                    {
                        RefreshVisibleChats(folderId, EnsureFolderVisibleChats(folderId).Count);
                        UpdateCurrentFolderUi(folderId);
                    }

                    var ignored = SaveFolderCacheAsync(folderId, chats);
                }
            }
        }

        private void ShowOperationError(Exception ex)
        {
            if (StatusText == null) return;
            StatusText.Text = ex == null ? "Action failed" : "Action failed: " + ex.Message;
            StatusText.Visibility = Visibility.Visible;
        }

        private T FindVisualChild<T>(DependencyObject obj, string name) where T : DependencyObject
        {
            for (int i = 0; i < Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(obj, i);
                if (child is T && (string)child.GetValue(FrameworkElement.NameProperty) == name)
                {
                    return (T)child;
                }
                var childOfChild = FindVisualChild<T>(child, name);
                if (childOfChild != null) return childOfChild;
            }
            return null;
        }

        private T FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
        {
            if (obj == null) return null;
            for (int i = 0; i < Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(obj, i);
                if (child is T) return (T)child;
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null) return childOfChild;
            }
            return null;
        }

        private void InsertArchivePlaceholder(IList<ChatViewModel> chats, HashSet<string> keys)
        {
            if (chats == null) return;
            if (keys != null && keys.Contains("archive")) return;
            for (var i = 0; i < chats.Count; i++)
            {
                var existing = chats[i];
                if (existing != null && existing.IsArchiveEntry)
                {
                    NormalizeArchiveEntry(existing);
                    if (keys != null) keys.Add("archive");
                    return;
                }
            }

            var entry = CreateArchiveEntry("Archived chats", 0, 0);
            chats.Insert(0, entry);
            if (keys != null) keys.Add("archive");
        }

        private ChatViewModel CreateArchiveEntry(string preview, int unread, int latestDate)
        {
            return new ChatViewModel
            {
                IsArchiveEntry = true,
                PeerType = "archive",
                PeerKey = "archive",
                Title = "Archive",
                LastMessage = string.IsNullOrEmpty(preview) ? "Archived chats" : preview,
                LastMessageDate = latestDate,
                UnreadCount = unread,
                AvatarUri = "ms-appx:///Assets/archive.png",
                IconText = ""
            };
        }

        private void NormalizeArchiveEntry(ChatViewModel entry)
        {
            if (entry == null || !entry.IsArchiveEntry) return;
            entry.AvatarUri = "ms-appx:///Assets/archive.png";
            entry.IconText = "";
        }

        private void ReplaceArchiveEntry(IList<ChatViewModel> chats, ChatViewModel entry)
        {
            if (chats == null || entry == null) return;
            NormalizeArchiveEntry(entry);
            for (var i = 0; i < chats.Count; i++)
            {
                var chat = chats[i];
                if (chat != null && chat.IsArchiveEntry)
                {
                    if (chat.LastMessage == entry.LastMessage &&
                        chat.LastMessageDate == entry.LastMessageDate &&
                        chat.UnreadCount == entry.UnreadCount &&
                        chat.AvatarUri == entry.AvatarUri)
                        return;
                    chats[i] = entry;
                    return;
                }
            }
            chats.Insert(0, entry);
        }

        private async System.Threading.Tasks.Task UpdateArchiveEntryAsync(IList<ChatViewModel> chats, HashSet<string> keys)
        {
            if (chats == null) return;
            InsertArchivePlaceholder(chats, keys);
            try
            {
                var archived = await TelegramService.Instance.GetArchivePreviewChatsAsync();
                if (archived == null || archived.Count == 0) return;

                var unread = 0;
                var preview = "";
                var latestDate = 0;
                var previewCount = 0;
                for (var i = 0; i < archived.Count; i++)
                {
                    var chat = archived[i];
                    if (chat == null || chat.IsPinned) continue;
                    unread += chat.UnreadCount;
                    if (previewCount < 4)
                    {
                        if (!string.IsNullOrEmpty(preview)) preview += ", ";
                        preview += chat.Title;
                    }
                    previewCount++;
                    if (chat.LastMessageDate > latestDate) latestDate = chat.LastMessageDate;
                }

                ReplaceArchiveEntry(chats, CreateArchiveEntry(preview, unread, latestDate));
                SortChatsForDisplay(chats);
                if (_currentFolderId != -1 && object.ReferenceEquals(chats, EnsureFolderChats(_currentFolderId)))
                    RefreshVisibleChats(_currentFolderId, Math.Max(EnsureFolderVisibleChats(_currentFolderId).Count, GetInitialVisibleChatsCount(EnsureFolderChats(_currentFolderId))));
                if (keys != null) keys.Add("archive");
            }
            catch
            {
                // Archive preview is optional; the main chat list should still open.
            }
        }
    }

    public sealed class ArchiveEntryVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var isArchiveEntry = value is bool && (bool)value;
            return isArchiveEntry ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return value is Visibility && (Visibility)value == Visibility.Visible;
        }
    }

    public sealed class NonArchiveEntryVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var isArchiveEntry = value is bool && (bool)value;
            return isArchiveEntry ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return !(value is Visibility && (Visibility)value == Visibility.Visible);
        }
    }
}
