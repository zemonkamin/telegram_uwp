using System.ComponentModel;
using System.Collections.ObjectModel;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Telegram.Models
{
    public sealed class FolderViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;
        private ObservableCollection<ChatViewModel> _visibleChats;

        public event PropertyChangedEventHandler PropertyChanged;

        public int Id { get; set; }
        public string Title { get; set; }
        public int Count { get; set; }

        public ObservableCollection<ChatViewModel> VisibleChats
        {
            get { return _visibleChats; }
            set
            {
                if (object.ReferenceEquals(_visibleChats, value)) return;
                _visibleChats = value;
                OnPropertyChanged("VisibleChats");
            }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged("IsSelected");
                OnPropertyChanged("HeaderForeground");
                OnPropertyChanged("SelectedUnderlineVisibility");
            }
        }

        public Brush HeaderForeground
        {
            get
            {
                if (IsSelected)
                    return new SolidColorBrush(IsLightTheme() ? Color.FromArgb(255, 17, 17, 17) : Colors.White);

                return new SolidColorBrush(IsLightTheme()
                    ? Color.FromArgb(255, 96, 96, 96)
                    : Color.FromArgb(255, 142, 142, 142));
            }
        }

        public string DisplayTitle
        {
            get { return Count > 0 ? Title + " (" + Count + ")" : Title; }
        }

        public Visibility SelectedUnderlineVisibility
        {
            get { return IsSelected ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string HeaderTitle
        {
            get
            {
                if (Id == -1) return "All chats";
                if (Id == 1) return "Archive";
                if (string.IsNullOrEmpty(Title)) return "Folder";
                return Title;
            }
        }

        private void OnPropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }

        private static bool IsLightTheme()
        {
            try
            {
                var color = new UISettings().GetColorValue(UIColorType.Background);
                return color.R + color.G + color.B > 384;
            }
            catch
            {
                return false;
            }
        }
    }
}
