using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Telegram.Models;
using Telegram.Notifications;
using Windows.Networking.Connectivity;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Telegram.Services
{
    internal sealed class TdLibTelegramClient
    {
        public event EventHandler<long> NewMessageArrived;
        public event EventHandler<long> MessageContentUpdated;
        public event EventHandler<long> MessagesDeleted;
        public event EventHandler<long> UserStatusChanged;

        private const int ArchiveFolderId = 1;
        private const int TdFolderIdOffset = 1000;
        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, TaskCompletionSource<JObject>> _pending = new Dictionary<string, TaskCompletionSource<JObject>>();
        private readonly Dictionary<long, JObject> _chats = new Dictionary<long, JObject>();
        private readonly Dictionary<long, JArray> _pendingChatPositions = new Dictionary<long, JArray>();
        private readonly HashSet<long> _archivedChatIds = new HashSet<long>();
        private readonly Dictionary<long, JObject> _users = new Dictionary<long, JObject>();
        private readonly Dictionary<long, JObject> _supergroups = new Dictionary<long, JObject>();
        private readonly Dictionary<long, JObject> _basicGroups = new Dictionary<long, JObject>();
        private readonly Dictionary<string, long> _peerChatIds = new Dictionary<string, long>();
        private readonly Dictionary<string, long> _messageIds = new Dictionary<string, long>();
        private readonly Dictionary<long, int> _messageIdsReverse = new Dictionary<long, int>();
        private readonly Dictionary<long, List<ChatViewModel>> _avatarTargets = new Dictionary<long, List<ChatViewModel>>();
        private readonly Dictionary<long, List<ChatMessageViewModel>> _messageAvatarTargets = new Dictionary<long, List<ChatMessageViewModel>>();
        private readonly Dictionary<long, List<ChatMessageViewModel>> _messagePreviewTargets = new Dictionary<long, List<ChatMessageViewModel>>();
        private readonly Dictionary<long, List<ChatMediaItemViewModel>> _mediaItemPreviewTargets = new Dictionary<long, List<ChatMediaItemViewModel>>();
        private readonly Dictionary<long, List<StickerItemViewModel>> _stickerFileTargets = new Dictionary<long, List<StickerItemViewModel>>();
        private readonly Dictionary<long, List<ChatMessageViewModel>> _messageDownloadTargets = new Dictionary<long, List<ChatMessageViewModel>>();
        private readonly Dictionary<long, List<ChatMediaItemViewModel>> _mediaItemDownloadTargets = new Dictionary<long, List<ChatMediaItemViewModel>>();
        private readonly Dictionary<long, FileDownloadWatcher> _fileDownloadWatchers = new Dictionary<long, FileDownloadWatcher>();
        private readonly List<JObject> _pendingMessageUpdates = new List<JObject>();
        private readonly Dictionary<long, HashSet<long>> _pendingMessageRefreshIds = new Dictionary<long, HashSet<long>>();
        private readonly HashSet<long> _pendingReplyMarkupResetChatIds = new HashSet<long>();
        private readonly Dictionary<long, List<int>> _pendingDeletedMessageIds = new Dictionary<long, List<int>>();
        private readonly Dictionary<long, string> _customEmojiIconUris = new Dictionary<long, string>();
        private readonly Dictionary<string, int> _scopeMuteFor = new Dictionary<string, int>();
        private JArray _chatFolderInfos = new JArray();
        private long _clientChatId;
        private int _extraId;
        private int _compactMessageId;
        private IntPtr _client = IntPtr.Zero;
        private bool _started;
        private bool _parametersSent;
        private bool _encryptionKeySent;
        private string _authorizationState = "";
        private string _lastTdLibError = "";
        private string _qrLink = "";
        private TaskCompletionSource<string> _authStateWaiter;
        private TaskCompletionSource<bool> _folderWaiter;
        private StorageFolder _filesFolder;
        private int _currentProxyId;
        private bool _proxyApplied;
        private Task _proxyApplyTask;
        private string _lastAppliedProxySignature = "";
        private long _selfUserId;
        private bool _selfPremiumKnown;
        private bool _selfIsPremium;
        private bool _notificationScopesLoaded;

        public ProxySettings Proxy { get; private set; }

        public TdLibTelegramClient()
        {
            Proxy = ProxySettings.Load();
        }

        public int CallProtocolVariantCount
        {
            get { return 1; }
        }

        public async Task StartAsync()
        {
            if (_started) return;

            lock (_syncRoot)
            {
                if (_started) return;
                _started = true;
                _client = TdJson.td_json_client_create();
            }

            var localFolder = ApplicationData.Current.LocalFolder;
            var appFolder = await localFolder.CreateFolderAsync("Unogram", CreationCollisionOption.OpenIfExists);
            _filesFolder = await appFolder.CreateFolderAsync("td_db_files", CreationCollisionOption.OpenIfExists);
            Task.Run((Action)ReceiveLoop);
            SendFireAndForget(new JObject { ["@type"] = "setLogVerbosityLevel", ["new_verbosity_level"] = 1 });
        }

        public void Start()
        {
            var ignored = StartAsync();
        }

        public async Task<bool> IsAuthorizedAsync()
        {
            await StartAsync();
            if (_authorizationState != "authorizationStateReady" &&
                _authorizationState != "authorizationStateWaitPhoneNumber" &&
                _authorizationState != "authorizationStateWaitCode" &&
                _authorizationState != "authorizationStateWaitPassword" &&
                _authorizationState != "authorizationStateWaitOtherDeviceConfirmation" &&
                _authorizationState != "authorizationStateClosed")
            {
                try
                {
                    await WaitForAnyAuthStateAsync(new[]
                    {
                        "authorizationStateWaitPhoneNumber",
                        "authorizationStateWaitCode",
                        "authorizationStateWaitPassword",
                        "authorizationStateWaitOtherDeviceConfirmation",
                        "authorizationStateReady",
                        "authorizationStateClosed"
                    }, TimeSpan.FromSeconds(30));
                }
                catch
                {
                }
            }
            return _authorizationState == "authorizationStateReady";
        }

        public async Task ResetAsync()
        {
            await StartAsync();
            _qrLink = "";
            _parametersSent = false;
            _encryptionKeySent = false;
            ApplicationData.Current.LocalSettings.Values["tdlib_authorized"] = false;
            SendFireAndForget(new JObject { ["@type"] = "logOut" });
        }

        public async Task RefreshConnectionSettingsAsync()
        {
            await StartAsync();
            ResetProxyApplyState();
            await ApplySavedProxyAsync();
        }

        public void ClearDialogsCache()
        {
            lock (_syncRoot)
            {
                _chats.Clear();
                _pendingChatPositions.Clear();
                _archivedChatIds.Clear();
                _peerChatIds.Clear();
                _notificationScopesLoaded = false;
            }
        }

        public void ApplyProxySettings(ProxySettings settings)
        {
            Proxy = settings ?? new ProxySettings();
            Proxy.Save();
            ResetProxyApplyState();
            ApplySavedProxy();
        }

        public void ApplySavedProxy()
        {
            var ignored = ApplySavedProxyAsync();
        }

        private Task ApplySavedProxyAsync()
        {
            if (_client == IntPtr.Zero)
            {
                ResetProxyApplyState();
                return Task.FromResult(true);
            }

            lock (_syncRoot)
            {
                if (_proxyApplied)
                    return Task.FromResult(true);
                if (_proxyApplyTask != null && !_proxyApplyTask.IsCompleted)
                    return _proxyApplyTask;

                _proxyApplyTask = ApplySavedProxyCoreAsync();
                return _proxyApplyTask;
            }
        }

        private async Task ApplySavedProxyCoreAsync()
        {
            try
            {
                if (Proxy == null || ProxySettings.IsSystemMode(Proxy.Mode))
                    await ApplySystemProxyAsync();
                else
                    ApplyExplicitProxy(Proxy);
            }
            finally
            {
                lock (_syncRoot)
                {
                    _proxyApplied = true;
                }
            }
        }

        private void ResetProxyApplyState()
        {
            lock (_syncRoot)
            {
                _proxyApplied = false;
                _proxyApplyTask = null;
            }
        }

        private async Task ApplySystemProxyAsync()
        {
            var systemProxy = await TryGetSystemProxyAsync();
            if (systemProxy == null)
            {
                Debug.WriteLine("TDLIB system proxy: no Windows HTTP/SOCKS proxy was returned; using direct route/VPN");
                RemoveCurrentProxy();
                return;
            }

            Debug.WriteLine("TDLIB system proxy: " + systemProxy.Mode + " " + systemProxy.Server + ":" + systemProxy.Port);
            ApplyExplicitProxy(systemProxy);
        }

        private async Task<ProxySettings> TryGetSystemProxyAsync()
        {
            var targets = new[]
            {
                "http://149.154.167.50/",
                "https://149.154.167.50/",
                "http://149.154.175.50/",
                "https://149.154.175.50/",
                "http://telegram.org/",
                "https://telegram.org/",
                "http://api.telegram.org/",
                "https://api.telegram.org/"
            };

            for (var i = 0; i < targets.Length; i++)
            {
                try
                {
                    var target = new Uri(targets[i]);
                    var configuration = await NetworkInformation.GetProxyConfigurationAsync(target);
                    if (configuration == null || configuration.ProxyUris == null)
                    {
                        Debug.WriteLine("TDLIB system proxy probe " + targets[i] + ": empty");
                        continue;
                    }

                    foreach (var uri in configuration.ProxyUris)
                    {
                        var proxy = BuildProxySettingsFromSystemUri(uri);
                        if (proxy != null)
                        {
                            Debug.WriteLine("TDLIB system proxy probe " + targets[i] + ": " + uri.AbsoluteUri);
                            return proxy;
                        }
                    }

                    Debug.WriteLine("TDLIB system proxy probe " + targets[i] + ": direct=" + configuration.CanConnectDirectly);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("TDLIB system proxy probe " + targets[i] + " failed: " + ex.Message);
                }
            }

            return null;
        }

        private static ProxySettings BuildProxySettingsFromSystemUri(Uri uri)
        {
            if (uri == null || string.IsNullOrWhiteSpace(uri.Host)) return null;
            var scheme = (uri.Scheme ?? "").ToLowerInvariant();
            var settings = new ProxySettings();
            if (scheme == "socks" || scheme == "socks5")
                settings.Mode = ProxySettings.ModeSocks;
            else if (scheme == "http" || scheme == "https")
                settings.Mode = ProxySettings.ModeHttp;
            else
                return null;

            settings.Server = uri.Host;
            settings.Port = uri.Port > 0 ? uri.Port.ToString() : (settings.Mode == ProxySettings.ModeSocks ? "1080" : "8080");
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(new[] { ':' }, 2);
                settings.Username = Uri.UnescapeDataString(parts[0]);
                if (parts.Length > 1) settings.Password = Uri.UnescapeDataString(parts[1]);
            }
            return settings;
        }

        private void ApplyExplicitProxy(ProxySettings settings)
        {
            int port;
            if (settings == null || string.IsNullOrWhiteSpace(settings.Server) || !int.TryParse(settings.Port, out port))
                return;

            var signature = BuildProxySignature(settings, port);
            if (_currentProxyId != 0 && string.Equals(_lastAppliedProxySignature, signature, StringComparison.Ordinal))
                return;

            RemoveCurrentProxy();

            JObject type;
            if (settings.Mode == ProxySettings.ModeMtproto)
                type = new JObject { ["@type"] = "proxyTypeMtproto", ["secret"] = settings.Secret ?? "" };
            else if (settings.Mode == ProxySettings.ModeHttp)
                type = new JObject { ["@type"] = "proxyTypeHttp", ["username"] = settings.Username ?? "", ["password"] = settings.Password ?? "", ["http_only"] = false };
            else
                type = new JObject { ["@type"] = "proxyTypeSocks5", ["username"] = settings.Username ?? "", ["password"] = settings.Password ?? "" };

            SendFireAndForget(new JObject
            {
                ["@type"] = "addProxy",
                ["proxy"] = new JObject
                {
                    ["@type"] = "proxy",
                    ["server"] = settings.Server,
                    ["port"] = port,
                    ["type"] = type
                },
                ["enable"] = true,
            });
            _lastAppliedProxySignature = signature;
            Debug.WriteLine("TDLIB => addProxy " + settings.Mode + " " + settings.Server + ":" + port);
        }

        private void RemoveCurrentProxy()
        {
            if (_currentProxyId != 0)
            {
                SendFireAndForget(new JObject { ["@type"] = "removeProxy", ["proxy_id"] = _currentProxyId });
                _currentProxyId = 0;
            }
            _lastAppliedProxySignature = "";
        }

        private static string BuildProxySignature(ProxySettings settings, int port)
        {
            return (settings.Mode ?? "") + "|" +
                (settings.Server ?? "").ToLowerInvariant() + "|" +
                port + "|" +
                (settings.Username ?? "") + "|" +
                (settings.Password ?? "") + "|" +
                (settings.Secret ?? "");
        }

        public async Task<PhoneCodeResponse> SendPhoneCodeAsync(string phoneNumber)
        {
            await StartAsync();
            if (_authorizationState == "authorizationStateReady")
                return new PhoneCodeResponse { Authorized = true, CodeType = "Telegram app" };

            await WaitForAnyAuthStateAsync(new[] { "authorizationStateWaitPhoneNumber", "authorizationStateWaitCode", "authorizationStateWaitPassword", "authorizationStateReady" }, TimeSpan.FromSeconds(20));
            if (_authorizationState == "authorizationStateReady")
                return new PhoneCodeResponse { Authorized = true, CodeType = "Telegram app" };
            if (_authorizationState == "authorizationStateWaitPassword")
                throw new TelegramCloudPasswordRequiredException("Two-step verification is enabled. Enter your Telegram cloud password.");

            await ApplySavedProxyAsync();
            _lastTdLibError = "";
            await SendAsync(new JObject { ["@type"] = "setAuthenticationPhoneNumber", ["phone_number"] = phoneNumber }, TimeSpan.FromSeconds(30));
            await WaitForAnyAuthStateAsync(new[] { "authorizationStateWaitCode", "authorizationStateWaitPassword", "authorizationStateReady" }, TimeSpan.FromSeconds(60));
            if (_authorizationState == "authorizationStateWaitPassword")
                throw new TelegramCloudPasswordRequiredException("Two-step verification is enabled. Enter your Telegram cloud password.");

            return new PhoneCodeResponse { Authorized = _authorizationState == "authorizationStateReady", CodeType = "Telegram app", Length = 0, Timeout = 0, PhoneCodeHash = "tdlib" };
        }

        public async Task SignInWithPhoneCodeAsync(string phoneNumber, string phoneCodeHash, string phoneCode)
        {
            await StartAsync();
            await WaitForAnyAuthStateAsync(new[] { "authorizationStateWaitCode", "authorizationStateWaitPassword", "authorizationStateReady" }, TimeSpan.FromSeconds(20));
            if (_authorizationState == "authorizationStateReady") return;
            if (_authorizationState == "authorizationStateWaitPassword")
                throw new TelegramCloudPasswordRequiredException("Two-step verification is enabled. Enter your Telegram cloud password.");

            _lastTdLibError = "";
            await SendAsync(new JObject { ["@type"] = "checkAuthenticationCode", ["code"] = phoneCode }, TimeSpan.FromSeconds(30));
            await WaitForAnyAuthStateAsync(new[] { "authorizationStateReady", "authorizationStateWaitPassword" }, TimeSpan.FromSeconds(60));
            if (_authorizationState == "authorizationStateWaitPassword")
                throw new TelegramCloudPasswordRequiredException("Two-step verification is enabled. Enter your Telegram cloud password.");
        }

        public async Task SignInWithCloudPasswordAsync(string password)
        {
            await StartAsync();
            await WaitForAnyAuthStateAsync(new[] { "authorizationStateWaitPassword", "authorizationStateReady" }, TimeSpan.FromSeconds(20));
            if (_authorizationState == "authorizationStateReady") return;

            _lastTdLibError = "";
            await SendAsync(new JObject { ["@type"] = "checkAuthenticationPassword", ["password"] = password }, TimeSpan.FromSeconds(30));
            await WaitForAnyAuthStateAsync(new[] { "authorizationStateReady" }, TimeSpan.FromSeconds(60));
        }

        public async Task<QrLoginInfo> CreateQrLoginAsync()
        {
            await StartAsync();
            if (_authorizationState == "authorizationStateReady")
                return new QrLoginInfo { LoginUrl = "authorized", ExpiresUnix = 0 };

            await WaitForAnyAuthStateAsync(new[]
            {
                "authorizationStateWaitPhoneNumber",
                "authorizationStateWaitCode",
                "authorizationStateWaitPassword",
                "authorizationStateWaitOtherDeviceConfirmation",
                "authorizationStateReady"
            }, TimeSpan.FromSeconds(30));
            if (_authorizationState == "authorizationStateReady")
                return new QrLoginInfo { LoginUrl = "authorized", ExpiresUnix = 0 };
            if (_authorizationState == "authorizationStateWaitPassword")
                throw new TelegramCloudPasswordRequiredException("Two-step verification is enabled. Enter your Telegram cloud password.");
            if (_authorizationState == "authorizationStateWaitOtherDeviceConfirmation" && !string.IsNullOrEmpty(_qrLink))
                return new QrLoginInfo { LoginUrl = _qrLink, ExpiresUnix = (int)(UnixNow() + 240) };

            await ApplySavedProxyAsync();
            _qrLink = "";
            _lastTdLibError = "";
            await SendAsync(new JObject { ["@type"] = "requestQrCodeAuthentication" }, TimeSpan.FromSeconds(30));
            await WaitForAnyAuthStateAsync(new[] { "authorizationStateWaitOtherDeviceConfirmation", "authorizationStateWaitPassword", "authorizationStateReady" }, TimeSpan.FromSeconds(30));
            if (_authorizationState == "authorizationStateReady")
                return new QrLoginInfo { LoginUrl = "authorized", ExpiresUnix = 0 };
            if (_authorizationState == "authorizationStateWaitPassword")
                throw new TelegramCloudPasswordRequiredException("Two-step verification is enabled. Enter your Telegram cloud password.");
            if (string.IsNullOrEmpty(_qrLink))
                throw new InvalidOperationException("Telegram did not return a QR login link.");

            return new QrLoginInfo { LoginUrl = _qrLink, ExpiresUnix = (int)(UnixNow() + 240) };
        }

        public async Task<LoginTokenResponse> ExportLoginTokenAsync()
        {
            var qr = await CreateQrLoginAsync();
            return new LoginTokenResponse { Success = qr.LoginUrl == "authorized", Link = qr.LoginUrl, Expires = qr.ExpiresUnix };
        }

        public async Task<LoginTokenResponse> CheckLoginTokenAsync()
        {
            await StartAsync();
            if (await IsAuthorizedAsync())
                return new LoginTokenResponse { Success = true };
            if (_authorizationState == "authorizationStateWaitPassword")
                throw new TelegramCloudPasswordRequiredException("Two-step verification is enabled. Enter your Telegram cloud password.");
            return new LoginTokenResponse { Success = false, Expired = false, Link = _qrLink, Expires = UnixNow() + 120 };
        }

        public async Task PollServiceUpdatesAsync()
        {
            await StartAsync();
            await Task.Delay(1);
        }

        public async Task<List<FolderViewModel>> GetDialogFiltersAsync()
        {
            await StartAsync();
            var result = new List<FolderViewModel> { new FolderViewModel { Id = -1, Title = "All chats" } };

            JArray folders;
            lock (_syncRoot)
            {
                folders = _chatFolderInfos == null ? null : (JArray)_chatFolderInfos.DeepClone();
                if (folders == null || folders.Count == 0)
                    _folderWaiter = new TaskCompletionSource<bool>();
            }

            if (folders == null || folders.Count == 0)
            {
                try
                {
                    var waiter = _folderWaiter;
                    if (waiter != null)
                        await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromSeconds(3)));
                    lock (_syncRoot)
                    {
                        folders = _chatFolderInfos == null ? null : (JArray)_chatFolderInfos.DeepClone();
                    }
                }
                catch
                {
                }
            }

            if (folders != null)
            {
                for (var i = 0; i < folders.Count; i++)
                {
                    var folder = folders[i] as JObject;
                    if (folder == null) continue;
                    var id = ReadInt(folder["id"]);
                    var title = ReadFolderTitle(folder);
                    if (id != 0) result.Add(new FolderViewModel { Id = ToAppFolderId(id), Title = title });
                }
            }
            return result;
        }

        public async Task<List<ChatViewModel>> GetDialogsAsync(int folderId)
        {
            var page = await GetDialogsPageAsync(folderId, 0, 80, false);
            return page.Item1;
        }

        private async Task EnsureNotificationScopesLoadedAsync()
        {
            lock (_syncRoot)
            {
                if (_notificationScopesLoaded) return;
            }

            var privateMute = await GetScopeMuteForAsync("notificationSettingsScopePrivateChats");
            var groupMute = await GetScopeMuteForAsync("notificationSettingsScopeGroupChats");
            var channelMute = await GetScopeMuteForAsync("notificationSettingsScopeChannelChats");

            lock (_syncRoot)
            {
                _scopeMuteFor["private"] = privateMute;
                _scopeMuteFor["group"] = groupMute;
                _scopeMuteFor["channel"] = channelMute;
                _notificationScopesLoaded = true;
            }
        }

        private async Task<int> GetScopeMuteForAsync(string scopeType)
        {
            try
            {
                var response = await SendAsync(new JObject
                {
                    ["@type"] = "getScopeNotificationSettings",
                    ["scope"] = new JObject { ["@type"] = scopeType }
                }, TimeSpan.FromSeconds(10));
                return ReadInt(response["mute_for"]);
            }
            catch
            {
                return 0;
            }
        }

        public async Task<Tuple<List<ChatViewModel>, bool>> GetDialogsPageAsync(int folderId, int offset, int limit, bool refresh)
        {
            await StartAsync();
            if (refresh) ClearDialogsCache();
            await EnsureNotificationScopesLoadedAsync();
            if (limit <= 0) limit = 50;

            var safeOffset = Math.Max(offset, 0);
            var requestLimit = Math.Max(limit, safeOffset + limit + 1);
            var ids = await GetChatIdsAsync(folderId, requestLimit);
            var initialIdsCount = ids.Count;
            var canLoadMore = true;
            var loadAttempts = 0;
            while (ids.Count < requestLimit && canLoadMore && loadAttempts < 5)
            {
                var beforeLoadCount = ids.Count;
                canLoadMore = await TryLoadMoreChatsAsync(folderId, limit);
                loadAttempts++;
                if (canLoadMore)
                {
                    ids = await GetChatIdsAsync(folderId, requestLimit);
                    if (ids.Count <= beforeLoadCount)
                    {
                        canLoadMore = false;
                        break;
                    }
                }
            }

            var all = new List<ChatViewModel>();
            for (var i = 0; i < ids.Count; i++)
            {
                var chat = await GetChatByIdAsync(ReadLong(ids[i]), folderId);
                if (chat != null) all.Add(chat);
            }

            var page = all.Skip(safeOffset).Take(limit).ToList();
            var loadedMoreIds = ids.Count > initialIdsCount;
            var hasMore = all.Count > safeOffset + page.Count || (canLoadMore && (loadedMoreIds || page.Count == limit));
            return Tuple.Create(page, hasMore);
        }

        private async Task<JArray> GetChatIdsAsync(int folderId, int limit)
        {
            var response = await SendAsync(new JObject
            {
                ["@type"] = "getChats",
                ["chat_list"] = BuildChatList(folderId),
                ["limit"] = limit <= 0 ? 50 : limit
            }, TimeSpan.FromSeconds(20));

            var ids = response["chat_ids"] as JArray;
            return ids == null ? new JArray() : ids;
        }

        private async Task<bool> TryLoadMoreChatsAsync(int folderId, int limit)
        {
            try
            {
                await SendAsync(new JObject
                {
                    ["@type"] = "loadChats",
                    ["chat_list"] = BuildChatList(folderId),
                    ["limit"] = limit <= 0 ? 20 : limit
                }, TimeSpan.FromSeconds(20));
                return true;
            }
            catch (InvalidOperationException ex)
            {
                if (IsTdNotFound(ex)) return false;
                throw;
            }
        }

        private static bool IsTdNotFound(Exception ex)
        {
            var message = ex == null ? null : ex.Message;
            if (string.IsNullOrEmpty(message)) return false;
            return message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("404", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public async Task<List<ChatViewModel>> GetNotificationDialogsAsync(int limit)
        {
            var page = await GetDialogsPageAsync(-1, 0, limit <= 0 ? 10 : limit, false);
            return FilterArchivedNotificationDialogs(page.Item1);
        }

        private static List<ChatViewModel> FilterArchivedNotificationDialogs(List<ChatViewModel> dialogs)
        {
            if (dialogs == null || dialogs.Count == 0) return dialogs;

            var result = new List<ChatViewModel>();
            for (var i = 0; i < dialogs.Count; i++)
            {
                var chat = dialogs[i];
                if (chat == null || chat.IsArchived || chat.FolderId == ArchiveFolderId) continue;
                result.Add(chat);
            }
            return result;
        }

        public async Task<ChatViewModel> GetSelfUserAsync()
        {
            await StartAsync();
            var me = await SendAsync(new JObject { ["@type"] = "getMe", ["@extra"] = NextExtra() }, TimeSpan.FromSeconds(10));
            UpdateUser(me);
            _selfUserId = ReadLong(me == null ? null : me["id"]);
            return await MapUserToChatAsync(me, true);
        }

        public async Task<ChatViewModel> GetSavedMessagesChatAsync()
        {
            var me = await GetSelfUserAsync();
            if (me == null) return null;
            me.PeerType = "self";
            me.Title = "Saved Messages";
            me.LastMessage = "Personal cloud storage";
            return me;
        }

        public async Task<UserProfileViewModel> GetUserProfileAsync(ChatViewModel peer)
        {
            await StartAsync();
            var chat = await ResolveChatAsync(peer);
            if (chat == null) return null;

            var profile = new UserProfileViewModel
            {
                Chat = chat,
                Title = chat.Title,
                Subtitle = chat.SubtitleText,
                Initials = chat.IconText,
                AvatarUri = chat.AvatarUri,
                PeerType = chat.PeerType,
                SubscriberCount = chat.SubscriberCount,
                OnlineCount = chat.OnlineCount,
                IsSelf = chat.PeerType == "self",
                IsChannel = chat.IsBroadcast || (chat.IsChannel && !chat.IsGroup),
                IsGroup = chat.IsGroup
            };

            AddProfileRow(profile, "Username", chat.Username);
            AddProfileRow(profile, "Phone", chat.Phone);
            AddProfileRow(profile, "Birthday", chat.Birthdate);
            AddProfileRow(profile, "Bio", chat.Bio);
            return profile;
        }

        public async Task<List<ProfilePhotoViewModel>> GetProfilePhotosAsync(ChatViewModel peer, int limit)
        {
            await StartAsync();
            var result = new List<ProfilePhotoViewModel>();
            var chat = await ResolveChatAsync(peer);
            await AddTdLibProfilePhotosAsync(result, peer, limit);
            if (chat != null && !string.IsNullOrEmpty(chat.AvatarUri))
                result.Add(new ProfilePhotoViewModel { PhotoId = chat.AvatarPhotoId, Uri = chat.AvatarUri });
            return result;
        }

        public async Task<List<ChatViewModel>> GetChatMembersAsync(ChatViewModel peer, int limit)
        {
            await StartAsync();
            var result = new List<ChatViewModel>();
            if (peer == null) return result;

            try
            {
                var rawChat = await GetChatRawAsync(ResolveChatId(peer));
                var type = rawChat == null ? null : rawChat["type"] as JObject;
                var typeName = ReadString(type == null ? null : type["@type"], "");
                var take = limit <= 0 ? 50 : limit;

                if (typeName == "chatTypeSupergroup")
                {
                    var supergroupId = ReadLong(type["supergroup_id"]);
                    var response = await SendAsync(new JObject
                    {
                        ["@type"] = "getSupergroupMembers",
                        ["supergroup_id"] = supergroupId,
                        ["filter"] = new JObject { ["@type"] = "supergroupMembersFilterRecent" },
                        ["offset"] = 0,
                        ["limit"] = take
                    }, TimeSpan.FromSeconds(15));
                    await AddMemberUsersAsync(result, response["members"] as JArray);
                }
                else if (typeName == "chatTypeBasicGroup")
                {
                    var basicGroupId = ReadLong(type["basic_group_id"]);
                    var full = await GetBasicGroupFullInfoAsync(basicGroupId);
                    var members = full == null ? null : full["members"] as JArray;
                    if (members != null)
                    {
                        var slice = new JArray(members.Take(take));
                        await AddMemberUsersAsync(result, slice);
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        public async Task<List<ChatViewModel>> GetContactsAsync()
        {
            await StartAsync();
            var result = new List<ChatViewModel>();
            try
            {
                var response = await SendAsync(new JObject { ["@type"] = "getContacts" }, TimeSpan.FromSeconds(15));
                var ids = response["user_ids"] as JArray;
                if (ids != null)
                {
                    foreach (var id in ids)
                    {
                        var user = await GetUserAsync(ReadLong(id));
                        var chat = await MapUserToChatAsync(user, false);
                        if (chat != null)
                        {
                            chat.IsContact = true;
                            result.Add(chat);
                        }
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        public async Task<List<ChatViewModel>> GetArchivePreviewDialogsAsync()
        {
            var page = await GetDialogsPageAsync(1, 0, 20, false);
            return page.Item1;
        }

        public async Task<List<ChatViewModel>> SearchChatsAsync(string query, int limit)
        {
            await StartAsync();
            var result = new List<ChatViewModel>();
            if (string.IsNullOrWhiteSpace(query)) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var response = await SendAsync(new JObject { ["@type"] = "searchChats", ["query"] = query, ["limit"] = limit <= 0 ? 20 : limit }, TimeSpan.FromSeconds(15));
                await AddSearchResultChatsAsync(result, seen, response["chat_ids"] as JArray, false);
            }
            catch
            {
            }

            try
            {
                var response = await SendAsync(new JObject
                {
                    ["@type"] = "searchChatsOnServer",
                    ["query"] = query,
                    ["limit"] = limit <= 0 ? 20 : limit
                }, TimeSpan.FromSeconds(15));
                await AddSearchResultChatsAsync(result, seen, response["chat_ids"] as JArray, true);
            }
            catch
            {
            }

            try
            {
                var response = await SendAsync(new JObject { ["@type"] = "searchPublicChats", ["query"] = query }, TimeSpan.FromSeconds(15));
                await AddSearchResultChatsAsync(result, seen, response["chat_ids"] as JArray, true);
            }
            catch
            {
            }

            try
            {
                await AddExactPublicChatResultAsync(result, seen, query);
            }
            catch
            {
            }
            return result;
        }

        private async Task AddExactPublicChatResultAsync(IList<ChatViewModel> result, HashSet<string> seen, string query)
        {
            if (result == null || seen == null) return;
            var username = ExtractUsername(query);
            if (string.IsNullOrEmpty(username)) username = (query ?? string.Empty).Trim().TrimStart('@');
            if (string.IsNullOrEmpty(username) || username.Length < 4 || username.IndexOf(' ') >= 0) return;

            var response = await SendAsync(new JObject { ["@type"] = "searchPublicChat", ["username"] = username }, TimeSpan.FromSeconds(15));
            var chat = MapChatForList(response);
            if (chat == null) return;

            var key = string.IsNullOrEmpty(chat.PeerKey) ? chat.PeerId.ToString() : chat.PeerKey;
            if (string.IsNullOrEmpty(key) || seen.Contains(key)) return;

            if (!ChatHasAnyPosition(chat.PeerId))
            {
                chat.IsJoined = false;
                chat.CanJoin = true;
                if (string.IsNullOrWhiteSpace(chat.LastMessage))
                    chat.LastMessage = "Global search";
            }

            seen.Add(key);
            result.Add(chat);
        }

        private async Task AddSearchResultChatsAsync(IList<ChatViewModel> result, HashSet<string> seen, JArray ids, bool markGlobal)
        {
            if (result == null || seen == null || ids == null) return;
            foreach (var id in ids)
            {
                var chat = await GetChatByIdAsync(ReadLong(id));
                if (chat == null) continue;
                var key = string.IsNullOrEmpty(chat.PeerKey) ? chat.PeerId.ToString() : chat.PeerKey;
                if (string.IsNullOrEmpty(key) || seen.Contains(key)) continue;

                if (markGlobal && !ChatHasAnyPosition(chat.PeerId))
                {
                    chat.IsJoined = false;
                    chat.CanJoin = true;
                    if (string.IsNullOrWhiteSpace(chat.LastMessage))
                        chat.LastMessage = "Global search";
                }

                seen.Add(key);
                result.Add(chat);
            }
        }

        private bool ChatHasAnyPosition(long chatId)
        {
            if (chatId == 0) return false;
            JObject chat;
            lock (_syncRoot)
            {
                if (!_chats.TryGetValue(chatId, out chat) || chat == null) return false;
            }
            var positions = chat["positions"] as JArray;
            return positions != null && positions.Count > 0;
        }

        public async Task<TelegramLinkTarget> ResolveTelegramLinkAsync(string link)
        {
            await StartAsync();
            try
            {
                var info = await SendAsync(new JObject { ["@type"] = "getMessageLinkInfo", ["url"] = link }, TimeSpan.FromSeconds(15));
                var message = info["message"] as JObject;
                var chatId = ReadLong(info["chat_id"]);
                if (chatId == 0) chatId = ReadLong(message == null ? null : message["chat_id"]);
                if (chatId != 0)
                {
                    var linkedChat = await GetChatByIdAsync(chatId, true);
                    if (linkedChat != null)
                    {
                        var tdMessageId = ReadLong(message == null ? null : message["id"]);
                        var compactMessageId = tdMessageId == 0 ? 0 : CompactMessageId(chatId, tdMessageId);
                        return new TelegramLinkTarget { Chat = linkedChat, MessageId = compactMessageId };
                    }
                }
            }
            catch
            {
            }

            var username = ExtractUsername(link);
            if (string.IsNullOrEmpty(username)) return null;

            var response = await SendAsync(new JObject { ["@type"] = "searchPublicChat", ["username"] = username }, TimeSpan.FromSeconds(15));
            var chat = await MapChatAsync(response);
            return chat == null ? null : new TelegramLinkTarget { Chat = chat, MessageId = ExtractMessageId(link) };
        }

        public async Task<ChatViewModel> GetFullPeerInfoAsync(ChatViewModel peer)
        {
            return await ResolveChatAsync(peer);
        }

        public async Task<List<ChatMessageViewModel>> GetHistoryAsync(ChatViewModel peer, int limit)
        {
            return await GetHistoryCoreAsync(peer, 0, limit, false);
        }

        public async Task<List<ChatMessageViewModel>> GetHistoryBeforeAsync(ChatViewModel peer, int beforeMessageId, int limit)
        {
            var beforeSortId = beforeMessageId > 0 ? ResolveMessageId(peer, beforeMessageId) : 0;
            var messages = await GetHistoryCoreAsync(peer, beforeMessageId, limit <= 0 ? limit : limit + 1, false, 0);
            if (beforeMessageId <= 0 || messages == null) return messages;

            var result = new List<ChatMessageViewModel>();
            for (var i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                var messageSortId = message == null ? 0 : message.SortId != 0 ? message.SortId : message.Id;
                if (message == null || (beforeSortId != 0 && messageSortId >= beforeSortId)) continue;
                result.Add(message);
            }
            while (limit > 0 && result.Count > limit)
                result.RemoveAt(0);
            return result;
        }

        public async Task<List<ChatMessageViewModel>> GetHistoryAroundAsync(ChatViewModel peer, int messageId, int limit)
        {
            if (limit <= 0) limit = 50;
            return await GetHistoryCoreAsync(peer, messageId, limit, false, -Math.Max(1, limit / 2));
        }

        public async Task<List<ChatMessageViewModel>> GetHistoryForwardAsync(ChatViewModel peer, int fromMessageId, int limit)
        {
            if (limit <= 1) limit = 100;
            if (limit > 100) limit = 100;

            // getChatHistory returns newer messages when offset is negative.
            // Keep the anchor in the result and request up to 99 messages
            // newer than it, then the caller advances to the largest id.
            var newerCount = Math.Min(99, limit - 1);
            var anchorSortId = ResolveMessageId(peer, fromMessageId);
            var messages = await GetHistoryCoreAsync(peer, fromMessageId, limit, false, -newerCount);
            if (anchorSortId == 0 || messages == null) return messages;

            var result = new List<ChatMessageViewModel>();
            for (var i = 0; i < messages.Count; i++)
            {
                var message = messages[i];
                if (message == null) continue;
                var sortId = message.SortId != 0 ? message.SortId : message.Id;
                if (sortId > anchorSortId)
                    result.Add(message);
            }
            return result;
        }

        public async Task<List<ChatMessageViewModel>> GetHistorySinceAsync(ChatViewModel peer, int minMessageId, int limit)
        {
            return await GetHistoryCoreAsync(peer, 0, limit, true);
        }

        public bool ConsumeReplyMarkupReset(ChatViewModel peer)
        {
            if (peer == null) return false;
            var chatId = ResolveChatId(peer);
            if (chatId == 0) return false;
            lock (_syncRoot) return _pendingReplyMarkupResetChatIds.Remove(chatId);
        }

        public async Task<List<ChatMessageViewModel>> TakeMessageUpdatesAsync(ChatViewModel peer)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0) return new List<ChatMessageViewModel>();
            SyncPeerReadOutboxFromCache(peer, chatId);

            var updates = new List<JObject>();
            var refreshIds = new List<long>();
            lock (_syncRoot)
            {
                for (var i = _pendingMessageUpdates.Count - 1; i >= 0; i--)
                {
                    var message = _pendingMessageUpdates[i];
                    if (message == null || ReadLong(message["chat_id"]) != chatId) continue;
                    if (!MessageBelongsToPeer(peer, message)) continue;
                    updates.Add(message);
                    _pendingMessageUpdates.RemoveAt(i);
                }

                HashSet<long> queuedRefreshIds;
                if (_pendingMessageRefreshIds.TryGetValue(chatId, out queuedRefreshIds) && queuedRefreshIds != null)
                {
                    foreach (var id in queuedRefreshIds)
                        refreshIds.Add(id);
                    for (var i = 0; i < refreshIds.Count; i++) queuedRefreshIds.Remove(refreshIds[i]);
                    if (queuedRefreshIds.Count == 0) _pendingMessageRefreshIds.Remove(chatId);
                }
            }

            var result = new List<ChatMessageViewModel>();
            var includedTdIds = new HashSet<long>();
            for (var i = 0; i < updates.Count; i++)
            {
                var mapped = MapMessage(peer, updates[i]);
                if (mapped != null) result.Add(mapped);
                var tdId = ReadLong(updates[i] == null ? null : updates[i]["id"]);
                if (tdId != 0) includedTdIds.Add(tdId);
            }

            // Content/interaction updates only contain ids. Refresh only those messages instead
            // of polling an entire history page. This keeps reaction/edit updates cheap.
            for (var i = 0; i < refreshIds.Count; i++)
            {
                var tdMessageId = refreshIds[i];
                if (tdMessageId == 0 || includedTdIds.Contains(tdMessageId)) continue;
                try
                {
                    var response = await SendAsync(new JObject
                    {
                        ["@type"] = "getMessage",
                        ["chat_id"] = chatId,
                        ["message_id"] = tdMessageId
                    }, TimeSpan.FromSeconds(10));
                    if (!MessageBelongsToPeer(peer, response)) continue;
                    var mapped = MapMessage(peer, response);
                    if (mapped != null) result.Add(mapped);
                }
                catch
                {
                    // A message may have been deleted between the update and refresh.
                }
            }

            result.Sort(CompareMessagesBySortIdAscending);
            return result;
        }

        public async Task<List<int>> TakeDeletedMessageIdsAsync(ChatViewModel peer)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0) return new List<int>();

            lock (_syncRoot)
            {
                List<int> ids;
                if (!_pendingDeletedMessageIds.TryGetValue(chatId, out ids) || ids == null || ids.Count == 0)
                    return new List<int>();
                _pendingDeletedMessageIds.Remove(chatId);
                return new List<int>(ids);
            }
        }

        public async Task<List<ChatMessageViewModel>> GetMessagesByIdAsync(ChatViewModel peer, int messageId)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            var tdMessageId = ResolveMessageId(peer, messageId);
            if (chatId == 0 || tdMessageId == 0) return new List<ChatMessageViewModel>();

            var response = await SendAsync(new JObject
            {
                ["@type"] = "getMessage",
                ["chat_id"] = chatId,
                ["message_id"] = tdMessageId
            }, TimeSpan.FromSeconds(15));
            return new List<ChatMessageViewModel> { MapMessage(peer, response) };
        }

        public async Task<ChatMessageViewModel> GetChatReplyMarkupMessageAsync(ChatViewModel peer)
        {
            if (peer == null) return null;
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0) return null;

            long messageId = 0;
            JObject cachedChat;
            lock (_syncRoot)
            {
                if (_chats.TryGetValue(chatId, out cachedChat) && cachedChat != null)
                    messageId = ReadLong(cachedChat["reply_markup_message_id"]);
            }
            if (messageId == 0) messageId = peer.ReplyMarkupMessageId;
            peer.ReplyMarkupMessageId = messageId;
            if (messageId == 0) return null;

            try
            {
                var response = await SendAsync(new JObject
                {
                    ["@type"] = "getMessage",
                    ["chat_id"] = chatId,
                    ["message_id"] = messageId
                }, TimeSpan.FromSeconds(12));
                return MapMessage(peer, response);
            }
            catch { return null; }
        }

        public async Task SendBotStartMessageAsync(ChatViewModel peer, string parameter)
        {
            if (peer == null || !peer.IsBot) return;
            await StartAsync();
            var chatId = ResolveChatId(peer);
            var botUserId = peer.BotUserId;
            if (chatId == 0 || botUserId == 0)
                throw new InvalidOperationException("Bot chat is not resolved.");
            await SendAsync(new JObject
            {
                ["@type"] = "sendBotStartMessage",
                ["bot_user_id"] = botUserId,
                ["chat_id"] = chatId,
                ["parameter"] = parameter ?? string.Empty
            }, TimeSpan.FromSeconds(20));
        }

        public async Task<List<ChatMessageViewModel>> GetPinnedMessagesAsync(ChatViewModel peer, int limit)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0) return new List<ChatMessageViewModel>();
            if (limit <= 0) limit = 20;

            try
            {
                var response = await SendSearchPinnedMessagesAsync(peer, chatId, limit, true);
                return MapPinnedSearchResult(peer, response);
            }
            catch
            {
                try
                {
                    var response = await SendSearchPinnedMessagesAsync(peer, chatId, limit, false);
                    return MapPinnedSearchResult(peer, response);
                }
                catch
                {
                    return new List<ChatMessageViewModel>();
                }
            }
        }

        private Task<JObject> SendSearchPinnedMessagesAsync(ChatViewModel peer, long chatId, int limit, bool includeThread)
        {
            var request = new JObject
            {
                ["@type"] = "searchChatMessages",
                ["chat_id"] = chatId,
                ["query"] = "",
                ["sender_id"] = null,
                ["from_message_id"] = 0,
                ["offset"] = 0,
                ["limit"] = limit,
                ["filter"] = new JObject { ["@type"] = "searchMessagesFilterPinned" }
            };

            if (includeThread && IsThreadPeer(peer))
                request["message_thread_id"] = ResolveThreadMessageId(peer);

            return SendAsync(request, TimeSpan.FromSeconds(20));
        }

        private List<ChatMessageViewModel> MapPinnedSearchResult(ChatViewModel peer, JObject response)
        {
            var result = new List<ChatMessageViewModel>();
            var messages = response == null ? null : response["messages"] as JArray;
            if (messages != null)
            {
                foreach (var token in messages.OfType<JObject>())
                {
                    var mapped = MapMessage(peer, token);
                    if (mapped != null) result.Add(mapped);
                }
            }
            result.Sort(CompareMessagesBySortIdDescending);
            ApplyPinnedMessagesToChat(peer, result);
            return result;
        }

        private void ApplyPinnedMessagesToChat(ChatViewModel peer, IList<ChatMessageViewModel> messages)
        {
            if (peer == null || messages == null) return;
            var ids = new List<int>();
            for (var i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];
                if (msg == null || msg.Id <= 0 || ids.Contains(msg.Id)) continue;
                ids.Add(msg.Id);
            }
            if (ids.Count == 0) return;
            peer.PinnedMessageIds = ids;
            if (peer.PinnedMessageId <= 0 || !ids.Contains(peer.PinnedMessageId))
                peer.PinnedMessageId = ids[0];
            peer.CurrentPinnedMessageIndex = Math.Max(0, ids.IndexOf(peer.PinnedMessageId));
        }

        public async Task<Dictionary<int, List<MessageReactionViewModel>>> GetMessageReactionsAsync(ChatViewModel peer, IList<int> messageIds)
        {
            await StartAsync();
            var result = new Dictionary<int, List<MessageReactionViewModel>>();
            var chatId = ResolveChatId(peer);
            if (chatId == 0 || messageIds == null || messageIds.Count == 0) return result;

            for (var i = 0; i < messageIds.Count; i++)
            {
                var compactId = messageIds[i];
                var tdMessageId = ResolveMessageId(peer, compactId);
                if (tdMessageId == 0) continue;
                try
                {
                    var response = await SendAsync(new JObject
                    {
                        ["@type"] = "getMessage",
                        ["chat_id"] = chatId,
                        ["message_id"] = tdMessageId
                    }, TimeSpan.FromSeconds(10));
                    var reactions = ReadMessageReactions(response["interaction_info"] as JObject);
                    result[compactId] = reactions;
                }
                catch
                {
                }
            }
            return result;
        }

        public async Task<List<CommentAvatarViewModel>> GetMessageViewersAsync(ChatViewModel peer, int messageId, int limit)
        {
            await StartAsync();
            var result = new List<CommentAvatarViewModel>();
            if (peer == null || messageId <= 0) return result;
            if (!IsGroupLikeMessagePeer(peer) && !peer.IsBroadcast) return result;

            var chatId = ResolveChatId(peer);
            var tdMessageId = ResolveMessageId(peer, messageId);
            if (chatId == 0 || tdMessageId == 0) return result;

            JObject response;
            try
            {
                response = await SendAsync(new JObject
                {
                    ["@type"] = "getMessageViewers",
                    ["chat_id"] = chatId,
                    ["message_id"] = tdMessageId
                }, TimeSpan.FromSeconds(10));
            }
            catch (InvalidOperationException ex)
            {
                if (IsMessageViewersUnavailableError(ex))
                    peer.MessageViewersUnavailable = true;
                return result;
            }
            catch
            {
                return result;
            }

            var max = limit <= 0 ? 20 : Math.Min(limit, 50);
            var viewers = response["viewers"] as JArray;
            if (viewers == null) return result;

            foreach (var token in viewers.OfType<JObject>())
            {
                if (result.Count >= max) break;
                var userId = ReadLong(token["user_id"]);
                if (userId == 0) continue;

                JObject user = null;
                try
                {
                    user = await GetUserAsync(userId);
                }
                catch
                {
                }

                var mapped = MapMessageViewer(userId, user);
                if (mapped != null)
                    result.Add(mapped);
            }

            return result;
        }

        private static bool IsMessageViewersUnavailableError(Exception ex)
        {
            var message = ex == null ? null : ex.Message;
            if (string.IsNullOrWhiteSpace(message)) return false;
            return message.IndexOf("Chat is too big", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("message viewers", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public async Task<List<ChatViewModel>> GetForumTopicsAsync(ChatViewModel peer, int limit)
        {
            await StartAsync();
            var result = new List<ChatViewModel>();
            if (peer == null || !peer.IsForum) return result;

            var chatId = ResolveChatId(peer);
            if (chatId == 0) return result;
            var requestedLimit = limit <= 0 ? 100 : limit;
            var remaining = Math.Max(1, requestedLimit);
            var offsetDate = 0;
            var offsetMessageId = 0L;
            var offsetTopicId = 0;

            try
            {
                while (remaining > 0)
                {
                    var pageLimit = Math.Min(100, remaining);
                    var response = await SendAsync(new JObject
                    {
                        ["@type"] = "getForumTopics",
                        ["chat_id"] = chatId,
                        ["query"] = "",
                        ["offset_date"] = offsetDate,
                        ["offset_message_id"] = offsetMessageId,
                        ["offset_forum_topic_id"] = offsetTopicId,
                        ["limit"] = pageLimit
                    }, TimeSpan.FromSeconds(15));
                    var topics = response["topics"] as JArray;
                    var added = 0;
                    if (topics != null)
                    {
                        foreach (var topic in topics.OfType<JObject>())
                        {
                            var mapped = MapTopic(peer, topic);
                            if (mapped != null && mapped.TopicIconEmojiId != 0)
                            {
                                mapped.TopicIconUri = GetCachedCustomEmojiStickerUri(mapped.TopicIconEmojiId);
                                if (string.IsNullOrEmpty(mapped.TopicIconUri))
                                {
                                    var ignored = LoadCustomEmojiStickerUriIntoTopicAsync(mapped);
                                }
                            }
                            result.Add(mapped);
                            added++;
                        }
                    }

                    remaining -= added;
                    offsetDate = ReadInt(response["next_offset_date"]);
                    offsetMessageId = ReadLong(response["next_offset_message_id"]);
                    offsetTopicId = ReadInt(response["next_offset_forum_topic_id"]);
                    if (added == 0 || offsetDate == 0 || offsetTopicId == 0) break;
                }
            }
            catch
            {
            }
            return result;
        }

        public async Task<ChatViewModel> ResolveDiscussionChatAsync(ChatViewModel sourcePeer, ChatMessageViewModel sourceMessage)
        {
            await StartAsync();
            if (sourcePeer == null || sourceMessage == null || sourceMessage.Id <= 0) return null;
            var chatId = ResolveChatId(sourcePeer);
            var tdMessageId = ResolveMessageId(sourcePeer, sourceMessage.Id);
            if (chatId == 0 || tdMessageId == 0) return null;

            JObject threadInfo = null;
            try
            {
                threadInfo = await SendAsync(new JObject
                {
                    ["@type"] = "getMessageThread",
                    ["chat_id"] = chatId,
                    ["message_id"] = tdMessageId
                }, TimeSpan.FromSeconds(15));
            }
            catch
            {
            }

            var threadChatId = ReadLong(threadInfo == null ? null : threadInfo["chat_id"]);
            if (threadChatId == 0) threadChatId = chatId;
            var threadMessageId = ReadLong(threadInfo == null ? null : threadInfo["message_thread_id"]);
            if (threadMessageId == 0) threadMessageId = tdMessageId;
            var compactThreadMessageId = CompactMessageId(threadChatId, threadMessageId);
            var threadTitle = string.IsNullOrEmpty(sourcePeer.Title) ? "Comments" : sourcePeer.Title;
            try
            {
                var threadChat = await GetChatRawAsync(threadChatId);
                if (threadChat != null)
                    threadTitle = ReadString(threadChat["title"], threadTitle);
            }
            catch
            {
            }

            return new ChatViewModel
            {
                PeerId = threadChatId,
                PeerType = "channel",
                PeerKey = "channel:" + threadChatId.ToString(),
                AccessHash = sourcePeer.ParentAccessHash != 0 ? sourcePeer.ParentAccessHash : sourcePeer.AccessHash,
                Title = "Comments",
                LastMessage = sourceMessage.Text,
                LastMessageSenderName = sourceMessage.SenderName,
                LastMessageDate = sourceMessage.Date,
                TopMessageId = compactThreadMessageId,
                IsGroup = true,
                IsChannel = true,
                IsBroadcast = false,
                IsForumTopic = true,
                IsCommentsThread = true,
                TopicId = compactThreadMessageId,
                TopicRootMessageId = compactThreadMessageId,
                ParentPeerType = sourcePeer.PeerType,
                ParentPeerId = threadChatId,
                ParentPeerKey = sourcePeer.PeerKey,
                ParentAccessHash = sourcePeer.AccessHash,
                ParentTitle = threadTitle,
                CanSendMessages = sourceMessage.CommentsDiscussionCanSend || sourcePeer.CanSendMessages,
                CanPinMessages = false,
                CanDeleteMessages = sourcePeer.CanDeleteMessages,
                NoForwards = sourcePeer.NoForwards,
                IconText = "C",
                AvatarUri = sourcePeer.AvatarUri,
                AvatarIsPreview = sourcePeer.AvatarIsPreview,
                AvatarPhotoId = sourcePeer.AvatarPhotoId,
                AvatarDcId = sourcePeer.AvatarDcId,
                AvatarStrippedThumb = sourcePeer.AvatarStrippedThumb
            };
        }

        public async Task DownloadMessageMediaAsync(ChatMessageViewModel message)
        {
            await DownloadMessageMediaAsync(null, message, (Action<long, long>)null);
        }

        public async Task DownloadMessageMediaAsync(ChatViewModel peer, ChatMessageViewModel message)
        {
            await DownloadMessageMediaAsync(peer, message, (Action<long, long>)null);
        }

        public async Task DownloadMessageMediaAsync(ChatViewModel peer, ChatMessageViewModel message, Action<long> progress)
        {
            await DownloadMessageMediaAsync(peer, message, progress == null ? (Action<long, long>)null : delegate(long done, long total) { progress(done); });
        }

        public async Task DownloadMessageMediaAsync(ChatViewModel peer, ChatMessageViewModel message, Action<long, long> progress)
        {
            if (message == null || message.MediaId == 0) return;
            RunOnUiThread(delegate { message.IsMediaDownloading = true; });
            RegisterMessageDownloadTarget(message.MediaId, message);
            try
            {
                var file = await DownloadFileAsync(message.MediaId, delegate(long done, long total)
                {
                    RunOnUiThread(delegate
                    {
                        message.MediaDownloadBytes = done;
                        message.MediaDownloadTotalBytes = total;
                        if (progress != null) progress(done, total);
                    });
                });
                RunOnUiThread(delegate { ApplyMessageDownloadFile(message, file); });
            }
            finally
            {
                RunOnUiThread(delegate { message.IsMediaDownloading = false; });
                UnregisterMessageDownloadTarget(message.MediaId, message);
            }
        }

        public async Task DownloadMessageMediaAsync(ChatMediaItemViewModel item)
        {
            await DownloadMessageMediaAsync(item, null);
        }

        public async Task DownloadMessageMediaAsync(ChatMediaItemViewModel item, Action<long, long> progress)
        {
            if (item == null || item.MediaId == 0) return;
            RunOnUiThread(delegate { item.IsMediaDownloading = true; });
            RegisterMediaItemDownloadTarget(item.MediaId, item);
            try
            {
                var file = await DownloadFileAsync(item.MediaId, delegate(long done, long total)
                {
                    RunOnUiThread(delegate
                    {
                        item.MediaDownloadBytes = done;
                        item.MediaDownloadTotalBytes = total;
                        if (progress != null) progress(done, total);
                    });
                });
                RunOnUiThread(delegate { ApplyMediaItemDownloadFile(item, file); });
            }
            finally
            {
                RunOnUiThread(delegate { item.IsMediaDownloading = false; });
                UnregisterMediaItemDownloadTarget(item.MediaId, item);
            }
        }

        public async Task<StorageFile> DownloadOriginalPhotoAsync(ChatViewModel peer, ChatMessageViewModel message)
        {
            if (message != null && message.MediaFullId != 0)
                return await DownloadStorageFileByIdAsync(message.MediaFullId);
            await DownloadMessageMediaAsync(peer, message);
            return await TryGetStorageFileAsync(message == null ? null : message.MediaFileUri);
        }

        public async Task<StorageFile> DownloadOriginalPhotoAsync(ChatMediaItemViewModel item)
        {
            if (item != null && item.MediaFullId != 0)
                return await DownloadStorageFileByIdAsync(item.MediaFullId);
            await DownloadMessageMediaAsync(item);
            return await TryGetStorageFileAsync(item == null ? null : item.MediaFileUri);
        }

        public async Task DownloadMessageVideoForPlaybackAsync(ChatViewModel peer, ChatMessageViewModel message, Action<string> ready, Action<long, long> progress)
        {
            await DownloadMessageMediaAsync(peer, message, progress);
            if (ready != null && message != null && !string.IsNullOrEmpty(message.MediaFileUri)) ready(message.MediaFileUri);
        }

        public async Task DownloadMessageVideoPreviewAsync(ChatViewModel peer, ChatMessageViewModel message)
        {
            if (message == null || message.MediaPreviewId == 0 || !string.IsNullOrEmpty(message.MediaPreviewUri)) return;
            RegisterMessagePreviewTarget(message.MediaPreviewId, message);
            var file = await DownloadFileAsync(message.MediaPreviewId, null);
            if (!IsDownloadCompleted(file)) return;
            var path = ReadFilePath(file);
            if (!string.IsNullOrEmpty(path)) message.MediaPreviewUri = ToImageFileUri(path);
        }

        public async Task DownloadMessageVideoPreviewAsync(ChatMediaItemViewModel item)
        {
            if (item == null || item.MediaPreviewId == 0 || !string.IsNullOrEmpty(item.MediaPreviewUri)) return;
            RegisterMediaItemPreviewTarget(item.MediaPreviewId, item);
            var file = await DownloadFileAsync(item.MediaPreviewId, null);
            if (!IsDownloadCompleted(file)) return;
            var path = ReadFilePath(file);
            if (!string.IsNullOrEmpty(path)) item.MediaPreviewUri = ToImageFileUri(path);
        }

        public async Task DownloadMessageVideoForPlaybackAsync(ChatMediaItemViewModel item, Action<string> ready, Action<long, long> progress)
        {
            await DownloadMessageMediaAsync(item, progress);
            if (ready != null && item != null && !string.IsNullOrEmpty(item.MediaFileUri)) ready(item.MediaFileUri);
        }

        public async Task<BotCallbackAnswerViewModel> AnswerBotCallbackAsync(ChatViewModel peer, BotKeyboardButtonViewModel button)
        {
            if (peer == null || button == null) return null;
            JObject payload;
            if (button.Type == "inlineKeyboardButtonTypeCallbackGame")
                payload = new JObject { ["@type"] = "callbackQueryPayloadGame", ["game_short_name"] = button.Data ?? string.Empty };
            else
                payload = new JObject { ["@type"] = "callbackQueryPayloadData", ["data"] = button.Data ?? string.Empty };

            var response = await SendAsync(new JObject
            {
                ["@type"] = "getCallbackQueryAnswer",
                ["chat_id"] = ResolveChatId(peer),
                ["message_id"] = ResolveMessageId(peer, button.MessageId),
                ["payload"] = payload
            }, TimeSpan.FromSeconds(20));
            return new BotCallbackAnswerViewModel
            {
                Text = ReadString(response == null ? null : response["text"], ""),
                ShowAlert = ReadBool(response == null ? null : response["show_alert"]),
                Url = ReadString(response == null ? null : response["url"], "")
            };
        }

        public async Task SendOwnContactAsync(ChatViewModel peer)
        {
            if (peer == null) return;
            await StartAsync();
            var me = await SendAsync(new JObject { ["@type"] = "getMe" }, TimeSpan.FromSeconds(10));
            if (me == null) throw new InvalidOperationException("Could not read your Telegram profile.");
            var phone = ReadString(me["phone_number"], "");
            if (string.IsNullOrWhiteSpace(phone)) throw new InvalidOperationException("Your Telegram account has no phone number available to share.");
            var contact = new JObject
            {
                ["@type"] = "contact",
                ["phone_number"] = phone,
                ["first_name"] = ReadString(me["first_name"], "Telegram"),
                ["last_name"] = ReadString(me["last_name"], ""),
                ["vcard"] = "",
                ["user_id"] = ReadLong(me["id"])
            };
            await SendAsync(new JObject
            {
                ["@type"] = "sendMessage",
                ["chat_id"] = ResolveChatId(peer),
                ["input_message_content"] = new JObject { ["@type"] = "inputMessageContact", ["contact"] = contact }
            }, TimeSpan.FromSeconds(20));
        }

        public async Task SendLocationAsync(ChatViewModel peer, double latitude, double longitude, double horizontalAccuracy)
        {
            if (peer == null) return;
            await StartAsync();
            var request = new JObject
            {
                ["@type"] = "sendMessage",
                ["chat_id"] = ResolveChatId(peer),
                ["input_message_content"] = new JObject
                {
                    ["@type"] = "inputMessageLocation",
                    ["location"] = new JObject
                    {
                        ["@type"] = "location",
                        ["latitude"] = latitude,
                        ["longitude"] = longitude,
                        ["horizontal_accuracy"] = horizontalAccuracy
                    },
                    ["live_period"] = 0,
                    ["heading"] = 0,
                    ["proximity_alert_radius"] = 0
                }
            };
            var topic = BuildMessageTopic(peer);
            if (topic != null) request["topic_id"] = topic;
            await SendAsync(request, TimeSpan.FromSeconds(20));
        }

        public async Task SendTextMessageAsync(ChatViewModel peer, string text)
        {
            await SendTextMessageAsync(peer, text, 0);
        }

        public async Task SendTextMessageAsync(ChatViewModel peer, string text, int replyToMessageId)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0 || string.IsNullOrWhiteSpace(text)) return;

            var request = new JObject
            {
                ["@type"] = "sendMessage",
                ["chat_id"] = chatId,
                ["input_message_content"] = new JObject
                {
                    ["@type"] = "inputMessageText",
                    ["text"] = new JObject { ["@type"] = "formattedText", ["text"] = text },
                    ["clear_draft"] = true
                }
            };
            var topic = BuildMessageTopic(peer);
            if (topic != null) request["topic_id"] = topic;
            if (replyToMessageId > 0)
                request["reply_to"] = new JObject { ["@type"] = "inputMessageReplyToMessage", ["message_id"] = ResolveMessageId(peer, replyToMessageId) };
            await SendAsync(request, TimeSpan.FromSeconds(20));
        }

        public async Task SendMediaMessageAsync(ChatViewModel peer, StorageFile file, string kind, string caption)
        {
            await SendMediaMessageAsync(peer, file, kind, caption, 0, 0);
        }

        public async Task SendMediaMessageAsync(ChatViewModel peer, StorageFile file, string kind, string caption, int replyToMessageId)
        {
            await SendMediaMessageAsync(peer, file, kind, caption, replyToMessageId, 0);
        }

        public async Task SendMediaMessageAsync(ChatViewModel peer, StorageFile file, string kind, string caption, int replyToMessageId, int duration)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0 || file == null) return;

            var content = await BuildInputMessageContentAsync(file, kind, caption, duration);
            if (content == null) return;

            var request = new JObject { ["@type"] = "sendMessage", ["chat_id"] = chatId, ["input_message_content"] = content };
            var topic = BuildMessageTopic(peer);
            if (topic != null) request["topic_id"] = topic;
            if (replyToMessageId > 0)
                request["reply_to"] = new JObject { ["@type"] = "inputMessageReplyToMessage", ["message_id"] = ResolveMessageId(peer, replyToMessageId) };
            await SendAsync(request, TimeSpan.FromSeconds(30));
        }

        public async Task SendMediaAlbumMessageAsync(ChatViewModel peer, IList<StorageFile> files, IList<string> kinds, string caption, int replyToMessageId)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0 || files == null || files.Count == 0) return;

            var contents = new JArray();
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var kind = kinds != null && i < kinds.Count ? kinds[i] : null;
                var itemCaption = i == 0 ? caption : string.Empty;
                var content = await BuildInputMessageContentAsync(file, kind, itemCaption, 0);
                if (content == null) continue;

                var type = ReadString(content["@type"], "");
                if (type != "inputMessagePhoto" && type != "inputMessageVideo")
                {
                    await SendMediaMessageAsync(peer, file, kind, itemCaption, replyToMessageId, 0);
                    continue;
                }

                contents.Add(content);
                if (contents.Count >= 10) break;
            }

            if (contents.Count == 0) return;
            if (contents.Count == 1)
            {
                var requestSingle = new JObject { ["@type"] = "sendMessage", ["chat_id"] = chatId, ["input_message_content"] = contents[0] };
                var topicSingle = BuildMessageTopic(peer);
                if (topicSingle != null) requestSingle["topic_id"] = topicSingle;
                if (replyToMessageId > 0)
                    requestSingle["reply_to"] = new JObject { ["@type"] = "inputMessageReplyToMessage", ["message_id"] = ResolveMessageId(peer, replyToMessageId) };
                await SendAsync(requestSingle, TimeSpan.FromSeconds(30));
                return;
            }

            var request = new JObject
            {
                ["@type"] = "sendMessageAlbum",
                ["chat_id"] = chatId,
                ["input_message_contents"] = contents
            };
            var topic = BuildMessageTopic(peer);
            if (topic != null) request["topic_id"] = topic;
            if (replyToMessageId > 0)
                request["reply_to"] = new JObject { ["@type"] = "inputMessageReplyToMessage", ["message_id"] = ResolveMessageId(peer, replyToMessageId) };
            try
            {
                await SendAsync(request, TimeSpan.FromSeconds(45));
            }
            catch
            {
                for (var i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    var kind = kinds != null && i < kinds.Count ? kinds[i] : null;
                    var itemCaption = i == 0 ? caption : string.Empty;
                    await SendMediaMessageAsync(peer, file, kind, itemCaption, replyToMessageId, 0);
                }
            }
        }

        private async Task<JObject> BuildInputMessageContentAsync(StorageFile sourceFile, string requestedKind, string caption, int duration)
        {
            if (sourceFile == null) return null;
            var file = await PrepareLocalSendFileAsync(sourceFile);
            if (file == null || string.IsNullOrEmpty(file.Path)) return null;

            var kind = ResolveSendMediaKind(file, requestedKind);
            var inputFile = new JObject { ["@type"] = "inputFileLocal", ["path"] = file.Path.Replace("\\", "/") };
            var content = string.Equals(kind, "roundvideo", StringComparison.OrdinalIgnoreCase)
                ? new JObject()
                : new JObject { ["caption"] = new JObject { ["@type"] = "formattedText", ["text"] = caption ?? "" } };

            if (kind == "photo")
            {
                content["@type"] = "inputMessagePhoto";
                content["photo"] = inputFile;
                await TryFillPhotoDimensionsAsync(content, file);
            }
            else if (kind == "video")
            {
                content["@type"] = "inputMessageVideo";
                content["video"] = inputFile;
                content["duration"] = duration;
                await TryFillVideoMetadataAsync(content, file);
            }
            else if (kind == "roundvideo")
            {
                content["@type"] = "inputMessageVideoNote";
                content["video_note"] = inputFile;
                content["duration"] = duration;
                content["length"] = 480;
                await TryFillVideoNoteMetadataAsync(content, file);
            }
            else if (kind == "gif")
            {
                content["@type"] = "inputMessageAnimation";
                content["animation"] = inputFile;
            }
            else if (kind == "voice")
            {
                content["@type"] = "inputMessageVoiceNote";
                content["voice_note"] = inputFile;
                content["duration"] = duration;
                content["waveform"] = "";
            }
            else if (kind == "audio")
            {
                content["@type"] = "inputMessageAudio";
                content["audio"] = inputFile;
                await TryFillAudioMetadataAsync(content, sourceFile, duration);
            }
            else
            {
                content["@type"] = "inputMessageDocument";
                content["document"] = inputFile;
            }

            return content;
        }

        private async Task<StorageFile> PrepareLocalSendFileAsync(StorageFile sourceFile)
        {
            if (sourceFile == null) return null;
            if (IsApplicationLocalFile(sourceFile))
            {
                await WaitForSendFileReadyAsync(sourceFile);
                return sourceFile;
            }

            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("td_send", CreationCollisionOption.OpenIfExists);
            var name = DateTime.UtcNow.Ticks.ToString() + "_" + SanitizeSendFileName(sourceFile.Name);
            var copied = await sourceFile.CopyAsync(folder, name, NameCollisionOption.GenerateUniqueName);
            await WaitForSendFileReadyAsync(copied);
            return copied;
        }

        private static bool IsApplicationLocalFile(StorageFile file)
        {
            var path = file == null ? null : file.Path;
            if (string.IsNullOrEmpty(path)) return false;
            var localPath = ApplicationData.Current.LocalFolder.Path;
            return !string.IsNullOrEmpty(localPath) && path.StartsWith(localPath, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task WaitForSendFileReadyAsync(StorageFile file)
        {
            if (file == null) return;
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    if (!string.IsNullOrEmpty(file.Path))
                        await StorageFile.GetFileFromPathAsync(file.Path);
                    var props = await file.GetBasicPropertiesAsync();
                    if (props != null && props.Size > 0) return;
                }
                catch
                {
                }

                await Task.Delay(100);
            }
        }

        private static string SanitizeSendFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "file";
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(name.Length);
            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                builder.Append(invalid.Contains(ch) ? '_' : ch);
            }
            var result = builder.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "file" : result;
        }

        private static string ResolveSendMediaKind(StorageFile file, string requestedKind)
        {
            var ext = file == null || file.FileType == null ? string.Empty : file.FileType.ToLowerInvariant();
            if (string.Equals(requestedKind, "roundvideo", StringComparison.OrdinalIgnoreCase))
                return "roundvideo";
            if (string.Equals(requestedKind, "voice", StringComparison.OrdinalIgnoreCase))
                return "voice";
            if (ext == ".gif") return "gif";
            if (IsImageExtension(ext)) return "photo";
            if (IsVideoExtension(ext)) return "video";
            if (IsAudioExtension(ext)) return "audio";
            if (!string.IsNullOrEmpty(requestedKind)) return requestedKind.ToLowerInvariant();
            return "document";
        }

        private static bool IsImageExtension(string ext)
        {
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp";
        }

        private static bool IsVideoExtension(string ext)
        {
            return ext == ".mp4" || ext == ".mov" || ext == ".m4v" || ext == ".webm";
        }

        private static bool IsAudioExtension(string ext)
        {
            return ext == ".mp3" || ext == ".m4a" || ext == ".ogg" || ext == ".oga" || ext == ".opus" || ext == ".wav";
        }

        private static bool IsTelegramVoiceNoteExtension(string ext)
        {
            return ext == ".ogg" || ext == ".oga" || ext == ".opus";
        }

        private static async Task TryFillPhotoDimensionsAsync(JObject content, StorageFile file)
        {
            try
            {
                var props = await file.Properties.GetImagePropertiesAsync();
                if (props == null) return;
                content["width"] = (int)props.Width;
                content["height"] = (int)props.Height;
            }
            catch { }
        }

        private static async Task TryFillVideoMetadataAsync(JObject content, StorageFile file)
        {
            try
            {
                var props = await file.Properties.GetVideoPropertiesAsync();
                if (props == null) return;
                if (props.Width > 0) content["width"] = (int)props.Width;
                if (props.Height > 0) content["height"] = (int)props.Height;
                if (props.Duration.TotalSeconds > 0) content["duration"] = Math.Max(1, (int)Math.Round(props.Duration.TotalSeconds));
            }
            catch { }
        }

        private static async Task TryFillVideoNoteMetadataAsync(JObject content, StorageFile file)
        {
            try
            {
                var props = await file.Properties.GetVideoPropertiesAsync();
                if (props == null) return;
                if (props.Duration.TotalSeconds > 0) content["duration"] = Math.Max(1, (int)Math.Round(props.Duration.TotalSeconds));
                var side = Math.Min(props.Width, props.Height);
                if (side > 0) content["length"] = (int)side;
            }
            catch { }
        }

        private static async Task TryFillAudioMetadataAsync(JObject content, StorageFile file, int duration)
        {
            if (content == null || file == null) return;

            if (duration > 0) content["duration"] = duration;
            var title = "";
            var performer = "";
            try
            {
                var props = await file.Properties.GetMusicPropertiesAsync();
                if (props != null)
                {
                    title = props.Title ?? "";
                    performer = props.Artist ?? "";
                    if (duration <= 0 && props.Duration.TotalSeconds > 0)
                        content["duration"] = Math.Max(1, (int)Math.Round(props.Duration.TotalSeconds));
                }
            }
            catch
            {
            }

            var fallbackTitle = FileNameWithoutExtension(file.Name);
            if (string.IsNullOrWhiteSpace(title))
            {
                var parsed = ParseAudioFileName(fallbackTitle);
                if (parsed != null)
                {
                    if (string.IsNullOrWhiteSpace(performer)) performer = parsed.Item1;
                    title = parsed.Item2;
                }
            }

            if (string.IsNullOrWhiteSpace(title)) title = fallbackTitle;
            content["title"] = title ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(performer)) content["performer"] = performer.Trim();
        }

        public async Task SetNotificationsMutedAsync(ChatViewModel peer, bool muted)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0) return;
            await SendAsync(new JObject
            {
                ["@type"] = "setChatNotificationSettings",
                ["chat_id"] = chatId,
                ["notification_settings"] = new JObject
                {
                    ["@type"] = "chatNotificationSettings",
                    ["mute_for"] = muted ? 2147483647 : 0,
                    ["use_default_mute_for"] = false
                }
            }, TimeSpan.FromSeconds(10));

            if (peer != null) peer.IsMuted = muted;
            UpdateCachedChatNotificationSettings(chatId, muted);
        }

        public async Task<bool> GetNotificationsMutedAsync(ChatViewModel peer)
        {
            await EnsureNotificationScopesLoadedAsync();
            var chat = await ResolveChatAsync(peer);
            return chat != null && chat.IsMuted;
        }

        public async Task<ChatViewModel> SetDialogPinnedAsync(ChatViewModel peer, bool pinned)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0) return peer;
            await SendAsync(new JObject { ["@type"] = "toggleChatIsPinned", ["chat_list"] = BuildChatList(peer == null ? -1 : peer.FolderId), ["chat_id"] = chatId, ["is_pinned"] = pinned }, TimeSpan.FromSeconds(10));
            var updated = await GetChatByIdFreshAsync(chatId);
            if (updated != null) return updated;
            if (peer != null) peer.IsPinned = pinned;
            return peer;
        }

        public async Task<ChatViewModel> SetDialogArchivedAsync(ChatViewModel peer, bool archived)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0) return peer;
            await SendAsync(new JObject { ["@type"] = "setChatChatList", ["chat_id"] = chatId, ["chat_list"] = archived ? BuildChatList(ArchiveFolderId) : BuildChatList(0) }, TimeSpan.FromSeconds(10));

            // Apply the new archive state right away so notifications stop (or resume)
            // without waiting for the matching updateChatPosition to arrive.
            lock (_syncRoot)
            {
                if (archived) _archivedChatIds.Add(chatId);
                else _archivedChatIds.Remove(chatId);
            }

            var updated = await GetChatByIdFreshAsync(chatId);
            if (updated != null)
            {
                updated.IsArchived = archived;
                return updated;
            }
            if (peer != null)
            {
                peer.FolderId = archived ? ArchiveFolderId : 0;
                peer.IsArchived = archived;
            }
            return peer;
        }

        public async Task<ChatViewModel> MarkDialogReadAsync(ChatViewModel peer)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0) return peer;

            var raw = await GetChatRawFreshAsync(chatId);
            var lastMessage = raw == null ? null : raw["last_message"] as JObject;
            var lastMessageId = ReadLong(lastMessage == null ? null : lastMessage["id"]);
            if (lastMessageId != 0)
            {
                await SendAsync(new JObject
                {
                    ["@type"] = "viewMessages",
                    ["chat_id"] = chatId,
                    ["message_ids"] = new JArray(lastMessageId),
                    ["source"] = null,
                    ["force_read"] = true
                }, TimeSpan.FromSeconds(10));
            }
            else
            {
                await SendAsync(new JObject { ["@type"] = "openChat", ["chat_id"] = chatId }, TimeSpan.FromSeconds(10));
            }

            var updated = await GetChatByIdFreshAsync(chatId);
            if (updated != null)
            {
                updated.UnreadCount = 0;
                return updated;
            }
            if (peer != null) peer.UnreadCount = 0;
            return peer;
        }

        public async Task MarkMessagesReadAsync(ChatViewModel peer, IList<int> compactMessageIds)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0 || compactMessageIds == null || compactMessageIds.Count == 0) return;

            var messageIds = new JArray();
            for (var i = 0; i < compactMessageIds.Count; i++)
            {
                var tdId = ResolveMessageId(peer, compactMessageIds[i]);
                if (tdId > 0) messageIds.Add(tdId);
            }
            if (messageIds.Count == 0) return;

            await SendAsync(new JObject
            {
                ["@type"] = "viewMessages",
                ["chat_id"] = chatId,
                ["message_ids"] = messageIds,
                ["source"] = null,
                ["force_read"] = true
            }, TimeSpan.FromSeconds(10));
        }

        public async Task PinMessageAsync(ChatViewModel peer, ChatMessageViewModel message)
        {
            await StartAsync();
            if (message == null) return;
            await SendAsync(new JObject { ["@type"] = "pinChatMessage", ["chat_id"] = ResolveChatId(peer), ["message_id"] = ResolveMessageId(peer, message.Id), ["disable_notification"] = false, ["only_for_self"] = false }, TimeSpan.FromSeconds(10));
            message.IsPinned = true;
            if (peer != null && message.Id > 0)
            {
                var ids = peer.PinnedMessageIds == null ? new List<int>() : new List<int>(peer.PinnedMessageIds);
                ids.Remove(message.Id);
                ids.Insert(0, message.Id);
                peer.PinnedMessageIds = ids;
                peer.PinnedMessageId = message.Id;
                peer.CurrentPinnedMessageIndex = 0;
            }
        }

        public async Task UnpinMessageAsync(ChatViewModel peer, ChatMessageViewModel message)
        {
            await StartAsync();
            if (message == null) return;
            await SendAsync(new JObject { ["@type"] = "unpinChatMessage", ["chat_id"] = ResolveChatId(peer), ["message_id"] = ResolveMessageId(peer, message.Id) }, TimeSpan.FromSeconds(10));
            message.IsPinned = false;
            if (peer != null && message.Id > 0)
            {
                var ids = peer.PinnedMessageIds == null ? new List<int>() : new List<int>(peer.PinnedMessageIds);
                ids.Remove(message.Id);
                peer.PinnedMessageIds = ids;
                if (peer.PinnedMessageId == message.Id)
                {
                    peer.PinnedMessageId = ids.Count > 0 ? ids[0] : 0;
                    peer.CurrentPinnedMessageIndex = 0;
                    peer.PinnedMessagePreview = "";
                }
            }
        }

        public async Task DeleteMessageAsync(ChatViewModel peer, ChatMessageViewModel message)
        {
            await DeleteMessageAsync(peer, message, true);
        }

        public async Task DeleteMessageAsync(ChatViewModel peer, ChatMessageViewModel message, bool revoke)
        {
            await StartAsync();
            if (message == null) return;
            await SendAsync(new JObject { ["@type"] = "deleteMessages", ["chat_id"] = ResolveChatId(peer), ["message_ids"] = new JArray(ResolveMessageId(peer, message.Id)), ["revoke"] = revoke }, TimeSpan.FromSeconds(10));
        }

        public async Task SendPollVoteAsync(ChatViewModel peer, int messageId, List<int> options)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            var tdMessageId = ResolveMessageId(peer, messageId);
            if (chatId == 0 || tdMessageId == 0) return;

            var optionIds = new JArray();
            if (options != null)
            {
                for (var i = 0; i < options.Count; i++)
                    optionIds.Add(options[i]);
            }

            await SendAsync(new JObject
            {
                ["@type"] = "setPollAnswer",
                ["chat_id"] = chatId,
                ["message_id"] = tdMessageId,
                ["option_ids"] = optionIds
            }, TimeSpan.FromSeconds(10));
        }

        public async Task AddPollOptionAsync(ChatViewModel peer, int messageId, string text)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            var tdMessageId = ResolveMessageId(peer, messageId);
            if (chatId == 0 || tdMessageId == 0 || string.IsNullOrWhiteSpace(text)) return;

            await SendAsync(new JObject
            {
                ["@type"] = "addPollOption",
                ["chat_id"] = chatId,
                ["message_id"] = tdMessageId,
                ["option"] = new JObject
                {
                    ["@type"] = "inputPollOption",
                    ["text"] = new JObject
                    {
                        ["@type"] = "formattedText",
                        ["text"] = text.Trim(),
                        ["entities"] = new JArray()
                    },
                    ["media"] = null
                }
            }, TimeSpan.FromSeconds(10));
        }

        public async Task ToggleTodoCompletedAsync(ChatViewModel peer, int messageId, List<int> completed, List<int> incompleted)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            var tdMessageId = ResolveMessageId(peer, messageId);
            if (chatId == 0 || tdMessageId == 0) return;

            var completedIds = new JArray();
            if (completed != null)
            {
                for (var i = 0; i < completed.Count; i++)
                    completedIds.Add(completed[i]);
            }

            var incompletedIds = new JArray();
            if (incompleted != null)
            {
                for (var i = 0; i < incompleted.Count; i++)
                    incompletedIds.Add(incompleted[i]);
            }

            await SendAsync(new JObject
            {
                ["@type"] = "markChecklistTasksAsDone",
                ["chat_id"] = chatId,
                ["message_id"] = tdMessageId,
                ["marked_as_done_task_ids"] = completedIds,
                ["marked_as_not_done_task_ids"] = incompletedIds
            }, TimeSpan.FromSeconds(10));
        }

        public async Task DeleteDialogAsync(ChatViewModel peer)
        {
            await StartAsync();
            await SendAsync(new JObject { ["@type"] = "deleteChatHistory", ["chat_id"] = ResolveChatId(peer), ["remove_from_chat_list"] = true, ["revoke"] = false }, TimeSpan.FromSeconds(10));
        }

        public async Task LeaveDialogAsync(ChatViewModel peer)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0) return;
            await SendAsync(new JObject { ["@type"] = "leaveChat", ["chat_id"] = chatId }, TimeSpan.FromSeconds(10));
        }

        public async Task JoinDialogAsync(ChatViewModel peer)
        {
            await StartAsync();
            await SendAsync(new JObject { ["@type"] = "joinChat", ["chat_id"] = ResolveChatId(peer) }, TimeSpan.FromSeconds(10));
        }

        public async Task OpenChatAsync(ChatViewModel peer)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0) return;
            await SendAsync(new JObject { ["@type"] = "openChat", ["chat_id"] = chatId }, TimeSpan.FromSeconds(10));
        }

        public async Task<ChatViewModel> GetPrivateChatAsync(long userId)
        {
            await StartAsync();
            if (userId == 0) return null;

            long cachedChatId;
            lock (_syncRoot)
            {
                if (!_peerChatIds.TryGetValue("user:" + userId.ToString(), out cachedChatId))
                    cachedChatId = 0;
            }

            if (cachedChatId != 0)
            {
                var cached = await GetChatByIdAsync(cachedChatId, true);
                if (cached != null) return cached;
            }

            try
            {
                var response = await SendAsync(new JObject
                {
                    ["@type"] = "createPrivateChat",
                    ["user_id"] = userId,
                    ["force"] = false
                }, TimeSpan.FromSeconds(15));

                var resolvedChatId = ReadLong(response["id"]);
                if (resolvedChatId != 0)
                {
                    lock (_syncRoot)
                        _peerChatIds["user:" + userId.ToString()] = resolvedChatId;
                }

                return await MapChatAsync(response);
            }
            catch
            {
                return null;
            }
        }

        public async Task ForwardMessageToSavedAsync(ChatViewModel peer, ChatMessageViewModel message)
        {
            var saved = await GetSavedMessagesChatAsync();
            await ForwardMessageAsync(peer, message, saved);
        }

        public async Task ForwardMessageAsync(ChatViewModel fromPeer, ChatMessageViewModel message, ChatViewModel toPeer)
        {
            await StartAsync();
            if (message == null) return;
            await SendAsync(new JObject
            {
                ["@type"] = "forwardMessages",
                ["chat_id"] = ResolveChatId(toPeer),
                ["from_chat_id"] = ResolveChatId(fromPeer),
                ["message_ids"] = new JArray(ResolveMessageId(fromPeer, message.Id)),
                ["send_copy"] = false,
                ["remove_caption"] = false
            }, TimeSpan.FromSeconds(20));
        }

        public async Task SendReactionAsync(ChatViewModel peer, int messageId, string emoticon, bool remove)
        {
            await SendReactionAsync(peer, messageId, emoticon, 0, remove);
        }

        public async Task SendReactionAsync(ChatViewModel peer, int messageId, string emoticon, long customEmojiDocumentId, bool remove)
        {
            await StartAsync();
            var reaction = customEmojiDocumentId != 0
                ? new JObject { ["@type"] = "reactionTypeCustomEmoji", ["custom_emoji_id"] = customEmojiDocumentId }
                : new JObject { ["@type"] = "reactionTypeEmoji", ["emoji"] = emoticon ?? "" };

            if (remove)
            {
                await SendAsync(new JObject
                {
                    ["@type"] = "removeMessageReaction",
                    ["chat_id"] = ResolveChatId(peer),
                    ["message_id"] = ResolveMessageId(peer, messageId),
                    ["reaction_type"] = reaction
                }, TimeSpan.FromSeconds(10));
            }
            else
            {
                await SendAsync(new JObject
                {
                    ["@type"] = "addMessageReaction",
                    ["chat_id"] = ResolveChatId(peer),
                    ["message_id"] = ResolveMessageId(peer, messageId),
                    ["reaction_type"] = reaction,
                    ["is_big"] = false,
                    ["update_recent_reactions"] = true
                }, TimeSpan.FromSeconds(10));
            }
        }

        public async Task<TelegramCallInfo> RequestCallAsync(ChatViewModel peer)
        {
            return await RequestCallAsync(peer, 0);
        }

        public async Task<TelegramCallInfo> RequestCallAsync(ChatViewModel peer, int protocolIndex)
        {
            await Task.Delay(1);
            return new TelegramCallInfo { State = "unsupported", IsDiscarded = true, DiscardReason = "TDLib call control is not available in this build.", ProtocolIndex = protocolIndex, ProtocolName = "TDLib" };
        }

        public async Task<TelegramCallInfo> GetCallAsync(TelegramCallInfo call)
        {
            await Task.Delay(1);
            return call;
        }

        public async Task SendChatActionAsync(ChatViewModel peer, string actionKind)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0) return;

            var request = new JObject
            {
                ["@type"] = "sendChatAction",
                ["chat_id"] = chatId,
                ["message_thread_id"] = IsThreadPeer(peer) ? ResolveThreadMessageId(peer) : 0,
                ["action"] = new JObject { ["@type"] = MapChatActionType(actionKind) }
            };
            SendFireAndForget(request);
        }

        public async Task DiscardCallAsync(TelegramCallInfo call, int durationSeconds)
        {
            await Task.Delay(1);
        }

        private async Task<List<ChatMessageViewModel>> GetHistoryCoreAsync(ChatViewModel peer, int offsetMessageId, int limit, bool since)
        {
            return await GetHistoryCoreAsync(peer, offsetMessageId, limit, since, int.MinValue);
        }

        private async Task<List<ChatMessageViewModel>> GetHistoryCoreAsync(ChatViewModel peer, int offsetMessageId, int limit, bool since, int requestOffsetOverride)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0) return new List<ChatMessageViewModel>();
            SendFireAndForget(new JObject { ["@type"] = "openChat", ["chat_id"] = chatId });
            if (limit <= 0) limit = 50;

            var tdOffset = offsetMessageId > 0 ? ResolveMessageId(peer, offsetMessageId) : 0;
            var requestOffset = requestOffsetOverride != int.MinValue ? requestOffsetOverride : since && tdOffset != 0 ? -limit : 0;
            JObject request;
            if (peer != null && peer.IsForumTopic && !peer.IsCommentsThread)
            {
                request = new JObject
                {
                    ["@type"] = "getForumTopicHistory",
                    ["chat_id"] = chatId,
                    ["forum_topic_id"] = peer.TopicId,
                    ["from_message_id"] = tdOffset,
                    ["offset"] = requestOffset,
                    ["limit"] = limit
                };
            }
            else if (IsThreadPeer(peer))
            {
                request = new JObject
                {
                    ["@type"] = "getMessageThreadHistory",
                    ["chat_id"] = chatId,
                    ["message_id"] = ResolveThreadMessageId(peer),
                    ["from_message_id"] = tdOffset,
                    ["offset"] = requestOffset,
                    ["limit"] = limit
                };
            }
            else
            {
                request = new JObject
                {
                    ["@type"] = "getChatHistory",
                    ["chat_id"] = chatId,
                    ["from_message_id"] = tdOffset,
                    ["offset"] = requestOffset,
                    ["limit"] = limit,
                    ["only_local"] = false
                };
            }

            var response = await SendAsync(request, TimeSpan.FromSeconds(20));

            var messages = response["messages"] as JArray;
            var result = new List<ChatMessageViewModel>();
            if (messages != null)
            {
                foreach (var message in messages.OfType<JObject>())
                {
                    var mapped = MapMessage(peer, message);
                    if (since && mapped != null && mapped.SortId <= tdOffset) continue;
                    if (mapped != null) result.Add(mapped);
                }
            }
            result.Sort(CompareMessagesBySortIdAscending);
            return result;
        }

        private async Task<ChatViewModel> ResolveChatAsync(ChatViewModel peer)
        {
            await StartAsync();
            if (peer != null && (peer.PeerType == "user" || peer.PeerType == "self"))
            {
                var userChat = await ResolvePrivateChatAsync(peer);
                if (userChat != null) return userChat;
            }

            var chatId = ResolveChatId(peer);
            if (chatId == 0) return peer;
            return await GetChatByIdAsync(chatId, true) ?? peer;
        }

        private async Task<ChatViewModel> ResolvePrivateChatAsync(ChatViewModel peer)
        {
            if (peer == null || peer.PeerId == 0) return null;

            var chatId = ResolveChatId(peer);
            JObject cached = null;
            lock (_syncRoot)
            {
                if (chatId != 0 && _chats.TryGetValue(chatId, out cached))
                {
                    var type = cached["type"] as JObject;
                    if (ReadString(type == null ? null : type["@type"], "") != "chatTypePrivate")
                        cached = null;
                }
            }
            if (cached != null) return await MapChatAsync(cached);

            try
            {
                var response = await SendAsync(new JObject
                {
                    ["@type"] = "createPrivateChat",
                    ["user_id"] = peer.PeerId,
                    ["force"] = false
                }, TimeSpan.FromSeconds(15));
                var resolvedChatId = ReadLong(response["id"]);
                if (resolvedChatId != 0)
                {
                    lock (_syncRoot)
                        _peerChatIds["user:" + peer.PeerId.ToString()] = resolvedChatId;
                }
                return await MapChatAsync(response);
            }
            catch
            {
                return await MapUserToChatAsync(await GetUserAsync(peer.PeerId), peer.PeerType == "self") ?? peer;
            }
        }

        private async Task<ChatViewModel> GetChatByIdAsync(long chatId)
        {
            return await GetChatByIdAsync(chatId, false);
        }

        private async Task<ChatViewModel> GetChatByIdAsync(long chatId, int folderId)
        {
            if (chatId == 0) return null;
            var response = await GetChatRawAsync(chatId);
            return MapChatForList(response, folderId);
        }

        private async Task<ChatViewModel> GetChatByIdAsync(long chatId, bool full)
        {
            if (chatId == 0) return null;
            var response = await GetChatRawAsync(chatId);
            return full ? await MapChatAsync(response) : MapChatForList(response);
        }

        private async Task<ChatViewModel> GetChatByIdFreshAsync(long chatId)
        {
            if (chatId == 0) return null;
            var response = await GetChatRawFreshAsync(chatId);
            return MapChatForList(response);
        }

        private async Task<JObject> GetChatRawAsync(long chatId)
        {
            if (chatId == 0) return null;

            JObject cached;
            lock (_syncRoot)
            {
                if (_chats.TryGetValue(chatId, out cached)) return cached;
            }

            var response = await SendAsync(new JObject { ["@type"] = "getChat", ["chat_id"] = chatId }, TimeSpan.FromSeconds(15));
            UpdateChat(response);
            return response;
        }

        private async Task<JObject> GetChatRawFreshAsync(long chatId)
        {
            if (chatId == 0) return null;
            var response = await SendAsync(new JObject { ["@type"] = "getChat", ["chat_id"] = chatId }, TimeSpan.FromSeconds(15));
            UpdateChat(response);
            return response;
        }

        private async Task AddMemberUsersAsync(List<ChatViewModel> result, JArray members)
        {
            if (result == null || members == null) return;
            foreach (var item in members.OfType<JObject>())
            {
                var userId = ReadMemberUserId(item);
                if (userId == 0) continue;
                var user = await GetUserAsync(userId);
                var chat = await MapUserToChatAsync(user, false);
                if (chat != null)
                {
                    chat.MemberRole = ReadMemberRole(item["status"] as JObject);
                    result.Add(chat);
                }
            }
        }

        private async Task AddTdLibProfilePhotosAsync(List<ProfilePhotoViewModel> result, ChatViewModel peer, int limit)
        {
            if (result == null || peer == null) return;
            try
            {
                var rawChat = await GetChatRawAsync(ResolveChatId(peer));
                var type = rawChat == null ? null : rawChat["type"] as JObject;
                if (ReadString(type == null ? null : type["@type"], "") != "chatTypePrivate") return;

                var userId = ReadLong(type["user_id"]);
                var response = await SendAsync(new JObject
                {
                    ["@type"] = "getUserProfilePhotos",
                    ["user_id"] = userId,
                    ["offset"] = 0,
                    ["limit"] = limit <= 0 ? 20 : limit
                }, TimeSpan.FromSeconds(15));

                var photos = response["photos"] as JArray;
                if (photos == null) return;
                foreach (var photo in photos.OfType<JObject>())
                {
                    var file = ReadBestPhotoFile(photo);
                    var path = await DownloadSmallFilePathAsync(file);
                    if (!string.IsNullOrEmpty(path))
                        result.Add(new ProfilePhotoViewModel { PhotoId = ReadLong(photo["id"]), Uri = path });
                }
            }
            catch
            {
            }
        }

        private async Task<JObject> GetUserAsync(long userId)
        {
            if (userId == 0) return null;
            JObject user;
            lock (_syncRoot)
            {
                if (_users.TryGetValue(userId, out user)) return user;
            }
            user = await SendAsync(new JObject { ["@type"] = "getUser", ["user_id"] = userId }, TimeSpan.FromSeconds(10));
            UpdateUser(user);
            return user;
        }

        private async Task<JObject> GetUserFullInfoAsync(long userId)
        {
            if (userId == 0) return null;
            try { return await SendAsync(new JObject { ["@type"] = "getUserFullInfo", ["user_id"] = userId }, TimeSpan.FromSeconds(10)); }
            catch { return null; }
        }

        private async Task<JObject> GetSupergroupAsync(long supergroupId)
        {
            if (supergroupId == 0) return null;
            JObject group;
            lock (_syncRoot)
            {
                if (_supergroups.TryGetValue(supergroupId, out group)) return group;
            }
            try
            {
                group = await SendAsync(new JObject { ["@type"] = "getSupergroup", ["supergroup_id"] = supergroupId }, TimeSpan.FromSeconds(10));
                UpdateSupergroup(group);
                return group;
            }
            catch { return null; }
        }

        private async Task<JObject> GetSupergroupFullInfoAsync(long supergroupId)
        {
            if (supergroupId == 0) return null;
            try { return await SendAsync(new JObject { ["@type"] = "getSupergroupFullInfo", ["supergroup_id"] = supergroupId }, TimeSpan.FromSeconds(10)); }
            catch { return null; }
        }

        private async Task<JObject> GetBasicGroupAsync(long basicGroupId)
        {
            if (basicGroupId == 0) return null;
            JObject group;
            lock (_syncRoot)
            {
                if (_basicGroups.TryGetValue(basicGroupId, out group)) return group;
            }
            try
            {
                group = await SendAsync(new JObject { ["@type"] = "getBasicGroup", ["basic_group_id"] = basicGroupId }, TimeSpan.FromSeconds(10));
                UpdateBasicGroup(group);
                return group;
            }
            catch { return null; }
        }

        private async Task<JObject> GetBasicGroupFullInfoAsync(long basicGroupId)
        {
            if (basicGroupId == 0) return null;
            try { return await SendAsync(new JObject { ["@type"] = "getBasicGroupFullInfo", ["basic_group_id"] = basicGroupId }, TimeSpan.FromSeconds(10)); }
            catch { return null; }
        }

        /// <summary>
        /// Tracks one in-flight download. TDLib pushes an updateFile for every progress step, so
        /// the download is driven by those pushes instead of polling getFile.
        /// </summary>
        private sealed class FileDownloadWatcher
        {
            public readonly TaskCompletionSource<JObject> Completion = new TaskCompletionSource<JObject>();
            public readonly List<Action<long, long>> Progress = new List<Action<long, long>>();
            public JObject LastFile;
        }

        /// <summary>
        /// Waits for a file to finish downloading.
        /// </summary>
        /// <remarks>
        /// This used to send a getFile every 400ms for the whole duration of the download, which
        /// is a full JSON round trip through tdjson plus a parse plus a dispatcher hop, several
        /// times a second per concurrent download - while TDLib was already pushing exactly the
        /// same data as updateFile. Unigram drives all of its file UI from those pushes; so does
        /// this now. The slow tick that remains is only a backstop in case an update is missed.
        /// </remarks>
        private async Task<JObject> DownloadFileAsync(long fileId, Action<long, long> progress)
        {
            FileDownloadWatcher watcher;
            lock (_syncRoot)
            {
                if (!_fileDownloadWatchers.TryGetValue(fileId, out watcher) || watcher == null)
                {
                    watcher = new FileDownloadWatcher();
                    _fileDownloadWatchers[fileId] = watcher;
                }
                if (progress != null) watcher.Progress.Add(progress);
            }

            try
            {
                var file = await SendAsync(new JObject
                {
                    ["@type"] = "downloadFile",
                    ["file_id"] = fileId,
                    ["priority"] = 32,
                    ["offset"] = 0,
                    ["limit"] = 0,
                    ["synchronous"] = false
                }, TimeSpan.FromSeconds(10));

                if (progress != null) progress(ReadDownloadedBytes(file), ReadDownloadTotal(file));
                if (IsDownloadCompleted(file)) return file;

                var deadline = DateTime.UtcNow.AddMinutes(30);
                while (DateTime.UtcNow < deadline)
                {
                    var finished = await Task.WhenAny(watcher.Completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                    if (finished == watcher.Completion.Task)
                        return watcher.Completion.Task.Result ?? file;

                    // Backstop: an updateFile can be dropped if the app was suspended, so
                    // re-check occasionally. Twelve times slower than the old poll.
                    var latest = watcher.LastFile;
                    if (latest == null)
                    {
                        try
                        {
                            latest = await SendAsync(new JObject
                            {
                                ["@type"] = "getFile",
                                ["file_id"] = fileId
                            }, TimeSpan.FromSeconds(10));
                        }
                        catch
                        {
                        }
                    }

                    if (latest != null)
                    {
                        file = latest;
                        if (progress != null) progress(ReadDownloadedBytes(file), ReadDownloadTotal(file));
                        if (IsDownloadCompleted(file)) return file;
                    }
                }

                return file;
            }
            finally
            {
                lock (_syncRoot)
                {
                    FileDownloadWatcher current;
                    if (_fileDownloadWatchers.TryGetValue(fileId, out current) && current == watcher)
                    {
                        if (progress != null) current.Progress.Remove(progress);
                        if (current.Progress.Count == 0) _fileDownloadWatchers.Remove(fileId);
                    }
                }
            }
        }

        /// <summary>
        /// Feeds an updateFile into any download that is waiting on it.
        /// </summary>
        private void NotifyFileDownloadWatchers(long fileId, JObject file)
        {
            if (file == null) return;

            FileDownloadWatcher watcher;
            Action<long, long>[] callbacks;
            lock (_syncRoot)
            {
                if (!_fileDownloadWatchers.TryGetValue(fileId, out watcher) || watcher == null) return;
                watcher.LastFile = file;
                callbacks = watcher.Progress.ToArray();
            }

            var downloaded = ReadDownloadedBytes(file);
            var total = ReadDownloadTotal(file);
            for (var i = 0; i < callbacks.Length; i++)
            {
                try { callbacks[i](downloaded, total); }
                catch { }
            }

            if (IsDownloadCompleted(file))
                watcher.Completion.TrySetResult(file);
        }

        private static bool IsDownloadCompleted(JObject file)
        {
            if (file == null) return false;
            var local = file["local"] as JObject;
            return ReadBool(local == null ? null : local["is_downloading_completed"]);
        }

        private async Task<StorageFile> DownloadStorageFileByIdAsync(long fileId)
        {
            if (fileId == 0) return null;
            var file = await DownloadFileAsync(fileId, null);
            return await TryGetStorageFileAsync(ToFileUri(ReadFilePath(file)));
        }

        public async Task<List<MessageReactionViewModel>> GetMessageAvailableReactionsAsync(ChatViewModel peer, int messageId)
        {
            await StartAsync();
            var result = new List<MessageReactionViewModel>();
            if (peer == null || messageId <= 0) return result;

            var response = await SendAsync(new JObject
            {
                ["@type"] = "getMessageAvailableReactions",
                ["chat_id"] = ResolveChatId(peer),
                ["message_id"] = ResolveMessageId(peer, messageId),
                ["row_size"] = 25
            }, TimeSpan.FromSeconds(10));

            // TDLib may return reaction candidates together with an explicit reason why the
            // current account can't react. In that case the menu must not offer fake actions.
            if (response["unavailability_reason"] as JObject != null) return result;

            var isPremium = await GetSelfIsPremiumAsync();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            AddAvailableReactions(result, seen, response["top_reactions"] as JArray, isPremium);
            AddAvailableReactions(result, seen, response["recent_reactions"] as JArray, isPremium);
            AddAvailableReactions(result, seen, response["popular_reactions"] as JArray, isPremium);
            return result;
        }

        private async Task<bool> GetSelfIsPremiumAsync()
        {
            lock (_syncRoot)
            {
                if (_selfPremiumKnown) return _selfIsPremium;
            }

            try
            {
                var me = await SendAsync(new JObject { ["@type"] = "getMe" }, TimeSpan.FromSeconds(10));
                var isPremium = ReadBool(me["is_premium"]);
                lock (_syncRoot)
                {
                    _selfIsPremium = isPremium;
                    _selfPremiumKnown = true;
                }
                return isPremium;
            }
            catch
            {
                // Conservative fallback: don't expose premium-only reactions if account
                // capabilities couldn't be resolved.
                return false;
            }
        }

        private void AddAvailableReactions(List<MessageReactionViewModel> result, HashSet<string> seen, JArray items, bool isPremium)
        {
            if (result == null || seen == null || items == null) return;
            foreach (var item in items.OfType<JObject>())
            {
                if (result.Count >= 64) break;
                if (ReadBool(item["needs_premium"]) && !isPremium) continue;
                var type = item["type"] as JObject;
                if (type == null) continue;
                var typeName = ReadString(type["@type"], "");
                var emoticon = "";
                long customEmojiId = 0;
                if (typeName == "reactionTypeEmoji")
                    emoticon = ReadString(type["emoji"], "");
                else if (typeName == "reactionTypeCustomEmoji")
                    customEmojiId = ReadLong(type["custom_emoji_id"]);
                else
                    continue;

                var key = customEmojiId != 0 ? "custom:" + customEmojiId.ToString() : "emoji:" + emoticon;
                if (string.IsNullOrEmpty(emoticon) && customEmojiId == 0) continue;
                if (!seen.Add(key)) continue;

                result.Add(new MessageReactionViewModel
                {
                    Emoticon = emoticon,
                    CustomEmojiDocumentId = customEmojiId,
                    CustomEmojiUri = customEmojiId == 0 ? "" : GetCachedCustomEmojiStickerUri(customEmojiId)
                });
            }
        }

        public async Task<string> GetCustomEmojiStickerUriAsync(long customEmojiId)
        {
            if (customEmojiId == 0) return "";
            string cached;
            lock (_syncRoot)
            {
                if (_customEmojiIconUris.TryGetValue(customEmojiId, out cached)) return cached;
            }
            cached = GetCachedCustomEmojiStickerUri(customEmojiId);
            if (!string.IsNullOrEmpty(cached)) return cached;

            try
            {
                var response = await SendAsync(new JObject
                {
                    ["@type"] = "getCustomEmojiStickers",
                    ["custom_emoji_ids"] = new JArray(customEmojiId)
                }, TimeSpan.FromSeconds(15));
                var stickers = response["stickers"] as JArray;
                var sticker = stickers == null ? null : stickers.OfType<JObject>().FirstOrDefault();
                var file = SelectStickerIconFile(sticker);
                var fileId = ReadLong(file == null ? null : file["id"]);
                if (fileId == 0) return "";

                var downloaded = await DownloadFileAsync(fileId, null);
                var uri = ToFileUri(ReadFilePath(downloaded));
                StoreCustomEmojiStickerUri(customEmojiId, uri);
                return uri;
            }
            catch
            {
                return "";
            }
        }

        private async Task LoadCustomEmojiStickerUriIntoTopicAsync(ChatViewModel topic)
        {
            if (topic == null || topic.TopicIconEmojiId == 0 || !string.IsNullOrEmpty(topic.TopicIconUri)) return;
            var uri = await GetCustomEmojiStickerUriAsync(topic.TopicIconEmojiId);
            if (!string.IsNullOrEmpty(uri)) topic.TopicIconUri = uri;
        }

        private string GetCachedCustomEmojiStickerUri(long customEmojiId)
        {
            if (customEmojiId == 0) return "";
            string cached;
            lock (_syncRoot)
            {
                if (_customEmojiIconUris.TryGetValue(customEmojiId, out cached)) return cached;
            }

            object value;
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(BuildCustomEmojiCacheKey(customEmojiId), out value) && value != null)
            {
                cached = value.ToString();
                if (!string.IsNullOrEmpty(cached))
                {
                    lock (_syncRoot) _customEmojiIconUris[customEmojiId] = cached;
                    return cached;
                }
            }
            return "";
        }

        private void StoreCustomEmojiStickerUri(long customEmojiId, string uri)
        {
            if (customEmojiId == 0 || string.IsNullOrEmpty(uri)) return;
            lock (_syncRoot) _customEmojiIconUris[customEmojiId] = uri;
            ApplicationData.Current.LocalSettings.Values[BuildCustomEmojiCacheKey(customEmojiId)] = uri;
        }

        private static string BuildCustomEmojiCacheKey(long customEmojiId)
        {
            return "td_custom_emoji_icon_" + customEmojiId.ToString();
        }

        private static JObject SelectStickerIconFile(JObject sticker)
        {
            if (sticker == null) return null;
            var format = sticker["format"] as JObject;
            var formatType = ReadString(format == null ? null : format["@type"], "");
            if (formatType == "stickerFormatWebp" || formatType == "stickerFormatWebm")
                return sticker["sticker"] as JObject;

            var thumbnail = sticker["thumbnail"] as JObject;
            var thumbnailFile = thumbnail == null ? null : thumbnail["file"] as JObject;
            return thumbnailFile ?? sticker["sticker"] as JObject;
        }

        public async Task<List<StickerSetViewModel>> GetStickerPanelSetsAsync()
        {
            await StartAsync();

            var result = new List<StickerSetViewModel>();
            var recent = await LoadStickerListSetAsync("recent", "Recent", new JObject
            {
                ["@type"] = "getRecentStickers",
                ["is_attached"] = false
            });
            result.Add(recent);

            var favorites = await LoadStickerListSetAsync("favorite", "Favorites", new JObject
            {
                ["@type"] = "getFavoriteStickers"
            });
            result.Add(favorites);

            JArray installedSets = null;
            try
            {
                var installed = await SendInstalledStickerSetsAsync();
                installedSets = installed == null ? null : installed["sets"] as JArray;
            }
            catch
            {
                installedSets = null;
            }

            if (installedSets != null)
            {
                foreach (var token in installedSets.OfType<JObject>())
                {
                    var setId = ReadLong(token["id"]);
                    if (setId == 0) continue;
                    try
                    {
                        var fullSet = await SendAsync(new JObject
                        {
                            ["@type"] = "getStickerSet",
                            ["set_id"] = setId
                        }, TimeSpan.FromSeconds(20));
                        var mapped = MapStickerSet(fullSet, "set");
                        if (mapped != null) result.Add(mapped);
                    }
                    catch
                    {
                    }
                }
            }

            return result;
        }

        private async Task<JObject> SendInstalledStickerSetsAsync()
        {
            try
            {
                return await SendAsync(new JObject
                {
                    ["@type"] = "getInstalledStickerSets",
                    ["sticker_type"] = new JObject { ["@type"] = "stickerTypeRegular" }
                }, TimeSpan.FromSeconds(20));
            }
            catch
            {
                return await SendAsync(new JObject
                {
                    ["@type"] = "getInstalledStickerSets",
                    ["is_masks"] = false
                }, TimeSpan.FromSeconds(20));
            }
        }

        private async Task<StickerSetViewModel> LoadStickerListSetAsync(string kind, string title, JObject request)
        {
            var set = new StickerSetViewModel
            {
                Id = kind == "favorite" ? -2 : -1,
                Kind = kind,
                Title = title
            };

            try
            {
                var response = await SendAsync(request, TimeSpan.FromSeconds(15));
                var stickers = response == null ? null : response["stickers"] as JArray;
                if (stickers != null)
                {
                    foreach (var token in stickers.OfType<JObject>())
                    {
                        var item = MapStickerItem(token, set.Id);
                        if (item != null) set.Stickers.Add(item);
                    }
                }
            }
            catch
            {
            }

            return set;
        }

        private StickerSetViewModel MapStickerSet(JObject set, string kind)
        {
            if (set == null) return null;
            var result = new StickerSetViewModel
            {
                Id = ReadLong(set["id"]),
                Kind = kind,
                Title = ReadString(set["title"], "Stickers"),
                ShortName = ReadString(set["name"], "")
            };

            var stickers = set["stickers"] as JArray;
            if (stickers != null)
            {
                foreach (var token in stickers.OfType<JObject>())
                {
                    var item = MapStickerItem(token, result.Id);
                    if (item != null) result.Stickers.Add(item);
                }
            }

            return result;
        }

        private StickerItemViewModel MapStickerItem(JObject sticker, long setId)
        {
            if (sticker == null) return null;

            var file = sticker["sticker"] as JObject;
            var fileId = ReadLong(file == null ? null : file["id"]);
            if (fileId == 0) fileId = ReadLong(sticker["id"]);
            if (fileId == 0) return null;

            var thumbnail = sticker["thumbnail"] as JObject;
            var thumbnailFile = thumbnail == null ? null : thumbnail["file"] as JObject;
            var format = sticker["format"] as JObject;
            var formatType = ReadString(format == null ? null : format["@type"], "");
            var previewFile = SelectStickerPanelPreviewFile(formatType, file, thumbnailFile);

            var item = new StickerItemViewModel
            {
                SetId = setId,
                FileId = fileId,
                Width = (int)Math.Max(0, ReadLong(sticker["width"])),
                Height = (int)Math.Max(0, ReadLong(sticker["height"])),
                Emoji = ReadString(sticker["emoji"], ""),
                Format = formatType
            };

            var filePath = ReadFilePath(file);
            if (!string.IsNullOrEmpty(filePath))
                item.StickerSourceUri = ToFileUri(filePath);

            var thumbPath = ReadFilePath(thumbnailFile);
            if (!string.IsNullOrEmpty(thumbPath))
                item.ThumbnailSourceUri = ToFileUri(thumbPath);

            var previewPath = ReadFilePath(previewFile);
            if (!string.IsNullOrEmpty(previewPath))
                item.StickerSourceUri = ToFileUri(previewPath);
            else
            {
                var previewFileId = ReadLong(previewFile == null ? null : previewFile["id"]);
                if (previewFileId != 0)
                {
                    RegisterStickerFileTarget(previewFileId, item);
                    SendFireAndForget(new JObject
                    {
                        ["@type"] = "downloadFile",
                        ["file_id"] = previewFileId,
                        ["priority"] = 3,
                        ["offset"] = 0,
                        ["limit"] = 0,
                        ["synchronous"] = false
                    });
                }
            }

            return item;
        }

        private static JObject SelectStickerPanelPreviewFile(string formatType, JObject file, JObject thumbnailFile)
        {
            if (formatType == "stickerFormatWebp" || formatType == "stickerFormatWebm")
                return file ?? thumbnailFile;
            return thumbnailFile ?? file;
        }

        private void RegisterStickerFileTarget(long fileId, StickerItemViewModel item)
        {
            if (fileId == 0 || item == null) return;
            lock (_syncRoot)
            {
                List<StickerItemViewModel> targets;
                if (!_stickerFileTargets.TryGetValue(fileId, out targets))
                {
                    targets = new List<StickerItemViewModel>();
                    _stickerFileTargets[fileId] = targets;
                }
                if (!targets.Contains(item)) targets.Add(item);
            }
        }

        public async Task SendStickerMessageAsync(ChatViewModel peer, StickerItemViewModel sticker, int replyToMessageId)
        {
            if (sticker == null) return;
            await SendStickerMessageAsync(peer, sticker.FileId, sticker.Width, sticker.Height, sticker.Emoji, replyToMessageId);
        }

        public async Task SendStickerMessageAsync(ChatViewModel peer, long fileId, int width, int height, string emoji, int replyToMessageId)
        {
            await StartAsync();
            var chatId = ResolveChatId(peer);
            if (chatId == 0 || fileId == 0) return;

            var content = new JObject
            {
                ["@type"] = "inputMessageSticker",
                ["sticker"] = new JObject { ["@type"] = "inputFileId", ["id"] = fileId },
                ["width"] = Math.Max(0, width),
                ["height"] = Math.Max(0, height),
                ["emoji"] = emoji ?? ""
            };

            var request = new JObject
            {
                ["@type"] = "sendMessage",
                ["chat_id"] = chatId,
                ["input_message_content"] = content
            };
            var topic = BuildMessageTopic(peer);
            if (topic != null) request["topic_id"] = topic;
            if (replyToMessageId > 0)
                request["reply_to"] = new JObject { ["@type"] = "inputMessageReplyToMessage", ["message_id"] = ResolveMessageId(peer, replyToMessageId) };
            await SendAsync(request, TimeSpan.FromSeconds(20));
        }

        private Task<JObject> SendAsync(JObject request, TimeSpan timeout)
        {
            var extra = NextExtra();
            request["@extra"] = extra;
            var tcs = new TaskCompletionSource<JObject>();
            lock (_syncRoot)
            {
                _pending[extra] = tcs;
            }
            Debug.WriteLine("TDLIB => " + request.ToString(Newtonsoft.Json.Formatting.None));
            TdJson.SendUtf8(_client, request.ToString(Newtonsoft.Json.Formatting.None));
            return WithTimeout(tcs.Task, timeout);
        }

        private async Task<JObject> WithTimeout(Task<JObject> task, TimeSpan timeout)
        {
            var delay = Task.Delay(timeout);
            var completed = await Task.WhenAny(task, delay);
            if (completed == delay) throw new TimeoutException("TDLib did not answer in time.");
            var result = await task;
            if (ReadString(result["@type"], "") == "error")
                throw new InvalidOperationException(ReadString(result["message"], "TDLib error"));
            return result;
        }

        private void SendFireAndForget(JObject request)
        {
            if (_client == IntPtr.Zero) return;
            var json = request.ToString(Newtonsoft.Json.Formatting.None);
            Debug.WriteLine("TDLIB => " + json);
            TdJson.SendUtf8(_client, json);
        }

        private string NextExtra()
        {
            return "td:" + Interlocked.Increment(ref _extraId).ToString();
        }

        private void ReceiveLoop()
        {
            while (true)
            {
                if (_client == IntPtr.Zero) return;
                var ptr = TdJson.td_json_client_receive(_client, 1.0);
                if (ptr == IntPtr.Zero) continue;
                var json = TdJson.IntPtrToStringUtf8(ptr);
                if (string.IsNullOrWhiteSpace(json)) continue;
                Debug.WriteLine("TDLIB <= " + json);
                try { HandleJson(JObject.Parse(json)); }
                catch { }
            }
        }

        private void HandleJson(JObject update)
        {
            var extra = ReadString(update["@extra"], "");
            if (!string.IsNullOrEmpty(extra))
            {
                TaskCompletionSource<JObject> tcs = null;
                lock (_syncRoot)
                {
                    if (_pending.TryGetValue(extra, out tcs))
                        _pending.Remove(extra);
                }
                if (tcs != null)
                {
                    tcs.TrySetResult(update);
                    return;
                }
            }

            var type = ReadString(update["@type"], "");
            if (type == "error")
            {
                _lastTdLibError = ReadString(update["message"], "TDLib error");
                SignalAuthState("error");
                return;
            }

            if (type == "updateAuthorizationState")
            {
                var state = update["authorization_state"] as JObject;
                var stateType = ReadString(state == null ? null : state["@type"], "");
                var previousAuthorizationState = _authorizationState;
                _authorizationState = stateType;
                if (stateType == "authorizationStateWaitTdlibParameters") SendTdlibParameters();
                else if (stateType == "authorizationStateWaitEncryptionKey") SendEncryptionKey();
                else if (stateType == "authorizationStateWaitPhoneNumber" || stateType == "authorizationStateWaitCode" || stateType == "authorizationStateWaitPassword")
                {
                    ApplicationData.Current.LocalSettings.Values["tdlib_authorized"] = false;
                    TelegramNotificationRuntime.ClearAuthorizationBaseline();
                    ApplySavedProxy();
                }
                else if (stateType == "authorizationStateWaitOtherDeviceConfirmation")
                {
                    ApplicationData.Current.LocalSettings.Values["tdlib_authorized"] = false;
                    TelegramNotificationRuntime.ClearAuthorizationBaseline();
                    _qrLink = ReadString(state["link"], _qrLink);
                    ApplySavedProxy();
                }
                else if (stateType == "authorizationStateReady")
                {
                    ApplicationData.Current.LocalSettings.Values["tdlib_authorized"] = true;
                    if (previousAuthorizationState != "authorizationStateReady")
                    {
                        var baselineIgnore = TelegramNotificationRuntime.MarkAuthorizationReadyAsync();
                    }
                    ApplySavedProxy();
                }
                else if (stateType == "authorizationStateClosed")
                {
                    ApplicationData.Current.LocalSettings.Values["tdlib_authorized"] = false;
                    TelegramNotificationRuntime.ClearAuthorizationBaseline();
                }
                SignalAuthState(stateType);
                return;
            }

            if (type == "updateChatNotificationSettings")
            {
                UpdateChatNotificationSettings(update);
                return;
            }

            if (type == "updateScopeNotificationSettings")
            {
                UpdateScopeNotificationSettings(update);
                return;
            }

            if (type == "chatFolders" || type == "updateChatFolders")
            {
                UpdateChatFolders(ReadFoldersArray(update));
                return;
            }

            if (type == "updateFile")
            {
                HandleFileUpdate(update["file"] as JObject);
                return;
            }

            if (type == "file")
            {
                HandleFileUpdate(update);
                return;
            }

            if (type == "updateOption")
            {
                if (ReadString(update["name"], "") == "enabled_proxy_id")
                    _currentProxyId = ReadInt(update["value"] == null ? null : update["value"]["value"]);
                return;
            }

            if (type == "updateNewChat")
            {
                UpdateChat(update["chat"] as JObject);
                return;
            }
            if (type == "updateChatLastMessage")
            {
                UpdateChatLastMessage(update);
                QueueMessageUpdate(update["last_message"] as JObject);
                return;
            }
            if (type == "updateNewMessage")
            {
                Debug.WriteLine("RT_TDLIB updateNewMessage received");
                var message = update["message"] as JObject;
                NotifyRealtimeMessage(message);
                QueueMessageUpdate(message);
                return;
            }
            if (type == "updateMessageContent" || type == "updateMessageEdited" ||
                type == "updateMessageInteractionInfo" || type == "updateMessageMentionRead" ||
                type == "updateMessageUnreadReactions" || type == "updateMessageFactCheck")
            {
                QueueMessageRefresh(update);
                return;
            }
            if (type == "updateChatReplyMarkup")
            {
                var chatId = ReadLong(update["chat_id"]);
                var messageId = ReadLong(update["reply_markup_message_id"]);
                if (chatId != 0)
                {
                    lock (_syncRoot)
                    {
                        JObject cachedChat;
                        if (_chats.TryGetValue(chatId, out cachedChat) && cachedChat != null)
                            cachedChat["reply_markup_message_id"] = messageId;
                        if (messageId == 0)
                        {
                            _pendingReplyMarkupResetChatIds.Add(chatId);
                        }
                        else
                        {
                            HashSet<long> ids;
                            if (!_pendingMessageRefreshIds.TryGetValue(chatId, out ids))
                            {
                                ids = new HashSet<long>();
                                _pendingMessageRefreshIds[chatId] = ids;
                            }
                            ids.Add(messageId);
                        }
                    }
                    RaiseOnUiThread(MessageContentUpdated, chatId);
                }
                return;
            }
            if (type == "updateMessageSendSucceeded")
            {
                QueueMessageSendSucceeded(update);
                return;
            }
            if (type == "updateDeleteMessages")
            {
                QueueDeletedMessages(update);
                return;
            }
            if (type == "updateChatPosition")
            {
                UpdateChatPosition(update);
                return;
            }
            if (type == "updateChatReadInbox")
            {
                UpdateChatReadInbox(update);
                return;
            }
            if (type == "updateChatReadOutbox")
            {
                UpdateChatReadOutbox(update);
                return;
            }
            if (type == "updateChatPinnedMessage")
            {
                UpdateChatPinnedMessage(update);
                return;
            }
            if (type == "updateUser")
            {
                UpdateUser(update["user"] as JObject);
                return;
            }
            if (type == "updateUserStatus")
            {
                var userId = ReadLong(update["user_id"]);
                var status = update["status"] as JObject;
                if (userId != 0 && status != null)
                {
                    lock (_syncRoot)
                    {
                        JObject cached;
                        if (_users.TryGetValue(userId, out cached) && cached != null)
                            cached["status"] = status;
                    }
                    RaiseOnUiThread(UserStatusChanged, userId);
                }
                return;
            }
            if (type == "updateSupergroup")
            {
                UpdateSupergroup(update["supergroup"] as JObject);
                return;
            }
            if (type == "updateBasicGroup")
            {
                UpdateBasicGroup(update["basic_group"] as JObject);
                return;
            }
            if (type == "proxy")
            {
                _currentProxyId = ReadInt(update["id"]);
            }
        }

        private void SendTdlibParameters()
        {
            if (_parametersSent) return;
            _parametersSent = true;

            var dbPath = ApplicationData.Current.LocalFolder.Path.Replace("\\", "/") + "/Unogram/td_db";
            var filesPath = (_filesFolder == null ? ApplicationData.Current.LocalFolder.Path : _filesFolder.Path).Replace("\\", "/");
            SendFireAndForget(new JObject
            {
                ["@type"] = "setTdlibParameters",
                ["use_test_dc"] = false,
                ["database_directory"] = dbPath,
                ["files_directory"] = filesPath,
                ["database_encryption_key"] = "",
                ["use_file_database"] = true,
                ["use_chat_info_database"] = true,
                ["use_message_database"] = true,
                ["use_secret_chats"] = false,
                ["api_id"] = AppConfig.ApiId,
                ["api_hash"] = AppConfig.ApiHash,
                ["system_language_code"] = "ru",
                ["device_model"] = GetDeviceModelName(),
                ["system_version"] = GetSystemVersionName(),
                ["application_version"] = "1.2"
            });
        }

        private static string GetDeviceModelName()
        {
            try
            {
                var info = new EasClientDeviceInformation();
                var manufacturer = CleanDeviceInfo(info.SystemManufacturer);
                var product = CleanDeviceInfo(info.SystemProductName);
                var friendly = CleanDeviceInfo(info.FriendlyName);
                var sku = CleanDeviceInfo(info.SystemSku);

                var model = FirstNonEmpty(
                    JoinDeviceName(manufacturer, product),
                    friendly,
                    sku,
                    product,
                    manufacturer);
                return string.IsNullOrWhiteSpace(model) ? "Windows device" : model;
            }
            catch
            {
                return "Windows device";
            }
        }

        private static string GetSystemVersionName()
        {
            try
            {
                var info = new EasClientDeviceInformation();
                var os = CleanDeviceInfo(info.OperatingSystem);
                if (!string.IsNullOrWhiteSpace(os))
                    return os;
            }
            catch
            {
            }

            return "Windows 10";
        }

        private static string JoinDeviceName(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first)) return second ?? "";
            if (string.IsNullOrWhiteSpace(second)) return first ?? "";
            if (second.IndexOf(first, StringComparison.OrdinalIgnoreCase) >= 0) return second;
            return first + " " + second;
        }

        private static string CleanDeviceInfo(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            value = value.Trim();
            if (string.Equals(value, "System Product Name", StringComparison.OrdinalIgnoreCase)) return "";
            if (string.Equals(value, "System Manufacturer", StringComparison.OrdinalIgnoreCase)) return "";
            if (string.Equals(value, "To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase)) return "";
            return value;
        }

        private void SendEncryptionKey()
        {
            if (_encryptionKeySent) return;
            _encryptionKeySent = true;
            SendFireAndForget(new JObject { ["@type"] = "checkDatabaseEncryptionKey", ["encryption_key"] = "" });
        }

        private async Task WaitForAnyAuthStateAsync(string[] states, TimeSpan timeout)
        {
            if (states.Contains(_authorizationState)) return;

            var tcs = new TaskCompletionSource<string>();
            _authStateWaiter = tcs;
            var delay = Task.Delay(timeout);
            while (!states.Contains(_authorizationState))
            {
                var completed = await Task.WhenAny(tcs.Task, delay);
                if (completed == delay) throw new TimeoutException("TDLib authorization state timeout.");
                var state = await tcs.Task;
                if (state == "error")
                    throw new InvalidOperationException(string.IsNullOrEmpty(_lastTdLibError) ? "TDLib error" : _lastTdLibError);
                if (states.Contains(state)) return;
                tcs = new TaskCompletionSource<string>();
                _authStateWaiter = tcs;
            }
        }

        private void SignalAuthState(string state)
        {
            var waiter = _authStateWaiter;
            if (waiter != null) waiter.TrySetResult(state);
        }

        private JObject BuildChatList(int folderId)
        {
            if (folderId == ArchiveFolderId) return new JObject { ["@type"] = "chatListArchive" };
            if (folderId > ArchiveFolderId) return new JObject { ["@type"] = "chatListFolder", ["chat_folder_id"] = ToTdFolderId(folderId) };
            return new JObject { ["@type"] = "chatListMain" };
        }

        private ChatViewModel MapChat(JObject chat)
        {
            if (chat == null) return null;
            UpdateChat(chat);

            var id = ReadLong(chat["id"]);
            var type = chat["type"] as JObject;
            var typeName = ReadString(type == null ? null : type["@type"], "");
            var peerType = "chat";
            var isGroup = false;
            var isChannel = false;
            var isBroadcast = false;
            var isBot = false;
            var userId = 0L;

            if (typeName == "chatTypePrivate")
            {
                peerType = "user";
                userId = ReadLong(type["user_id"]);
                if (_selfUserId != 0 && userId == _selfUserId)
                    peerType = "self";
                JObject user;
                if (_users.TryGetValue(userId, out user))
                    isBot = ReadString(user["type"] == null ? null : user["type"]["@type"], "") == "userTypeBot";
            }
            else if (typeName == "chatTypeBasicGroup")
            {
                peerType = "chat";
                isGroup = true;
            }
            else if (typeName == "chatTypeSupergroup")
            {
                peerType = "channel";
                isGroup = !ReadBool(type["is_channel"]);
                isChannel = true;
                isBroadcast = ReadBool(type["is_channel"]);
            }

            var vm = new ChatViewModel
            {
                PeerId = id,
                UserId = userId,
                PeerType = peerType,
                PeerKey = peerType == "self" ? "self:" + id.ToString() : peerType + ":" + id.ToString(),
                Title = ReadString(chat["title"], "Chat"),
                LastMessage = ReadLastMessage(chat["last_message"] as JObject),
                LastMessageSenderName = ReadSenderName(chat["last_message"] as JObject),
                LastMessageDate = ReadInt(chat["last_message"] == null ? null : chat["last_message"]["date"]),
                LastMessageIsOutgoing = ReadBool(chat["last_message"] == null ? null : chat["last_message"]["is_outgoing"]),
                UnreadCount = ReadInt(chat["unread_count"]),
                TopMessageId = CompactMessageId(id, ReadLong(chat["last_message"] == null ? null : chat["last_message"]["id"])),
                ReadOutboxMaxId = CompactMessageId(id, ReadLong(chat["last_read_outbox_message_id"])),
                PinnedMessageId = CompactMessageId(id, ReadLong(chat["pinned_message_id"])),
                FolderId = ReadFolderId(chat["positions"] as JArray),
                IsArchived = ReadArchived(chat["positions"] as JArray) || IsChatArchived(id),
                IsPinned = ReadPinned(chat["positions"] as JArray),
                IsMuted = ReadEffectiveChatMuted(chat, peerType, isGroup, isBroadcast, isChannel),
                IsContact = peerType == "user",
                IsBot = isBot,
                BotUserId = isBot ? userId : 0,
                ReplyMarkupMessageId = ReadLong(chat["reply_markup_message_id"]),
                IsGroup = isGroup,
                IsChannel = isChannel,
                IsBroadcast = isBroadcast,
                IsJoined = true,
                CanSendMessages = ReadCanSendMessages(chat),
                CanPinMessages = ReadCanPinMessages(chat, peerType, isGroup, isBroadcast),
                CanDeleteMessages = ReadCanDeleteMessages(chat, peerType),
                IconText = BuildInitials(ReadString(chat["title"], "C")),
                TypeIcon = isBroadcast ? "channel" : isGroup ? "group" : "user",
                TypeGlyph = isBroadcast ? "\uE789" : isGroup ? "\uE716" : "\uE77B"
            };

            if (vm.PinnedMessageId > 0)
                vm.PinnedMessageIds = new List<int> { vm.PinnedMessageId };
            FillPhoto(vm, chat["photo"] as JObject);
            lock (_syncRoot) _peerChatIds[vm.PeerKey] = id;
            return vm;
        }

        private ChatViewModel MapChatForList(JObject chat)
        {
            return MapChatForList(chat, int.MinValue);
        }

        private ChatViewModel MapChatForList(JObject chat, int folderId)
        {
            var vm = MapChat(chat);
            if (vm == null || chat == null) return vm;
            if (folderId != int.MinValue)
            {
                vm.FolderId = folderId;
                if (folderId == ArchiveFolderId) vm.IsArchived = true;
                vm.IsPinned = ReadPinned(chat["positions"] as JArray, folderId);
            }
            ApplyCachedPeerInfo(vm, chat);
            FillPhotoForList(vm, chat["photo"] as JObject);
            return vm;
        }

        private bool ReadEffectiveChatMuted(JObject chat, string peerType, bool isGroup, bool isBroadcast, bool isChannel)
        {
            var settings = chat == null ? null : chat["notification_settings"] as JObject;
            if (settings == null) return false;

            var muteFor = ReadInt(settings["mute_for"]);
            if (!ReadBool(settings["use_default_mute_for"]))
                return muteFor > 0;

            var scopeKey = GetNotificationScopeKey(peerType, isGroup, isBroadcast, isChannel);
            lock (_syncRoot)
            {
                int scopeMuteFor;
                if (_scopeMuteFor.TryGetValue(scopeKey, out scopeMuteFor)) return scopeMuteFor > 0;
                return !_notificationScopesLoaded;
            }
        }

        private bool ReadEffectiveTopicMuted(JObject topic, ChatViewModel parent)
        {
            var settings = topic == null ? null : topic["notification_settings"] as JObject;
            if (settings == null) return parent != null && parent.IsMuted;

            var muteFor = ReadInt(settings["mute_for"]);
            if (!ReadBool(settings["use_default_mute_for"]))
                return muteFor > 0;

            return parent != null && parent.IsMuted;
        }

        private static string GetNotificationScopeKey(string peerType, bool isGroup, bool isBroadcast, bool isChannel)
        {
            if (isBroadcast || (isChannel && !isGroup)) return "channel";
            if (isGroup || peerType == "chat" || peerType == "channel") return "group";
            return "private";
        }

        private async Task<ChatViewModel> MapChatAsync(JObject chat)
        {
            var vm = MapChat(chat);
            if (vm == null || chat == null) return vm;
            await EnrichChatAsync(vm, chat);
            await FillPhotoAsync(vm, chat["photo"] as JObject);
            return vm;
        }

        private void ApplyCachedPeerInfo(ChatViewModel vm, JObject chat)
        {
            var type = chat["type"] as JObject;
            var typeName = ReadString(type == null ? null : type["@type"], "");
            JObject value;

            if (typeName == "chatTypePrivate")
            {
                var userId = ReadLong(type["user_id"]);
                lock (_syncRoot) _users.TryGetValue(userId, out value);
                if ((_selfUserId != 0 && userId == _selfUserId) || ReadBool(value == null ? null : value["is_self"]))
                {
                    _selfUserId = userId;
                    vm.PeerType = "self";
                    vm.PeerKey = "self:" + vm.PeerId.ToString();
                    lock (_syncRoot) _peerChatIds[vm.PeerKey] = vm.PeerId;
                }
                ApplyUser(vm, value);
            }
            else if (typeName == "chatTypeSupergroup")
            {
                lock (_syncRoot) _supergroups.TryGetValue(ReadLong(type["supergroup_id"]), out value);
                ApplySupergroup(vm, value);
            }
            else if (typeName == "chatTypeBasicGroup")
            {
                lock (_syncRoot) _basicGroups.TryGetValue(ReadLong(type["basic_group_id"]), out value);
                ApplyBasicGroup(vm, value);
            }
        }

        private async Task EnrichChatAsync(ChatViewModel vm, JObject chat)
        {
            var type = chat["type"] as JObject;
            var typeName = ReadString(type == null ? null : type["@type"], "");

            if (typeName == "chatTypePrivate")
            {
                var userId = ReadLong(type["user_id"]);
                var user = await GetUserAsync(userId);
                ApplyUser(vm, user);
                var full = await GetUserFullInfoAsync(userId);
                ApplyUserFullInfo(vm, full);
            }
            else if (typeName == "chatTypeSupergroup")
            {
                var supergroupId = ReadLong(type["supergroup_id"]);
                var group = await GetSupergroupAsync(supergroupId);
                ApplySupergroup(vm, group);
                var full = await GetSupergroupFullInfoAsync(supergroupId);
                ApplySupergroupFullInfo(vm, full);
            }
            else if (typeName == "chatTypeBasicGroup")
            {
                var basicGroupId = ReadLong(type["basic_group_id"]);
                var group = await GetBasicGroupAsync(basicGroupId);
                ApplyBasicGroup(vm, group);
                var full = await GetBasicGroupFullInfoAsync(basicGroupId);
                ApplyBasicGroupFullInfo(vm, full);
            }
        }

        private ChatViewModel MapUserToChat(JObject user, bool self)
        {
            if (user == null) return null;
            var id = ReadLong(user["id"]);
            var name = (ReadString(user["first_name"], "") + " " + ReadString(user["last_name"], "")).Trim();
            if (string.IsNullOrEmpty(name)) name = ReadString(user["username"], "User");
            return new ChatViewModel
            {
                PeerId = id,
                PeerType = self ? "self" : "user",
                PeerKey = (self ? "self" : "user") + ":" + id.ToString(),
                Title = name,
                Username = ReadUsername(user),
                Phone = ReadString(user["phone_number"], ""),
                IsContact = true,
                CanSendMessages = true,
                IconText = BuildInitials(name),
                TypeIcon = "user",
                TypeGlyph = "\uE77B"
            };
        }

        private async Task<ChatViewModel> MapUserToChatAsync(JObject user, bool self)
        {
            var chat = MapUserToChat(user, self);
            if (chat == null) return null;
            ApplyUser(chat, user);
            var full = await GetUserFullInfoAsync(chat.PeerId);
            ApplyUserFullInfo(chat, full);
            await FillPhotoAsync(chat, user == null ? null : user["profile_photo"] as JObject);
            return chat;
        }

        private void ApplyUser(ChatViewModel vm, JObject user)
        {
            if (vm == null || user == null) return;
            var name = (ReadString(user["first_name"], "") + " " + ReadString(user["last_name"], "")).Trim();
            if (!string.IsNullOrEmpty(name))
            {
                vm.Title = name;
                vm.IconText = BuildInitials(name);
            }
            vm.Username = ReadUsername(user);
            vm.Phone = ReadString(user["phone_number"], vm.Phone ?? "");
            vm.IsContact = ReadBool(user["is_contact"]) || vm.IsContact;
            vm.IsBot = ReadString(user["type"] == null ? null : user["type"]["@type"], "") == "userTypeBot";
            if (vm.IsBot) vm.BotUserId = ReadLong(user["id"]);
            ApplyUserStatus(vm, user["status"] as JObject);
        }

        private void ApplyUserFullInfo(ChatViewModel vm, JObject full)
        {
            if (vm == null || full == null) return;
            vm.Bio = ReadFormattedTextToken(full["bio"], vm.Bio ?? "");
            if (string.IsNullOrEmpty(vm.Bio))
                vm.Bio = ReadString(full["description"], "");

            vm.Birthdate = FormatBirthdate(full["birthdate"] as JObject);

            var botInfo = full["bot_info"] as JObject;
            if (botInfo == null) return;
            vm.BotDescription = ReadString(botInfo["description"], vm.BotDescription ?? "");
            vm.BotCommands = new List<BotCommandViewModel>();
            var commands = botInfo["commands"] as JArray;
            if (commands != null)
            {
                foreach (var token in commands)
                {
                    var command = token as JObject;
                    if (command == null) continue;
                    var name = ReadString(command["command"], "");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    vm.BotCommands.Add(new BotCommandViewModel
                    {
                        Command = name,
                        Description = ReadString(command["description"], "")
                    });
                }
            }

            var menu = botInfo["menu_button"] as JObject;
            vm.BotMenuButtonType = ReadString(menu == null ? null : menu["@type"], "");
            vm.BotMenuButtonText = ReadString(menu == null ? null : menu["text"], "");
            vm.BotMenuButtonUrl = ReadString(menu == null ? null : menu["url"], "");
        }

        private void ApplySupergroup(ChatViewModel vm, JObject group)
        {
            if (vm == null || group == null) return;
            vm.Username = ReadUsername(group);
            vm.IsBroadcast = ReadBool(group["is_channel"]);
            vm.IsChannel = true;
            vm.IsGroup = !vm.IsBroadcast;
            vm.IsForum = ReadBool(group["is_forum"]);
            vm.SubscriberCount = Math.Max(vm.SubscriberCount, ReadInt(group["member_count"]));
            vm.IsJoined = true;
            ApplySupergroupStatusPermissions(vm, group["status"] as JObject);
        }

        private void ApplySupergroupFullInfo(ChatViewModel vm, JObject full)
        {
            if (vm == null || full == null) return;
            vm.Bio = ReadFormattedTextToken(full["description"], vm.Bio ?? "");
            vm.SubscriberCount = Math.Max(vm.SubscriberCount, ReadInt(full["member_count"]));
            if (ReadBool(full["can_send_messages"]) || ReadBool(full["can_post_messages"]))
                vm.CanSendMessages = true;
        }

        private static void ApplySupergroupStatusPermissions(ChatViewModel vm, JObject status)
        {
            if (vm == null || status == null) return;
            var type = ReadString(status["@type"], "");
            if (type == "chatMemberStatusCreator")
            {
                vm.CanSendMessages = true;
                vm.IsJoined = true;
                return;
            }

            if (type == "chatMemberStatusAdministrator")
            {
                var rights = status["rights"] as JObject;
                if (rights == null ||
                    ReadBool(rights["can_post_messages"]) ||
                    ReadBool(rights["can_send_messages"]) ||
                    ReadBool(rights["can_manage_chat"]) ||
                    !vm.IsBroadcast)
                {
                    vm.CanSendMessages = true;
                }
                vm.IsJoined = true;
                return;
            }

            if (type == "chatMemberStatusRestricted")
            {
                var permissions = status["permissions"] as JObject;
                vm.CanSendMessages = ReadBool(permissions == null ? null : permissions["can_send_basic_messages"]);
                vm.IsJoined = true;
                return;
            }

            if (type == "chatMemberStatusLeft" || type == "chatMemberStatusBanned")
            {
                vm.CanSendMessages = false;
                vm.IsJoined = false;
                vm.CanJoin = true;
            }
        }

        private void ApplyBasicGroup(ChatViewModel vm, JObject group)
        {
            if (vm == null || group == null) return;
            vm.IsGroup = true;
            vm.IsChannel = false;
            vm.IsBroadcast = false;
            vm.SubscriberCount = Math.Max(vm.SubscriberCount, ReadInt(group["member_count"]));
            vm.IsJoined = true;
        }

        private void ApplyBasicGroupFullInfo(ChatViewModel vm, JObject full)
        {
            if (vm == null || full == null) return;
            vm.Bio = ReadFormattedTextToken(full["description"], vm.Bio ?? "");
            var members = full["members"] as JArray;
            if (members != null) vm.SubscriberCount = Math.Max(vm.SubscriberCount, members.Count);
        }

        private static void ApplyUserStatus(ChatViewModel vm, JObject status)
        {
            if (vm == null || status == null) return;
            var type = ReadString(status["@type"], "");
            vm.UserStatusKind = type.Replace("userStatus", "").ToLowerInvariant();
            if (type == "userStatusOnline")
            {
                vm.LastSeenText = "online";
                vm.LastSeenUnixTime = ReadInt(status["expires"]);
            }
            else if (type == "userStatusOffline")
            {
                vm.LastSeenUnixTime = ReadInt(status["was_online"]);
                vm.LastSeenText = "";
            }
            else if (type == "userStatusRecently")
                vm.LastSeenText = "last seen recently";
            else if (type == "userStatusLastWeek")
                vm.LastSeenText = "last seen within a week";
            else if (type == "userStatusLastMonth")
                vm.LastSeenText = "last seen within a month";
        }

        public void ApplyCachedUserStatus(long userId, ChatViewModel vm)
        {
            if (userId == 0 || vm == null) return;
            JObject user;
            lock (_syncRoot)
            {
                if (!_users.TryGetValue(userId, out user) || user == null) return;
            }
            ApplyUserStatus(vm, user["status"] as JObject);
        }

        private ChatViewModel MapTopic(ChatViewModel parent, JObject topic)
        {
            var info = topic["info"] as JObject ?? topic;
            var id = ReadInt(info["forum_topic_id"]);
            var title = ReadString(info["name"], "Topic");
            var lastMessage = topic["last_message"] as JObject;
            var icon = info["icon"] as JObject;
            return new ChatViewModel
            {
                PeerId = parent.PeerId,
                PeerType = parent.PeerType,
                PeerKey = parent.PeerKey + "/topic/" + id.ToString(),
                Title = title,
                LastMessage = ReadLastMessage(lastMessage),
                LastMessageSenderName = ReadSenderName(lastMessage),
                LastMessageDate = ReadInt(lastMessage == null ? null : lastMessage["date"]),
                LastMessageIsOutgoing = ReadBool(lastMessage == null ? null : lastMessage["is_outgoing"]),
                UnreadCount = ReadInt(topic["unread_count"]),
                TopMessageId = CompactMessageId(parent.PeerId, ReadLong(lastMessage == null ? null : lastMessage["id"])),
                ReadOutboxMaxId = CompactMessageId(parent.PeerId, Math.Max(ReadLong(topic["last_read_inbox_message_id"]), ReadLong(topic["last_read_outbox_message_id"]))),
                IsMuted = ReadEffectiveTopicMuted(topic, parent),
                IsGroup = parent.IsGroup || parent.PeerType == "chat" || (parent.PeerType == "channel" && !parent.IsBroadcast),
                IsChannel = parent.IsChannel || parent.PeerType == "channel",
                IsBroadcast = false,
                IsForum = true,
                IsForumTopic = true,
                TopicId = id,
                TopicRootMessageId = id,
                TopicIconColor = ReadInt(icon == null ? null : icon["color"]),
                TopicIconEmojiId = ReadLong(icon == null ? null : icon["custom_emoji_id"]),
                IsTopicClosed = ReadBool(info["is_closed"]),
                ParentPeerType = parent.PeerType,
                ParentPeerId = parent.PeerId,
                ParentPeerKey = parent.PeerKey,
                ParentAccessHash = parent.AccessHash,
                ParentTitle = parent.Title,
                CanSendMessages = parent.CanSendMessages,
                IconText = BuildInitials(title),
                TypeIcon = "topic",
                TypeGlyph = "\uE8F1"
            };
        }

        private ChatMessageViewModel MapMessage(ChatViewModel peer, JObject message)
        {
            if (message == null) return null;
            var chatId = ReadLong(message["chat_id"]);
            if (chatId == 0 && peer != null) chatId = ResolveChatId(peer);
            var tdId = ReadLong(message["id"]);
            var id = CompactMessageId(chatId, tdId);
            var content = message["content"] as JObject;
            var sender = message["sender_id"] as JObject;
            var vm = new ChatMessageViewModel
            {
                Id = id,
                SortId = tdId != 0 ? tdId : id,
                Date = ReadInt(message["date"]),
                EditDate = ReadInt(message["edit_date"]),
                IsOutgoing = ReadBool(message["is_outgoing"]),
                IsSending = message["sending_state"] != null,
                IsGroupChat = IsGroupLikeMessagePeer(peer),
                IsChannelPost = peer != null && peer.IsBroadcast,
                PostAuthor = ReadString(message["author_signature"], ""),
                Text = ReadMessageText(content),
                IsPinned = ReadBool(message["is_pinned"]),
                GroupedId = ReadLong(message["media_album_id"])
            };

            vm.SetTextEntities(ReadMessageTextEntities(content));
            FillSender(vm, sender);
            FillMedia(vm, content);
            FillReplyAndForwardInfo(vm, message, chatId);
            FillMessageInteraction(vm, message);
            FillReplyMarkup(vm, message["reply_markup"] as JObject);
            FillServiceMessage(vm, content, peer);
            ApplyMessageActionPermissions(vm, message, peer);
            return vm;
        }

        private static bool IsGroupLikeMessagePeer(ChatViewModel peer)
        {
            if (peer == null) return false;
            if (peer.IsGroup || peer.IsForumTopic || peer.IsCommentsThread) return true;
            if (peer.PeerType == "chat") return true;
            if (peer.PeerType == "channel" && !peer.IsBroadcast) return true;
            return false;
        }

        private static void ApplyMessageActionPermissions(ChatMessageViewModel vm, JObject message, ChatViewModel peer)
        {
            if (vm == null) return;

            var hasRealMessageId = vm.Id > 0 && (vm.SortId > 0 || message != null);
            var isService = vm.IsServiceMessage;
            var hasProtectedContent = ReadBool(message == null ? null : message["has_protected_content"]) ||
                                      (peer != null && peer.NoForwards);

            var canSend = peer == null || peer.CanSendMessages;
            var canPinInChat = peer != null && peer.CanPinMessages;
            var canDeleteInChat = peer == null || peer.CanDeleteMessages;
            var canPinToken = message == null ? null : message["can_be_pinned"];
            var canGetViewersToken = message == null ? null : message["can_get_viewers"];
            var canReactToken = message == null ? null :
                (message["can_get_added_reactions"] ?? message["can_be_reacted"] ?? message["can_be_reacted_to"]);

            vm.CanReply = hasRealMessageId && !isService && canSend &&
                          ReadBoolDefault(message == null ? null : message["can_be_replied"], true);
            vm.CanPin = hasRealMessageId && !isService &&
                        (canPinInChat || canPinToken != null) &&
                        ReadBoolDefault(canPinToken, canPinInChat);
            vm.CanForward = hasRealMessageId && !isService && !hasProtectedContent &&
                            ReadBoolDefault(message == null ? null : message["can_be_forwarded"], true);
            vm.CanDelete = hasRealMessageId && !isService &&
                           (ReadBool(message == null ? null : message["can_be_deleted_for_all_users"]) ||
                            ReadBool(message == null ? null : message["can_be_deleted_only_for_self"]) ||
                            canDeleteInChat ||
                            vm.IsOutgoing);
            vm.CanReact = hasRealMessageId && !isService && ReadBoolDefault(canReactToken, true);
            vm.HasCanGetViewersFlag = canGetViewersToken != null;
            vm.CanGetViewers = hasRealMessageId && !isService && ReadBool(canGetViewersToken);
        }

        private static int CompareMessagesBySortIdAscending(ChatMessageViewModel a, ChatMessageViewModel b)
        {
            var left = a == null ? 0 : a.SortId != 0 ? a.SortId : a.Id;
            var right = b == null ? 0 : b.SortId != 0 ? b.SortId : b.Id;
            return left.CompareTo(right);
        }

        private static int CompareMessagesBySortIdDescending(ChatMessageViewModel a, ChatMessageViewModel b)
        {
            return CompareMessagesBySortIdAscending(b, a);
        }

        private void FillServiceMessage(ChatMessageViewModel vm, JObject content, ChatViewModel peer)
        {
            if (vm == null || content == null) return;
            var text = BuildServiceMessageText(vm, content, peer);
            if (string.IsNullOrWhiteSpace(text)) return;

            vm.IsServiceMessage = true;
            vm.ServiceActionText = text;
            vm.Text = "";
        }

        private string BuildServiceMessageText(ChatMessageViewModel vm, JObject content, ChatViewModel peer)
        {
            if (content == null) return "";
            var type = ReadString(content["@type"], "");
            var senderName = vm.PostAuthor;
            if (string.IsNullOrEmpty(senderName)) senderName = vm.SenderName;
            if (string.IsNullOrEmpty(senderName) && vm.IsOutgoing) senderName = "You";
            if (string.IsNullOrEmpty(senderName) && peer != null && peer.IsGroup)
                senderName = "Someone";
            if (string.IsNullOrEmpty(senderName)) senderName = "Someone";

            switch (type)
            {
                case "messageActionChatAddMember":
                    var addedUserId = ReadLong(content["user_id"]);
                    return (addedUserId > 0 ? ResolveUserName(addedUserId) : senderName) + " joined the group";
                case "messageActionChatDeleteMember":
                    var removedUserId = ReadLong(content["user_id"]);
                    return (removedUserId > 0 ? ResolveUserName(removedUserId) : senderName) + " left the group";
                case "messageActionChatCreate":
                    var title = ReadString(content["title"], "");
                    return senderName + " created the group" + (string.IsNullOrEmpty(title) ? "" : " \"" + title + "\"");
                case "messageActionChatRename":
                    var newTitle = ReadString(content["new_title"], "");
                    return senderName + " changed the group name to \"" + newTitle + "\"";
                case "messageActionChatChangePhoto":
                    return senderName + " changed the group photo";
                case "messageActionChatSetTtl":
                    var ttl = ReadInt(content["ttl"]);
                    return "Messages auto-delete set to " + FormatAutoDeleteTime(ttl);
                case "messageActionChatUpgradeTo":
                    return "Group was upgraded to a supergroup";
                case "messageActionChatUpgradeFrom":
                    return "This group was migrated from a basic group";
                case "messageActionMessageTtl":
                    var msgTtl = ReadInt(content["ttl"]);
                    return "Messages auto-delete timer set to " + FormatAutoDeleteTime(msgTtl);
                case "messageActionPinMessage":
                    return senderName + " pinned a message";
                case "messageActionHistoryClear":
                    return "Chat history was cleared";
                case "messageActionScreenshotTaken":
                    return senderName + " took a screenshot";
                case "messageActionContactSignUp":
                    return senderName + " joined Telegram";
                case "messageActionTopicCreate":
                    var topicTitle = ReadString(content["title"], "");
                    return senderName + " created the topic \"" + topicTitle + "\"";
                case "messageActionTopicEdit":
                    var editTitle = ReadString(content["title"], "");
                    return senderName + " edited the topic" + (string.IsNullOrEmpty(editTitle) ? "" : " \"" + editTitle + "\"");
                case "messageExpiredPhoto":
                    return "Photo expired";
                case "messageExpiredVideo":
                    return "Video expired";
                case "messageExpiredVideoNote":
                    return "Video message expired";
                case "messageExpiredVoiceNote":
                    return "Voice message expired";
                case "messageCall":
                    return BuildCallServiceText(content);
                case "messageGroupCall":
                    return BuildGroupCallServiceText(content);
                case "messageVideoChatScheduled":
                    return "Video chat scheduled" + FormatOptionalDate(ReadInt(content["start_date"]));
                case "messageVideoChatStarted":
                    return "Video chat started";
                case "messageVideoChatEnded":
                    return "Video chat ended" + FormatOptionalDuration(ReadInt(content["duration"]));
                case "messageInviteVideoChatParticipants":
                    return senderName + " invited " + FormatUserNames(content["user_ids"] as JArray, 3) + " to the video chat";
                case "messagePollOptionAdded":
                    return senderName + " added poll option \"" + ReadFormattedTextToken(content["text"], "Option") + "\"";
                case "messagePollOptionDeleted":
                    return senderName + " removed poll option \"" + ReadFormattedTextToken(content["text"], "Option") + "\"";
                case "messageBasicGroupChatCreate":
                    return senderName + " created the group \"" + ReadString(content["title"], "Group") + "\"";
                case "messageSupergroupChatCreate":
                    return senderName + " created the group \"" + ReadString(content["title"], "Group") + "\"";
                case "messageChatChangeTitle":
                    return senderName + " changed the chat title to \"" + ReadString(content["title"], "Chat") + "\"";
                case "messageChatChangePhoto":
                    return senderName + " changed the chat photo";
                case "messageChatDeletePhoto":
                    return senderName + " removed the chat photo";
                case "messageChatOwnerLeft":
                    return "Owner left the chat";
                case "messageChatOwnerChanged":
                    var ownerId = ReadLong(content["new_owner_user_id"]);
                    return "Chat owner changed to " + (ownerId > 0 ? ResolveUserName(ownerId) : "Someone");
                case "messageChatHasProtectedContentToggled":
                    return ReadBool(content["new_has_protected_content"]) ? "Content saving was restricted" : "Content saving restriction was removed";
                case "messageChatHasProtectedContentDisableRequested":
                    return ReadBool(content["is_expired"]) ? "Content protection request expired" : "Content protection disable requested";
                case "messageChatAddMembers":
                    return FormatUserNames(content["member_user_ids"] as JArray, 4) + " joined the group";
                case "messageChatJoinByLink":
                    return senderName + " joined via invite link";
                case "messageChatJoinByRequest":
                    return senderName + " joined by request";
                case "messageChatDeleteMember":
                    var userId = ReadLong(content["user_id"]);
                    return (userId > 0 ? ResolveUserName(userId) : senderName) + " left the group";
                case "messageChatAddedToCommunity":
                    return "Chat was added to a community";
                case "messageChatRemovedFromCommunity":
                    return "Chat was removed from the community";
                case "messageChatUpgradeTo":
                    return "Group was upgraded to a supergroup";
                case "messageChatUpgradeFrom":
                    return "This group was migrated from \"" + ReadString(content["title"], "a basic group") + "\"";
                case "messagePinMessage":
                    return senderName + " pinned a message";
                case "messageScreenshotTaken":
                    return senderName + " took a screenshot";
                case "messageChatSetBackground":
                    return senderName + " changed the chat background";
                case "messageChatSetTheme":
                    return senderName + " changed the chat theme";
                case "messageChatSetMessageAutoDeleteTime":
                    return "Messages auto-delete timer set to " + FormatAutoDeleteTime(ReadInt(content["message_auto_delete_time"]));
                case "messageChatBoost":
                    return "Chat boosted " + ReadLong(content["boost_count"]).ToString() + " times";
                case "messageForumTopicCreated":
                    return "Topic \"" + ReadString(content["name"], "Topic") + "\" created";
                case "messageForumTopicEdited":
                    return string.IsNullOrEmpty(ReadString(content["name"], "")) ? "Topic edited" : "Topic renamed to \"" + ReadString(content["name"], "Topic") + "\"";
                case "messageForumTopicIsClosedToggled":
                    return ReadBool(content["is_closed"]) ? "Topic closed" : "Topic reopened";
                case "messageForumTopicIsHiddenToggled":
                    return ReadBool(content["is_hidden"]) ? "Topic hidden" : "Topic shown";
                case "messageSuggestProfilePhoto":
                    return senderName + " suggested a profile photo";
                case "messageSuggestBirthdate":
                    return senderName + " suggested a birthdate";
                case "messageCustomServiceAction":
                    return ReadString(content["text"], "");
                case "messageGameScore":
                    return senderName + " scored " + ReadLong(content["score"]).ToString() + " in a game";
                case "messageManagedBotCreated":
                    return "Bot created";
                case "messagePaymentSuccessful":
                    return "Payment successful" + FormatServiceAmount(content);
                case "messagePaymentSuccessfulBot":
                    return "Payment successful" + FormatServiceAmount(content);
                case "messagePaymentRefunded":
                    return "Payment refunded" + FormatServiceAmount(content);
                case "messageGiftedPremium":
                    return "Telegram Premium gifted";
                case "messagePremiumGiftCode":
                    return "Premium gift code";
                case "messageGiveawayCreated":
                    return "Giveaway created";
                case "messageGiveaway":
                    return "Giveaway";
                case "messageGiveawayCompleted":
                    return "Giveaway completed";
                case "messageGiveawayWinners":
                    return "Giveaway winners announced";
                case "messageGiftedStars":
                    return "Telegram Stars gifted";
                case "messageGiftedTon":
                    return "TON gifted";
                case "messageGiveawayPrizeStars":
                    return "Giveaway prize received";
                case "messageGift":
                    return "Gift received";
                case "messageUpgradedGift":
                    return "Gift upgraded";
                case "messageRefundedUpgradedGift":
                    return "Upgraded gift refunded";
                case "messageUpgradedGiftPurchaseOffer":
                    return "Gift purchase offer";
                case "messageUpgradedGiftPurchaseOfferRejected":
                    return "Gift purchase offer rejected";
                case "messagePaidMessagesRefunded":
                    return ReadLong(content["message_count"]).ToString() + " paid messages refunded";
                case "messagePaidMessagePriceChanged":
                    return "Paid message price changed";
                case "messageDirectMessagePriceChanged":
                    return ReadBool(content["is_enabled"]) ? "Paid direct messages enabled" : "Paid direct messages disabled";
                case "messageChecklistTasksDone":
                    return "Checklist tasks updated";
                case "messageChecklistTasksAdded":
                    return "Checklist tasks added";
                case "messageSuggestedPostApprovalFailed":
                    return "Suggested post approval failed";
                case "messageSuggestedPostApproved":
                    return "Suggested post approved";
                case "messageSuggestedPostDeclined":
                    return "Suggested post declined";
                case "messageSuggestedPostPaid":
                    return "Suggested post paid";
                case "messageSuggestedPostRefunded":
                    return "Suggested post refunded";
                case "messageContactRegistered":
                    return senderName + " joined Telegram";
                case "messageUsersShared":
                    return "Users shared";
                case "messageChatShared":
                    return "Chat shared";
                case "messageBotWriteAccessAllowed":
                    return "Bot write access allowed";
                case "messageWebAppDataSent":
                    return "Web app data sent";
                case "messageWebAppDataReceived":
                    return "Web app data received";
                case "messagePassportDataSent":
                    return "Passport data sent";
                case "messagePassportDataReceived":
                    return "Passport data received";
                case "messageProximityAlertTriggered":
                    return "Proximity alert triggered";
                case "messageUnsupported":
                    return "Unsupported message";
            }

            if (IsSystemMessageContentType(type))
                return BuildGenericServicePreviewText(content);
            return "";
        }

        private string BuildCallServiceText(JObject content)
        {
            if (content == null) return "Call";
            var missed = ReadString(content["discard_reason"] == null ? null : content["discard_reason"]["@type"], "") == "callDiscardReasonMissed";
            var video = ReadBool(content["is_video"]);
            var text = missed ? "Missed " : "";
            text += video ? "video call" : "call";
            text += FormatOptionalDuration(ReadInt(content["duration"]));
            return text;
        }

        private string BuildGroupCallServiceText(JObject content)
        {
            if (content == null) return "Group call";
            var missed = ReadBool(content["was_missed"]);
            var active = ReadBool(content["is_active"]);
            var video = ReadBool(content["is_video"]);
            var text = missed ? "Missed " : "";
            text += video ? "group video call" : "group call";
            if (active) text += " started";
            text += FormatOptionalDuration(ReadInt(content["duration"]));
            return text;
        }

        private string FormatUserNames(JArray userIds, int limit)
        {
            if (userIds == null || userIds.Count == 0) return "Someone";
            var names = new List<string>();
            for (var i = 0; i < userIds.Count && names.Count < limit; i++)
            {
                var userId = ReadLong(userIds[i]);
                if (userId > 0) names.Add(ResolveUserName(userId));
            }
            if (names.Count == 0) return "Someone";
            var text = string.Join(", ", names);
            if (userIds.Count > names.Count)
                text += " +" + (userIds.Count - names.Count).ToString();
            return text;
        }

        private static string FormatOptionalDate(int unixTime)
        {
            var text = FormatShortDate(unixTime);
            return string.IsNullOrEmpty(text) ? "" : " on " + text;
        }

        private static string FormatOptionalDuration(int seconds)
        {
            return seconds <= 0 ? "" : " (" + FormatRelativeDuration(seconds) + ")";
        }

        private static string FormatAutoDeleteTime(int seconds)
        {
            if (seconds <= 0) return "off";
            return FormatRelativeDuration(seconds);
        }

        private static string FormatServiceAmount(JObject content)
        {
            if (content == null) return "";
            var currency = ReadString(content["currency"], "");
            var amount = ReadLong(content["total_amount"]);
            if (amount <= 0 || string.IsNullOrEmpty(currency)) return "";
            return ": " + amount.ToString() + " " + currency;
        }

        private static bool IsSystemMessageContentType(string type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            if (type.StartsWith("messageAction", StringComparison.Ordinal)) return true;
            if (type.StartsWith("messageChat", StringComparison.Ordinal)) return true;
            if (type.StartsWith("messageForumTopic", StringComparison.Ordinal)) return true;
            if (type.StartsWith("messageVideoChat", StringComparison.Ordinal)) return true;
            if (type.StartsWith("messagePollOption", StringComparison.Ordinal)) return true;
            if (type.StartsWith("messagePayment", StringComparison.Ordinal)) return true;
            if (type.StartsWith("messageGift", StringComparison.Ordinal)) return true;
            if (type.StartsWith("messageGiveaway", StringComparison.Ordinal)) return true;
            if (type.StartsWith("messageUpgradedGift", StringComparison.Ordinal)) return true;
            if (type.StartsWith("messageSuggestedPost", StringComparison.Ordinal)) return true;
            if (type.StartsWith("messageChecklistTasks", StringComparison.Ordinal)) return true;
            switch (type)
            {
                case "messageExpiredPhoto":
                case "messageExpiredVideo":
                case "messageExpiredVideoNote":
                case "messageExpiredVoiceNote":
                case "messageCall":
                case "messageGroupCall":
                case "messageInviteVideoChatParticipants":
                case "messageBasicGroupChatCreate":
                case "messageSupergroupChatCreate":
                case "messagePinMessage":
                case "messageScreenshotTaken":
                case "messageSuggestProfilePhoto":
                case "messageSuggestBirthdate":
                case "messageCustomServiceAction":
                case "messageGameScore":
                case "messageManagedBotCreated":
                case "messagePremiumGiftCode":
                case "messagePaidMessagesRefunded":
                case "messagePaidMessagePriceChanged":
                case "messageDirectMessagePriceChanged":
                case "messageContactRegistered":
                case "messageUsersShared":
                case "messageChatShared":
                case "messageBotWriteAccessAllowed":
                case "messageWebAppDataSent":
                case "messageWebAppDataReceived":
                case "messagePassportDataSent":
                case "messagePassportDataReceived":
                case "messageProximityAlertTriggered":
                case "messageUnsupported":
                    return true;
            }
            return false;
        }

        private static string BuildGenericServicePreviewText(JObject content)
        {
            if (content == null) return "Service message";
            var type = ReadString(content["@type"], "");
            if (type.StartsWith("message", StringComparison.Ordinal))
                type = type.Substring("message".Length);
            if (string.IsNullOrEmpty(type)) return "Service message";

            var words = new List<char>();
            for (var i = 0; i < type.Length; i++)
            {
                var ch = type[i];
                if (i > 0 && char.IsUpper(ch)) words.Add(' ');
                words.Add(ch);
            }
            return new string(words.ToArray()).Trim();
        }

        private string ResolveUserName(long userId)
        {
            lock (_syncRoot)
            {
                JObject user;
                if (_users.TryGetValue(userId, out user) && user != null)
                {
                    var firstName = ReadString(user["first_name"], "");
                    var lastName = ReadString(user["last_name"], "");
                    var name = (firstName + " " + lastName).Trim();
                    if (!string.IsNullOrEmpty(name)) return name;
                    var username = ReadString(user["username"], "");
                    if (!string.IsNullOrEmpty(username)) return "@" + username;
                }
            }
            return "Someone";
        }

        private void FillReplyMarkup(ChatMessageViewModel vm, JObject markup)
        {
            if (vm == null || markup == null) return;
            var type = ReadString(markup["@type"], "");
            if (type == "replyMarkupRemoveKeyboard")
            {
                vm.RemovesReplyKeyboard = true;
                return;
            }
            var rows = markup["rows"] as JArray;
            if (rows == null) return;

            if (type == "replyMarkupInlineKeyboard")
                FillBotKeyboardRows(vm.InlineKeyboardRows, rows, vm.Id, true);
            else if (type == "replyMarkupShowKeyboard")
            {
                FillBotKeyboardRows(vm.ReplyKeyboardRows, rows, vm.Id, false);
                vm.ReplyKeyboardOneTime = ReadBool(markup["one_time"]);
                vm.ReplyKeyboardPersistent = ReadBool(markup["is_persistent"]);
                vm.ReplyKeyboardPlaceholder = ReadString(markup["input_field_placeholder"], "");
            }
        }

        private void FillBotKeyboardRows(System.Collections.ObjectModel.ObservableCollection<BotKeyboardRowViewModel> target, JArray rows, int messageId, bool inline)
        {
            if (target == null || rows == null) return;
            foreach (var rowToken in rows)
            {
                var rowArray = rowToken as JArray;
                if (rowArray == null) continue;
                var row = new BotKeyboardRowViewModel();
                foreach (var buttonToken in rowArray)
                {
                    var button = buttonToken as JObject;
                    if (button == null) continue;
                    var buttonType = button["type"] as JObject;
                    var vm = new BotKeyboardButtonViewModel
                    {
                        MessageId = messageId,
                        Text = ReadString(button["text"], ""),
                        Type = ReadString(buttonType == null ? null : buttonType["@type"], "")
                    };
                    if (buttonType != null)
                    {
                        vm.Data = ReadString(buttonType["data"], "");
                        vm.Url = FirstNonEmpty(ReadString(buttonType["url"], ""), ReadString(buttonType["web_app_url"], ""));
                        vm.Query = FirstNonEmpty(ReadString(buttonType["query"], ""), ReadString(buttonType["text"], ""));
                        vm.UserId = ReadLong(buttonType["user_id"]);
                    }
                    if (!string.IsNullOrEmpty(vm.Text)) row.Buttons.Add(vm);
                }
                if (row.Buttons.Count > 0) target.Add(row);
            }
        }

        private void FillReplyAndForwardInfo(ChatMessageViewModel vm, JObject message, long messageChatId)
        {
            if (vm == null || message == null) return;

            // TDLib 1.8+ exposes reply information through message.reply_to.
            // Keep support for older snapshots that still expose reply_to_message_id.
            var replyTo = message["reply_to"] as JObject;
            if (replyTo != null)
            {
                var replyType = ReadString(replyTo["@type"], "");
                if (replyType == "messageReplyToMessage" || string.IsNullOrEmpty(replyType))
                {
                    var replyChatId = ReadLong(replyTo["chat_id"]);
                    if (replyChatId == 0) replyChatId = messageChatId;
                    var replyMessageId = ReadLong(replyTo["message_id"]);
                    if (replyMessageId != 0 && replyChatId == messageChatId)
                        vm.ReplyToMessageId = CompactMessageId(replyChatId, replyMessageId);

                    var quote = replyTo["quote"] as JObject;
                    if (quote != null)
                    {
                        vm.ReplyToText = FirstNonEmpty(ReadFormattedTextToken(quote["text"], ""), ReadFormattedText(quote["text"] as JObject, ""));
                        if (vm.ReplyToMessageId <= 0 && !string.IsNullOrWhiteSpace(vm.ReplyToText))
                            vm.ReplyToMessageId = CompactMessageId(messageChatId, ReadLong(replyTo["message_id"]));
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(vm.ReplyToText) && string.IsNullOrWhiteSpace(vm.ReplyToSenderName))
                vm.ReplyToSenderName = "Quote";

            if (vm.ReplyToMessageId <= 0)
            {
                var legacyReplyId = ReadLong(message["reply_to_message_id"]);
                if (legacyReplyId != 0)
                    vm.ReplyToMessageId = CompactMessageId(messageChatId, legacyReplyId);
            }

            var forwardInfo = message["forward_info"] as JObject;
            if (forwardInfo == null) return;
            var origin = forwardInfo["origin"] as JObject;
            if (origin == null) return;

            var originType = ReadString(origin["@type"], "");
            if (originType == "messageOriginUser")
            {
                var userId = ReadLong(origin["sender_user_id"]);
                JObject user;
                lock (_syncRoot) _users.TryGetValue(userId, out user);
                var name = user == null ? "User" : (ReadString(user["first_name"], "") + " " + ReadString(user["last_name"], "")).Trim();
                vm.ForwardedFrom = string.IsNullOrEmpty(name) ? "User" : name;
                vm.ForwardedInitials = BuildInitials(vm.ForwardedFrom);
                vm.ForwardedPeerType = "user";
                vm.ForwardedPeerId = userId;
                vm.ForwardedPeerKey = "user:" + userId.ToString();
            }
            else if (originType == "messageOriginChat")
            {
                var chatId = ReadLong(origin["sender_chat_id"]);
                JObject chat;
                lock (_syncRoot) _chats.TryGetValue(chatId, out chat);
                var title = ReadString(chat == null ? null : chat["title"], "Chat");
                var signature = ReadString(origin["author_signature"], "");
                vm.ForwardedFrom = string.IsNullOrEmpty(signature) ? title : title + " (" + signature + ")";
                vm.ForwardedInitials = BuildInitials(title);
                vm.ForwardedPeerType = "chat";
                vm.ForwardedPeerId = chatId;
                vm.ForwardedPeerKey = "chat:" + chatId.ToString();
            }
            else if (originType == "messageOriginChannel")
            {
                var chatId = ReadLong(origin["chat_id"]);
                JObject chat;
                lock (_syncRoot) _chats.TryGetValue(chatId, out chat);
                var title = ReadString(chat == null ? null : chat["title"], "Channel");
                var signature = ReadString(origin["author_signature"], "");
                vm.ForwardedFrom = string.IsNullOrEmpty(signature) ? title : title + " (" + signature + ")";
                vm.ForwardedInitials = BuildInitials(title);
                vm.ForwardedPeerType = "chat";
                vm.ForwardedPeerId = chatId;
                vm.ForwardedPeerKey = "chat:" + chatId.ToString();
            }
            else if (originType == "messageOriginHiddenUser")
            {
                vm.ForwardedFrom = ReadString(origin["sender_name"], "Forwarded message");
                vm.ForwardedInitials = BuildInitials(vm.ForwardedFrom);
            }
            else
            {
                // Graceful fallback for TDLib variants/new origin types.
                vm.ForwardedFrom = FirstNonEmpty(ReadString(origin["sender_name"], ""), ReadString(origin["author_signature"], ""), "Forwarded message");
                vm.ForwardedInitials = BuildInitials(vm.ForwardedFrom);
            }
        }

        private void FillMessageInteraction(ChatMessageViewModel vm, JObject message)
        {
            if (vm == null || message == null) return;
            var interaction = message["interaction_info"] as JObject;
            if (interaction == null) return;

            var replyInfo = interaction["reply_info"] as JObject;
            if (replyInfo != null && vm.IsChannelPost)
            {
                // reply_info is also present for ordinary reply threads.
                // Only channel posts may expose the Telegram comments UI.
                var replyCount = Math.Max(0, ReadInt(replyInfo["reply_count"]));
                var lastMessageId = ReadLong(replyInfo["last_message_id"]);
                var chatId = ReadLong(message["chat_id"]);

                vm.CommentsCount = replyCount;
                vm.CommentsMaxId = lastMessageId != 0
                    ? CompactMessageId(chatId, lastMessageId)
                    : vm.Id;
                vm.CommentsReadMaxId = CompactMessageId(
                    chatId,
                    Math.Max(
                        ReadLong(replyInfo["last_read_inbox_message_id"]),
                        ReadLong(replyInfo["last_read_outbox_message_id"])));

                vm.CanOpenComments = true;
                vm.CommentsDiscussionCanSend = true;
                vm.SetCommentAvatars(ReadCommentAvatars(replyInfo["recent_replier_ids"] as JArray));
            }
            else
            {
                vm.CanOpenComments = false;
                vm.CommentsDiscussionCanSend = false;
                vm.CommentsCount = 0;
                vm.CommentsMaxId = 0;
                vm.CommentsReadMaxId = 0;
            }

            var reactions = ReadMessageReactions(interaction);
            if (reactions.Count > 0) vm.SetReactions(reactions);
        }

        private List<MessageReactionViewModel> ReadMessageReactions(JObject interaction)
        {
            var result = new List<MessageReactionViewModel>();
            if (interaction == null) return result;

            var reactionsInfo = interaction["reactions"] as JObject;
            var reactions = reactionsInfo == null ? interaction["reactions"] as JArray : reactionsInfo["reactions"] as JArray;
            if (reactions == null) return result;

            foreach (var token in reactions.OfType<JObject>())
            {
                var reactionType = token["type"] as JObject;
                var type = ReadString(reactionType == null ? null : reactionType["@type"], "");
                var emoticon = "";
                var customEmojiId = 0L;
                if (type == "reactionTypeEmoji")
                    emoticon = ReadString(reactionType["emoji"], "");
                else if (type == "reactionTypeCustomEmoji")
                    customEmojiId = ReadLong(reactionType["custom_emoji_id"]);
                else if (type == "reactionTypePaid")
                    emoticon = "\u2B50";

                if (string.IsNullOrEmpty(emoticon) && customEmojiId == 0) continue;
                var count = ReadInt(token["total_count"]);
                if (count <= 0) count = ReadInt(token["count"]);
                if (count <= 0) continue;
                result.Add(new MessageReactionViewModel
                {
                    Emoticon = emoticon,
                    CustomEmojiDocumentId = customEmojiId,
                    CustomEmojiUri = customEmojiId == 0 ? "" : GetCachedCustomEmojiStickerUri(customEmojiId),
                    Count = count,
                    IsChosen = ReadBool(token["is_chosen"])
                });
            }
            return result;
        }

        private List<CommentAvatarViewModel> ReadCommentAvatars(JArray senders)
        {
            var result = new List<CommentAvatarViewModel>();
            if (senders == null) return result;

            foreach (var sender in senders.OfType<JObject>())
            {
                var avatar = ReadCommentAvatar(sender);
                if (avatar != null) result.Add(avatar);
                if (result.Count >= 3) break;
            }
            return result;
        }

        private CommentAvatarViewModel ReadCommentAvatar(JObject sender)
        {
            if (sender == null) return null;
            var type = ReadString(sender["@type"], "");
            if (type == "messageSenderUser")
            {
                var userId = ReadLong(sender["user_id"]);
                JObject user;
                lock (_syncRoot) _users.TryGetValue(userId, out user);
                var title = user == null ? "User" : (ReadString(user["first_name"], "") + " " + ReadString(user["last_name"], "")).Trim();
                if (string.IsNullOrWhiteSpace(title)) title = "User";
                return new CommentAvatarViewModel
                {
                    PeerType = "user",
                    PeerId = userId,
                    PeerKey = "user:" + userId.ToString(),
                    Title = title,
                    Initials = BuildInitials(title)
                };
            }
            if (type == "messageSenderChat")
            {
                var chatId = ReadLong(sender["chat_id"]);
                JObject chat;
                lock (_syncRoot) _chats.TryGetValue(chatId, out chat);
                var title = ReadString(chat == null ? null : chat["title"], "Chat");
                return new CommentAvatarViewModel
                {
                    PeerType = "chat",
                    PeerId = chatId,
                    PeerKey = "chat:" + chatId.ToString(),
                    Title = title,
                    Initials = BuildInitials(title)
                };
            }
            return null;
        }

        private void FillMedia(ChatMessageViewModel vm, JObject content)
        {
            if (content == null) return;
            var type = ReadString(content["@type"], "");
            JObject media = null;
            string kind = null;
            var stickerEmojiText = "";
            if (type == "messagePhoto") { media = content["photo"] as JObject; kind = "photo"; }
            else if (type == "messageVideo") { media = content["video"] as JObject; kind = "video"; }
            else if (type == "messageAnimation") { media = content["animation"] as JObject; kind = "gif"; }
            else if (type == "messageDocument") { media = content["document"] as JObject; kind = "document"; }
            else if (type == "messageSticker")
            {
                media = content["sticker"] as JObject;
                stickerEmojiText = ReadString(media == null ? null : media["emoji"], "");
                kind = "sticker";
            }
            else if (type == "messageAnimatedEmoji")
            {
                return;
            }
            else if (type == "messageAudio") { media = content["audio"] as JObject; kind = "audio"; }
            else if (type == "messageVoiceNote") { media = content["voice_note"] as JObject; kind = "voice"; }
            else if (type == "messageVideoNote") { media = content["video_note"] as JObject; kind = "roundvideo"; }
            else if (type == "messageLocation" || type == "messageVenue")
            {
                vm.HasMedia = true;
                vm.MediaKind = "location";
                vm.MediaTitle = "";
                vm.MediaFileName = "";
                vm.MediaFallbackUri = "ms-appx:///Assets/Maps/Map_Pin.png";
                vm.Text = "";
                return;
            }
            else if (type == "messagePoll") { FillPoll(vm, content["poll"] as JObject, content["description"] as JObject, ReadBool(content["can_add_option"])); return; }
            else if (type == "messageChecklist") { FillChecklist(vm, content["list"] as JObject); return; }

            if (media == null || kind == null) return;
            vm.HasMedia = true;
            vm.MediaKind = kind;
            if (kind == "sticker" && !string.IsNullOrEmpty(stickerEmojiText))
                vm.MediaFallbackUri = BuildStaticEmojiAssetUri(stickerEmojiText);
            var fileName = ReadString(media["file_name"], "");
            vm.MediaFileName = fileName;
            if (kind == "audio")
            {
                var title = ReadString(media["title"], "");
                var performer = ReadString(media["performer"], "");
                vm.MediaTitle = FirstNonEmpty(title, FileNameWithoutExtension(fileName), "Audio");
                vm.MediaPerformer = performer;
            }
            else
            {
                vm.MediaTitle = FirstNonEmpty(fileName, kind);
            }
            vm.MediaMimeType = ReadString(media["mime_type"], "");
            vm.MediaDurationSeconds = ReadInt(media["duration"]);
            if (kind == "photo")
            {
                var photoAspectRatio = ReadPhotoAspectRatio(media);
                if (photoAspectRatio > 0.1)
                    vm.SetMediaPreviewAspectRatio(photoAspectRatio);
            }
            if (kind == "video" || kind == "gif")
            {
                var mediaWidth = ReadInt(media["width"]);
                var mediaHeight = ReadInt(media["height"]);
                if (mediaWidth > 0 && mediaHeight > 0)
                    vm.SetMediaPreviewAspectRatio((double)mediaWidth / mediaHeight);
            }
            var file = ReadMediaFile(media, kind);
            if (file != null)
            {
                vm.MediaId = ReadLong(file["id"]);
                vm.MediaSize = ReadDownloadTotal(file);
                vm.MediaFileUri = ToFileUri(ReadFilePath(file));
            }
            if (kind == "photo")
            {
                var full = ReadBestPhotoFile(media);
                vm.MediaFullId = ReadLong(full == null ? null : full["id"]);
            }
            var thumb = ReadThumbnail(media);
            if (thumb != null)
            {
                vm.MediaPreviewId = ReadLong(thumb["id"]);
                vm.MediaPreviewUri = ToImageFileUri(ReadFilePath(thumb));
                vm.MediaThumbBytes = ReadMediaThumbnailBytes(media);
                if (kind == "audio" && vm.MediaPreviewId != 0 && string.IsNullOrEmpty(vm.MediaPreviewUri))
                {
                    RegisterMessagePreviewTarget(vm.MediaPreviewId, vm);
                    SendFireAndForget(new JObject { ["@type"] = "downloadFile", ["file_id"] = vm.MediaPreviewId, ["priority"] = 8, ["synchronous"] = false });
                }
            }
            else
            {
                vm.MediaThumbBytes = ReadMediaThumbnailBytes(media);
            }
        }

        private void FillPoll(ChatMessageViewModel vm, JObject poll, JObject description, bool canAddOption)
        {
            if (vm == null || poll == null) return;

            vm.HasMedia = true;
            vm.MediaKind = "poll";
            vm.StructuredMediaTitle = FirstNonEmpty(ReadFormattedTextToken(poll["question"], ""), "Poll");
            vm.StructuredMediaSubtitle = BuildPollStatusText(poll);
            vm.StructuredMediaAllowsMultiple = ReadBool(poll["allows_multiple_answers"]);
            vm.StructuredMediaTotalVoters = Math.Max(0, ReadInt(poll["total_voter_count"]));
            vm.PollIsPublic = !ReadBool(poll["is_anonymous"]);
            vm.PollCanAddOption = canAddOption;
            vm.PollClosePeriodSeconds = ReadInt(poll["open_period"]);
            vm.PollCloseDate = ReadInt(poll["close_date"]);

            var pollType = poll["type"] as JObject;
            var pollTypeName = ReadString(pollType == null ? null : pollType["@type"], "");
            vm.PollIsQuiz = pollTypeName == "pollTypeQuiz";
            vm.PollSolutionText = ReadFormattedTextToken(pollType == null ? null : pollType["explanation"], "");
            if (string.IsNullOrWhiteSpace(vm.PollSolutionText))
                vm.PollSolutionText = ReadFormattedTextToken(description, "");

            var correctOptionIds = ReadCorrectPollOptionIds(pollType);
            var items = new ObservableCollection<StructuredMediaItemViewModel>();
            var options = poll["options"] as JArray;
            var displayOrder = ReadPollOptionDisplayOrder(poll, options);
            var hasSelected = false;
            if (options != null && displayOrder != null)
            {
                for (var i = 0; i < displayOrder.Count; i++)
                {
                    var optionId = displayOrder[i];
                    if (optionId < 0 || optionId >= options.Count) continue;
                    var option = options[optionId] as JObject;
                    if (option == null) continue;

                    var selected = ReadBool(option["is_chosen"]) || ReadBool(option["is_being_chosen"]);
                    var votePercentageToken = option["vote_percentage"];
                    if (selected) hasSelected = true;
                    var isCorrect = correctOptionIds.Contains(optionId);
                    items.Add(new StructuredMediaItemViewModel
                    {
                        OwnerMessage = vm,
                        Kind = "poll",
                        PollOptionId = optionId,
                        Text = FirstNonEmpty(ReadFormattedTextToken(option["text"], ""), "Option"),
                        Voters = Math.Max(0, ReadInt(option["voter_count"])),
                        VotePercentage = votePercentageToken == null || votePercentageToken.Type == JTokenType.Null ? -1 : ReadInt(votePercentageToken),
                        TotalVoters = vm.StructuredMediaTotalVoters,
                        IsSelected = selected,
                        IsCorrect = isCorrect,
                        IsWrong = vm.PollIsQuiz && selected && !isCorrect && correctOptionIds.Count > 0,
                        Subtitle = BuildPollOptionSubtitle(option)
                    });
                }
            }

            vm.StructuredMediaItems = items;
            var isClosed = ReadBool(poll["is_closed"]);
            var hasRestriction = poll["vote_restriction_reason"] != null && poll["vote_restriction_reason"].Type != JTokenType.Null;
            var allowsRevoting = ReadBool(poll["allows_revoting"]);
            vm.PollAllowsRevoting = allowsRevoting;
            vm.StructuredMediaIsClosed = isClosed || hasRestriction || (!allowsRevoting && hasSelected);

            var recentVoters = poll["recent_voter_ids"] as JArray;
            if (recentVoters != null && recentVoters.Count > 0 && vm.PollIsPublic)
                vm.PollRecentVotersText = "Recent voters: " + FormatMessageSenderNames(recentVoters, 4);
        }

        private static List<int> ReadPollOptionDisplayOrder(JObject poll, JArray options)
        {
            var result = new List<int>();
            if (options == null) return result;

            var order = poll == null ? null : poll["option_order"] as JArray;
            if (order != null && order.Count > 0)
            {
                for (var i = 0; i < order.Count; i++)
                {
                    var optionId = ReadInt(order[i]);
                    if (optionId >= 0 && optionId < options.Count && !result.Contains(optionId))
                        result.Add(optionId);
                }
            }

            for (var i = 0; i < options.Count; i++)
                if (!result.Contains(i)) result.Add(i);
            return result;
        }

        private static HashSet<int> ReadCorrectPollOptionIds(JObject pollType)
        {
            var result = new HashSet<int>();
            if (pollType == null) return result;
            var type = ReadString(pollType["@type"], "");
            if (type != "pollTypeQuiz") return result;

            var ids = pollType["correct_option_ids"] as JArray;
            if (ids != null)
            {
                for (var i = 0; i < ids.Count; i++)
                {
                    var correctId = ReadInt(ids[i]);
                    if (correctId >= 0) result.Add(correctId);
                }
            }

            if (pollType["correct_option_id"] != null && pollType["correct_option_id"].Type != JTokenType.Null)
            {
                var correctId = ReadInt(pollType["correct_option_id"]);
                if (correctId >= 0) result.Add(correctId);
            }
            return result;
        }

        private string BuildPollOptionSubtitle(JObject option)
        {
            if (option == null) return "";
            var parts = new List<string>();
            var recentVoters = option["recent_voter_ids"] as JArray;
            var recent = FormatMessageSenderNames(recentVoters, 3);
            if (!string.IsNullOrEmpty(recent)) parts.Add(recent);

            var author = ReadMessageSenderName(option["author"] as JObject);
            if (!string.IsNullOrEmpty(author)) parts.Add("Added by " + author);

            var date = ReadInt(option["addition_date"]);
            if (date > 0) parts.Add(FormatShortDate(date));
            return string.Join(" - ", parts);
        }

        private string BuildPollStatusText(JObject poll)
        {
            if (poll == null) return "";
            var total = Math.Max(0, ReadInt(poll["total_voter_count"]));
            var parts = new List<string>();
            var pollType = poll["type"] as JObject;
            if (ReadString(pollType == null ? null : pollType["@type"], "") == "pollTypeQuiz")
                parts.Add("Quiz");
            else
                parts.Add("Poll");
            parts.Add(total.ToString() + (total == 1 ? " vote" : " votes"));
            if (ReadBool(poll["allows_multiple_answers"])) parts.Add("multiple choice");
            if (ReadBool(poll["allows_revoting"])) parts.Add("changeable");
            if (ReadBool(poll["is_anonymous"])) parts.Add("anonymous");
            if ((poll["option_order"] as JArray) != null && ((JArray)poll["option_order"]).Count > 0) parts.Add("random order");
            var closeText = FormatPollCloseText(ReadInt(poll["open_period"]), ReadInt(poll["close_date"]));
            if (!string.IsNullOrEmpty(closeText)) parts.Add(closeText);
            if (ReadBool(poll["is_closed"])) parts.Add("closed");
            return string.Join(" - ", parts);
        }

        private string FormatMessageSenderNames(JArray senders, int limit)
        {
            if (senders == null || senders.Count == 0 || limit <= 0) return "";
            var names = new List<string>();
            for (var i = 0; i < senders.Count && names.Count < limit; i++)
            {
                var name = ReadMessageSenderName(senders[i] as JObject);
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            if (names.Count == 0) return "";
            var text = string.Join(", ", names);
            if (senders.Count > names.Count)
                text += " +" + (senders.Count - names.Count).ToString();
            return text;
        }

        private string ReadMessageSenderName(JObject sender)
        {
            if (sender == null) return "";
            var type = ReadString(sender["@type"], "");
            if (type == "messageSenderUser")
            {
                var userId = ReadLong(sender["user_id"]);
                return userId == 0 ? "" : ResolveUserName(userId);
            }

            if (type == "messageSenderChat")
            {
                var chatId = ReadLong(sender["chat_id"]);
                if (chatId == 0) return "";
                lock (_syncRoot)
                {
                    JObject chat;
                    if (_chats.TryGetValue(chatId, out chat) && chat != null)
                        return ReadString(chat["title"], "Chat");
                }
                return "Chat";
            }

            return "";
        }

        private static string FormatPollCloseText(int openPeriod, int closeDate)
        {
            var now = UnixNow();
            if (closeDate > 0)
            {
                if (closeDate <= now) return "closed";
                return "closes in " + FormatRelativeDuration(closeDate - now);
            }

            if (openPeriod > 0)
                return "open for " + FormatRelativeDuration(openPeriod);

            return "";
        }

        private static string FormatShortDate(int unixTime)
        {
            if (unixTime <= 0) return "";
            try
            {
                var date = new DateTime(1970, 1, 1).AddSeconds(unixTime).ToLocalTime();
                return date.ToString("d MMM");
            }
            catch
            {
                return "";
            }
        }

        private static string FormatRelativeDuration(long seconds)
        {
            if (seconds <= 0) return "0m";
            if (seconds < 60) return seconds.ToString() + "s";
            var minutes = seconds / 60;
            if (minutes < 60) return minutes.ToString() + "m";
            var hours = minutes / 60;
            if (hours < 48) return hours.ToString() + "h";
            return (hours / 24).ToString() + "d";
        }

        private void FillChecklist(ChatMessageViewModel vm, JObject checklist)
        {
            if (vm == null || checklist == null) return;

            vm.HasMedia = true;
            vm.MediaKind = "todo";
            vm.StructuredMediaTitle = FirstNonEmpty(ReadFormattedTextToken(checklist["title"], ""), "Checklist");
            vm.StructuredMediaSubtitle = ReadBool(checklist["others_can_mark_tasks_as_done"]) ? "Shared checklist" : "Checklist";
            vm.StructuredMediaIsClosed = !ReadBool(checklist["can_mark_tasks_as_done"]);

            var items = new ObservableCollection<StructuredMediaItemViewModel>();
            var tasks = checklist["tasks"] as JArray;
            if (tasks != null)
            {
                for (var i = 0; i < tasks.Count; i++)
                {
                    var task = tasks[i] as JObject;
                    if (task == null) continue;

                    var completed = (task["completed_by"] != null && task["completed_by"].Type != JTokenType.Null) ||
                        ReadInt(task["completion_date"]) > 0;
                    items.Add(new StructuredMediaItemViewModel
                    {
                        OwnerMessage = vm,
                        Kind = "todo",
                        TodoId = ReadInt(task["id"]),
                        Text = FirstNonEmpty(ReadFormattedTextToken(task["text"], ""), "Task"),
                        IsCompleted = completed
                    });
                }
            }

            vm.StructuredMediaItems = items;
        }

        private JObject ReadMediaFile(JObject media, string kind)
        {
            if (kind == "photo")
            {
                return ReadChatPhotoFile(media);
            }
            if (kind == "video") return media["video"] as JObject;
            if (kind == "gif") return media["animation"] as JObject;
            if (kind == "audio") return media["audio"] as JObject;
            if (kind == "voice") return media["voice"] as JObject;
            if (kind == "roundvideo") return media["video"] as JObject;
            if (kind == "sticker") return media["sticker"] as JObject;
            return media["document"] as JObject;
        }

        private static JObject ReadChatPhotoFile(JObject photo)
        {
            if (photo == null) return null;
            var sizes = photo["sizes"] as JArray;
            JObject bestUnderLimit = null;
            JObject smallestLarger = null;
            long bestUnderScore = 0;
            long smallestLargerScore = long.MaxValue;
            if (sizes != null)
            {
                foreach (var size in sizes.OfType<JObject>())
                {
                    var file = size["photo"] as JObject;
                    if (file == null) continue;
                    var width = ReadLong(size["width"]);
                    var height = ReadLong(size["height"]);
                    var score = width * height;
                    if (score == 0) score = ReadLong(file["size"]);
                    if (score <= 0) continue;

                    const long targetScore = 720 * 720;
                    if (score <= targetScore)
                    {
                        if (score >= bestUnderScore)
                        {
                            bestUnderScore = score;
                            bestUnderLimit = file;
                        }
                    }
                    else if (score < smallestLargerScore)
                    {
                        smallestLargerScore = score;
                        smallestLarger = file;
                    }
                }
            }
            return bestUnderLimit ?? smallestLarger ?? ReadBestPhotoFile(photo);
        }

        private static double ReadPhotoAspectRatio(JObject photo)
        {
            if (photo == null) return 0;
            var sizes = photo["sizes"] as JArray;
            if (sizes == null) return 0;

            long bestScore = 0;
            double bestRatio = 0;
            foreach (var size in sizes.OfType<JObject>())
            {
                var width = ReadLong(size["width"]);
                var height = ReadLong(size["height"]);
                if (width <= 0 || height <= 0) continue;

                var score = width * height;
                if (score <= bestScore) continue;
                bestScore = score;
                bestRatio = (double)width / (double)height;
            }

            if (double.IsNaN(bestRatio) || double.IsInfinity(bestRatio)) return 0;
            return bestRatio;
        }

        private JObject ReadThumbnail(JObject media)
        {
            var albumCover = media["album_cover_thumbnail"] as JObject;
            var file = albumCover == null ? null : albumCover["file"] as JObject;
            if (file != null) return file;

            var externalCovers = media["external_album_covers"] as JArray;
            if (externalCovers != null)
            {
                foreach (var cover in externalCovers.OfType<JObject>())
                {
                    file = cover["file"] as JObject;
                    if (file != null) return file;
                }
            }

            var thumbnail = media["thumbnail"] as JObject;
            return thumbnail == null ? null : thumbnail["file"] as JObject;
        }

        private static byte[] ReadMediaThumbnailBytes(JObject media)
        {
            if (media == null) return null;
            var albumMini = media["album_cover_minithumbnail"] as JObject;
            var bytes = ReadBytes(albumMini == null ? null : albumMini["data"]);
            if (bytes != null && bytes.Length > 0) return bytes;

            var mini = media["minithumbnail"] as JObject;
            bytes = ReadBytes(mini == null ? null : mini["data"]);
            if (bytes != null && bytes.Length > 0) return bytes;

            var thumbnail = media["thumbnail"] as JObject;
            mini = thumbnail == null ? null : thumbnail["minithumbnail"] as JObject;
            return ReadBytes(mini == null ? null : mini["data"]);
        }

        private static string FileNameWithoutExtension(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return "";
            try
            {
                var name = Path.GetFileNameWithoutExtension(fileName.Trim());
                return string.IsNullOrWhiteSpace(name) ? fileName.Trim() : name;
            }
            catch
            {
                return fileName.Trim();
            }
        }

        private static Tuple<string, string> ParseAudioFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var separator = value.IndexOf(" - ", StringComparison.Ordinal);
            if (separator <= 0 || separator >= value.Length - 3) return null;

            var performer = value.Substring(0, separator).Trim();
            var title = value.Substring(separator + 3).Trim();
            if (string.IsNullOrWhiteSpace(performer) || string.IsNullOrWhiteSpace(title)) return null;
            return Tuple.Create(performer, title);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return "";
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return "";
        }

        private void FillSender(ChatMessageViewModel vm, JObject sender)
        {
            if (sender == null) return;
            var type = ReadString(sender["@type"], "");
            if (type == "messageSenderUser")
            {
                var userId = ReadLong(sender["user_id"]);
                JObject user;
                lock (_syncRoot) _users.TryGetValue(userId, out user);
                var name = user == null ? "User" : (ReadString(user["first_name"], "") + " " + ReadString(user["last_name"], "")).Trim();
                vm.SenderPeerType = "user";
                vm.SenderPeerId = userId;
                vm.SenderPeerKey = "user:" + userId.ToString();
                vm.SenderName = string.IsNullOrEmpty(name) ? "User" : name;
                vm.SenderInitials = BuildInitials(vm.SenderName);
                FillMessageSenderPhoto(vm, user == null ? null : user["profile_photo"] as JObject);
            }
            else if (type == "messageSenderChat")
            {
                var chatId = ReadLong(sender["chat_id"]);
                JObject chat;
                lock (_syncRoot) _chats.TryGetValue(chatId, out chat);
                var chatType = chat == null ? null : chat["type"] as JObject;
                var chatTypeName = ReadString(chatType == null ? null : chatType["@type"], "");
                var isSupergroup = chatTypeName == "chatTypeSupergroup";
                var isBasicGroup = chatTypeName == "chatTypeBasicGroup";
                var isBroadcast = isSupergroup && ReadBool(chatType["is_channel"]);
                var isGroup = isBasicGroup || (isSupergroup && !isBroadcast);
                var isChannel = isSupergroup;
                var peerType = isSupergroup ? "channel" : "chat";
                var title = ReadString(chat == null ? null : chat["title"], isBroadcast ? "Channel" : "Group");
                vm.SenderPeerType = peerType;
                vm.SenderPeerId = chatId;
                vm.SenderPeerKey = peerType + ":" + chatId.ToString();
                vm.SenderIsGroup = isGroup || chat == null;
                vm.SenderIsChannel = isChannel;
                vm.SenderIsBroadcast = isBroadcast;
                vm.SenderName = title;
                vm.SenderInitials = BuildInitials(title);
                FillMessageSenderPhoto(vm, chat == null ? null : chat["photo"] as JObject);
            }
        }

        private CommentAvatarViewModel MapMessageViewer(long userId, JObject user)
        {
            var firstName = ReadString(user == null ? null : user["first_name"], "");
            var lastName = ReadString(user == null ? null : user["last_name"], "");
            var username = ReadString(user == null ? null : user["username"], "");
            var title = (firstName + " " + lastName).Trim();
            if (string.IsNullOrWhiteSpace(title))
                title = string.IsNullOrWhiteSpace(username) ? "User" : username;

            var result = new CommentAvatarViewModel
            {
                PeerType = "user",
                PeerId = userId,
                PeerKey = "user:" + userId.ToString(),
                Title = title,
                Initials = BuildInitials(title)
            };

            var photo = user == null ? null : user["profile_photo"] as JObject;
            var small = photo == null ? null : photo["small"] as JObject;
            result.AvatarPhotoId = ReadLong(photo == null ? null : photo["id"]);
            result.AvatarUri = ToFileUri(ReadFilePath(small));
            return result;
        }

        private string ReadSenderName(JObject message)
        {
            if (message == null) return "";
            var sender = message["sender_id"] as JObject;
            if (sender == null) return "";

            var type = ReadString(sender["@type"], "");
            if (type == "messageSenderUser")
            {
                var userId = ReadLong(sender["user_id"]);
                JObject user;
                lock (_syncRoot) _users.TryGetValue(userId, out user);
                if (user == null) return "";
                var name = (ReadString(user["first_name"], "") + " " + ReadString(user["last_name"], "")).Trim();
                return string.IsNullOrWhiteSpace(name) ? ReadString(user["username"], "") : name;
            }

            if (type == "messageSenderChat")
            {
                var chatId = ReadLong(sender["chat_id"]);
                JObject chat;
                lock (_syncRoot) _chats.TryGetValue(chatId, out chat);
                return ReadString(chat == null ? null : chat["title"], "");
            }

            return "";
        }

        private void FillMessageSenderPhoto(ChatMessageViewModel vm, JObject photo)
        {
            if (vm == null || photo == null) return;
            vm.SenderAvatarPhotoId = ReadLong(photo["id"]);
            vm.SenderAvatarStrippedThumb = ReadBytes(photo["minithumbnail"] == null ? null : photo["minithumbnail"]["data"]);
            var small = photo["small"] as JObject ?? photo["big"] as JObject;
            if (small == null) return;
            var path = ReadFilePath(small);
            if (!string.IsNullOrEmpty(path))
            {
                vm.SenderAvatarUri = ToFileUri(path);
                return;
            }
            var fileId = ReadLong(small["id"]);
            if (fileId == 0) return;
            RegisterMessageAvatarTarget(fileId, vm);
            SendFireAndForget(new JObject { ["@type"] = "downloadFile", ["file_id"] = fileId, ["priority"] = 1, ["synchronous"] = false });
        }

        private void UpdateChat(JObject chat)
        {
            if (chat == null) return;
            var id = ReadLong(chat["id"]);
            if (id == 0) return;
            lock (_syncRoot)
            {
                JObject existing;
                if (_chats.TryGetValue(id, out existing) && existing != null)
                {
                    var incomingPositions = chat["positions"] as JArray;
                    var existingPositions = existing["positions"] as JArray;
                    if ((incomingPositions == null || incomingPositions.Count == 0) && existingPositions != null && existingPositions.Count > 0)
                        chat["positions"] = existingPositions.DeepClone();
                }

                JArray pendingPositions;
                if (_pendingChatPositions.TryGetValue(id, out pendingPositions) && pendingPositions != null && pendingPositions.Count > 0)
                {
                    var positions = chat["positions"] as JArray;
                    if (positions == null)
                    {
                        positions = new JArray();
                        chat["positions"] = positions;
                    }
                    foreach (var pendingPosition in pendingPositions.OfType<JObject>())
                        ApplyChatPosition(positions, (JObject)pendingPosition.DeepClone());
                    _pendingChatPositions.Remove(id);
                }
                _chats[id] = chat;
                TrackArchiveState(id, chat["positions"] as JArray);
            }
        }

        private void QueueMessageUpdate(JObject message)
        {
            if (message == null) return;
            var chatId = ReadLong(message["chat_id"]);
            var messageId = ReadLong(message["id"]);
            if (chatId == 0 || messageId == 0) return;

            lock (_syncRoot)
            {
                _pendingMessageUpdates.Add((JObject)message.DeepClone());
                if (_pendingMessageUpdates.Count > 300)
                    _pendingMessageUpdates.RemoveAt(0);
            }
            Debug.WriteLine("RT_QUEUE chatId=" + chatId + " msgId=" + messageId + " pendingCount=" + _pendingMessageUpdates.Count);
            var handler = NewMessageArrived;
            if (handler != null)
            {
                Debug.WriteLine("RT_EVENT_FIRE NewMessageArrived chatId=" + chatId);
                RaiseOnUiThread(handler, chatId);
            }
            else
            {
                Debug.WriteLine("RT_EVENT_FIRE NewMessageArrived chatId=" + chatId + " NO_SUBSCRIBERS");
            }
        }

        private void NotifyRealtimeMessage(JObject message)
        {
            try
            {
                if (message == null) return;
                if (!TelegramAppSettings.NotificationsEnabled) return;

                var chatId = ReadLong(message["chat_id"]);
                var tdMessageId = ReadLong(message["id"]);
                if (chatId == 0 || tdMessageId == 0) return;

                var isOutgoing = ReadBool(message["is_outgoing"]);
                var compactMessageId = CompactMessageId(chatId, tdMessageId);
                if (compactMessageId <= 0) return;

                if (IsChatArchived(chatId)) return;

                var ignored = NotifyRealtimeMessageAsync(chatId, compactMessageId, isOutgoing, message);
            }
            catch
            {
            }
        }

        private async Task NotifyRealtimeMessageAsync(long chatId, int compactMessageId, bool isOutgoing, JObject message)
        {
            try
            {
                JObject chatJson = null;
                lock (_syncRoot)
                {
                    _chats.TryGetValue(chatId, out chatJson);
                }

                // The app only keeps main-list chats around, so a message from an
                // archived chat usually arrives for a chat that is not cached yet.
                // Resolve it before deciding, otherwise the archive state is unknown
                // and the chat looks like an ordinary main-list chat.
                if (chatJson == null)
                {
                    try
                    {
                        chatJson = await GetChatRawAsync(chatId);
                    }
                    catch
                    {
                        chatJson = null;
                    }
                }

                if (chatJson == null || IsChatArchived(chatId)) return;

                var chat = MapChatForList(chatJson);
                if (chat == null || chat.IsArchived) return;

                var text = ReadMessagePreviewText(message["content"] as JObject);
                var senderName = ReadSenderName(message);
                var date = ReadInt(message["date"]);
                await TelegramNotificationRuntime.NotifyRealtimeMessageAsync(chat, compactMessageId, text, isOutgoing, date, senderName);
            }
            catch
            {
            }
        }

        private void QueueMessageRefresh(JObject update)
        {
            if (update == null) return;
            var chatId = ReadLong(update["chat_id"]);
            var messageId = ReadLong(update["message_id"]);
            if (chatId == 0 || messageId == 0) return;

            lock (_syncRoot)
            {
                HashSet<long> ids;
                if (!_pendingMessageRefreshIds.TryGetValue(chatId, out ids))
                {
                    ids = new HashSet<long>();
                    _pendingMessageRefreshIds[chatId] = ids;
                }
                ids.Add(messageId);

                // Bound memory if a chat receives a very large burst while not open.
                if (ids.Count > 200) ids.Clear();
            }
            RaiseOnUiThread(MessageContentUpdated, chatId);
        }

        private void QueueMessageSendSucceeded(JObject update)
        {
            if (update == null) return;
            var message = update["message"] as JObject;
            var chatId = ReadLong(message == null ? null : message["chat_id"]);
            var oldMessageId = ReadLong(update["old_message_id"]);
            if (chatId != 0 && oldMessageId != 0)
                QueueDeletedMessageId(chatId, oldMessageId);
            QueueMessageUpdate(message);
        }

        private void QueueDeletedMessages(JObject update)
        {
            if (update == null) return;
            var chatId = ReadLong(update["chat_id"]);
            var ids = update["message_ids"] as JArray;
            if (chatId == 0 || ids == null || ids.Count == 0) return;

            lock (_syncRoot)
            {
                foreach (var id in ids)
                    AddPendingDeletedMessageId(chatId, ReadLong(id));
            }
            RaiseOnUiThread(MessagesDeleted, chatId);
        }

        private void QueueDeletedMessageId(long chatId, long tdMessageId)
        {
            if (chatId == 0 || tdMessageId == 0) return;
            lock (_syncRoot) AddPendingDeletedMessageId(chatId, tdMessageId);
        }

        private void AddPendingDeletedMessageId(long chatId, long tdMessageId)
        {
            var compact = CompactMessageId(chatId, tdMessageId);
            if (compact <= 0) return;
            List<int> list;
            if (!_pendingDeletedMessageIds.TryGetValue(chatId, out list))
            {
                list = new List<int>();
                _pendingDeletedMessageIds[chatId] = list;
            }
            if (!list.Contains(compact)) list.Add(compact);
        }

        private void UpdateChatNotificationSettings(JObject update)
        {
            if (update == null) return;
            var chatId = ReadLong(update["chat_id"]);
            if (chatId == 0) return;

            var settings = update["notification_settings"] as JObject;
            if (settings == null) return;

            lock (_syncRoot)
            {
                JObject chat;
                if (!_chats.TryGetValue(chatId, out chat) || chat == null) return;
                chat["notification_settings"] = (JObject)settings.DeepClone();
            }
        }

        private void ResetNotificationScopeCache()
        {
            lock (_syncRoot)
            {
                _scopeMuteFor.Clear();
                _notificationScopesLoaded = false;
            }
        }

        private void UpdateScopeNotificationSettings(JObject update)
        {
            if (update == null) return;
            var scopeKey = GetNotificationScopeKey(update["scope"] as JObject);
            if (string.IsNullOrEmpty(scopeKey)) return;

            var settings = update["notification_settings"] as JObject;
            if (settings == null) return;

            lock (_syncRoot)
            {
                _scopeMuteFor[scopeKey] = ReadInt(settings["mute_for"]);
            }
        }

        private static string GetNotificationScopeKey(JObject scope)
        {
            var type = ReadString(scope == null ? null : scope["@type"], "");
            if (type == "notificationSettingsScopePrivateChats") return "private";
            if (type == "notificationSettingsScopeGroupChats") return "group";
            if (type == "notificationSettingsScopeChannelChats") return "channel";
            return string.Empty;
        }

        private void UpdateCachedChatNotificationSettings(long chatId, bool muted)
        {
            if (chatId == 0) return;
            lock (_syncRoot)
            {
                JObject chat;
                if (!_chats.TryGetValue(chatId, out chat) || chat == null) return;
                chat["notification_settings"] = new JObject
                {
                    ["@type"] = "chatNotificationSettings",
                    ["mute_for"] = muted ? 2147483647 : 0,
                    ["use_default_mute_for"] = false
                };
            }
        }

        private void UpdateChatLastMessage(JObject update)
        {
            if (update == null) return;
            var chatId = ReadLong(update["chat_id"]);
            if (chatId == 0) return;
            lock (_syncRoot)
            {
                JObject chat;
                if (!_chats.TryGetValue(chatId, out chat) || chat == null) return;
                var lastMessage = update["last_message"] as JObject;
                if (lastMessage == null)
                    chat["last_message"] = null;
                else
                    chat["last_message"] = (JObject)lastMessage.DeepClone();

                var positions = chat["positions"] as JArray;
                var updatePositions = update["positions"] as JArray;
                if (updatePositions != null)
                {
                    if (positions == null)
                    {
                        positions = new JArray();
                        chat["positions"] = positions;
                    }
                    foreach (var position in updatePositions.OfType<JObject>())
                        ApplyChatPosition(positions, (JObject)position.DeepClone());
                    TrackArchiveState(chatId, positions);
                }
            }
        }

        private void UpdateChatPosition(JObject update)
        {
            if (update == null) return;
            var chatId = ReadLong(update["chat_id"]);
            var position = update["position"] as JObject;
            if (chatId == 0 || position == null) return;

            lock (_syncRoot)
            {
                JObject chat;
                if (!_chats.TryGetValue(chatId, out chat) || chat == null)
                {
                    JArray pendingPositions;
                    if (!_pendingChatPositions.TryGetValue(chatId, out pendingPositions))
                    {
                        pendingPositions = new JArray();
                        _pendingChatPositions[chatId] = pendingPositions;
                    }
                    ApplyChatPosition(pendingPositions, position);
                    TrackArchiveState(chatId, pendingPositions);
                    return;
                }

                var positions = chat["positions"] as JArray;
                if (positions == null)
                {
                    positions = new JArray();
                    chat["positions"] = positions;
                }

                ApplyChatPosition(positions, position);
                TrackArchiveState(chatId, positions);
            }
        }

        private void UpdateChatReadInbox(JObject update)
        {
            if (update == null) return;
            var chatId = ReadLong(update["chat_id"]);
            if (chatId == 0) return;
            lock (_syncRoot)
            {
                JObject chat;
                if (!_chats.TryGetValue(chatId, out chat) || chat == null) return;
                chat["last_read_inbox_message_id"] = ReadLong(update["last_read_inbox_message_id"]);
                chat["unread_count"] = ReadInt(update["unread_count"]);
            }
        }

        private void UpdateChatReadOutbox(JObject update)
        {
            if (update == null) return;
            var chatId = ReadLong(update["chat_id"]);
            if (chatId == 0) return;
            lock (_syncRoot)
            {
                JObject chat;
                if (!_chats.TryGetValue(chatId, out chat) || chat == null) return;
                chat["last_read_outbox_message_id"] = ReadLong(update["last_read_outbox_message_id"]);
            }
            RaiseOnUiThread(MessageContentUpdated, chatId);
        }

        private void UpdateChatPinnedMessage(JObject update)
        {
            if (update == null) return;
            var chatId = ReadLong(update["chat_id"]);
            if (chatId == 0) return;
            lock (_syncRoot)
            {
                JObject chat;
                if (!_chats.TryGetValue(chatId, out chat) || chat == null) return;
                chat["pinned_message_id"] = ReadLong(update["pinned_message_id"]);
            }
        }

        private void ApplyChatPosition(JArray positions, JObject position)
        {
            if (positions == null || position == null) return;
            var list = position["list"] as JObject;
            var replaceIndex = -1;
            for (var i = 0; i < positions.Count; i++)
            {
                var existing = positions[i] as JObject;
                if (SameChatList(existing == null ? null : existing["list"] as JObject, list))
                {
                    replaceIndex = i;
                    break;
                }
            }

            if (ReadLong(position["order"]) == 0)
            {
                if (replaceIndex >= 0) positions.RemoveAt(replaceIndex);
            }
            else if (replaceIndex >= 0)
                positions[replaceIndex] = position;
            else
                positions.Add(position);
        }

        private void UpdateUser(JObject user)
        {
            if (user == null) return;
            var id = ReadLong(user["id"]);
            if (id == 0) return;
            if (ReadBool(user["is_self"]))
                _selfUserId = id;
            lock (_syncRoot) _users[id] = user;
            RaiseOnUiThread(UserStatusChanged, id);
        }

        private void UpdateSupergroup(JObject group)
        {
            if (group == null) return;
            var id = ReadLong(group["id"]);
            if (id == 0) return;
            lock (_syncRoot) _supergroups[id] = group;
        }

        private void UpdateBasicGroup(JObject group)
        {
            if (group == null) return;
            var id = ReadLong(group["id"]);
            if (id == 0) return;
            lock (_syncRoot) _basicGroups[id] = group;
        }

        private void UpdateChatFolders(JArray folders)
        {
            lock (_syncRoot)
            {
                _chatFolderInfos = folders == null ? new JArray() : (JArray)folders.DeepClone();
                var waiter = _folderWaiter;
                _folderWaiter = null;
                if (waiter != null) waiter.TrySetResult(true);
            }
        }

        private void RegisterAvatarTarget(long fileId, ChatViewModel chat)
        {
            if (fileId == 0 || chat == null) return;
            lock (_syncRoot)
            {
                List<ChatViewModel> targets;
                if (!_avatarTargets.TryGetValue(fileId, out targets))
                {
                    targets = new List<ChatViewModel>();
                    _avatarTargets[fileId] = targets;
                }
                if (!targets.Contains(chat)) targets.Add(chat);
            }
        }

        private void RegisterMessageAvatarTarget(long fileId, ChatMessageViewModel message)
        {
            if (fileId == 0 || message == null) return;
            lock (_syncRoot)
            {
                List<ChatMessageViewModel> targets;
                if (!_messageAvatarTargets.TryGetValue(fileId, out targets))
                {
                    targets = new List<ChatMessageViewModel>();
                    _messageAvatarTargets[fileId] = targets;
                }
                if (!targets.Contains(message)) targets.Add(message);
            }
        }

        private void RegisterMessagePreviewTarget(long fileId, ChatMessageViewModel message)
        {
            if (fileId == 0 || message == null) return;
            lock (_syncRoot)
            {
                List<ChatMessageViewModel> targets;
                if (!_messagePreviewTargets.TryGetValue(fileId, out targets))
                {
                    targets = new List<ChatMessageViewModel>();
                    _messagePreviewTargets[fileId] = targets;
                }
                if (!targets.Contains(message)) targets.Add(message);
            }
        }

        private void RegisterMediaItemPreviewTarget(long fileId, ChatMediaItemViewModel item)
        {
            if (fileId == 0 || item == null) return;
            lock (_syncRoot)
            {
                List<ChatMediaItemViewModel> targets;
                if (!_mediaItemPreviewTargets.TryGetValue(fileId, out targets))
                {
                    targets = new List<ChatMediaItemViewModel>();
                    _mediaItemPreviewTargets[fileId] = targets;
                }
                if (!targets.Contains(item)) targets.Add(item);
            }
        }

        private void RegisterMessageDownloadTarget(long fileId, ChatMessageViewModel message)
        {
            if (fileId == 0 || message == null) return;
            lock (_syncRoot)
            {
                List<ChatMessageViewModel> targets;
                if (!_messageDownloadTargets.TryGetValue(fileId, out targets))
                {
                    targets = new List<ChatMessageViewModel>();
                    _messageDownloadTargets[fileId] = targets;
                }
                if (!targets.Contains(message)) targets.Add(message);
            }
        }

        private void UnregisterMessageDownloadTarget(long fileId, ChatMessageViewModel message)
        {
            lock (_syncRoot)
            {
                List<ChatMessageViewModel> targets;
                if (!_messageDownloadTargets.TryGetValue(fileId, out targets)) return;
                targets.Remove(message);
                if (targets.Count == 0) _messageDownloadTargets.Remove(fileId);
            }
        }

        private void RegisterMediaItemDownloadTarget(long fileId, ChatMediaItemViewModel item)
        {
            if (fileId == 0 || item == null) return;
            lock (_syncRoot)
            {
                List<ChatMediaItemViewModel> targets;
                if (!_mediaItemDownloadTargets.TryGetValue(fileId, out targets))
                {
                    targets = new List<ChatMediaItemViewModel>();
                    _mediaItemDownloadTargets[fileId] = targets;
                }
                if (!targets.Contains(item)) targets.Add(item);
            }
        }

        private void UnregisterMediaItemDownloadTarget(long fileId, ChatMediaItemViewModel item)
        {
            lock (_syncRoot)
            {
                List<ChatMediaItemViewModel> targets;
                if (!_mediaItemDownloadTargets.TryGetValue(fileId, out targets)) return;
                targets.Remove(item);
                if (targets.Count == 0) _mediaItemDownloadTargets.Remove(fileId);
            }
        }

        private static long ReadDownloadTotal(JObject file)
        {
            if (file == null) return 0;
            var expected = ReadLong(file["expected_size"]);
            var size = ReadLong(file["size"]);
            return expected > 0 ? expected : size;
        }

        private static long ReadDownloadedBytes(JObject file)
        {
            if (file == null) return 0;
            var local = file["local"] as JObject;
            if (local == null) return 0;
            var downloaded = ReadLong(local["downloaded_size"]);
            if (downloaded > 0) return downloaded;
            return ReadLong(local["downloaded_prefix_size"]);
        }

        // Captured once from the UI thread. Resolving the dispatcher lazily from the TDLib
        // receive thread throws RPC_E_WRONG_THREAD on every call, which is both slow and the
        // source of the constant "System.Exception in Telegram.McgInterop.dll" first-chance
        // exceptions in the debugger output.
        private static Windows.UI.Core.CoreDispatcher _uiDispatcher;

        internal static void CaptureUiDispatcher(Windows.UI.Core.CoreDispatcher dispatcher)
        {
            if (dispatcher != null) _uiDispatcher = dispatcher;
        }

        private static Windows.UI.Core.CoreDispatcher GetUiDispatcher()
        {
            var dispatcher = _uiDispatcher;
            if (dispatcher != null) return dispatcher;

            // Only safe to resolve while we are on a thread that owns a CoreWindow.
            try
            {
                var window = Windows.UI.Xaml.Window.Current;
                if (window != null)
                {
                    dispatcher = window.Dispatcher;
                    if (dispatcher != null) _uiDispatcher = dispatcher;
                }
            }
            catch
            {
            }

            return dispatcher;
        }

        /// <summary>
        /// Raises a realtime event on the UI thread. Subscribers merge messages into collections
        /// that are bound to a ListView; doing that straight from the TDLib receive thread throws
        /// RPC_E_WRONG_THREAD and the update is silently lost.
        /// </summary>
        private void RaiseOnUiThread(EventHandler<long> handler, long argument)
        {
            if (handler == null) return;
            var self = this;
            RunOnUiThread(delegate { handler(self, argument); });
        }

        private static void RunOnUiThread(Action action)
        {
            if (action == null) return;

            var dispatcher = GetUiDispatcher();
            if (dispatcher == null)
            {
                // No UI thread yet (background task / early startup). Running view-model
                // mutations here would touch DependencyObjects off-thread, so skip instead.
                try { action(); }
                catch { }
                return;
            }

            if (dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            var ignored = dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, delegate
            {
                try { action(); }
                catch { }
            });
        }

        private void ApplyMessageDownloadFile(ChatMessageViewModel target, JObject file)
        {
            if (target == null || file == null) return;
            var local = file["local"] as JObject;
            var completed = ReadBool(local == null ? null : local["is_downloading_completed"]);
            var downloaded = ReadDownloadedBytes(file);
            var total = ReadDownloadTotal(file);
            if (total <= 0) total = target.MediaSize > 0 ? target.MediaSize : target.MediaDownloadTotalBytes;
            if (completed)
            {
                if (total <= 0) total = downloaded;
                if (total > 0) downloaded = total;
            }
            target.MediaDownloadTotalBytes = total;
            target.MediaDownloadBytes = downloaded;
            var path = ReadFilePath(file);
            if (completed && !string.IsNullOrEmpty(path))
                target.MediaFileUri = ToFileUri(path);
        }

        private void ApplyMediaItemDownloadFile(ChatMediaItemViewModel target, JObject file)
        {
            if (target == null || file == null) return;
            var local = file["local"] as JObject;
            var completed = ReadBool(local == null ? null : local["is_downloading_completed"]);
            var downloaded = ReadDownloadedBytes(file);
            var total = ReadDownloadTotal(file);
            if (total <= 0) total = target.MediaSize > 0 ? target.MediaSize : target.MediaDownloadTotalBytes;
            if (completed)
            {
                if (total <= 0) total = downloaded;
                if (total > 0) downloaded = total;
            }
            target.MediaDownloadTotalBytes = total;
            target.MediaDownloadBytes = downloaded;
            var path = ReadFilePath(file);
            if (completed && !string.IsNullOrEmpty(path))
                target.MediaFileUri = ToFileUri(path);
        }

        private void HandleFileUpdate(JObject file)
        {
            if (file == null) return;
            var fileId = ReadLong(file["id"]);
            if (fileId == 0) return;

            NotifyFileDownloadWatchers(fileId, file);

            List<ChatMessageViewModel> downloadMessages = null;
            List<ChatMediaItemViewModel> downloadItems = null;
            lock (_syncRoot)
            {
                List<ChatMessageViewModel> activeDownloadMessageTargets;
                if (_messageDownloadTargets.TryGetValue(fileId, out activeDownloadMessageTargets))
                    downloadMessages = new List<ChatMessageViewModel>(activeDownloadMessageTargets);

                List<ChatMediaItemViewModel> activeDownloadItemTargets;
                if (_mediaItemDownloadTargets.TryGetValue(fileId, out activeDownloadItemTargets))
                    downloadItems = new List<ChatMediaItemViewModel>(activeDownloadItemTargets);
            }

            if (downloadMessages != null || downloadItems != null)
            {
                var activeDownloadMessages = downloadMessages;
                var activeDownloadItems = downloadItems;
                var activeFile = file;
                RunOnUiThread(delegate
                {
                    if (activeDownloadMessages != null)
                        foreach (var target in activeDownloadMessages) ApplyMessageDownloadFile(target, activeFile);
                    if (activeDownloadItems != null)
                        foreach (var target in activeDownloadItems) ApplyMediaItemDownloadFile(target, activeFile);
                });
            }

            // TDLib exposes local.path before the file is complete. Binding that partial
            // thumbnail to BitmapImage produces intermittent ImageFailed and used to remove
            // the target before the final updateFile arrived.
            if (!IsDownloadCompleted(file)) return;

            var path = ReadFilePath(file);
            if (string.IsNullOrEmpty(path)) return;

            List<ChatViewModel> targets = null;
            List<ChatMessageViewModel> messageTargets = null;
            List<ChatMessageViewModel> previewTargets = null;
            List<ChatMediaItemViewModel> mediaItemPreviewTargets = null;
            List<StickerItemViewModel> stickerTargets = null;
            lock (_syncRoot)
            {
                if (_avatarTargets.TryGetValue(fileId, out targets))
                    _avatarTargets.Remove(fileId);
                if (_messageAvatarTargets.TryGetValue(fileId, out messageTargets))
                    _messageAvatarTargets.Remove(fileId);
                if (_messagePreviewTargets.TryGetValue(fileId, out previewTargets))
                    _messagePreviewTargets.Remove(fileId);
                if (_mediaItemPreviewTargets.TryGetValue(fileId, out mediaItemPreviewTargets))
                    _mediaItemPreviewTargets.Remove(fileId);
                if (_stickerFileTargets.TryGetValue(fileId, out stickerTargets))
                    _stickerFileTargets.Remove(fileId);
            }

            var uri = ToFileUri(path);
            var imageUri = ToImageFileUri(path);
            if (targets == null && messageTargets == null && previewTargets == null && mediaItemPreviewTargets == null && stickerTargets == null)
                return;

            // These setters raise PropertyChanged, which makes XAML evaluate bindings such as
            // SenderAvatarImageSource. Creating a BitmapImage off the UI thread throws
            // RPC_E_WRONG_THREAD and the avatar silently never appears, so marshal first.
            var avatarTargets = targets;
            var senderTargets = messageTargets;
            var mediaPreviewTargets = previewTargets;
            var mediaItemUriTargets = mediaItemPreviewTargets;
            var stickerUriTargets = stickerTargets;
            RunOnUiThread(delegate
            {
                if (avatarTargets != null)
                {
                    foreach (var target in avatarTargets)
                        target.AvatarUri = uri;
                }
                if (senderTargets != null)
                {
                    foreach (var target in senderTargets)
                        target.SenderAvatarUri = uri;
                }
                if (mediaPreviewTargets != null)
                {
                    foreach (var target in mediaPreviewTargets)
                        target.MediaPreviewUri = imageUri;
                }
                if (mediaItemUriTargets != null)
                {
                    foreach (var target in mediaItemUriTargets)
                        target.MediaPreviewUri = imageUri;
                }
                if (stickerUriTargets != null)
                {
                    foreach (var target in stickerUriTargets)
                        target.StickerSourceUri = uri;
                }
            });
        }

        private long ResolveChatId(ChatViewModel peer)
        {
            if (peer == null) return 0;
            if (peer.PeerId != 0) return peer.ParentPeerId != 0 ? peer.ParentPeerId : peer.PeerId;
            long id;
            lock (_syncRoot)
            {
                if (!string.IsNullOrEmpty(peer.PeerKey) && _peerChatIds.TryGetValue(peer.PeerKey, out id)) return id;
            }
            return 0;
        }

        private static string MapChatActionType(string actionKind)
        {
            if (string.Equals(actionKind, "recording_voice", StringComparison.OrdinalIgnoreCase))
                return "chatActionRecordingVoiceNote";
            if (string.Equals(actionKind, "recording_video_note", StringComparison.OrdinalIgnoreCase))
                return "chatActionRecordingVideoNote";
            if (string.Equals(actionKind, "uploading_photo", StringComparison.OrdinalIgnoreCase))
                return "chatActionUploadingPhoto";
            if (string.Equals(actionKind, "uploading_video", StringComparison.OrdinalIgnoreCase))
                return "chatActionUploadingVideo";
            if (string.Equals(actionKind, "uploading_video_note", StringComparison.OrdinalIgnoreCase))
                return "chatActionUploadingVideoNote";
            if (string.Equals(actionKind, "uploading_voice", StringComparison.OrdinalIgnoreCase))
                return "chatActionUploadingVoiceNote";
            if (string.Equals(actionKind, "uploading_document", StringComparison.OrdinalIgnoreCase))
                return "chatActionUploadingDocument";
            if (string.Equals(actionKind, "cancel", StringComparison.OrdinalIgnoreCase))
                return "chatActionCancel";
            return "chatActionTyping";
        }

        private int CompactMessageId(long chatId, long tdMessageId)
        {
            if (tdMessageId == 0) return 0;
            lock (_syncRoot)
            {
                int compact;
                if (tdMessageId > 0 && tdMessageId <= int.MaxValue)
                    compact = (int)tdMessageId;
                else if (!_messageIdsReverse.TryGetValue(tdMessageId, out compact))
                    compact = ++_compactMessageId;

                _messageIdsReverse[tdMessageId] = compact;
                _messageIds[chatId.ToString() + ":" + compact.ToString()] = tdMessageId;
                return compact;
            }
        }

        private long ResolveMessageId(ChatViewModel peer, int compactId)
        {
            if (compactId <= 0) return 0;
            var chatId = ResolveChatId(peer);
            long tdId;
            lock (_syncRoot)
            {
                if (_messageIds.TryGetValue(chatId.ToString() + ":" + compactId.ToString(), out tdId)) return tdId;
            }
            return compactId;
        }

        private bool IsThreadPeer(ChatViewModel peer)
        {
            return peer != null && (peer.IsForumTopic || peer.IsCommentsThread);
        }

        private void SyncPeerReadOutboxFromCache(ChatViewModel peer, long chatId)
        {
            if (peer == null || chatId == 0) return;

            long tdReadOutboxId = 0;
            lock (_syncRoot)
            {
                JObject chat;
                if (_chats.TryGetValue(chatId, out chat) && chat != null)
                    tdReadOutboxId = ReadLong(chat["last_read_outbox_message_id"]);
            }

            if (tdReadOutboxId > 0)
                peer.ReadOutboxMaxId = CompactMessageId(chatId, tdReadOutboxId);
        }

        private bool MessageBelongsToPeer(ChatViewModel peer, JObject message)
        {
            if (peer == null || message == null) return true;
            if (!IsThreadPeer(peer)) return true;

            var topic = message["topic_id"] as JObject;
            if (topic == null) return true;

            var type = ReadString(topic["@type"], "");
            if (type == "messageTopicForum")
            {
                var forumTopicId = ReadInt(topic["forum_topic_id"]);
                return forumTopicId == 0 || peer.TopicId == 0 || forumTopicId == peer.TopicId;
            }

            if (type == "messageTopicThread")
            {
                var messageThreadId = ReadLong(topic["message_thread_id"]);
                var expectedThreadId = ResolveThreadMessageId(peer);
                return messageThreadId == 0 || expectedThreadId == 0 || messageThreadId == expectedThreadId;
            }

            return true;
        }

        private long ResolveThreadMessageId(ChatViewModel peer)
        {
            if (peer == null) return 0;
            var compact = peer.TopicRootMessageId > 0 ? peer.TopicRootMessageId : peer.TopicId;
            var resolved = ResolveMessageId(peer, compact);
            return resolved != 0 ? resolved : compact;
        }

        private JObject BuildMessageTopic(ChatViewModel peer)
        {
            if (!IsThreadPeer(peer)) return null;
            if (peer.IsCommentsThread)
                return new JObject { ["@type"] = "messageTopicThread", ["message_thread_id"] = ResolveThreadMessageId(peer) };
            var topicId = peer.TopicId > 0 ? peer.TopicId : peer.TopicRootMessageId;
            return topicId > 0 ? new JObject { ["@type"] = "messageTopicForum", ["forum_topic_id"] = topicId } : null;
        }

        private static void AddProfileRow(UserProfileViewModel profile, string label, string value)
        {
            if (profile == null || string.IsNullOrWhiteSpace(value)) return;
            profile.Rows.Add(new ProfileInfoRowViewModel { Label = label, Value = value });
        }

        private static readonly string[] MonthAbbreviations =
        {
            "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"
        };

        // TDLib birthdate -> "01 Jan 1990 (35 years old)" (the year and age are optional).
        private static string FormatBirthdate(JObject birthdate)
        {
            if (birthdate == null) return "";
            var day = (int)ReadLong(birthdate["day"]);
            var month = (int)ReadLong(birthdate["month"]);
            var year = (int)ReadLong(birthdate["year"]);
            if (day <= 0 || month < 1 || month > 12) return "";

            var text = day.ToString("00") + " " + MonthAbbreviations[month - 1];
            if (year > 0)
            {
                text += " " + year.ToString();
                var age = ComputeAge(day, month, year);
                if (age >= 0)
                    text += " (" + age.ToString() + (age == 1 ? " year old)" : " years old)");
            }
            return text;
        }

        private static int ComputeAge(int day, int month, int year)
        {
            try
            {
                var today = DateTime.Now;
                var age = today.Year - year;
                if (today.Month < month || (today.Month == month && today.Day < day))
                    age--;
                return age < 0 ? -1 : age;
            }
            catch
            {
                return -1;
            }
        }

        private static bool ReadCanSendMessages(JObject chat)
        {
            if (chat == null) return true;
            if (ReadBool(chat["can_send_messages"]) || ReadBool(chat["can_post_messages"]))
                return true;
            var permissions = chat["permissions"] as JObject;
            if (permissions == null) return true;
            if (ReadBool(permissions["can_send_basic_messages"]) || ReadBool(permissions["can_send_messages"]))
                return true;
            return false;
        }

        private static bool ReadCanPinMessages(JObject chat, string peerType, bool isGroup, bool isBroadcast)
        {
            if (chat == null) return false;
            if (peerType == "user") return true;

            var status = chat["member_status"] as JObject;
            var statusType = ReadString(status == null ? null : status["@type"], "");
            if (statusType == "chatMemberStatusCreator") return true;
            if (statusType == "chatMemberStatusAdministrator")
            {
                var rights = status["rights"] as JObject;
                if (ReadBool(rights == null ? null : rights["can_pin_messages"])) return true;
                if (ReadBool(rights == null ? null : rights["can_manage_topics"])) return true;
                if (isBroadcast && ReadBool(rights == null ? null : rights["can_edit_messages"])) return true;
                return false;
            }

            var permissions = chat["permissions"] as JObject;
            if (permissions != null && ReadBool(permissions["can_pin_messages"])) return true;
            return isGroup && ReadBool(chat["can_pin_messages"]);
        }

        private static bool ReadCanDeleteMessages(JObject chat, string peerType)
        {
            if (chat == null) return true;
            if (peerType == "user") return true;

            var status = chat["member_status"] as JObject;
            var statusType = ReadString(status == null ? null : status["@type"], "");
            if (statusType == "chatMemberStatusCreator") return true;
            if (statusType == "chatMemberStatusAdministrator")
            {
                var rights = status["rights"] as JObject;
                if (ReadBool(rights == null ? null : rights["can_delete_messages"])) return true;
                return false;
            }

            return ReadBool(chat["can_delete_messages"]);
        }

        private static int ReadFolderId(JArray positions)
        {
            if (positions == null) return -1;

            // A chat can hold several positions at once: one main list position
            // (chatListMain or chatListArchive) plus one per custom chat folder.
            // The archive position wins, otherwise an archived chat that is also
            // part of a custom folder would report that folder instead.
            if (ReadArchived(positions)) return ArchiveFolderId;

            foreach (var position in positions.OfType<JObject>())
            {
                var list = position["list"] as JObject;
                var type = ReadString(list == null ? null : list["@type"], "");
                if (type == "chatListFolder") return ToAppFolderId(ReadInt(list["chat_folder_id"]));
                if (type == "chatListMain") return -1;
            }
            return -1;
        }

        private static bool ReadArchived(JArray positions)
        {
            if (positions == null) return false;
            foreach (var position in positions.OfType<JObject>())
            {
                var list = position["list"] as JObject;
                if (ReadString(list == null ? null : list["@type"], "") == "chatListArchive")
                    return true;
            }
            return false;
        }

        private static bool ReadInMainList(JArray positions)
        {
            if (positions == null) return false;
            foreach (var position in positions.OfType<JObject>())
            {
                var list = position["list"] as JObject;
                if (ReadString(list == null ? null : list["@type"], "") == "chatListMain")
                    return true;
            }
            return false;
        }

        // Must be called while _syncRoot is held. Archive membership is remembered
        // per chat id so it survives the folder-scoped FolderId overrides done by
        // MapChatForList, and so it is available for chats that were never part of
        // a loaded chat list page.
        private void TrackArchiveState(long chatId, JArray positions)
        {
            if (chatId == 0) return;

            if (ReadArchived(positions))
            {
                _archivedChatIds.Add(chatId);
                return;
            }

            // An empty position list only means the chat is currently in no list at
            // all, which is not proof that it left the archive. Drop the archive flag
            // only when the chat is positively seen in the main list or in a folder.
            if (positions != null && positions.Count > 0 &&
                (ReadInMainList(positions) || ReadFolderId(positions) > ArchiveFolderId))
                _archivedChatIds.Remove(chatId);
        }

        private bool IsChatArchived(long chatId)
        {
            if (chatId == 0) return false;
            lock (_syncRoot)
            {
                return _archivedChatIds.Contains(chatId);
            }
        }

        private static int ToAppFolderId(int tdFolderId)
        {
            return tdFolderId <= 0 ? -1 : tdFolderId + TdFolderIdOffset;
        }

        private static int ToTdFolderId(int appFolderId)
        {
            return appFolderId >= TdFolderIdOffset ? appFolderId - TdFolderIdOffset : appFolderId;
        }

        private static bool ReadPinned(JArray positions)
        {
            if (positions == null) return false;
            foreach (var position in positions.OfType<JObject>())
            {
                if (ReadBool(position["is_pinned"])) return true;
            }
            return false;
        }

        private bool ReadPinned(JArray positions, int folderId)
        {
            if (positions == null) return false;
            var expectedList = BuildChatList(folderId);
            foreach (var position in positions.OfType<JObject>())
            {
                if (!SameChatList(position["list"] as JObject, expectedList)) continue;
                return ReadBool(position["is_pinned"]);
            }
            return false;
        }

        private static bool SameChatList(JObject a, JObject b)
        {
            var at = ReadString(a == null ? null : a["@type"], "");
            var bt = ReadString(b == null ? null : b["@type"], "");
            if (at != bt) return false;
            if (at == "chatListFolder")
                return ReadInt(a["chat_folder_id"]) == ReadInt(b["chat_folder_id"]);
            return true;
        }

        private static long ReadMemberUserId(JObject member)
        {
            if (member == null) return 0;
            var memberId = member["member_id"] as JObject;
            if (memberId != null)
            {
                var type = ReadString(memberId["@type"], "");
                if (type == "messageSenderUser") return ReadLong(memberId["user_id"]);
                var userId = ReadLong(memberId["user_id"]);
                if (userId != 0) return userId;
            }
            return ReadLong(member["user_id"]);
        }

        private static string ReadMemberRole(JObject status)
        {
            if (status == null) return "";
            var type = ReadString(status["@type"], "");
            if (type == "chatMemberStatusCreator") return "owner";
            if (type == "chatMemberStatusAdministrator") return "admin";
            if (type == "chatMemberStatusRestricted") return "restricted";
            if (type == "chatMemberStatusBanned") return "banned";
            return "";
        }

        private static string ReadLastMessage(JObject message)
        {
            if (message == null) return "";
            return ReadMessagePreviewText(message["content"] as JObject);
        }

        private static string ReadMessageText(JObject content)
        {
            if (content == null) return "";
            var type = ReadString(content["@type"], "");
            if (type == "messageText") return ReadFormattedText(content["text"] as JObject, "");
            if (type == "messagePhoto") return ReadCaptionOr(content, "");
            if (type == "messageVideo") return ReadCaptionOr(content, "");
            if (type == "messageAnimation") return ReadCaptionOr(content, "");
            if (type == "messageDocument") return ReadCaptionOr(content, "");
            if (type == "messageSticker") return "";
            if (type == "messageAnimatedEmoji") return ReadString(content["emoji"], "");
            if (type == "messageAudio") return ReadCaptionOr(content, "");
            if (type == "messageVoiceNote") return ReadCaptionOr(content, "");
            if (type == "messageVideoNote") return ReadCaptionOr(content, "");
            if (type == "messageLocation" || type == "messageVenue") return "";
            if (type == "messagePoll") return "";
            if (type == "messageChecklist") return "";
            if (IsSystemMessageContentType(type)) return "";
            return "Unsupported message";
        }

        private static string ReadMessagePreviewText(JObject content)
        {
            if (content == null) return "";
            var text = ReadMessageText(content);
            if (!string.IsNullOrWhiteSpace(text)) return text;

            var type = ReadString(content["@type"], "");
            if (type == "messagePhoto") return "Photo";
            if (type == "messageVideo") return "Video";
            if (type == "messageAnimation") return "GIF";
            if (type == "messageDocument") return "File";
            if (type == "messageSticker") return "Sticker";
            if (type == "messageAnimatedEmoji") return FirstNonEmpty(ReadString(content["emoji"], ""), "Emoji");
            if (type == "messageAudio") return "Audio";
            if (type == "messageVoiceNote") return "Voice message";
            if (type == "messageVideoNote") return "Video message";
            if (type == "messageLocation" || type == "messageVenue") return "Location";
            if (type == "messagePoll") return "Poll";
            if (type == "messageChecklist") return "Checklist";
            if (IsSystemMessageContentType(type)) return BuildGenericServicePreviewText(content);
            return "Unsupported message";
        }

        private static string ReadCaptionOr(JObject content, string fallback)
        {
            var caption = ReadFormattedText(content["caption"] as JObject, "");
            return string.IsNullOrWhiteSpace(caption) ? fallback : caption;
        }

        private static string ReadFormattedText(JObject formattedText, string fallback)
        {
            if (formattedText == null) return fallback;
            return ReadString(formattedText["text"], fallback);
        }

        private static string BuildStaticEmojiAssetUri(string emoji)
        {
            return ChatPage.ResolveLocalEmojiAssetUri(emoji);
        }

        private static string BuildEmojiAssetKey(string emoji)
        {
            if (string.IsNullOrEmpty(emoji)) return "";
            var parts = new List<string>();
            for (var i = 0; i < emoji.Length; i++)
            {
                var codeUnit = (int)emoji[i];
                if (codeUnit == 0xFE0F) continue;
                parts.Add(codeUnit.ToString("X4"));
            }
            return string.Join("", parts);
        }

        private static List<MessageTextEntityViewModel> ReadMessageTextEntities(JObject content)
        {
            var result = new List<MessageTextEntityViewModel>();
            var formattedText = ReadContentFormattedText(content);
            var entities = formattedText == null ? null : formattedText["entities"] as JArray;
            if (entities == null) return result;

            foreach (var token in entities.OfType<JObject>())
            {
                var type = token["type"] as JObject;
                var entityType = ReadString(type == null ? null : type["@type"], "");
                var offset = ReadInt(token["offset"]);
                var length = ReadInt(token["length"]);
                if (offset < 0 || length <= 0 || string.IsNullOrEmpty(entityType)) continue;

                result.Add(new MessageTextEntityViewModel
                {
                    Offset = offset,
                    Length = length,
                    Type = entityType,
                    Url = ReadString(type == null ? null : type["url"], "")
                });
            }

            return result;
        }

        private static JObject ReadContentFormattedText(JObject content)
        {
            if (content == null) return null;
            var type = ReadString(content["@type"], "");
            if (type == "messageText") return content["text"] as JObject;
            return content["caption"] as JObject;
        }

        private static string ReadFormattedTextToken(JToken token, string fallback)
        {
            if (token == null) return fallback;
            if (token.Type == JTokenType.String) return ReadString(token, fallback);
            return ReadFormattedText(token as JObject, fallback);
        }

        private static JArray ReadFoldersArray(JObject update)
        {
            if (update == null) return null;
            var folders = update["chat_folders"] as JArray;
            if (folders != null) return folders;
            folders = update["folders"] as JArray;
            if (folders != null) return folders;
            return update["chatFolders"] as JArray;
        }

        private static string ReadFolderTitle(JObject folder)
        {
            if (folder == null) return "Folder";
            var title = ReadFormattedTextToken(folder["title"], "");
            if (!string.IsNullOrWhiteSpace(title)) return title;

            var name = folder["name"] as JObject;
            if (name != null)
            {
                title = ReadFormattedTextToken(name["text"], "");
                if (!string.IsNullOrWhiteSpace(title)) return title;
            }

            return "Folder";
        }

        private static void FillPhoto(ChatViewModel vm, JObject photo)
        {
            if (vm == null || photo == null) return;
            var small = photo["small"] as JObject;
            var local = small == null ? null : small["local"] as JObject;
            vm.AvatarUri = ToFileUri(ReadString(local == null ? null : local["path"], ""));
            vm.AvatarPhotoId = ReadLong(photo["id"]);
        }

        private void FillPhotoForList(ChatViewModel vm, JObject photo)
        {
            if (vm == null || photo == null) return;
            var small = photo["small"] as JObject ?? photo["big"] as JObject;
            if (small == null) return;

            var path = ReadFilePath(small);
            if (!string.IsNullOrEmpty(path))
            {
                vm.AvatarUri = ToFileUri(path);
                vm.AvatarPhotoId = ReadLong(photo["id"]);
                return;
            }

            var fileId = ReadLong(small["id"]);
            if (fileId == 0) return;
            vm.AvatarPhotoId = ReadLong(photo["id"]);
            RegisterAvatarTarget(fileId, vm);
            SendFireAndForget(new JObject { ["@type"] = "downloadFile", ["file_id"] = fileId, ["priority"] = 1, ["synchronous"] = false });
        }

        private async Task FillPhotoAsync(ChatViewModel vm, JObject photo)
        {
            if (vm == null || photo == null) return;
            var big = photo["big"] as JObject ?? photo["small"] as JObject;
            vm.AvatarPhotoId = ReadLong(photo["id"]);
            vm.AvatarUri = ToFileUri(ReadFilePath(big));
            if (!string.IsNullOrEmpty(vm.AvatarUri)) return;

            vm.AvatarUri = await DownloadSmallFilePathAsync(big);
        }

        private async Task<string> DownloadSmallFilePathAsync(JObject file)
        {
            var path = ReadFilePath(file);
            if (!string.IsNullOrEmpty(path)) return ToFileUri(path);

            var fileId = ReadLong(file == null ? null : file["id"]);
            if (fileId == 0) return "";

            try
            {
                var downloaded = await SendAsync(new JObject
                {
                    ["@type"] = "downloadFile",
                    ["file_id"] = fileId,
                    ["priority"] = 24,
                    ["offset"] = 0,
                    ["limit"] = 0,
                    ["synchronous"] = true
                }, TimeSpan.FromSeconds(15));
                return ToFileUri(ReadFilePath(downloaded));
            }
            catch
            {
                return "";
            }
        }

        public async Task<ChatWallpaperInfo> GetChatWallpaperAsync(long chatId)
        {
            await StartAsync();
            if (chatId == 0) return null;

            JObject chat;
            try
            {
                chat = await SendAsync(new JObject
                {
                    ["@type"] = "getChat",
                    ["chat_id"] = chatId,
                    ["@extra"] = NextExtra()
                }, TimeSpan.FromSeconds(15));
            }
            catch
            {
                return null;
            }

            var chatBackground = chat == null ? null : chat["background"] as JObject;
            if (chatBackground == null) return null;

            var background = chatBackground["background"] as JObject;
            if (background == null) return null;

            var type = background["type"] as JObject;
            var typeName = ReadString(type == null ? null : type["@type"], "");

            var info = new ChatWallpaperInfo();

            if (typeName == "backgroundTypeWallpaper")
            {
                info.IsBlurred = ReadBool(type["is_blurred"]);
                var document = background["document"] as JObject;
                var file = document == null ? null : document["document"] as JObject;
                if (file != null)
                {
                    try { info.ImageUri = await DownloadSmallFilePathAsync(file); }
                    catch { }
                }
            }

            // Solid/gradient fill: on backgroundTypeFill it is the type itself, on
            // backgroundTypePattern it is the "fill" sub-object.
            ReadBackgroundFill(type == null ? null : type["fill"] as JObject, info);
            if (!info.HasFill)
                ReadBackgroundFill(type, info);

            return info.IsEmpty ? null : info;
        }

        private void ReadBackgroundFill(JObject fill, ChatWallpaperInfo info)
        {
            if (fill == null || info == null) return;
            var fillType = ReadString(fill["@type"], "");
            if (fillType == "backgroundFillSolid")
            {
                info.HasSolid = true;
                info.SolidColor = (int)ReadLong(fill["color"]);
            }
            else if (fillType == "backgroundFillGradient")
            {
                info.HasGradient = true;
                info.GradientTopColor = (int)ReadLong(fill["top_color"]);
                info.GradientBottomColor = (int)ReadLong(fill["bottom_color"]);
                info.GradientRotation = (int)ReadLong(fill["rotation_angle"]);
            }
            else if (fillType == "backgroundFillFreeformGradient")
            {
                var colors = fill["colors"] as JArray;
                if (colors != null)
                {
                    var list = new List<int>();
                    foreach (var c in colors) list.Add((int)ReadLong(c));
                    info.FreeformColors = list.ToArray();
                }
            }
        }

        private static JObject ReadBestPhotoFile(JObject photo)
        {
            if (photo == null) return null;
            var sizes = photo["sizes"] as JArray;
            JObject best = null;
            long bestScore = 0;
            if (sizes != null)
            {
                foreach (var size in sizes.OfType<JObject>())
                {
                    var file = size["photo"] as JObject;
                    var score = ReadLong(file == null ? null : file["size"]);
                    if (score == 0) score = ReadLong(size["width"]) * ReadLong(size["height"]);
                    if (file != null && score >= bestScore)
                    {
                        best = file;
                        bestScore = score;
                    }
                }
            }
            if (best != null) return best;
            return photo["small"] as JObject;
        }

        private static string ReadFilePath(JObject file)
        {
            if (file == null) return "";
            var local = file["local"] as JObject;
            return ReadString(local == null ? null : local["path"], "");
        }

        private static string ToImageFileUri(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                var localRoot = ApplicationData.Current.LocalFolder.Path;
                var normalizedPath = path.Replace('\\', '/');
                var normalizedRoot = string.IsNullOrEmpty(localRoot)
                    ? string.Empty
                    : localRoot.Replace('\\', '/').TrimEnd('/');
                if (!string.IsNullOrEmpty(normalizedRoot) &&
                    normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    var relative = normalizedPath.Substring(normalizedRoot.Length).TrimStart('/');
                    var parts = relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 0; i < parts.Length; i++)
                        parts[i] = Uri.EscapeDataString(parts[i]);
                    return "ms-appdata:///local/" + string.Join("/", parts);
                }
            }
            catch
            {
            }
            return ToFileUri(path);
        }

        private static string ToFileUri(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    return new Uri(path).AbsoluteUri;
                return new Uri(path).AbsoluteUri;
            }
            catch
            {
                return "file:///" + path.Replace("\\", "/");
            }
        }

        private static byte[] ReadBytes(JToken token)
        {
            var text = ReadString(token, "");
            if (string.IsNullOrEmpty(text)) return null;
            try { return Convert.FromBase64String(text); }
            catch { return null; }
        }

        private static async Task<StorageFile> TryGetStorageFileAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    return await StorageFile.GetFileFromPathAsync(new Uri(path).LocalPath);
                return await StorageFile.GetFileFromPathAsync(path);
            }
            catch { return null; }
        }

        private static string ExtractUsername(string link)
        {
            if (string.IsNullOrWhiteSpace(link)) return "";
            Uri uri;
            if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out uri)) return "";
            if (!uri.Host.EndsWith("t.me", StringComparison.OrdinalIgnoreCase)) return "";
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length == 0) return "";
            if (parts[0] == "c") return "";
            return parts[0];
        }

        private static int ExtractMessageId(string link)
        {
            Uri uri;
            if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out uri)) return 0;
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            int id;
            return parts.Length > 1 && int.TryParse(parts[1], out id) ? id : 0;
        }

        private static string ReadUsername(JObject user)
        {
            var usernames = user == null ? null : user["usernames"] as JObject;
            var active = usernames == null ? null : usernames["active_usernames"] as JArray;
            if (active != null && active.Count > 0) return ReadString(active[0], "");
            return ReadString(user == null ? null : user["username"], "");
        }

        private static string BuildInitials(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "?";
            var parts = title.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return parts[0].Substring(0, 1).ToUpperInvariant();
            return (parts[0].Substring(0, 1) + parts[parts.Length - 1].Substring(0, 1)).ToUpperInvariant();
        }

        private static long UnixNow()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        }

        private static long ReadLong(JToken token)
        {
            if (token == null) return 0;
            long value;
            return long.TryParse(token.ToString(), out value) ? value : 0;
        }

        private static int ReadInt(JToken token)
        {
            if (token == null) return 0;
            int value;
            return int.TryParse(token.ToString(), out value) ? value : 0;
        }

        private static bool ReadBool(JToken token)
        {
            if (token == null) return false;
            bool value;
            return bool.TryParse(token.ToString(), out value) && value;
        }

        private static bool ReadBoolDefault(JToken token, bool fallback)
        {
            if (token == null) return fallback;
            bool value;
            return bool.TryParse(token.ToString(), out value) ? value : fallback;
        }

        private static string ReadString(JToken token, string fallback)
        {
            if (token == null) return fallback;
            var value = token.ToString();
            return value ?? fallback;
        }
    }

    internal sealed class PhoneCodeResponse
    {
        public bool Authorized { get; set; }
        public string PhoneCodeHash { get; set; }
        public string CodeType { get; set; }
        public int Length { get; set; }
        public int Timeout { get; set; }
    }

    internal sealed class LoginTokenResponse
    {
        public bool Success { get; set; }
        public bool Expired { get; set; }
        public byte[] Token { get; set; }
        public string Link { get; set; }
        public long Expires { get; set; }
    }
}
