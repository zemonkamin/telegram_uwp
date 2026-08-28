using System;
using Telegram.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram
{
    /// <summary>
    /// Puts an incoming call on screen.
    ///
    /// Somebody has to be listening for a call when no call page exists yet, which
    /// is the whole point: the page cannot show a call it has not been navigated to,
    /// and nothing else in the app was watching. Without this, an inbound call
    /// reaches the call service, negotiates perfectly, and rings nowhere.
    ///
    /// Attached once at startup and never detached. A call can arrive at any moment
    /// the app is running, so there is no state in which this should stop watching.
    /// </summary>
    internal static class IncomingCallWatcher
    {
        private static bool _attached;

        /// <summary>
        /// The call currently on screen.
        ///
        /// Navigating twice for the same call would tear down the page mid-ring, and
        /// TDLib sends several updates for one call as it progresses.
        /// </summary>
        private static long _showing;

        public static void Attach()
        {
            if (_attached) return;
            _attached = true;

            TelegramService.Instance.CallStateChanged += OnCallStateChanged;
        }

        private static void OnCallStateChanged(object sender, CallInfo call)
        {
            if (call == null) return;

            if (call.IsDiscarded)
            {
                if (call.Id == _showing) _showing = 0;
                return;
            }

            // Only a call somebody else placed, and only while it is still ringing.
            // An outgoing call already has a page in front of it - the one that
            // placed it - and navigating again would replace it with a copy.
            if (call.IsOutgoing || call.State != "callStatePending") return;

            if (call.Id == _showing) return;

            var frame = Window.Current == null ? null : Window.Current.Content as Frame;
            if (frame == null) return;

            // Already looking at a call. Whether that is this one or another, putting
            // a second call page on top of it helps nobody.
            if (frame.Content is CallPage) return;

            _showing = call.Id;
            frame.Navigate(typeof(CallPage), call);
        }
    }
}
