using Telegram.Models;

namespace Telegram.Services
{
    internal static class ActiveChatService
    {
        private static string _activePeerKey;
        private static bool _isForeground;

        public static void SetForeground(bool isForeground)
        {
            _isForeground = isForeground;
        }

        public static void SetActive(ChatViewModel chat)
        {
            _activePeerKey = GetPeerKey(chat);
        }

        public static void ClearActive(ChatViewModel chat)
        {
            var key = GetPeerKey(chat);
            if (!string.IsNullOrEmpty(key) && key == _activePeerKey)
                _activePeerKey = null;
        }

        public static bool IsActive(ChatViewModel chat)
        {
            if (!_isForeground) return false;
            var key = GetPeerKey(chat);
            return !string.IsNullOrEmpty(key) && key == _activePeerKey;
        }

        private static string GetPeerKey(ChatViewModel chat)
        {
            if (chat == null) return null;
            if (!string.IsNullOrEmpty(chat.PeerKey)) return chat.PeerKey;
            if (string.IsNullOrEmpty(chat.PeerType) || chat.PeerId == 0) return null;
            return chat.PeerType + ":" + chat.PeerId.ToString();
        }
    }
}
