// Ported from Unogram (D:\GitHub\unogram, TelegramWP10\CallsService.cs).
//
// Both clients drive TDLib through tdjson and raw JSON rather than a typed
// projection, so this transfers as written. Only the namespace changed.
//
// The version note in the original said TDLib 1.7.10; the library it was actually
// verified against is 1.8.66, and this client carries 1.8.63. Every call type and
// field name used here is present in both.
using System;
using System.Collections.Generic;
using System.IO;
using libtgvoip;
using Newtonsoft.Json.Linq;
using Windows.Storage;

namespace Telegram.Services
{
    /// <summary>
    /// Drives a voice call: turns TDLib call updates into libtgvoip controller
    /// calls, and user actions back into TDLib requests.
    ///
    /// Ported from UnigramMobile's Services/CallsService.cs. The controller
    /// sequence is deliberately identical, because the order matters: the server
    /// config must be published before a controller exists, and the encryption
    /// key and endpoints must both be set before Start/Connect.
    ///
    /// Updates arrive on the UI thread (MainPage.LongPolling dispatches through
    /// Dispatcher.RunAsync), so no marshalling is done here. CallStateChanged
    /// from libtgvoip, by contrast, is raised on a native thread - anything that
    /// touches UI from <see cref="ControllerStateChanged"/> must marshal itself.
    /// </summary>
    internal sealed class CallsService : IDisposable
    {
        private readonly Action<string> _send;
        private readonly Action<string> _log;

        private VoIPControllerWrapper _controller;
        private TdCall _call;
        private DateTime _establishedUtc;

        /// <summary>Raised for every call update, after internal state is applied.</summary>
        public event EventHandler<TdCall> CallChanged;

        /// <summary>Raised on a libtgvoip thread as the media state advances.</summary>
        public event EventHandler<CallState> ControllerStateChanged;

        /// <summary>
        /// TDLib options of the same name, in milliseconds. Defaults match
        /// TDLib. Fill them from getOption if you want the server's values.
        /// </summary>
        public int CallPacketTimeoutMs = 10000;
        public int CallConnectTimeoutMs = 30000;

        public CallsService(Action<string> send, Action<string> log)
        {
            if (send == null) throw new ArgumentNullException("send");
            _send = send;
            _log = log ?? delegate { };
        }

        public TdCall ActiveCall { get { return _call; } }

        public bool IsCallActive
        {
            get { return _call != null && _call.State != "callStateDiscarded" && _call.State != "callStateError"; }
        }

        // ---------- user actions ----------------------------------------------

        public void StartOutgoingCall(long userId)
        {
            _log("call: createCall user_id=" + userId);
            _send(CallsSignalling.CreateCall(userId));
        }

        public void AcceptIncomingCall()
        {
            if (_call == null) return;
            _log("call: acceptCall id=" + _call.Id + " protocol=" +
                 CallsSignalling.ProtocolLibraryVersionForLog + " maxLayer=" +
                 VoIPControllerWrapper.GetConnectionMaxLayer() + " (build " +
                 VoIPControllerWrapper.GetVersion() + ")");
            _send(CallsSignalling.AcceptCall(_call.Id));
        }

        public void HangUp()
        {
            if (_call == null) return;

            // GetPreferredRelayID is only meaningful once media is up; TDLib
            // treats 0 as "no relay was used", which is the honest answer when
            // the call never connected.
            long relayId = 0;
            if (_controller != null)
            {
                try { relayId = _controller.GetPreferredRelayID(); }
                catch (Exception ex) { _log("call: GetPreferredRelayID failed: " + ex.Message); }
            }

            var duration = _establishedUtc == default(DateTime)
                ? 0
                : (int)(DateTime.UtcNow - _establishedUtc).TotalSeconds;

            _log("call: discardCall id=" + _call.Id + " duration=" + duration + " relay=" + relayId);
            _send(CallsSignalling.DiscardCall(_call.Id, false, duration, relayId));
        }

        // ---------- TDLib updates ---------------------------------------------

        /// <summary>
        /// Handles one updateCall. Safe to call with any update; non-call
        /// updates are ignored.
        /// </summary>
        public void HandleUpdateCall(JToken update)
        {
            var call = CallsSignalling.ParseUpdateCall(update);
            if (call == null) return;

            _call = call;
            _log("call: id=" + call.Id + " state=" + call.State +
                 " outgoing=" + call.IsOutgoing + " video=" + call.IsVideo);

            switch (call.State)
            {
                case "callStateReady":
                    StartController(call);
                    break;

                case "callStateDiscarded":
                    HandleDiscarded(call);
                    break;

                case "callStateError":
                    _log("call: error " + call.ErrorCode + " " + call.ErrorMessage);
                    Teardown();
                    break;
            }

            var handler = CallChanged;
            if (handler != null) handler(this, call);
        }

