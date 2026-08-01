using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Models;
using Telegram.Services;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Notifications;
using Windows.UI.Xaml;
using Windows.Data.Xml.Dom;

namespace Telegram.Notifications
{
    internal static class TelegramNotificationRuntime
    {
        private const int DialogPollLimit = 40;
        private const string ReplyInputId = "replyText";
        private const string ToastGroup = "telegram";
        private const string StateFileName = "telegram-notification-state.txt";
        private const string RegisteredAtKey = "TelegramNotificationRegisteredAt";
        private const string AuthorizationBaselineReadyKey = "TelegramNotificationAuthorizationBaselineReady";
        private const string StateVersionKey = "TelegramNotificationStateVersion";
        private const int CurrentStateVersion = 2;
        private static readonly SemaphoreSlim NotificationGate = new SemaphoreSlim(1, 1);

        public static void EnsureRegistrationMarker()
        {
            if (!TelegramAppSettings.NotificationsEnabled) return;
            var values = ApplicationData.Current.LocalSettings.Values;
            if (!values.ContainsKey(RegisteredAtKey))
                values[RegisteredAtKey] = UnixNow();
        }

        public static async Task MarkAuthorizationReadyAsync()
        {
            await NotificationGate.WaitAsync();
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                object readyValue;
                var baselineReady = values.TryGetValue(AuthorizationBaselineReadyKey, out readyValue) &&
                    readyValue != null && Convert.ToBoolean(readyValue);

                if (baselineReady && GetStateVersion() == CurrentStateVersion)
                    return;

                values[RegisteredAtKey] = UnixNow();
                values[AuthorizationBaselineReadyKey] = true;
                await NotificationState.SaveAsync(new Dictionary<string, long>());
                values[StateVersionKey] = CurrentStateVersion;
                TelegramContinuousNotificationPoller.Diag("Notification baseline reset at authorization.");
            }
            catch (Exception ex)
            {
                TelegramContinuousNotificationPoller.Diag("Notification baseline reset failed: " + ex.Message);
            }
            finally
            {
                NotificationGate.Release();
            }
        }

