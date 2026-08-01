using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Networking.PushNotifications;
using Windows.Security.Authentication.Web;
using Windows.Storage;

namespace Telegram.Notifications
{
    internal static class TelegramFixedSystemNotificationBridge
    {
        private const string StatusKey = "TelegramFixedSystemNotificationStatus";
        private const string ChannelUriKey = "TelegramFixedSystemNotificationChannelUri";
        private const string ChannelExpirationKey = "TelegramFixedSystemNotificationChannelExpiration";
        private static readonly object Gate = new object();
        private static PushNotificationChannel _channel;
        private static bool _starting;

        public static string LastStatus
        {
            get
            {
                try
                {
                    object value;
                    if (ApplicationData.Current.LocalSettings.Values.TryGetValue(StatusKey, out value) && value != null)
                        return value.ToString();
                }
                catch
                {
                }

                return string.Empty;
            }
        }

        public static async Task RegisterAndStartAsync()
        {
            if (TelegramAppSettings.NotificationMode != TelegramNotificationMode.FixedSystem)
            {
                Stop();
                return;
            }

            await EnsureChannelAsync();
        }

        public static void Stop()
        {
            lock (Gate)
            {
                try
                {
                    if (_channel != null)
                    {
                        _channel.PushNotificationReceived -= OnPushNotificationReceived;
                        _channel.Close();
                        _channel = null;
                    }
                }
                catch
                {
                }
            }
        }

