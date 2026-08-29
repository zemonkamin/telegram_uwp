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

        /// <summary>
        /// Puts the caller's name on a notification that has already been raised.
        ///
        /// An updateCall carries a user id only. Re-showing under the same tag replaces
        /// the notification in place, so the ringing screen gains the name without a
        /// second one appearing beneath it.
        /// </summary>
        private static async System.Threading.Tasks.Task NameCallerAsync(CallInfo call)
        {
            if (call == null || call.UserId == 0) return;

            Telegram.Models.ChatViewModel peer = null;
            try
            {
                peer = await TelegramService.Instance.GetPrivateChatAsync(call.UserId);
            }
            catch (Exception)
            {
            }

            if (peer == null || string.IsNullOrEmpty(peer.Title)) return;

            // Still ringing, and still this call.
            if (_showing != call.Id) return;

            Notifications.IncomingCallToast.Refresh(call.Id, peer.Title);
        }

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

                // The ringing screen is a looping notification. Left up, it goes on
                // ringing for a call that has already ended.
                Notifications.IncomingCallToast.Clear(call.Id);
                return;
            }

            // Only a call somebody else placed, and only while it is still ringing.
            // An outgoing call already has a page in front of it - the one that
            // placed it - and navigating again would replace it with a copy.
            if (call.IsOutgoing) return;

            if (call.State != "callStatePending")
            {
                // Answered, or moved on. Either way it is no longer ringing.
                Notifications.IncomingCallToast.Clear(call.Id);
                return;
            }

            if (call.Id == _showing) return;
            _showing = call.Id;

            // Off screen, this is the only thing the user will see.
            //
            // A backgrounded app cannot bring itself to the foreground, so the
            // navigation below happens to a window nobody is looking at - which is
            // exactly how an incoming call behaved with background mode on: it
            // negotiated correctly, the page was reached, and the call was found only
            // after it had been missed. The notification is not a message about the
            // call, it is the means of answering one.
            //
            // Gated on visibility rather than raised always. A scenario="incomingCall"
            // notification takes over the whole screen, so raising it while the user is
            // already in the app replaces the app's own call screen with a system one.
            if (!AppVisibility.IsOnScreen)
            {
                // Raised now with whatever is known, and named afterwards. Waiting for
                // the lookup would mean the phone stays silent while it runs, which is
                // the one thing a ringing notification must not do.
                var ignoredName = NameCallerAsync(call);

                // No name to show: an incoming update carries a user id, and nothing
                // in this path resolves it. The notification falls back to the app
                // name, which is the same thing the call page shows.
                Notifications.IncomingCallToast.Show(call.Id, string.Empty);
            }

            var frame = Window.Current == null ? null : Window.Current.Content as Frame;
            if (frame == null) return;

            // Already looking at a call. Whether that is this one or another, putting
            // a second call page on top of it helps nobody.
            if (frame.Content is CallPage) return;

            frame.Navigate(typeof(CallPage), call);
        }
    }
}