        public static void ClearAuthorizationBaseline()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                values.Remove(RegisteredAtKey);
                values.Remove(AuthorizationBaselineReadyKey);
                values.Remove(StateVersionKey);
            }
            catch
            {
            }
        }

        public static async Task PollAndNotifyAsync()
        {
            if (!TelegramAppSettings.NotificationsEnabled)
                return;

            await NotificationGate.WaitAsync();
            try
            {
                if (!TelegramAppSettings.NotificationsEnabled)
                    return;

                if (!await TelegramService.Instance.IsAuthorizedAsync())
                    return;

                await EnsureNotificationStateVersionAsync();

                var dialogs = await TelegramService.Instance.GetNotificationDialogsAsync(DialogPollLimit);
                if (dialogs == null || dialogs.Count == 0)
                    return;

                var state = await NotificationState.LoadAsync();
                var hasBaseline = state.Count > 0;
                var registeredAt = GetRegisteredAt();
                var changed = false;

                for (var i = 0; i < dialogs.Count; i++)
                {
                    var chat = dialogs[i];
                    var currentMessageId = GetNotificationMessageId(chat);
                    if (chat == null || string.IsNullOrEmpty(chat.PeerKey) || currentMessageId <= 0)
                        continue;
                    if (IsArchivedChat(chat))
                        continue;

                    long previousMessageId;
                    var known = state.TryGetValue(chat.PeerKey, out previousMessageId);
                    if (!known || currentMessageId > previousMessageId)
                    {
                        state[chat.PeerKey] = currentMessageId;
                        changed = true;
                    }

                    if ((!hasBaseline || !known) && chat.LastMessageDate <= registeredAt)
                        continue;

                    if (currentMessageId <= previousMessageId || chat.UnreadCount <= 0 || chat.IsMuted)
                        continue;

                    if (ActiveChatService.IsActive(chat))
                        continue;

                    var shown = await ShowNewMessageToastsAsync(chat, previousMessageId, registeredAt);
                    if (!shown && !chat.LastMessageIsOutgoing)
                        ShowToast(chat, currentMessageId, chat.LastMessage);
                }

                if (changed)
                    await NotificationState.SaveAsync(state);
            }
            finally
            {
                NotificationGate.Release();
            }
        }

        public static async Task NotifyRealtimeMessageAsync(ChatViewModel chat, long messageId, string messageText, bool isOutgoing, int messageDate, string senderName)
        {
            if (!TelegramAppSettings.NotificationsEnabled)
                return;
            if (isOutgoing)
                return;
            if (chat == null || string.IsNullOrEmpty(chat.PeerKey) || messageId <= 0)
                return;
            if (IsArchivedChat(chat))
                return;
            if (chat.IsMuted)
                return;
            if (ActiveChatService.IsActive(chat))
                return;

            await NotificationGate.WaitAsync();
            try
            {
                var stateReset = await EnsureNotificationStateVersionAsync();
                var registeredAt = GetRegisteredAt();
                if (!stateReset && messageDate > 0 && messageDate <= registeredAt)
                    return;

                var state = await NotificationState.LoadAsync();
                long previousMessageId;
                if (state.TryGetValue(chat.PeerKey, out previousMessageId) && messageId <= previousMessageId)
                    return;

                state[chat.PeerKey] = messageId;
                await NotificationState.SaveAsync(state);

                if (!ActiveChatService.IsActive(chat))
                {
                    TelegramContinuousNotificationPoller.Diag("Realtime toast requested. peer=" + chat.PeerKey + " message=" + messageId.ToString());
                    ShowToast(chat, messageId, messageText, senderName);
                }
            }
            finally
            {
                NotificationGate.Release();
            }
        }

        public static async Task<bool> HandleToastActionAsync(string arguments, ValueSet userInput)
        {
            var values = NotificationArguments.Parse(arguments);
            string action;
            values.TryGetValue("action", out action);

            if (action == "open" || string.IsNullOrEmpty(action))
                return await OpenChatFromToastAsync(values);

            if (action != "reply" || !TelegramAppSettings.NotificationsEnabled)
                return false;

            var replyText = GetReplyText(userInput);
            if (string.IsNullOrWhiteSpace(replyText))
                return false;

            string peerType;
            string peerIdText;
            string accessHashText;
            string canSendText;
            if (!values.TryGetValue("type", out peerType) ||
                !values.TryGetValue("id", out peerIdText) ||
                !values.TryGetValue("hash", out accessHashText))
                return false;

            values.TryGetValue("canSend", out canSendText);

            long peerId;
            long accessHash;
            if (!long.TryParse(peerIdText, out peerId))
                return false;
            if (!long.TryParse(accessHashText, out accessHash))
                accessHash = 0;

            var canSend = canSendText == "1";
            await TelegramService.Instance.SendNotificationReplyAsync(peerType, peerId, accessHash, canSend, replyText);

            string tag;
            if (values.TryGetValue("tag", out tag))
                RemoveToastByTag(tag);

            return true;
        }

        private static string GetReplyText(ValueSet userInput)
        {
            if (userInput == null) return null;
            object value;
            if (!userInput.TryGetValue(ReplyInputId, out value)) return null;
            return value == null ? null : value.ToString();
        }

        private static long GetNotificationMessageId(ChatViewModel chat)
        {
            if (chat == null) return 0;
            return chat.NotificationMessageId > 0 ? chat.NotificationMessageId : chat.TopMessageId;
        }

        private static int GetStateVersion()
        {
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(StateVersionKey, out value) && value != null)
                {
                    int version;
                    if (int.TryParse(value.ToString(), out version))
                        return version;
                }
            }
            catch
            {
            }
            return 0;
        }

        private static async Task<bool> EnsureNotificationStateVersionAsync()
        {
            if (GetStateVersion() == CurrentStateVersion)
                return false;

            await NotificationState.SaveAsync(new Dictionary<string, long>());
            var values = ApplicationData.Current.LocalSettings.Values;
            values[RegisteredAtKey] = UnixNow();
            values[StateVersionKey] = CurrentStateVersion;
            TelegramContinuousNotificationPoller.Diag("Notification state upgraded to stable TDLib message ids.");
            return true;
        }

        private static int GetRegisteredAt()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                object value;
                if (values.TryGetValue(RegisteredAtKey, out value) && value != null)
                    return Convert.ToInt32(value);
            }
            catch
            {
            }
            return UnixNow();
        }

        private static int UnixNow()
        {
            return (int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        }

        private static async Task<bool> ShowNewMessageToastsAsync(ChatViewModel chat, long previousMessageId, int registeredAt)
        {
            try
            {
                var messages = await TelegramService.Instance.GetHistorySinceAsync(chat, 0, Math.Min(Math.Max(chat.UnreadCount, 1), 10));
                if (messages == null || messages.Count == 0)
                    return false;
                if (ActiveChatService.IsActive(chat))
                    return true;

                var shown = false;
                for (var i = 0; i < messages.Count; i++)
                {
                    var message = messages[i];
                    if (message == null || message.IsOutgoing)
                        continue;

                    var notificationMessageId = message.SortId > 0 ? message.SortId : message.Id;
                    if (notificationMessageId <= previousMessageId)
                        continue;
                    if (message.Date > 0 && message.Date <= registeredAt && previousMessageId <= 0)
                        continue;

                    if (!ActiveChatService.IsActive(chat))
                        ShowToast(chat, notificationMessageId, BuildMessagePreview(chat, message), message.SenderName);
                    shown = true;
                }

                return shown;
            }
            catch
            {
                return false;
            }
        }

        private static string BuildMessagePreview(ChatViewModel chat, ChatMessageViewModel message)
        {
            if (message == null) return null;

            var text = message.Text;
            if (string.IsNullOrWhiteSpace(text))
                text = message.HasMedia ? "Media message" : "New message";

            return text;
        }

        private static void ShowToast(ChatViewModel chat, long messageId, string messageText)
        {
            ShowToast(chat, messageId, messageText, null);
        }

        private static void ShowToast(ChatViewModel chat, long messageId, string messageText, string senderName)
        {
            try
            {
                if (!TelegramAppSettings.NotificationsEnabled)
                    return;
                if (IsArchivedChat(chat))
                    return;
                if (ActiveChatService.IsActive(chat))
                    return;

                var title = BuildToastTitle(chat, senderName);
                var text = BuildToastText(messageText, senderName);
                var tag = BuildToastTag(chat.PeerKey, messageId);
                var arguments = BuildArguments(chat, "open", messageId, tag);
                var replyArguments = BuildArguments(chat, "reply", messageId, tag);
                var xml = BuildToastXml(title, text, arguments, replyArguments, chat.CanSendMessages);

                var document = new XmlDocument();
                document.LoadXml(xml);

                var toast = new ToastNotification(document);
                toast.Group = ToastGroup;
                toast.Tag = tag;
                ToastNotificationManager.CreateToastNotifier().Show(toast);
                TelegramContinuousNotificationPoller.Diag("Toast shown. peer=" + chat.PeerKey + " message=" + messageId.ToString());
                TelegramLiveTileRuntime.UpdateFromNotification(chat, text);
            }
            catch (Exception ex)
            {
                TelegramContinuousNotificationPoller.Diag("Toast failed: " + ex.Message);
            }
        }

        private const int ArchiveFolderId = 1;

        private static bool IsArchivedChat(ChatViewModel chat)
        {
            if (chat == null) return false;

            // FolderId carries the chat list a chat was loaded for, which becomes a
            // custom folder id for archived chats that also live in a folder, so it
            // cannot be the only archive check.
            return chat.IsArchived || chat.FolderId == ArchiveFolderId;
        }

        private static string BuildToastTitle(ChatViewModel chat, string senderName)
        {
            if (!string.IsNullOrWhiteSpace(senderName))
                return SingleLine(senderName, 80);

            var title = chat == null ? "" : chat.Title;
            if (string.IsNullOrWhiteSpace(title))
                return "Telegram";

            title = title.Trim();
            return string.IsNullOrWhiteSpace(title) ? "Telegram" : SingleLine(title, 80);
        }

        private static string BuildToastText(string messageText, string senderName)
        {
            var text = string.IsNullOrWhiteSpace(messageText) ? "New message" : messageText.Trim();
            if (!string.IsNullOrWhiteSpace(senderName))
            {
                var prefix = senderName.Trim() + ": ";
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    text = text.Substring(prefix.Length).Trim();
            }
            return SingleLine(text, 180);
        }

        private static string BuildToastXml(string title, string text, string launchArguments, string replyArguments, bool canReply)
        {
            var builder = new StringBuilder();
            builder.Append("<toast launch=\"").Append(XmlEscape(launchArguments)).Append("\">");
            builder.Append("<visual><binding template=\"ToastGeneric\">");
            builder.Append("<text>").Append(XmlEscape(title)).Append("</text>");
            builder.Append("<text>").Append(XmlEscape(text)).Append("</text>");
            builder.Append("</binding></visual>");

            if (canReply)
            {
                builder.Append("<actions>");
                builder.Append("<input id=\"").Append(ReplyInputId).Append("\" type=\"text\" placeHolderContent=\"Reply\" />");
                builder.Append("<action content=\"Send\" activationType=\"background\" arguments=\"")
                    .Append(XmlEscape(replyArguments))
                    .Append("\" hint-inputId=\"").Append(ReplyInputId).Append("\" />");
                builder.Append("</actions>");
            }

            builder.Append("</toast>");
            return builder.ToString();
        }

        private static string BuildArguments(ChatViewModel chat, string action, long messageId, string tag)
        {
            var values = new Dictionary<string, string>();
            values["action"] = action;
            values["peer"] = chat.PeerKey ?? MakePeerKey(chat.PeerType, chat.PeerId);
            values["type"] = chat.PeerType ?? string.Empty;
            values["id"] = chat.PeerId.ToString();
            values["hash"] = chat.AccessHash.ToString();
            values["canSend"] = chat.CanSendMessages ? "1" : "0";
            values["title"] = chat.Title ?? string.Empty;
            values["avatar"] = chat.AvatarUri ?? string.Empty;
            values["group"] = chat.IsGroup ? "1" : "0";
            values["channel"] = chat.IsChannel ? "1" : "0";
            values["broadcast"] = chat.IsBroadcast ? "1" : "0";
            values["joined"] = chat.IsJoined ? "1" : "0";
            values["msg"] = messageId.ToString();
            values["tag"] = tag ?? string.Empty;
            return NotificationArguments.Build(values);
        }

        private static async Task<bool> OpenChatFromToastAsync(Dictionary<string, string> values)
        {
            var chat = BuildChatFromArguments(values);
            if (chat == null)
                return false;

            var dispatcher = Window.Current == null ? null : Window.Current.Dispatcher;
            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                AdaptiveShellNavigationService.NavigateChatFromExternalActivation(chat);
                return true;
            }

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                AdaptiveShellNavigationService.NavigateChatFromExternalActivation(chat);
            });
            return true;
        }

        private static ChatViewModel BuildChatFromArguments(Dictionary<string, string> values)
        {
            if (values == null) return null;

            string peerType;
            string peerIdText;
            string accessHashText;
            if (!values.TryGetValue("type", out peerType) ||
                !values.TryGetValue("id", out peerIdText) ||
                !values.TryGetValue("hash", out accessHashText))
                return null;

            long peerId;
            long accessHash;
            if (!long.TryParse(peerIdText, out peerId))
                return null;
            if (!long.TryParse(accessHashText, out accessHash))
                accessHash = 0;

            string peerKey;
            string title;
            string avatar;
            string canSendText;
            values.TryGetValue("peer", out peerKey);
            values.TryGetValue("title", out title);
            values.TryGetValue("avatar", out avatar);
            values.TryGetValue("canSend", out canSendText);

            var chat = new ChatViewModel();
            chat.PeerType = peerType;
            chat.PeerId = peerId;
            chat.PeerKey = string.IsNullOrEmpty(peerKey) ? MakePeerKey(peerType, peerId) : peerKey;
            chat.AccessHash = accessHash;
            chat.Title = string.IsNullOrWhiteSpace(title) ? "Telegram" : title;
            chat.IconText = BuildInitials(chat.Title);
            chat.AvatarUri = avatar;
            chat.CanSendMessages = canSendText == "1";
            chat.IsGroup = GetBool(values, "group");
            chat.IsChannel = GetBool(values, "channel");
            chat.IsBroadcast = GetBool(values, "broadcast");
            chat.IsJoined = !values.ContainsKey("joined") || GetBool(values, "joined");
            return chat;
        }

        private static bool GetBool(Dictionary<string, string> values, string key)
        {
            string text;
            return values != null && values.TryGetValue(key, out text) && text == "1";
        }

        private static string BuildInitials(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "T";
            title = title.Trim();
            return title.Substring(0, 1).ToUpper();
        }

        private static void RemoveToastByTag(string tag)
        {
            try
            {
                if (!string.IsNullOrEmpty(tag))
                    ToastNotificationManager.History.Remove(tag, ToastGroup);
            }
            catch
            {
            }
        }

        private static string BuildToastTag(string peerKey, long messageId)
        {
            return "tg" + StableHash(peerKey).ToString("x8") + "_" + messageId.ToString();
        }

        private static uint StableHash(string value)
        {
            unchecked
            {
                var hash = 2166136261u;
                if (value != null)
                {
                    for (var i = 0; i < value.Length; i++)
                    {
                        hash ^= value[i];
                        hash *= 16777619u;
                    }
                }
                return hash;
            }
        }

        private static string MakePeerKey(string peerType, long peerId)
        {
            if (string.IsNullOrEmpty(peerType) || peerId == 0) return string.Empty;
            return peerType + ":" + peerId.ToString();
        }

        private static string SingleLine(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
                text = text.Replace("  ", " ");
            if (maxLength > 0 && text.Length > maxLength)
                text = text.Substring(0, maxLength) + "...";
            return text;
        }

        private static string XmlEscape(string value)
        {
            return SecurityElementEscape(value ?? string.Empty);
        }

        private static string SecurityElementEscape(string value)
        {
            return value.Replace("&", "&amp;")
                .Replace("\"", "&quot;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("'", "&apos;");
        }

        private static class NotificationArguments
        {
            public static string Build(Dictionary<string, string> values)
            {
                var builder = new StringBuilder();
                foreach (var item in values)
                {
                    if (builder.Length > 0) builder.Append("&");
                    builder.Append(Uri.EscapeDataString(item.Key ?? string.Empty));
                    builder.Append("=");
                    builder.Append(Uri.EscapeDataString(item.Value ?? string.Empty));
                }
                return builder.ToString();
            }

            public static Dictionary<string, string> Parse(string value)
            {
                var result = new Dictionary<string, string>();
                if (string.IsNullOrEmpty(value)) return result;

                var parts = value.Split('&');
                for (var i = 0; i < parts.Length; i++)
                {
                    var part = parts[i];
                    if (string.IsNullOrEmpty(part)) continue;

                    var equals = part.IndexOf('=');
                    var key = equals < 0 ? part : part.Substring(0, equals);
                    var val = equals < 0 ? string.Empty : part.Substring(equals + 1);
                    key = Uri.UnescapeDataString(key.Replace("+", " "));
                    val = Uri.UnescapeDataString(val.Replace("+", " "));
                    if (!string.IsNullOrEmpty(key)) result[key] = val;
                }

                return result;
            }
        }

        private static class NotificationState
        {
            public static async Task<Dictionary<string, long>> LoadAsync()
            {
                var result = new Dictionary<string, long>();
                try
                {
                    var file = await ApplicationData.Current.LocalFolder.GetFileAsync(StateFileName);
                    var text = await FileIO.ReadTextAsync(file);
                    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i];
                        var tab = line.IndexOf('\t');
                        if (tab <= 0) continue;

                        var key = Uri.UnescapeDataString(line.Substring(0, tab));
                        long topMessageId;
                        if (long.TryParse(line.Substring(tab + 1), out topMessageId) && !string.IsNullOrEmpty(key))
                            result[key] = topMessageId;
                    }
                }
                catch
                {
                }
                return result;
            }

            public static async Task SaveAsync(Dictionary<string, long> state)
            {
                var builder = new StringBuilder();
                if (state != null)
                {
                    foreach (var item in state)
                    {
                        builder.Append(Uri.EscapeDataString(item.Key ?? string.Empty));
                        builder.Append('\t');
                        builder.Append(item.Value);
                        builder.Append("\r\n");
                    }
                }

                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(StateFileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, builder.ToString());
            }
        }
    }
}
