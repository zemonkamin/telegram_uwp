using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Storage;

namespace Telegram.Notifications
{
    internal static class TelegramNotificationRegistrar
    {
        private const string PollTaskName = "TelegramTdLibNotificationPoll";
        private const string InternetTaskName = "TelegramTdLibInternetWake";
        private const string NetworkTaskName = "TelegramTdLibNetworkWake";
        private const string ReplyTaskName = "TelegramToastReply";
        private const string PushTaskName = "TelegramWnsPushWake";
        private const string SocketTaskName = "TelegramTdLibSocketWake";
        private const string LiveTileTaskName = "TelegramLiveTileUpdate";
        private const string SocketTaskIdKey = "TelegramSocketActivityTaskId";
        private const string StatusKey = "TelegramNotificationRegistrationStatus";
        private const string LiveTileStatusKey = "TelegramLiveTileRegistrationStatus";

        public static async Task RegisterAsync()
        {
            await RegisterAsync(false);
        }

        public static async Task RegisterAndStartAsync()
        {
            await RegisterAsync(true);
        }

        private static async Task RegisterAsync(bool startAlwaysKeepAlive)
        {
            try
            {
                var mode = TelegramAppSettings.NotificationMode;
                if (mode == TelegramNotificationMode.None)
                {
                    Disable();
                    return;
                }

                TelegramNotificationRuntime.EnsureRegistrationMarker();
                var access = await BackgroundExecutionManager.RequestAccessAsync();
                var status = "Background access: " + access.ToString();

                if (mode == TelegramNotificationMode.Always)
                {
                    UnregisterTask(PollTaskName);
                    UnregisterTask(InternetTaskName);
                    UnregisterTask(NetworkTaskName);
                    UnregisterTask(PushTaskName);
                    UnregisterTask(SocketTaskName);
                    TelegramFixedSystemNotificationBridge.Stop();
                    ApplicationData.Current.LocalSettings.Values.Remove(SocketTaskIdKey);
                    status += " periodic wake-ups: off;";
                    status += RegisterTaskSafely(" reply", RegisterReplyTask);

                    TelegramContinuousNotificationPoller.Start();
                    if (startAlwaysKeepAlive)
                    {
                        var keepAlive = await TelegramContinuousNotificationPoller.StartKeepAliveAsync();
                        status += keepAlive ? " always-on: ok;" : " always-on: denied;";
                        if (!string.IsNullOrEmpty(TelegramContinuousNotificationPoller.LastKeepAliveStatus))
                            status += " " + TelegramContinuousNotificationPoller.LastKeepAliveStatus;
                    }
                    else
                    {
                        status += " always-on: configured;";
                    }
                }
                else if (mode == TelegramNotificationMode.FixedSystem)
                {
                    TelegramContinuousNotificationPoller.StopKeepAlive();
                    UnregisterTask(PollTaskName);
                    UnregisterTask(InternetTaskName);
                    UnregisterTask(NetworkTaskName);
                    UnregisterTask(SocketTaskName);
                    ApplicationData.Current.LocalSettings.Values.Remove(SocketTaskIdKey);
                    status += RegisterTaskSafely(" push", RegisterPushTask);
                    status += RegisterTaskSafely(" reply", RegisterReplyTask);
                    await TelegramFixedSystemNotificationBridge.RegisterAndStartAsync();
                    TelegramContinuousNotificationPoller.Start();
                    status += " " + TelegramFixedSystemNotificationBridge.LastStatus;
                }
                else
                {
                    TelegramContinuousNotificationPoller.StopKeepAlive();
                    TelegramFixedSystemNotificationBridge.Stop();
                    UnregisterTask(PushTaskName);
                    status += RegisterTaskSafely(" timer", RegisterPollTask);
                    status += RegisterTaskSafely(" internet", RegisterInternetTask);
                    status += RegisterTaskSafely(" network", RegisterNetworkTask);
                    status += RegisterTaskSafely(" reply", RegisterReplyTask);
                    status += RegisterTaskSafely(" socket", RegisterSocketWakeTask);
                }

                SaveStatus(status);
            }
            catch (Exception ex)
            {
                SaveStatus("Background registration failed: " + ex.Message);
            }
        }

        public static void Disable()
        {
            try
            {
                TelegramContinuousNotificationPoller.Stop();
                UnregisterTask(PollTaskName);
                UnregisterTask(InternetTaskName);
                UnregisterTask(NetworkTaskName);
                UnregisterTask(ReplyTaskName);
                UnregisterTask(PushTaskName);
                UnregisterTask(SocketTaskName);
                TelegramFixedSystemNotificationBridge.Stop();
                ApplicationData.Current.LocalSettings.Values.Remove(SocketTaskIdKey);
                SaveStatus("Notifications are disabled.");
            }
            catch
            {
            }
        }

