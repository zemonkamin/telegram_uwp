using System.Reflection;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Foundation.Collections;

namespace Telegram.Notifications
{
    internal static class TelegramNotificationBackgroundTask
    {
        public static async Task RunAsync(IBackgroundTaskInstance taskInstance)
        {
            try
            {
                var details = taskInstance == null ? null : taskInstance.TriggerDetails;
                if (details != null)
                {
                    if (TelegramFixedSystemNotificationBridge.IsPushNotificationTriggerDetails(details))
                    {
                        await TelegramFixedSystemNotificationBridge.HandleBackgroundPushAsync(details);
                        return;
                    }

                    var argument = ReadStringProperty(details, "Argument");
                    var userInput = ReadValueSetProperty(details, "UserInput");
                    if (argument != null || userInput != null)
                    {
                        await TelegramNotificationRuntime.HandleToastActionAsync(argument, userInput);
                        return;
                    }
                }

                await TelegramNotificationRuntime.PollAndNotifyAsync();
            }
            catch
            {
                // Background work should not bring down the app process.
            }
        }

        private static string ReadStringProperty(object source, string name)
        {
            try
            {
                var property = source.GetType().GetRuntimeProperty(name);
                var value = property == null ? null : property.GetValue(source);
                return value == null ? null : value.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static ValueSet ReadValueSetProperty(object source, string name)
        {
            try
            {
                var property = source.GetType().GetRuntimeProperty(name);
                return property == null ? null : property.GetValue(source) as ValueSet;
            }
            catch
            {
                return null;
            }
        }
    }
}
