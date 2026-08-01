using System;
using System.Reflection;
using Windows.Foundation.Metadata;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram
{
    internal static class StatusBarLoadingIndicator
    {
        public static bool SetActive(bool active)
        {
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
                    return false;

                var statusBarType = Type.GetType("Windows.UI.ViewManagement.StatusBar, Windows, ContentType=WindowsRuntime");
                if (statusBarType == null)
                    return false;

                var getForCurrentView = statusBarType.GetRuntimeMethod("GetForCurrentView", new Type[0]);
                if (getForCurrentView == null)
                    return false;

                var statusBar = getForCurrentView.Invoke(null, null);
                if (statusBar == null)
                    return false;

                var progressProperty = statusBar.GetType().GetRuntimeProperty("ProgressIndicator");
                if (progressProperty == null)
                    return false;

                var progress = progressProperty.GetValue(statusBar);
                if (progress == null)
                    return false;

                var progressType = progress.GetType();
                var progressValueProperty = progressType.GetRuntimeProperty("ProgressValue");
                if (progressValueProperty != null)
                    progressValueProperty.SetValue(progress, null);

                var textProperty = progressType.GetRuntimeProperty("Text");
                if (textProperty != null)
                    textProperty.SetValue(progress, string.Empty);

                if (active)
                    InvokeProgressMethod(progressType, progress, "ShowAsync");
                else
                    InvokeProgressMethod(progressType, progress, "HideAsync");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void SetActive(bool active, ProgressBar fallbackBar)
        {
            var statusBarActive = SetActive(active);
            if (fallbackBar == null)
                return;

            fallbackBar.IsIndeterminate = !statusBarActive && active;
            fallbackBar.Visibility = !statusBarActive && active ? Visibility.Visible : Visibility.Collapsed;
            fallbackBar.Opacity = !statusBarActive && active ? 1.0 : 0.0;
        }

        public static void Hide()
        {
            SetActive(false);
        }

        private static void InvokeProgressMethod(Type progressType, object progress, string methodName)
        {
            if (progressType == null || progress == null || string.IsNullOrEmpty(methodName))
                return;

            var method = progressType.GetRuntimeMethod(methodName, new Type[0]);
            if (method != null)
                method.Invoke(progress, null);
        }
    }
}