        private void StartController(TdCall call)
        {
            // Log what the peer negotiated before deciding. A modern iOS or
            // Android client may pick the tgcalls protocol, whose callStateReady
            // carries callServerTypeWebrtc servers; this libtgvoip build predates
            // that and can only use callServerTypeTelegramReflector.
            _log("call: ready, protocol layers " +
                 (call.Protocol != null ? call.Protocol.MinLayer + "-" + call.Protocol.MaxLayer : "?") +
                 ", library_versions=[" + string.Join(",", call.LibraryVersions) + "]" +
                 ", servers offered=[" + string.Join(",", call.OfferedServerTypes) + "]" +
                 ", usable reflectors=" + call.Servers.Count);

            if (call.Servers.Count == 0)
            {
                // Without a reflector there is nowhere to send media. Bail out
                // loudly rather than starting a controller that cannot connect.
                _log("call: no usable reflector - this libtgvoip cannot do tgcalls/WebRTC");
                return;
            }

            try
            {
                // Static, and must happen before a controller exists: it seeds
                // the shared server configuration libtgvoip reads at construction.
                VoIPControllerWrapper.UpdateServerConfig(call.Config);

                Teardown();

                var config = new VoIPConfig
                {
                    initTimeout = CallPacketTimeoutMs / 1000.0,
                    recvTimeout = CallConnectTimeoutMs / 1000.0,
                    dataSaving = 0,
                    enableAEC = true,
                    enableNS = true,
                    enableAGC = true,
                    enableVolumeControl = true,
                    logFilePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "voip" + call.Id + ".txt"),
                    statsDumpFilePath = string.Empty,
                };

                _controller = new VoIPControllerWrapper();
                _controller.SetConfig(config);
                _controller.CallStateChanged += OnControllerStateChanged;

                var endpoints = new List<Endpoint>(call.Servers.Count);
                foreach (var server in call.Servers)
                {
                    endpoints.Add(new Endpoint
                    {
                        id = server.Id,
                        ipv4 = server.IpAddress,
                        ipv6 = server.Ipv6Address,
                        peerTag = server.PeerTag,
                        port = (ushort)server.Port,
                    });
                }

                // The second argument is "peer-to-peer allowed": both the
                // negotiated protocol and the server must permit it.
                _controller.SetEncryptionKey(call.EncryptionKey, call.IsOutgoing);
                _controller.SetPublicEndpoints(
                    endpoints.ToArray(),
                    call.Protocol != null && call.Protocol.UdpP2p && call.AllowP2p,
                    call.Protocol != null ? call.Protocol.MaxLayer : 65);

                _controller.Start();
                _controller.Connect();

                _log("call: media started, " + endpoints.Count + " endpoint(s), layer=" +
                     (call.Protocol != null ? call.Protocol.MaxLayer : 0));
            }
            catch (Exception ex)
            {
                _log("call: failed to start media: " + ex.Message);
                Teardown();
            }
        }

        private void OnControllerStateChanged(VoIPControllerWrapper sender, CallState state)
        {
            if (state == CallState.Established && _establishedUtc == default(DateTime))
            {
                _establishedUtc = DateTime.UtcNow;
            }

            _log("call: media state " + state);

            var handler = ControllerStateChanged;
            if (handler != null) handler(this, state);
        }

        private void HandleDiscarded(TdCall call)
        {
            // Telegram asks for the libtgvoip log when a call went badly; it has
            // to be read before the controller is torn down.
            if (call.NeedDebugInformation && _controller != null)
            {
                try
                {
                    _send(CallsSignalling.SendCallDebugInformation(call.Id, _controller.GetDebugLog()));
                }
                catch (Exception ex)
                {
                    _log("call: could not collect debug log: " + ex.Message);
                }
            }

            Teardown();
        }

        private void Teardown()
        {
            if (_controller == null) return;

            try
            {
                _controller.CallStateChanged -= OnControllerStateChanged;
                // Projected from WinRT IClosable; the winmd itself exposes Close.
                _controller.Dispose();
            }
            catch (Exception ex)
            {
                _log("call: controller teardown failed: " + ex.Message);
            }
            finally
            {
                _controller = null;
                _establishedUtc = default(DateTime);
            }
        }

        public void Dispose()
        {
            Teardown();
            _call = null;
        }
    }
}
