// Ported from Unogram (D:\GitHub\unogram, TelegramWP10\CallsSignalling.cs).
//
// Both clients drive TDLib through tdjson and raw JSON rather than a typed
// projection, so this transfers as written. Only the namespace changed.
//
// The version note in the original said TDLib 1.7.10; the library it was actually
// verified against is 1.8.66, and this client carries 1.8.63. Every call type and
// field name used here is present in both.
using System;
using System.Collections.Generic;
using libtgvoip;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Telegram.Services
{
    /// <summary>
    /// TDLib call signalling over the JSON interface.
    ///
    /// UnigramMobile talks to TDLib through the typed C++/CX projection
    /// (TdApi.CreateCall, UpdateCall, CallStateReady and friends). Unogram uses
    /// tdjson.dll and raw JSON, so the same protocol has to be built and parsed
    /// by hand. This file is that translation layer: request builders on one
    /// side, an updateCall parser on the other.
    ///
    /// Field names are TDLib wire names in snake_case, not the PascalCase of the
    /// typed projection. Verified against the tdjson.dll this client ships, which
    /// is TDLib 1.8.63.
    /// </summary>
    internal static class CallsSignalling
    {
        /// <summary>
        /// Oldest protocol layer we accept. Matches UnigramMobile; libtgvoip
        /// supplies the upper bound, so both ends negotiate within that range.
        /// </summary>
        private const int MinLayer = 65;

        /// <summary>
        /// Protocol library name we implement, from Telegram's fixed vocabulary
        /// (2.4.4, 2.7.7, 3.0.0, ...). NOT VoIPControllerWrapper.GetVersion(),
        /// which reports the build version - this libtgvoip returns "2.5", which
        /// is not a name any peer recognises, so advertising it is no better than
        /// advertising nothing. 2.4.4 is the legacy libtgvoip protocol, and is what
        /// a successful call against Telegram Android negotiates.
        /// </summary>
        private const string ProtocolLibraryVersion = "2.4.4";

        /// <summary>Exposed for diagnostics only.</summary>
        public static string ProtocolLibraryVersionForLog { get { return ProtocolLibraryVersion; } }

        private static JObject Protocol()
        {
            return new JObject
            {
                ["@type"] = "callProtocol",
                ["udp_p2p"] = true,
                ["udp_reflector"] = true,
                ["min_layer"] = MinLayer,
                ["max_layer"] = VoIPControllerWrapper.GetConnectionMaxLayer(),
                // Peers that negotiate by library name rather than by layer -
                // current Telegram iOS does - find no intersection with an empty
                // list, and the server rejects acceptCall with 406
                // CALL_PROTOCOL_COMPAT_LAYER_INVALID. Android still offers legacy
                // layers, which is why it worked regardless.
                ["library_versions"] = new JArray(ProtocolLibraryVersion),
            };
        }

        private static string Compact(JObject value)
        {
            return value.ToString(Formatting.None);
        }

        // ---------- outgoing requests ----------------------------------------

        public static string CreateCall(long userId)
        {
            return Compact(new JObject
            {
                ["@type"] = "createCall",
                ["user_id"] = userId,
                ["protocol"] = Protocol(),
                ["is_video"] = false,
            });
        }

        public static string AcceptCall(long callId)
        {
            return Compact(new JObject
            {
                ["@type"] = "acceptCall",
                ["call_id"] = callId,
                ["protocol"] = Protocol(),
            });
        }

        /// <param name="connectionId">
        /// The relay libtgvoip actually used, from GetPreferredRelayID(). Zero is
        /// valid and means no relay was established.
        /// </param>
        public static string DiscardCall(long callId, bool isDisconnected, int durationSeconds, long connectionId)
        {
            return Compact(new JObject
            {
                ["@type"] = "discardCall",
                ["call_id"] = callId,
                ["is_disconnected"] = isDisconnected,
                ["duration"] = durationSeconds,
                ["is_video"] = false,
                ["connection_id"] = connectionId,
            });
        }

        public static string SendCallDebugInformation(long callId, string debugInformation)
        {
            return Compact(new JObject
            {
                ["@type"] = "sendCallDebugInformation",
                ["call_id"] = callId,
                ["debug_information"] = debugInformation ?? string.Empty,
            });
        }

        public static string SendCallRating(long callId, int rating, string comment)
        {
            return Compact(new JObject
            {
                ["@type"] = "sendCallRating",
                ["call_id"] = callId,
                ["rating"] = rating,
                ["comment"] = comment ?? string.Empty,
                ["problems"] = new JArray(),
            });
        }

        // ---------- incoming updates -----------------------------------------

        /// <summary>
        /// Parses an updateCall payload. Pass the whole update object. Returns
        /// null when the update is not an updateCall or carries no call.
        /// </summary>
        public static TdCall ParseUpdateCall(JToken update)
        {
            if (update == null) return null;
            if ((string)update["@type"] != "updateCall") return null;

            var call = update["call"];
            if (call == null) return null;

            var result = new TdCall
            {
                Id = (long?)call["id"] ?? 0,
                UserId = (long?)call["user_id"] ?? 0,
                IsOutgoing = (bool?)call["is_outgoing"] ?? false,
                IsVideo = (bool?)call["is_video"] ?? false,
            };

            var state = call["state"];
            result.State = (string)state?["@type"] ?? string.Empty;

            switch (result.State)
            {
                case "callStatePending":
                    result.IsCreated = (bool?)state["is_created"] ?? false;
                    result.IsReceived = (bool?)state["is_received"] ?? false;
                    break;

                case "callStateReady":
                    ParseReady(state, result);
                    break;

                case "callStateDiscarded":
                    result.DiscardReason = (string)state["reason"]?["@type"] ?? string.Empty;
                    result.NeedRating = (bool?)state["need_rating"] ?? false;
                    result.NeedDebugInformation = (bool?)state["need_debug_information"] ?? false;
                    break;

                case "callStateError":
                    result.ErrorCode = (int?)state["error"]?["code"] ?? 0;
                    result.ErrorMessage = (string)state["error"]?["message"] ?? string.Empty;
                    break;
            }

            return result;
        }

        private static void ParseReady(JToken state, TdCall result)
        {
            // Handed verbatim to VoIPControllerWrapper.UpdateServerConfig. It is a
            // JSON document produced by the server; we never interpret it.
            result.Config = (string)state["config"] ?? string.Empty;

            // TDLib returns binary as base64 over the JSON interface. The typed
            // projection hands back byte[] directly, which is why UnigramMobile
            // has no equivalent of these two conversions.
            result.EncryptionKey = DecodeBase64((string)state["encryption_key"]);
            result.AllowP2p = (bool?)state["allow_p2p"] ?? false;

            var protocol = state["protocol"];
            result.Protocol = new TdCallProtocol
            {
                UdpP2p = (bool?)protocol?["udp_p2p"] ?? false,
                UdpReflector = (bool?)protocol?["udp_reflector"] ?? false,
                MinLayer = (int?)protocol?["min_layer"] ?? MinLayer,
                MaxLayer = (int?)protocol?["max_layer"] ?? MinLayer,
            };

            var libs = state["protocol"] != null ? state["protocol"]["library_versions"] as JArray : null;
            if (libs != null)
            {
                foreach (var lib in libs) result.LibraryVersions.Add((string)lib);
            }

            var emojis = state["emojis"] as JArray;
            if (emojis != null)
            {
                var list = new List<string>(emojis.Count);
                foreach (var emoji in emojis)
                {
                    list.Add((string)emoji);
                }
                result.Emojis = list;
            }

            var servers = state["servers"] as JArray;
            if (servers == null) return;

            foreach (var server in servers)
            {
                // Only Telegram reflectors are usable. callServerTypeWebrtc belongs
                // to the newer WebRTC stack, which this libtgvoip build does not
                // implement, so skipping those entries is correct rather than lossy.
                var typeName = (string)server["type"]?["@type"];
                result.OfferedServerTypes.Add(typeName ?? "(none)");
                if (typeName != "callServerTypeTelegramReflector") continue;

                result.Servers.Add(new TdCallServer
                {
                    Id = (long?)server["id"] ?? 0,
                    IpAddress = (string)server["ip_address"] ?? string.Empty,
                    Ipv6Address = (string)server["ipv6_address"] ?? string.Empty,
                    Port = (int?)server["port"] ?? 0,
                    PeerTag = DecodeBase64((string)server["type"]["peer_tag"]),
                });
            }
        }

        private static byte[] DecodeBase64(string value)
        {
            if (string.IsNullOrEmpty(value)) return new byte[0];
            try
            {
                return Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                // A malformed key would otherwise take down the receive loop.
                return new byte[0];
            }
        }
    }

    internal sealed class TdCallProtocol
    {
        public bool UdpP2p;
        public bool UdpReflector;
        public int MinLayer;
        public int MaxLayer;
    }

    internal sealed class TdCallServer
    {
        public long Id;
        public string IpAddress;
        public string Ipv6Address;
        public int Port;
        public byte[] PeerTag;
    }

    /// <summary>
    /// Flattened view of the TDLib call object. State-specific fields are only
    /// meaningful for the matching <see cref="State"/> value.
    /// </summary>
    internal sealed class TdCall
    {
        public long Id;
        public long UserId;
        public bool IsOutgoing;
        public bool IsVideo;

        /// <summary>callStatePending, callStateReady, callStateDiscarded, ...</summary>
        public string State = string.Empty;

        // callStatePending
        public bool IsCreated;
        public bool IsReceived;

        // callStateReady
        public TdCallProtocol Protocol;
        public readonly List<TdCallServer> Servers = new List<TdCallServer>();
        public byte[] EncryptionKey = new byte[0];
        public string Config = string.Empty;
        public bool AllowP2p;
        /// <summary>library_versions from the negotiated protocol; non-empty means tgcalls.</summary>
        public readonly List<string> LibraryVersions = new List<string>();
        public List<string> Emojis;

        /// <summary>
        /// Every server type offered, including ones we cannot use. Diagnostic:
        /// callServerTypeWebrtc means the peer negotiated the tgcalls protocol,
        /// which this libtgvoip build does not implement.
        /// </summary>
        public readonly List<string> OfferedServerTypes = new List<string>();

        // callStateDiscarded
        public string DiscardReason = string.Empty;
        public bool NeedRating;
        public bool NeedDebugInformation;

        // callStateError
        public int ErrorCode;
        public string ErrorMessage = string.Empty;

        public bool IsReady { get { return State == "callStateReady"; } }
        public bool IsDiscarded { get { return State == "callStateDiscarded"; } }
        public bool WasDeclined { get { return DiscardReason == "callDiscardReasonDeclined"; } }
    }
}
