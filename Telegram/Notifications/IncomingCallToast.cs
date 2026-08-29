using System;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Telegram.Notifications
{
    /// <summary>
    /// Announces an incoming call while the app is not on screen.
    ///
    /// A backgrounded app cannot bring itself to the foreground - Windows does not
    /// allow it - so the call page can be navigated to and still be invisible. That
    /// is what happened before this existed: the call arrived, the page was reached
    /// and rendered, and nothing was seen until the app was opened by hand, by which
    /// time the caller had given up.
    ///
    /// A toast is the only thing that can interrupt from the background, so this is
    /// not a notification about a call - it is the means of answering one.
    ///
    /// Built from XML rather than from a template, and this is the part that matters:
    /// scenario="incomingCall" renders a full-screen ringing UI on Mobile and takes
    /// its buttons from &lt;actions&gt;. A binding on its own gives a ringing screen
    /// with nothing on it and no way to answer or reject. Copied in shape from
    /// Unogram, where it is known to work on a real handset.
    /// </summary>
    internal static class IncomingCallToast
    {
        private const string Group = "telegram-call";

        /// <summary>Tapping the notification body rather than a button.</summary>
        public const string ArgumentOpen = "call-open:";

        public const string ArgumentAnswer = "call-answer:";
        public const string ArgumentDecline = "call-decline:";

        /// <summary>
        /// Shows the call, replacing any previous one.
        ///
        /// Tagged by call id so a second update for the same call refreshes the
        /// notification rather than stacking another beneath it.
        /// </summary>
        public static void Show(long callId, string caller)
        {
            try
            {
                string name = Escape(string.IsNullOrEmpty(caller) ? "Telegram" : caller);

                // No audio element. The incomingCall scenario supplies the ring
                // itself, and adding a looping sound on top of it gives two.
                var xml = new XmlDocument();
                xml.LoadXml(
                    "<toast scenario='incomingCall' duration='long' launch='" +
                    ArgumentOpen + callId + "'>" +
                    "<visual><binding template='ToastText02'>" +
                    "<text id='1'>" + name + "</text>" +
                    "<text id='2'>Incoming call</text>" +
                    "</binding></visual>" +
                    "<actions>" +
                    "<action content='Answer' arguments='" + ArgumentAnswer + callId +
                    "' activationType='foreground'/>" +
                    "<action content='Decline' arguments='" + ArgumentDecline + callId +
                    "' activationType='foreground'/>" +
                    "</actions>" +
                    "</toast>");

                var toast = new ToastNotification(xml);
                toast.Group = Group;
                toast.Tag = callId.ToString();

                ToastNotificationManager.CreateToastNotifier().Show(toast);
            }
            catch (Exception)
            {
                // A call that cannot be announced is still a call. The page has been
                // navigated to and will be there when the app is opened.
            }
        }

        /// <summary>
        /// Shows the call again with the caller's name now that it is known.
        ///
        /// Identical to Show, and separate only so the intent reads at the call site:
        /// this is a replacement of a notification already on screen, which works
        /// because both carry the same tag and group.
        /// </summary>
        public static void Refresh(long callId, string caller)
        {
            Show(callId, caller);
        }

        /// <summary>Takes the ringing screen down once the call is answered or over.</summary>
        public static void Clear(long callId)
        {
            try
            {
                ToastNotificationManager.History.Remove(callId.ToString(), Group);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Takes down every call notification.
        ///
        /// Used on activation, where the choice has already been made and the call id
        /// is not worth parsing back out of the argument to withdraw one of what is
        /// only ever one notification.
        /// </summary>
        public static void ClearAll()
        {
            try
            {
                var notifier = ToastNotificationManager.History;
                notifier.RemoveGroup(Group);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Whether an activation argument came from a call notification.</summary>
        public static bool IsCallArgument(string argument)
        {
            return Kind(argument) != null;
        }

        /// <summary>
        /// What the user chose: "open", "answer" or "decline", or null if this
        /// argument is not from a call notification at all.
        /// </summary>
        public static string Kind(string argument)
        {
            if (string.IsNullOrEmpty(argument)) return null;

            if (argument.StartsWith(ArgumentAnswer, StringComparison.Ordinal)) return "answer";
            if (argument.StartsWith(ArgumentDecline, StringComparison.Ordinal)) return "decline";
            if (argument.StartsWith(ArgumentOpen, StringComparison.Ordinal)) return "open";

            return null;
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            return value.Replace("&", "&amp;")
                        .Replace("<", "&lt;")
                        .Replace(">", "&gt;")
                        .Replace("'", "&apos;")
                        .Replace("\"", "&quot;");
        }
    }
}
