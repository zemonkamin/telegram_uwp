using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Telegram.Services
{
    public static class AdaptiveShellNavigationService
    {
        private static Frame _navigationFrame;
        private static Frame _detailFrame;
        private static FrameworkElement _emptyDetailPanel;
        private static object _pendingChatParameter;
        private static bool _isLandscape;

        public static bool IsLandscape
        {
            get { return _isLandscape && _navigationFrame != null && _detailFrame != null; }
        }

        public static void Register(Frame navigationFrame, Frame detailFrame, FrameworkElement emptyDetailPanel)
        {
            if (_detailFrame != null)
                _detailFrame.Navigated -= DetailFrame_Navigated;

            _navigationFrame = navigationFrame;
            _detailFrame = detailFrame;
            _emptyDetailPanel = emptyDetailPanel;

            if (_detailFrame != null)
                _detailFrame.Navigated += DetailFrame_Navigated;

            UpdateDetailState();
        }

        public static void Unregister(Frame navigationFrame, Frame detailFrame)
        {
            if (_navigationFrame != navigationFrame || _detailFrame != detailFrame)
                return;

            if (_detailFrame != null)
                _detailFrame.Navigated -= DetailFrame_Navigated;

            _navigationFrame = null;
            _detailFrame = null;
            _emptyDetailPanel = null;
            _isLandscape = false;
        }

        public static void SetLandscape(bool isLandscape)
        {
            _isLandscape = isLandscape;
            UpdateDetailState();
        }

        public static bool NavigateLeft(Type pageType)
        {
            return NavigateLeft(pageType, null);
        }

        public static bool NavigateLeft(Type pageType, object parameter)
        {
            if (!IsLandscape || pageType == null || _navigationFrame == null)
                return false;

            return Navigate(_navigationFrame, pageType, parameter);
        }

        public static bool NavigateChat(object parameter)
        {
            if (!IsLandscape || _detailFrame == null)
                return false;

            var result = Navigate(_detailFrame, typeof(Telegram.ChatPage), parameter);
            UpdateDetailState();
            return result;
        }

        public static bool NavigateChatFromExternalActivation(object parameter)
        {
            if (NavigateChat(parameter))
                return true;

            if (_navigationFrame != null)
            {
                var result = Navigate(_navigationFrame, typeof(Telegram.ChatPage), parameter);
                UpdateDetailState();
                return result;
            }

            _pendingChatParameter = parameter;
            return false;
        }

        public static void OpenPendingChat()
        {
            if (_pendingChatParameter == null)
                return;

            var parameter = _pendingChatParameter;
            _pendingChatParameter = null;
            if (!NavigateChatFromExternalActivation(parameter))
                _pendingChatParameter = parameter;
        }

        public static bool ClearDetail()
        {
            if (!IsLandscape || _detailFrame == null)
                return false;

            _detailFrame.BackStack.Clear();
            _detailFrame.Content = null;
            UpdateDetailState();
            return true;
        }

        private static bool Navigate(Frame frame, Type pageType, object parameter)
        {
            if (frame == null || pageType == null)
                return false;

            if (parameter == null)
                return frame.Navigate(pageType);

            return frame.Navigate(pageType, parameter);
        }

        private static void DetailFrame_Navigated(object sender, NavigationEventArgs e)
        {
            UpdateDetailState();
        }

        private static void UpdateDetailState()
        {
            if (_detailFrame != null)
                _detailFrame.Visibility = IsLandscape && _detailFrame.Content != null ? Visibility.Visible : Visibility.Collapsed;

            if (_emptyDetailPanel != null)
                _emptyDetailPanel.Visibility = IsLandscape && (_detailFrame == null || _detailFrame.Content == null) ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
