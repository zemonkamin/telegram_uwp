using System;
using System.Collections.Generic;
using System.ComponentModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Telegram.Models
{
    public sealed class InlineQueryBotViewModel
    {
        public long UserId { get; set; }
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string Placeholder { get; set; }
    }

    public sealed class InlineQueryResultsButtonViewModel
    {
        public string Text { get; set; }
        public string Type { get; set; }
        public string Parameter { get; set; }
        public string Url { get; set; }
        public string BotUsername { get; set; }
    }

    public sealed class InlineQueryResultItemViewModel : INotifyPropertyChanged
    {
        private string _previewUri;
        private byte[] _miniThumbnailBytes;
        private ImageSource _previewImageSource;
        private bool _isSending;

        public long QueryId { get; set; }
        public string ResultId { get; set; }
        public string BotUsername { get; set; }
        public string ResultType { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
        public string KindGlyph { get; set; }
        public long PreviewFileId { get; set; }
        public int PreviewWidth { get; set; }
        public int PreviewHeight { get; set; }

        public byte[] MiniThumbnailBytes
        {
            get { return _miniThumbnailBytes; }
            set
            {
                if (object.ReferenceEquals(_miniThumbnailBytes, value)) return;
                _miniThumbnailBytes = value;
                if (string.IsNullOrEmpty(_previewUri)) _previewImageSource = null;
                OnPreviewChanged();
            }
        }

        public string PreviewUri
        {
            get { return _previewUri; }
            set
            {
                if (string.Equals(_previewUri, value, StringComparison.Ordinal)) return;
                _previewUri = value;
                _previewImageSource = null;
                OnPreviewChanged();
            }
        }

        public ImageSource PreviewImageSource
        {
            get
            {
                if (_previewImageSource == null)
                    _previewImageSource = CreatePreviewImageSource();
                return _previewImageSource;
            }
        }

        public bool IsSending
        {
            get { return _isSending; }
            set
            {
                if (_isSending == value) return;
                _isSending = value;
                OnPropertyChanged("IsSending");
                OnPropertyChanged("SendingVisibility");
            }
        }

        public Visibility PreviewVisibility
        {
            get { return PreviewImageSource == null ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility PlaceholderVisibility
        {
            get { return PreviewImageSource == null ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility SendingVisibility
        {
            get { return IsSending ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Title)) return Title;
                if (!string.IsNullOrWhiteSpace(Description)) return Description;
                return GetFallbackTitle(ResultType);
            }
        }

        public string DisplayDescription
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Description)) return string.Empty;
                if (string.Equals(Description, DisplayTitle, StringComparison.Ordinal)) return string.Empty;
                return Description;
            }
        }

        public string DisplayUrl
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Url)) return string.Empty;
                if (string.Equals(Url, DisplayDescription, StringComparison.OrdinalIgnoreCase)) return string.Empty;
                return Url;
            }
        }

        public Visibility DescriptionVisibility
        {
            get { return string.IsNullOrWhiteSpace(DisplayDescription) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility UrlVisibility
        {
            get { return string.IsNullOrWhiteSpace(DisplayUrl) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private ImageSource CreatePreviewImageSource()
        {
            if (!string.IsNullOrWhiteSpace(_previewUri))
            {
                try
                {
                    var image = new BitmapImage();
                    image.DecodePixelWidth = 256;
                    image.ImageFailed += delegate
                    {
                        if (!object.ReferenceEquals(_previewImageSource, image)) return;
                        var fallback = ChatMediaPreviewHelper.CreateImageSource(_miniThumbnailBytes, 256, null);
                        if (fallback == null) return;
                        _previewImageSource = fallback;
                        OnPropertyChanged("PreviewImageSource");
                        OnPropertyChanged("PreviewVisibility");
                        OnPropertyChanged("PlaceholderVisibility");
                    };
                    image.UriSource = new Uri(_previewUri);
                    return image;
                }
                catch
                {
                }
            }

            return ChatMediaPreviewHelper.CreateImageSource(_miniThumbnailBytes, 256, null);
        }

        private static string GetFallbackTitle(string resultType)
        {
            if (resultType == "inlineQueryResultAnimation") return "GIF";
            if (resultType == "inlineQueryResultPhoto") return "Photo";
            if (resultType == "inlineQueryResultVideo") return "Video";
            if (resultType == "inlineQueryResultAudio") return "Audio";
            if (resultType == "inlineQueryResultVoiceNote") return "Voice message";
            if (resultType == "inlineQueryResultContact") return "Contact";
            if (resultType == "inlineQueryResultLocation") return "Location";
            if (resultType == "inlineQueryResultVenue") return "Venue";
            if (resultType == "inlineQueryResultSticker") return "Sticker";
            if (resultType == "inlineQueryResultGame") return "Game";
            if (resultType == "inlineQueryResultArticle") return "Article";
            return "Document";
        }

        private void OnPreviewChanged()
        {
            OnPropertyChanged("PreviewUri");
            OnPropertyChanged("PreviewImageSource");
            OnPropertyChanged("PreviewVisibility");
            OnPropertyChanged("PlaceholderVisibility");
        }

        private void OnPropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class InlineQueryResultsPageViewModel
    {
        public InlineQueryBotViewModel Bot { get; set; }
        public InlineQueryResultsButtonViewModel Button { get; set; }
        public long QueryId { get; set; }
        public string NextOffset { get; set; }
        public bool IsGallery { get; set; }
        public List<InlineQueryResultItemViewModel> Results { get; set; }

        public InlineQueryResultsPageViewModel()
        {
            Results = new List<InlineQueryResultItemViewModel>();
        }
    }
}