        public static Task RegisterLiveTileAsync()
        {
            try
            {
                if (!TelegramAppSettings.LiveTileEnabled)
                {
                    DisableLiveTile();
                    return Task.FromResult(true);
                }

                UnregisterTask(LiveTileTaskName);
                TelegramLiveTileRuntime.RestoreLast();
                SaveLiveTileStatus("Live tile is enabled. It updates from the latest shown notification.");
            }
            catch (Exception ex)
            {
                SaveLiveTileStatus("Live tile registration failed: " + ex.Message);
            }

            return Task.FromResult(true);
        }

        public static void DisableLiveTile()
        {
            try
            {
                UnregisterTask(LiveTileTaskName);
                TelegramLiveTileRuntime.Clear();
                SaveLiveTileStatus("Live tile is disabled.");
            }
            catch
            {
            }
        }

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

        public static string LastLiveTileStatus
        {
            get
            {
                try
                {
                    object value;
                    if (ApplicationData.Current.LocalSettings.Values.TryGetValue(LiveTileStatusKey, out value) && value != null)
                        return value.ToString();
                }
                catch
                {
                }
                return string.Empty;
            }
        }

        private static void RegisterPollTask()
        {
            if (IsRegistered(PollTaskName)) return;

            var builder = new BackgroundTaskBuilder();
            builder.Name = PollTaskName;
            builder.SetTrigger(new TimeTrigger(15, false));
            builder.Register();
        }

        private static void RegisterInternetTask()
        {
            if (IsRegistered(InternetTaskName)) return;

            var builder = new BackgroundTaskBuilder();
            builder.Name = InternetTaskName;
            builder.SetTrigger(new SystemTrigger(SystemTriggerType.InternetAvailable, false));
            builder.Register();
        }

        private static void RegisterNetworkTask()
        {
            if (IsRegistered(NetworkTaskName)) return;

            var builder = new BackgroundTaskBuilder();
            builder.Name = NetworkTaskName;
            builder.SetTrigger(new SystemTrigger(SystemTriggerType.NetworkStateChange, false));
            builder.Register();
        }

        private static void RegisterReplyTask()
        {
            if (IsRegistered(ReplyTaskName)) return;

            var builder = new BackgroundTaskBuilder();
            builder.Name = ReplyTaskName;
            builder.SetTrigger(new ToastNotificationActionTrigger());
            builder.Register();
        }

        private static void RegisterPushTask()
        {
            if (IsRegistered(PushTaskName)) return;

            var builder = new BackgroundTaskBuilder();
            builder.Name = PushTaskName;
            builder.SetTrigger(new PushNotificationTrigger());
            builder.Register();
        }

        private static void RegisterSocketWakeTask()
        {
            var existing = FindRegistration(SocketTaskName);
            if (existing != null)
            {
                SaveSocketTaskId(existing.TaskId);
                return;
            }

            var builder = new BackgroundTaskBuilder();
            builder.Name = SocketTaskName;
            builder.SetTrigger(new SocketActivityTrigger());
            var registration = builder.Register();
            SaveSocketTaskId(registration.TaskId);
        }

        public static Guid? GetSocketWakeTaskId()
        {
            try
            {
                object value;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(SocketTaskIdKey, out value) || value == null)
                    return null;

                Guid id;
                if (Guid.TryParse(value.ToString(), out id))
                    return id;
            }
            catch
            {
            }

            return null;
        }

        private static bool IsRegistered(string name)
        {
            return FindRegistration(name) != null;
        }

        private static IBackgroundTaskRegistration FindRegistration(string name)
        {
            foreach (var task in BackgroundTaskRegistration.AllTasks)
            {
                if (task.Value != null && task.Value.Name == name)
                    return task.Value;
            }
            return null;
        }

        private static void UnregisterTask(string name)
        {
            var registration = FindRegistration(name);
            if (registration != null)
                registration.Unregister(true);
        }

        private static void SaveSocketTaskId(Guid taskId)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[SocketTaskIdKey] = taskId.ToString();
            }
            catch
            {
            }
        }

        private static string RegisterTaskSafely(string label, Action register)
        {
            try
            {
                register();
                return label + ": ok;";
            }
            catch (Exception ex)
            {
                return label + ": " + ex.Message + ";";
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

        private static void SaveLiveTileStatus(string status)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[LiveTileStatusKey] = status ?? string.Empty;
            }
            catch
            {
            }
        }
    }
}
