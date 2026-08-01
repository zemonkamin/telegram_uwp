using System;
using System.Text;
using Telegram.Models;
using Windows.Data.Xml.Dom;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI.Notifications;

namespace Telegram.Notifications
{
    internal static class TelegramLiveTileRuntime
    {
        private const string AppIconUri = "ms-appx:///Assets/Square44x44Logo.png";
        private const string LastTitleKey = "TelegramLiveTileLastTitle";
        private const string LastTextKey = "TelegramLiveTileLastText";
        private const string LastImageKey = "TelegramLiveTileLastImage";

        public static void UpdateFromNotification(ChatViewModel chat, string messageText)
        {
            if (!TelegramAppSettings.LiveTileEnabled) return;

            var title = chat == null || string.IsNullOrWhiteSpace(chat.Title) ? "Telegram" : chat.Title.Trim();
            var text = Shorten(SingleLine(string.IsNullOrWhiteSpace(messageText) ? "New message" : messageText), 64);
            var image = chat == null ? string.Empty : (chat.AvatarUri ?? string.Empty);

            SaveLast(title, text, image);
            UpdateTile(title, text, image);
        }

        public static void RestoreLast()
        {
            if (!TelegramAppSettings.LiveTileEnabled)
            {
                Clear();
                return;
            }

            var values = ApplicationData.Current.LocalSettings.Values;
            var title = ReadSetting(values, LastTitleKey);
            var text = ReadSetting(values, LastTextKey);
            var image = ReadSetting(values, LastImageKey);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(text))
                return;

            UpdateTile(title, text, image);
        }

        public static void Clear()
        {
            try
            {
                TileUpdateManager.CreateTileUpdaterForApplication().Clear();
            }
            catch
            {
            }

            try
            {
                BadgeUpdateManager.CreateBadgeUpdaterForApplication().Clear();
            }
            catch
            {
            }
        }

        private static void UpdateTile(string title, string text, string image)
        {
            try
            {
                var document = new XmlDocument();
                document.LoadXml(BuildTileXml(title, text, image));

                var notification = new TileNotification(document);
                var updater = TileUpdateManager.CreateTileUpdaterForApplication();
                updater.EnableNotificationQueue(false);
                updater.Update(notification);
            }
            catch
            {
            }
        }

        private static string BuildTileXml(string title, string text, string image)
        {
            var builder = new StringBuilder();
            builder.Append("<tile><visual branding=\"name\">");
            AppendTileBinding(builder, "TileMedium", title, text, image, 2);
            AppendTileBinding(builder, "TileWide", title, text, image, 2);
            AppendTileBinding(builder, "TileLarge", title, text, image, 3);
            builder.Append("</visual></tile>");
            return builder.ToString();
        }

        private static void AppendTileBinding(StringBuilder builder, string template, string title, string text, string image, int maxLines)
        {
            builder.Append("<binding template=\"");
            builder.Append(template);
            builder.Append("\">");
            if (!string.IsNullOrWhiteSpace(image))
            {
                builder.Append("<image placement=\"background\" hint-overlay=\"70\" src=\"");
                builder.Append(EscapeAttribute(image));
                builder.Append("\"/>");
            }
            else
            {
                builder.Append("<image src=\"");
                builder.Append(AppIconUri);
                builder.Append("\" placement=\"inline\" hint-align=\"left\"/>");
            }
            builder.Append("<text hint-style=\"caption\" hint-wrap=\"false\">");
            builder.Append(EscapeText(Shorten(title, 28)));
            builder.Append("</text>");
            builder.Append("<text hint-style=\"captionSubtle\" hint-wrap=\"true\" hint-maxLines=\"");
            builder.Append(maxLines.ToString());
            builder.Append("\">");
            builder.Append(EscapeText(text));
            builder.Append("</text>");
            builder.Append("</binding>");
        }

        private static void SaveLast(string title, string text, string image)
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                values[LastTitleKey] = title ?? string.Empty;
                values[LastTextKey] = text ?? string.Empty;
                values[LastImageKey] = image ?? string.Empty;
            }
            catch
            {
            }
        }

        private static string ReadSetting(IPropertySet values, string key)
        {
            try
            {
                if (values == null) return string.Empty;
                object value;
                if (values.TryGetValue(key, out value) && value != null)
                    return value.ToString();
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string SingleLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static string Shorten(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || maxLength <= 0) return string.Empty;
            if (value.Length <= maxLength) return value;
            if (maxLength <= 3) return value.Substring(0, maxLength);
            return value.Substring(0, maxLength - 3).TrimEnd() + "...";
        }

        private static string EscapeText(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string EscapeAttribute(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return EscapeText(value).Replace("\"", "&quot;");
        }
    }
}
