using System;
using System.Collections.Generic;
using Windows.Storage;

namespace Telegram
{
    internal enum TelegramNotificationMode
    {
        Periodic = 0,
        Always = 1,
        None = 2,
        FixedSystem = 3
    }

    internal static class TelegramAppSettings
    {
        public const bool FixedSystemNotificationsAvailable = false;
        private const string NotificationsEnabledKey = "TelegramNotificationsEnabled";
        private const string NotificationModeKey = "TelegramNotificationMode";
        private const string ContactSyncPromptEnabledKey = "TelegramContactSyncPromptEnabled";
        private const string ChatPageMessageBatchSizeKey = "TelegramChatPageMessageBatchSize";
        private const string ChatsInitialDisplayCountKey = "TelegramChatsInitialDisplayCount";
        private const string ChatsIncrementalDisplayCountKey = "TelegramChatsIncrementalDisplayCount";
        private const string ChatsShowAllImmediatelyKey = "TelegramChatsShowAllImmediately";
        private const string GlassEffectEnabledKey = "TelegramGlassEffectEnabled";
        private const string WallpaperDimmingKey = "TelegramWallpaperDimming";
        private const string LiveTileEnabledKey = "TelegramLiveTileEnabled";
        private const string ChatAutoDownloadPhotosEnabledKey = "TelegramChatAutoDownloadPhotosEnabled";
        private const string ChatAutoDownloadGifsEnabledKey = "TelegramChatAutoDownloadGifsEnabled";
        private const string ChatAutoDownloadStickersEnabledKey = "TelegramChatAutoDownloadStickersEnabled";
        private const string ChatAutoDownloadVideosEnabledKey = "TelegramChatAutoDownloadVideosEnabled";
        private const string ChatAutoDownloadOtherEnabledKey = "TelegramChatAutoDownloadOtherEnabled";
        private const int DefaultChatPageMessageBatchSize = 20;
        private const int MaxChatPageMessageBatchSize = 20;
        private const int DefaultChatsDisplayCount = 20;
        private const int MaxChatsDisplayCount = 300;

        public static bool NotificationsEnabled
        {
            get
            {
                return NotificationMode != TelegramNotificationMode.None;
            }
            set
            {
                NotificationMode = value ? TelegramNotificationMode.Periodic : TelegramNotificationMode.None;
            }
        }

        public static TelegramNotificationMode NotificationMode
        {
            get
            {
                object modeValue;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(NotificationModeKey, out modeValue) && modeValue != null)
                {
                    var modeText = modeValue.ToString();
                    int modeNumber;
                    if (int.TryParse(modeText, out modeNumber) && IsValidNotificationMode(modeNumber))
                        return (TelegramNotificationMode)modeNumber;

                    TelegramNotificationMode parsed;
                    if (Enum.TryParse<TelegramNotificationMode>(modeText, true, out parsed) && IsValidNotificationMode((int)parsed))
                        return parsed;
                }

                return GetLegacyNotificationsEnabled() ? TelegramNotificationMode.Periodic : TelegramNotificationMode.None;
            }
            set
            {
                if (!IsValidNotificationMode((int)value))
                    value = TelegramNotificationMode.Periodic;

                ApplicationData.Current.LocalSettings.Values[NotificationModeKey] = ((int)value).ToString();
                ApplicationData.Current.LocalSettings.Values[NotificationsEnabledKey] = value != TelegramNotificationMode.None;
            }
        }

        private static bool IsValidNotificationMode(int value)
        {
            return value == (int)TelegramNotificationMode.Periodic ||
                value == (int)TelegramNotificationMode.Always ||
                value == (int)TelegramNotificationMode.None ||
                (FixedSystemNotificationsAvailable && value == (int)TelegramNotificationMode.FixedSystem);
        }