        public static bool IsPushNotificationTriggerDetails(object details)
        {
            if (details == null) return false;
            var typeName = details.GetType().FullName;
            if (!string.IsNullOrEmpty(typeName) && typeName.IndexOf("Push", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return ReadStringProperty(details, "Content") != null ||
                ReadObjectProperty(details, "RawNotification") != null ||
                ReadObjectProperty(details, "ToastNotification") != null ||
                ReadObjectProperty(details, "TileNotification") != null ||
                ReadObjectProperty(details, "BadgeNotification") != null;
        }

        public static async Task HandleBackgroundPushAsync(object details)
        {
            if (TelegramAppSettings.NotificationMode == TelegramNotificationMode.None)
                return;

            SaveStatus("WNS background push received. " + DescribePushDetails(details));
            TelegramContinuousNotificationPoller.Diag("Fixed WNS background push: " + DescribePushDetails(details));
            await TelegramNotificationRuntime.PollAndNotifyAsync();
        }

        private static async Task EnsureChannelAsync()
        {
            lock (Gate)
            {
                if (_starting)
                    return;

                _starting = true;
            }

            try
            {
                SaveStatus("CreatePushNotificationChannelForApplicationAsync()... " + GetPackageIdentityText());
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                {
                    var started = DateTimeOffset.Now;
                    var channel = await PushNotificationChannelManager
                        .CreatePushNotificationChannelForApplicationAsync()
                        .AsTask(cts.Token);
                    var elapsed = (DateTimeOffset.Now - started).TotalMilliseconds;

                    AttachChannel(channel);
                    SaveChannel(channel, elapsed);
                    TelegramContinuousNotificationPoller.Diag("Fixed WNS channel ready. elapsed=" + elapsed.ToString("F0"));
                }
            }
            catch (OperationCanceledException)
            {
                SaveStatus("WNS channel timeout after 60 seconds. Fixed-system fallback polling is active.");
            }
            catch (Exception ex)
            {
                SaveStatus("WNS channel error: " + Describe(ex) + " " + Hint(ex) + " " + GetPackageIdentityText() +
                    " Fixed-system fallback polling is active.");
            }
            finally
            {
                lock (Gate)
                {
                    _starting = false;
                }
            }
        }

        private static void AttachChannel(PushNotificationChannel channel)
        {
            lock (Gate)
            {
                try
                {
                    if (_channel != null)
                    {
                        _channel.PushNotificationReceived -= OnPushNotificationReceived;
                        _channel.Close();
                    }
                }
                catch
                {
                }

                _channel = channel;
                if (_channel != null)
                    _channel.PushNotificationReceived += OnPushNotificationReceived;
            }
        }

        private static void SaveChannel(PushNotificationChannel channel, double elapsedMilliseconds)
        {
            if (channel == null)
            {
                SaveStatus("WNS channel was not returned.");
                return;
            }

            var uri = channel.Uri ?? string.Empty;
            var host = string.Empty;
            try
            {
                host = new Uri(uri).Host;
            }
            catch
            {
            }

            try
            {
                ApplicationData.Current.LocalSettings.Values[ChannelUriKey] = uri;
                ApplicationData.Current.LocalSettings.Values[ChannelExpirationKey] = channel.ExpirationTime.ToString("o");
            }
            catch
            {
            }

            SaveStatus("WNS channel ready in " + elapsedMilliseconds.ToString("F0") + " ms; host=" +
                (string.IsNullOrEmpty(host) ? "unknown" : host) +
                "; expires=" + channel.ExpirationTime.ToString("yyyy-MM-dd HH:mm:ss zzz") + ". " + GetPackageIdentityText());
        }

        private static async void OnPushNotificationReceived(PushNotificationChannel sender, PushNotificationReceivedEventArgs args)
        {
            try
            {
                if (args != null)
                    args.Cancel = true;

                var description = DescribeForegroundPush(args);
                SaveStatus("WNS foreground push received. " + description);
                TelegramContinuousNotificationPoller.Diag("Fixed WNS foreground push: " + description);
                await TelegramNotificationRuntime.PollAndNotifyAsync();
            }
            catch (Exception ex)
            {
                SaveStatus("WNS foreground push error: " + Describe(ex));
            }
        }

        private static string DescribeForegroundPush(PushNotificationReceivedEventArgs args)
        {
            if (args == null) return "empty details.";

            try
            {
                switch (args.NotificationType)
                {
                    case PushNotificationType.Raw:
                        return "raw: " + SingleLine(args.RawNotification == null ? string.Empty : args.RawNotification.Content, 120);
                    case PushNotificationType.Toast:
                        return "toast.";
                    case PushNotificationType.Tile:
                        return "tile.";
                    case PushNotificationType.Badge:
                        return "badge.";
                }
            }
            catch
            {
            }

            return args.NotificationType.ToString() + ".";
        }

        private static string DescribePushDetails(object details)
        {
            if (details == null) return "empty details.";

            var content = ReadStringProperty(details, "Content");
            if (!string.IsNullOrEmpty(content))
                return "raw: " + SingleLine(content, 120);

            var notificationType = ReadStringProperty(details, "NotificationType");
            if (!string.IsNullOrEmpty(notificationType))
                return notificationType + ".";

            return details.GetType().Name + ".";
        }

        private static string ReadStringProperty(object source, string name)
        {
            try
            {
                var value = ReadObjectProperty(source, name);
                return value == null ? null : value.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static object ReadObjectProperty(object source, string name)
        {
            try
            {
                if (source == null) return null;
                var property = source.GetType().GetRuntimeProperty(name);
                return property == null ? null : property.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        private static void SaveStatus(string status)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[StatusKey] = status ?? string.Empty;
            }
            catch
            {
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex == null) return string.Empty;
            var code = string.Format("0x{0:X8}", (uint)ex.HResult);
            return ex.GetType().Name + " " + code + ": " +
                (ex.Message ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
        }

        private static string Hint(Exception ex)
        {
            if (ex == null) return string.Empty;

            var hr = (uint)ex.HResult;
            if (hr == 0x80070490)
                return "Windows notification platform did not find a WNS identity for this package. " +
                    "Check Package.appxmanifest Identity Name/Publisher against the WnsProbe identity test.";

            if ((hr & 0xFFFF0000) == 0x803E0000)
                return "This is a Windows notification platform error.";

            return string.Empty;
        }

        private static string GetPackageIdentityText()
        {
            try
            {
                var id = Package.Current.Id;
                var text = "PFN=" + id.FamilyName + "; Package=" + id.FullName + "; Publisher=" + id.Publisher + ".";

                try
                {
                    text += " SID=" + WebAuthenticationBroker.GetCurrentApplicationCallbackUri().ToString() + ".";
                }
                catch
                {
                }

                return text;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SingleLine(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            value = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (maxLength > 0 && value.Length > maxLength)
                value = value.Substring(0, maxLength) + "...";
            return value;
        }
    }
}
