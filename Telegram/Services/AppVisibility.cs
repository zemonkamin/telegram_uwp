using System;
using Windows.UI.Xaml;

namespace Telegram.Services
{
    /// <summary>
    /// Whether the user can actually see the app.
    ///
    /// This exists because "the app is running" and "the app is on screen" are
    /// different questions once background execution is on, and an incoming call has
    /// to tell them apart. On screen, the call page is enough. Off screen, a
    /// backgrounded app cannot bring itself forward, so the call has to be announced
    /// through a notification or it rings nowhere.
    ///
    /// Asking the window on its own is not enough, which is how this was got wrong
    /// before: Window.Current is null on any thread that does not own the view, so a
    /// false from it is not proof the app is hidden - it is often just proof of which
    /// thread is asking. That produced a full-screen ringing notification on top of an
    /// app the user was already looking at.
    ///
    /// Copied in shape from Unogram, where the same three layers are known to work on
    /// a real handset.
    /// </summary>
    internal static class AppVisibility
    {
        /// <summary>
        /// How long after coming to the foreground the app still counts as on screen.
        ///
        /// Covers the gap between the window being told to show and it actually being
        /// visible, which a call arriving during activation would otherwise fall into.
        /// </summary>
        private const int ForegroundGraceSeconds = 3;

        private static bool _inForeground;
        private static DateTime _foregroundSince = DateTime.MinValue;

        /// <summary>The app is about to be shown: launch, activation, resume.</summary>
        public static void NoteComingToForeground()
        {
            _inForeground = true;
            _foregroundSince = DateTime.UtcNow;
        }

        /// <summary>The app has left the screen: EnteredBackground or Suspending.</summary>
        public static void NoteWentToBackground()
        {
            _inForeground = false;

            // Clearing the stamp matters. Left set, minimising inside the grace period
            // would keep the next call silent - the one case this must never do.
            _foregroundSince = DateTime.MinValue;
        }

        public static bool IsOnScreen
        {
            get
            {
                // A visible window outranks everything else.
                if (IsWindowVisible()) return true;

                // Otherwise the lifecycle events are the second opinion. They are
                // raised on every visibility change, including the screen lock, which
                // a held keep-alive session would otherwise hide from us.
                if (_inForeground) return true;

                return _foregroundSince != DateTime.MinValue
                    && DateTime.UtcNow - _foregroundSince < TimeSpan.FromSeconds(ForegroundGraceSeconds);
            }
        }

        private static bool IsWindowVisible()
        {
            try
            {
                // Null off the view's own thread, and absent entirely in a background
                // task process. Neither means the app is hidden, which is why this is
                // only the first of three answers.
                var window = Window.Current;
                return window != null && window.Visible;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
