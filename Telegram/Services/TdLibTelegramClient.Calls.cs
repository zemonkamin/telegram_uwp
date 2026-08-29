using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using libtgvoip;

namespace Telegram.Services
{
    /// <summary>
    /// The client's call surface, compiled only for ARM.
    ///
    /// Split into its own partial rather than guarded with #if, because libtgvoip has
    /// no x86 build: on the desktop configuration this file is simply not compiled,
    /// the partial method below has no implementation, and the compiler removes the
    /// call sites entirely. Nothing about the shared code changes shape between
    /// platforms.
    /// </summary>
    internal sealed partial class TdLibTelegramClient
    {
        private CallsService _calls;
        private readonly object _callsGate = new object();

        /// <summary>
        /// The call service, created on first use.
        ///
        /// Lazily, because most sessions never place a call and building it touches
        /// the libtgvoip projection - which is worth deferring until something
        /// actually needs it.
        /// </summary>
        internal CallsService Calls
        {
            get
            {
                lock (_callsGate)
                {
                    if (_calls == null)
                    {
                        _calls = new CallsService(
                            SendRawJson,
                            delegate (string message) { Debug.WriteLine("VOIP " + message); });

                        AttachCallEvents(_calls);
                    }

                    return _calls;
                }
            }
        }

        /// <summary>
        /// The call service builds its own JSON, so it needs a string-shaped send.
        /// </summary>
        private void SendRawJson(string json)
        {
            if (_client == IntPtr.Zero || string.IsNullOrEmpty(json)) return;

            Debug.WriteLine("TDLIB => " + json);
            TdJson.SendUtf8(_client, json);
        }

        /// <summary>
        /// Hands an updateCall to the call service, on the UI thread.
        ///
        /// The marshalling is the point. This client parses TDLib updates on the
        /// receive loop's own thread, while the call service was written against a
        /// client that dispatched them to the UI thread first, and says so: it does no
        /// marshalling of its own and its consumers touch the call UI directly.
        /// Handing it an update from a background thread would work until the first
        /// call actually connected, and then throw somewhere unrelated.
        ///
        /// libtgvoip's own state events are the other direction and remain the
        /// caller's problem - they arrive on a native thread, as the service warns.
        /// </summary>
        partial void OnUpdateCall(JObject update)
        {
            if (update == null) return;

            CallsService calls = Calls;

            RunOnUiThread(delegate
            {
                try { calls.HandleUpdateCall(update); }
                catch (Exception ex) { Debug.WriteLine("VOIP updateCall failed: " + ex.Message); }
            });
        }

        partial void QueryCallsSupported(ref bool supported)
        {
            supported = true;
        }

        partial void StartCallCore(long userId)
        {
            Calls.StartOutgoingCall(userId);
        }

        partial void AcceptCallCore()
        {
            Calls.AcceptIncomingCall();
        }

        partial void HangUpCallCore()
        {
            Calls.HangUp();
        }

        /// <summary>
        /// Subscribes to the call service, once.
        ///
        /// Both of its events are turned into the one neutral update the UI reads.
        /// They arrive from different places and only one of them is safe: TDLib
        /// updates come through OnUpdateCall, which has already marshalled them,
        /// while the media state is raised on a libtgvoip thread and has to be
        /// marshalled here.
        /// </summary>
        private void AttachCallEvents(CallsService calls)
        {
            calls.CallChanged += delegate (object sender, TdCall call)
            {
                _lastCall = call;
                RaiseCallStateChanged(Describe(call, _lastMediaState));
            };

            calls.ControllerStateChanged += delegate (object sender, CallState state)
            {
                _lastMediaState = state.ToString();

                TdCall call = _lastCall;
                RunOnUiThread(delegate { RaiseCallStateChanged(Describe(call, _lastMediaState)); });
            };
        }

        private TdCall _lastCall;
        private string _lastMediaState = string.Empty;

        /// <summary>
        /// Flattens the call service's model into the one the UI reads.
        ///
        /// The two are deliberately separate types. This one has to compile on x86
        /// where libtgvoip does not exist, so it cannot mention TdCall or CallState
        /// anywhere the shared code can see.
        /// </summary>
        private static CallInfo Describe(TdCall call, string mediaState)
        {
            if (call == null)
            {
                return new CallInfo { MediaState = mediaState ?? string.Empty };
            }

            return new CallInfo
            {
                Id = call.Id,
                UserId = call.UserId,
                IsOutgoing = call.IsOutgoing,
                State = call.State ?? string.Empty,
                IsActive = call.IsReady,
                IsDiscarded = call.IsDiscarded,
                DiscardReason = call.DiscardReason ?? string.Empty,
                Emojis = call.Emojis,
                MediaState = mediaState ?? string.Empty,
                ErrorMessage = call.ErrorMessage ?? string.Empty,
            };
        }
    }
}
