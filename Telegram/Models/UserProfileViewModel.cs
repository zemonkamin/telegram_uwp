using System.Collections.ObjectModel;
using Windows.UI.Xaml;

namespace Telegram.Models
{
    public sealed class UserProfileViewModel
    {
        public UserProfileViewModel()
        {
            Rows = new ObservableCollection<ProfileInfoRowViewModel>();
        }

        public ChatViewModel Chat { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Initials { get; set; }
        public string AvatarUri { get; set; }
        public string PeerType { get; set; }
        public int SubscriberCount { get; set; }
        public int OnlineCount { get; set; }
        public bool IsSelf { get; set; }
        public bool IsChannel { get; set; }
        public bool IsGroup { get; set; }
        public ObservableCollection<ProfileInfoRowViewModel> Rows { get; private set; }

        public string SectionTitle
        {
            get { return IsChannel ? "CHANNEL" : IsGroup ? "GROUP" : "INFO"; }
        }

        public string CounterText
        {
            get
            {
                if (SubscriberCount > 0)
                    return ChatViewModel.FormatCount(SubscriberCount).ToUpperInvariant() + (IsChannel ? " SUBSCRIBERS" : " MEMBERS");
                if (OnlineCount > 0)
                    return ChatViewModel.FormatCount(OnlineCount).ToUpperInvariant() + " ONLINE";
                return string.Empty;
            }
        }

        public Visibility CounterVisibility
        {
            get { return string.IsNullOrEmpty(CounterText) ? Visibility.Collapsed : Visibility.Visible; }
        }
    }

    public sealed class ProfileInfoRowViewModel
    {
        public string Label { get; set; }
        public string Value { get; set; }

        public Visibility Visibility
        {
            get { return string.IsNullOrWhiteSpace(Value) ? Visibility.Collapsed : Visibility.Visible; }
        }
    }

    public sealed class ProfilePhotoViewModel
    {
        public long PhotoId { get; set; }
        public string Uri { get; set; }
    }
}