        private static bool GetLegacyNotificationsEnabled()
        {
            try
            {
                object value;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(NotificationsEnabledKey, out value) || value == null)
                    return true;

                try
                {
                    return Convert.ToBoolean(value);
                }
                catch
                {
                    return true;
                }
            }
            catch
            {
                return true;
            }
        }

        public static bool ContactSyncPromptEnabled
        {
            get
            {
                object value;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(ContactSyncPromptEnabledKey, out value) || value == null)
                    return true;

                try
                {
                    return Convert.ToBoolean(value);
                }
                catch
                {
                    return true;
                }
            }
            set
            {
                ApplicationData.Current.LocalSettings.Values[ContactSyncPromptEnabledKey] = value;
            }
        }

        public static bool GlassEffectEnabled
        {
            get
            {
                object value;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(GlassEffectEnabledKey, out value) || value == null)
                    return true;

                try
                {
                    return Convert.ToBoolean(value);
                }
                catch
                {
                    return true;
                }
            }
            set
            {
                ApplicationData.Current.LocalSettings.Values[GlassEffectEnabledKey] = value;
            }
        }

        public static int ChatPageMessageBatchSize
        {
            get
            {
                object value;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(ChatPageMessageBatchSizeKey, out value) || value == null)
                    return DefaultChatPageMessageBatchSize;

                int size;
                if (int.TryParse(value.ToString(), out size))
                    return NormalizeMessageBatchSize(size);

                return DefaultChatPageMessageBatchSize;
            }
            set
            {
                ApplicationData.Current.LocalSettings.Values[ChatPageMessageBatchSizeKey] = NormalizeMessageBatchSize(value);
            }
        }

        // Chat wallpaper dimming, 0-80 percent.
        public static int WallpaperDimming
        {
            get
            {
                object value;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(WallpaperDimmingKey, out value) || value == null)
                    return 20;

                int dim;
                if (int.TryParse(value.ToString(), out dim))
                    return NormalizeWallpaperDimming(dim);

                return 20;
            }
            set
            {
                ApplicationData.Current.LocalSettings.Values[WallpaperDimmingKey] = NormalizeWallpaperDimming(value);
            }
        }

        public static int NormalizeWallpaperDimming(int value)
        {
            if (value < 0) return 0;
            if (value > 80) return 80;
            return value;
        }

        public static int NormalizeMessageBatchSize(int value)
        {
            if (value < DefaultChatPageMessageBatchSize) return DefaultChatPageMessageBatchSize;
            if (value > MaxChatPageMessageBatchSize) return MaxChatPageMessageBatchSize;
            return value;
        }

        // These are read once per message row while a chat is being scrolled. Each read is a
        // chain of WinRT calls (ApplicationData.Current -> LocalSettings -> Values -> TryGetValue),
        // which is far too expensive for that frequency, so the values are cached in memory and
        // refreshed by the setters.
        private static readonly Dictionary<string, bool> CachedBooleanSettings = new Dictionary<string, bool>();
        private static readonly object CachedBooleanSettingsGate = new object();

        private static bool GetCachedBooleanSetting(string key, bool defaultValue)
        {
            bool cached;
            lock (CachedBooleanSettingsGate)
            {
                if (CachedBooleanSettings.TryGetValue(key, out cached)) return cached;
            }

            var value = GetBooleanSetting(key, defaultValue);

            lock (CachedBooleanSettingsGate)
                CachedBooleanSettings[key] = value;

            return value;
        }

        private static void SetCachedBooleanSetting(string key, bool value)
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
            lock (CachedBooleanSettingsGate)
                CachedBooleanSettings[key] = value;
        }

        public static bool ChatAutoDownloadPhotosEnabled
        {
            get { return GetCachedBooleanSetting(ChatAutoDownloadPhotosEnabledKey, true); }
            set { SetCachedBooleanSetting(ChatAutoDownloadPhotosEnabledKey, value); }
        }

        public static bool ChatAutoDownloadGifsEnabled
        {
            get { return GetCachedBooleanSetting(ChatAutoDownloadGifsEnabledKey, true); }
            set { SetCachedBooleanSetting(ChatAutoDownloadGifsEnabledKey, value); }
        }

        public static bool ChatAutoDownloadStickersEnabled
        {
            get { return GetCachedBooleanSetting(ChatAutoDownloadStickersEnabledKey, true); }
            set { SetCachedBooleanSetting(ChatAutoDownloadStickersEnabledKey, value); }
        }

        public static bool ChatAutoDownloadVideosEnabled
        {
            get { return GetCachedBooleanSetting(ChatAutoDownloadVideosEnabledKey, false); }
            set { SetCachedBooleanSetting(ChatAutoDownloadVideosEnabledKey, value); }
        }

        public static bool ChatAutoDownloadOtherEnabled
        {
            get { return GetCachedBooleanSetting(ChatAutoDownloadOtherEnabledKey, false); }
            set { SetCachedBooleanSetting(ChatAutoDownloadOtherEnabledKey, value); }
        }

        public static bool AnyChatAutoDownloadEnabled
        {
            get
            {
                return ChatAutoDownloadPhotosEnabled ||
                    ChatAutoDownloadGifsEnabled ||
                    ChatAutoDownloadStickersEnabled ||
                    ChatAutoDownloadVideosEnabled ||
                    ChatAutoDownloadOtherEnabled;
            }
        }

        public static int ChatsInitialDisplayCount
        {
            get
            {
                object value;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(ChatsInitialDisplayCountKey, out value) || value == null)
                    return DefaultChatsDisplayCount;

                int size;
                if (int.TryParse(value.ToString(), out size))
                    return NormalizeChatsDisplayCount(size);

                return DefaultChatsDisplayCount;
            }
            set
            {
                ApplicationData.Current.LocalSettings.Values[ChatsInitialDisplayCountKey] = NormalizeChatsDisplayCount(value);
            }
        }

        public static int ChatsIncrementalDisplayCount
        {
            get
            {
                object value;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(ChatsIncrementalDisplayCountKey, out value) || value == null)
                    return DefaultChatsDisplayCount;

                int size;
                if (int.TryParse(value.ToString(), out size))
                    return NormalizeChatsDisplayCount(size);

                return DefaultChatsDisplayCount;
            }
            set
            {
                ApplicationData.Current.LocalSettings.Values[ChatsIncrementalDisplayCountKey] = NormalizeChatsDisplayCount(value);
            }
        }

        public static bool ChatsShowAllImmediately
        {
            get
            {
                object value;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(ChatsShowAllImmediatelyKey, out value) || value == null)
                    return false;

                try
                {
                    return Convert.ToBoolean(value);
                }
                catch
                {
                    return false;
                }
            }
            set
            {
                ApplicationData.Current.LocalSettings.Values[ChatsShowAllImmediatelyKey] = value;
            }
        }

        public static int NormalizeChatsDisplayCount(int value)
        {
            if (value < DefaultChatsDisplayCount) return DefaultChatsDisplayCount;
            if (value > MaxChatsDisplayCount) return MaxChatsDisplayCount;
            return value;
        }

        public static bool LiveTileEnabled
        {
            get
            {
                object value;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(LiveTileEnabledKey, out value) || value == null)
                    return true;

                try
                {
                    return Convert.ToBoolean(value);
                }
                catch
                {
                    return true;
                }
            }
            set
            {
                ApplicationData.Current.LocalSettings.Values[LiveTileEnabledKey] = value;
            }
        }

        private static bool GetBooleanSetting(string key, bool defaultValue)
        {
            object value;
            if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out value) || value == null)
                return defaultValue;

            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}
