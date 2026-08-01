using System;
using System.ComponentModel;

namespace Telegram.Models
{
    public sealed class StickerItemViewModel : INotifyPropertyChanged
    {
        private string _stickerSourceUri;
        private string _thumbnailSourceUri;

        public long SetId { get; set; }
        public long FileId { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Emoji { get; set; }
        public string Format { get; set; }

        public string StickerSourceUri
        {
            get { return _stickerSourceUri; }
            set
            {
                if (string.Equals(_stickerSourceUri, value, StringComparison.Ordinal)) return;
                _stickerSourceUri = value;
                OnPropertyChanged("StickerSourceUri");
                OnPropertyChanged("BestSourceUri");
            }
        }

        public string ThumbnailSourceUri
        {
            get { return _thumbnailSourceUri; }
            set
            {
                if (string.Equals(_thumbnailSourceUri, value, StringComparison.Ordinal)) return;
                _thumbnailSourceUri = value;
                OnPropertyChanged("ThumbnailSourceUri");
                OnPropertyChanged("BestSourceUri");
            }
        }

        public string BestSourceUri
        {
            get { return string.IsNullOrEmpty(StickerSourceUri) ? ThumbnailSourceUri : StickerSourceUri; }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public void OnPropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
