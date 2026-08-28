using System.Collections.Generic;

namespace Telegram.Services
{
    /// <summary>
    /// A call, as the app's UI needs to see it.
    ///
    /// This replaces TelegramCallInfo, which carried LocalA, LocalGA, RemoteGB, DhP,
    /// DhG and a key fingerprint - the working state of a Diffie-Hellman exchange
    /// performed by hand over raw MTProto. That model belonged to a client that
    /// speaks the protocol itself. This one does not: TDLib performs the exchange
    /// and hands over a finished call, so there is nothing here to hold a secret in.
    ///
    /// Deliberately platform-neutral and always compiled. libtgvoip is ARM only, so
    /// anything the call UI touches has to exist on x86 as well, or the desktop
    /// configuration stops building over a feature it was never going to have.
    /// </summary>
    public sealed class CallInfo
    {
        public long Id;
        public long UserId;
        public bool IsOutgoing;

        /// <summary>TDLib's own name: callStatePending, callStateReady, and so on.</summary>
        public string State = string.Empty;

        /// <summary>Ready, meaning media should be flowing.</summary>
        public bool IsActive;

        public bool IsDiscarded;
        public string DiscardReason = string.Empty;

        /// <summary>
        /// The four emoji both ends display to confirm nobody is in the middle.
        ///
        /// They are derived from the shared key, so they only exist once the call is
        /// ready, and they match on both phones or the call is not what it claims.
        /// </summary>
        public List<string> Emojis;

        /// <summary>
        /// What the media layer is doing, in its own words - Established, Failed and
        /// the rest. Separate from <see cref="State"/> because a call can be ready as
        /// far as Telegram is concerned while no audio is flowing at all, and telling
        /// those apart is the difference between a bug report and a diagnosis.
        /// </summary>
        public string MediaState = string.Empty;

        public string ErrorMessage = string.Empty;
    }
}
