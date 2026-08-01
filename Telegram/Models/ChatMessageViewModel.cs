using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Display;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;
using Windows.Storage.Streams;

namespace Telegram.Models
{
    internal static class ChatMessageLayoutMetrics
    {
        public static double CalculateMediaWidth(bool isOutgoing, bool isGroupChat)
        {
            var width = GetViewWidth();
            var horizontalMargins = isOutgoing ? 74.0 : (isGroupChat ? 118.0 : 74.0);
            const double contentMargins = 18.0;
            const double safety = 14.0;
            return Clamp(Math.Floor(width - horizontalMargins - contentMargins - safety), 156, GetAdaptiveMaxWidth(306, 180));
        }

        public static double CalculateTextWidth(bool isOutgoing, bool isGroupChat)
        {
            var width = GetViewWidth();
            var horizontalMargins = isOutgoing ? 74.0 : (isGroupChat ? 118.0 : 74.0);
            // 432 is Unigram's MessageMaxWidth. On a phone the screen-width term decides anyway;
            // this only matters on a wide window.
            return Clamp(Math.Floor(width - horizontalMargins - 16.0), 180, GetAdaptiveMaxWidth(432, 260));
        }

        private static double GetViewWidth()
        {
            var width = 360.0;
            try
            {
                if (Window.Current != null && Window.Current.Bounds.Width > 0)
                    width = Window.Current.Bounds.Width;
            }
            catch
            {
            }
            return width;
        }

        private static double GetAdaptiveMaxWidth(double baseMax, double minimumMax)
        {
            var width = GetViewWidth();
            if (width < 500.0)
                return baseMax;

            var rawScale = 1.0;
            try
            {
                rawScale = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
            }
            catch
            {
            }

            if (rawScale <= 1.05)
                return baseMax;

            var shrink = Clamp(1.0 + ((rawScale - 1.0) * 0.45), 1.0, 1.55);
            return Math.Max(minimumMax, Math.Floor(baseMax / shrink));
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    internal static class ChatMediaPreviewHelper
    {
        /// <summary>
        /// Decodes an inline thumbnail without blocking the caller. This runs while the chat
        /// history is being realized, so waiting on the decode (as the previous implementation
        /// did) stalled the UI thread once per media message. The aspect ratio is not known
        /// until the decode finishes, so it is reported through <paramref name="aspectRatioReady"/>.
        /// </summary>
        public static ImageSource CreateImageSource(byte[] bytes, int decodePixelWidth, Action<double> aspectRatioReady)
        {
            if (bytes == null || bytes.Length == 0) return null;

            try
            {
                var image = new BitmapImage();
                if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth;

                if (aspectRatioReady != null)
                {
                    // Fallback only. Callers read the size from the header first (see
                    // TryReadImageSize) so the row does not resize once the decode lands.
                    image.ImageOpened += delegate
                    {
                        if (image.PixelWidth > 0 && image.PixelHeight > 0)
                            aspectRatioReady((double)image.PixelWidth / image.PixelHeight);
                    };
                }

                var stream = new InMemoryRandomAccessStream();
                using (var output = stream.AsStreamForWrite())
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush();
                }

                stream.Seek(0);
                var ignored = SetSourceAndDisposeAsync(image, stream);
                return image;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Pulls the pixel dimensions out of a JPEG or PNG header without decoding the image.
        /// TDLib minithumbnails are JPEG.
        /// </summary>
        internal static bool TryReadImageSize(byte[] bytes, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (bytes == null || bytes.Length < 8) return false;

            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                if (bytes.Length < 24) return false;
                width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                return width > 0 && height > 0;
            }

            if (bytes[0] != 0xFF || bytes[1] != 0xD8) return false;

            var index = 2;
            while (index + 9 < bytes.Length)
            {
                if (bytes[index] != 0xFF)
                {
                    index++;
                    continue;
                }

                var marker = bytes[index + 1];
                if (marker == 0xFF)
                {
                    index++;
                    continue;
                }
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    index += 2;
                    continue;
                }

                var segmentLength = (bytes[index + 2] << 8) | bytes[index + 3];
                if (segmentLength < 2) return false;

                // SOF0..SOF15 carry the frame size; C4/C8/CC are Huffman/arithmetic tables.
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    height = (bytes[index + 5] << 8) | bytes[index + 6];
                    width = (bytes[index + 7] << 8) | bytes[index + 8];
                    return width > 0 && height > 0;
                }

                index += 2 + segmentLength;
            }

            return false;
        }

        private static async System.Threading.Tasks.Task SetSourceAndDisposeAsync(BitmapImage image, InMemoryRandomAccessStream stream)
        {
            try
            {
                await image.SetSourceAsync(stream);
            }
            catch
            {
            }
            finally
            {
                try { stream.Dispose(); }
                catch { }
            }
        }
    }

    public sealed class MessageReactionViewModel : INotifyPropertyChanged
    {
        private bool _isChosen;
        private int _count;
        private string _customEmojiUri;
        private string _localEmojiImageUri;
        private ImageSource _localEmojiImageSource;

        public event PropertyChangedEventHandler PropertyChanged;

        public ChatMessageViewModel OwnerMessage { get; set; }
        public string Emoticon { get; set; }
        public long CustomEmojiDocumentId { get; set; }

        public string CustomEmojiUri
        {
            get { return _customEmojiUri; }
            set
            {
                if (string.Equals(_customEmojiUri, value, StringComparison.OrdinalIgnoreCase)) return;
                _customEmojiUri = value;
                OnPropertyChanged("CustomEmojiUri");
                OnPropertyChanged("CustomEmojiVisibility");
                OnPropertyChanged("EmoticonVisibility");
                OnPropertyChanged("DisplayText");
            }
        }

        public string ReactionKey
        {
            get { return CustomEmojiDocumentId != 0 ? "custom:" + CustomEmojiDocumentId.ToString() : (Emoticon ?? string.Empty); }
        }

        public string DisplayText
        {
            get { return CustomEmojiDocumentId != 0 ? (string.IsNullOrEmpty(CustomEmojiUri) ? "\u2726" : string.Empty) : Emoticon; }
        }

        public string LocalEmojiUri
        {
            get
            {
                if (CustomEmojiDocumentId != 0) return string.Empty;
                return Telegram.ChatPage.ResolveLocalEmojiAssetUri(Emoticon);
            }
        }

        public ImageSource LocalEmojiImageSource
        {
            get
            {
                var uri = LocalEmojiUri;
                if (string.IsNullOrEmpty(uri)) return null;
                if (_localEmojiImageSource != null && string.Equals(_localEmojiImageUri, uri, StringComparison.OrdinalIgnoreCase))
                    return _localEmojiImageSource;

                try
                {
                    _localEmojiImageUri = uri;
                    _localEmojiImageSource = new BitmapImage(new Uri(uri));
                    return _localEmojiImageSource;
                }
                catch
                {
                    _localEmojiImageUri = null;
                    _localEmojiImageSource = null;
                    return null;
                }
            }
        }

        public Visibility CustomEmojiVisibility
        {
            get { return CustomEmojiDocumentId != 0 && !string.IsNullOrEmpty(CustomEmojiUri) ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility LocalEmojiVisibility
        {
            get { return CustomEmojiDocumentId == 0 && !string.IsNullOrEmpty(LocalEmojiUri) ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility EmoticonVisibility
        {
            get { return CustomEmojiVisibility == Visibility.Visible || LocalEmojiVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; }
        }

        public int Count
        {
            get { return _count; }
            set
            {
                if (_count == value) return;
                _count = value;
                OnPropertyChanged("Count");
                OnPropertyChanged("CountText");
                OnPropertyChanged("Visibility");
            }
        }

        public bool IsChosen
        {
            get { return _isChosen; }
            set
            {
                if (_isChosen == value) return;
                _isChosen = value;
                OnPropertyChanged("IsChosen");
                OnPropertyChanged("ChipBackground");
                OnPropertyChanged("ChipForeground");
            }
        }

        public string CountText
        {
            get { return Count <= 0 ? string.Empty : Count.ToString(); }
        }

        public Visibility Visibility
        {
            get { return Count > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public SolidColorBrush ChipBackground
        {
            get { return new SolidColorBrush(IsChosen ? GetAccentColor() : GetDarkAccentColor()); }
        }

        public SolidColorBrush ChipForeground
        {
            get { return new SolidColorBrush(Colors.White); }
        }

        private static Color GetAccentColor()
        {
            try { return new UISettings().GetColorValue(UIColorType.Accent); }
            catch { return Color.FromArgb(0xff, 0x00, 0x78, 0xd7); }
        }

        private static Color GetDarkAccentColor()
        {
            var accent = GetAccentColor();
            return Color.FromArgb(
                0xff,
                (byte)Math.Max(0, accent.R * 55 / 100),
                (byte)Math.Max(0, accent.G * 55 / 100),
                (byte)Math.Max(0, accent.B * 55 / 100));
        }

        public void NotifyStateChanged()
        {
            OnPropertyChanged("Count");
            OnPropertyChanged("CountText");
            OnPropertyChanged("IsChosen");
            OnPropertyChanged("Visibility");
            OnPropertyChanged("ChipBackground");
            OnPropertyChanged("ChipForeground");
            OnPropertyChanged("ReactionKey");
            OnPropertyChanged("DisplayText");
            OnPropertyChanged("CustomEmojiUri");
            OnPropertyChanged("CustomEmojiVisibility");
            OnPropertyChanged("LocalEmojiUri");
            OnPropertyChanged("LocalEmojiImageSource");
            OnPropertyChanged("LocalEmojiVisibility");
            OnPropertyChanged("EmoticonVisibility");
        }

        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class MessageTextEntityViewModel
    {
        public int Offset { get; set; }
        public int Length { get; set; }
        public string Type { get; set; }
        public string Url { get; set; }
    }

    public sealed class CommentAvatarViewModel : INotifyPropertyChanged
    {
        private string _avatarUri;
        private ImageSource _avatarImageSource;
        private string _avatarImageSourceUri;

        public event PropertyChangedEventHandler PropertyChanged;

        public string PeerKey { get; set; }
        public string PeerType { get; set; }
        public long PeerId { get; set; }
        public long AccessHash { get; set; }
        public string Title { get; set; }
        public string Initials { get; set; }
        public long AvatarPhotoId { get; set; }
        public int AvatarDcId { get; set; }
        public byte[] AvatarStrippedThumb { get; set; }

        public string AvatarUri
        {
            get { return _avatarUri; }
            set
            {
                if (_avatarUri == value) return;
                _avatarUri = value;
                OnPropertyChanged("AvatarUri");
                OnPropertyChanged("AvatarImageSource");
                OnPropertyChanged("InitialsVisibility");
            }
        }

        public ImageSource AvatarImageSource
        {
            get
            {
                var uri = AvatarUri;
                if (string.IsNullOrEmpty(uri)) return null;
                if (_avatarImageSource != null && string.Equals(_avatarImageSourceUri, uri, StringComparison.OrdinalIgnoreCase))
                    return _avatarImageSource;
                try
                {
                    var image = new BitmapImage();
                    image.DecodePixelWidth = 64;
                    image.UriSource = new Uri(uri);
                    _avatarImageSource = image;
                    _avatarImageSourceUri = uri;
                    return image;
                }
                catch { return null; }
            }
        }

        public Visibility InitialsVisibility
        {
            get { return string.IsNullOrEmpty(AvatarUri) ? Visibility.Visible : Visibility.Collapsed; }
        }

        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class StructuredMediaItemViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isCompleted;
        private bool _isBusy;
        private int _voters;
        private int _totalVoters;
        private int _votePercentage = -1;

        public event PropertyChangedEventHandler PropertyChanged;

        public ChatMessageViewModel OwnerMessage { get; set; }
        public string Kind { get; set; }
        public string Text { get; set; }
        public string Subtitle { get; set; }
        public byte[] PollOption { get; set; }
        public int PollOptionId { get; set; }
        public int TodoId { get; set; }
        public bool IsCorrect { get; set; }
        public bool IsWrong { get; set; }

        public int Voters
        {
            get { return _voters; }
            set
            {
                if (_voters == value) return;
                _voters = value;
                OnPropertyChanged("Voters");
                OnPropertyChanged("DisplayText");
                OnPropertyChanged("VotePercent");
                OnPropertyChanged("PercentText");
                OnPropertyChanged("VotersText");
            }
        }

        public int TotalVoters
        {
            get { return _totalVoters; }
            set
            {
                if (_totalVoters == value) return;
                _totalVoters = value;
                OnPropertyChanged("TotalVoters");
                OnPropertyChanged("VotePercent");
                OnPropertyChanged("PercentText");
            }
        }

        public int VotePercentage
        {
            get { return _votePercentage; }
            set
            {
                if (value < -1) value = -1;
                if (value > 100) value = 100;
                if (_votePercentage == value) return;
                _votePercentage = value;
                OnPropertyChanged("VotePercentage");
                OnPropertyChanged("VotePercent");
                OnPropertyChanged("PercentText");
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
                OnPropertyChanged("Glyph");
                OnPropertyChanged("PollGlyph");
            }
        }

        public bool IsCompleted
        {
            get { return _isCompleted; }
            set
            {
                if (_isCompleted == value) return;
                _isCompleted = value;
                OnPropertyChanged("IsCompleted");
                OnPropertyChanged("Glyph");
            }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            set
            {
                if (_isBusy == value) return;
                _isBusy = value;
                OnPropertyChanged("IsBusy");
                OnPropertyChanged("IsEnabled");
            }
        }

        public bool IsEnabled
        {
            get { return !IsBusy && (OwnerMessage == null || !OwnerMessage.StructuredMediaIsClosed); }
        }

        public string Glyph
        {
            get
            {
                if (Kind == "todo") return IsCompleted ? "\u2611" : "\u2610";
                return PollGlyph;
            }
        }

        public string PollGlyph
        {
            get
            {
                if (IsCorrect) return "\uE73E";
                if (IsWrong) return "\uE711";
                return IsSelected ? "\u2611" : "\u2610";
            }
        }

        public string DisplayText
        {
            get
            {
                var text = string.IsNullOrEmpty(Text) ? "Option" : Text;
                if (Kind == "poll" && Voters >= 0) text += " (" + Voters.ToString() + ")";
                return text;
            }
        }

        public string VotersText
        {
            get { return Voters < 0 ? string.Empty : Voters.ToString(); }
        }

        public double VotePercent
        {
            get
            {
                if (VotePercentage >= 0) return VotePercentage;
                var total = TotalVoters;
                if (total <= 0 && OwnerMessage != null) total = OwnerMessage.StructuredMediaTotalVoters;
                if (total <= 0 || Voters <= 0) return 0;
                var value = (double)Voters * 100.0 / (double)total;
                if (value < 0) return 0;
                if (value > 100) return 100;
                return value;
            }
        }

        public string PercentText
        {
            get
            {
                if (VotePercentage >= 0) return VotePercent.ToString("0") + "%";
                var total = TotalVoters;
                if (total <= 0 && OwnerMessage != null) total = OwnerMessage.StructuredMediaTotalVoters;
                if (total <= 0 || Voters < 0) return string.Empty;
                return Math.Round(VotePercent).ToString("0") + "%";
            }
        }

        public Visibility SubtitleVisibility
        {
            get { return string.IsNullOrWhiteSpace(Subtitle) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public SolidColorBrush Foreground
        {
            get { return OwnerMessage == null ? new SolidColorBrush(Colors.White) : OwnerMessage.MessageForeground; }
        }

        public SolidColorBrush SubtleForeground
        {
            get { return OwnerMessage == null ? new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)) : OwnerMessage.MessageSubtleForeground; }
        }

        public SolidColorBrush AccentBrush
        {
            get
            {
                if (IsCorrect) return new SolidColorBrush(Color.FromArgb(255, 55, 164, 86));
                if (IsWrong) return new SolidColorBrush(Color.FromArgb(255, 214, 74, 74));
                return new SolidColorBrush(GetAccentColor());
            }
        }

        public SolidColorBrush ProgressTrackBrush
        {
            get
            {
                if (OwnerMessage != null && OwnerMessage.IsOutgoing) return new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
                return new SolidColorBrush(IsLightTheme()
                    ? Color.FromArgb(55, 0, 0, 0)
                    : Color.FromArgb(70, 255, 255, 255));
            }
        }

        public void NotifyPollVisualStateChanged()
        {
            OnPropertyChanged("VotePercent");
            OnPropertyChanged("VotePercentage");
            OnPropertyChanged("PercentText");
            OnPropertyChanged("VotersText");
            OnPropertyChanged("PollGlyph");
            OnPropertyChanged("Glyph");
            OnPropertyChanged("Foreground");
            OnPropertyChanged("SubtleForeground");
            OnPropertyChanged("AccentBrush");
            OnPropertyChanged("ProgressTrackBrush");
            OnPropertyChanged("IsEnabled");
            OnPropertyChanged("Subtitle");
            OnPropertyChanged("SubtitleVisibility");
        }

        private static Color GetAccentColor()
        {
            try { return new UISettings().GetColorValue(UIColorType.Accent); }
            catch { return Color.FromArgb(0xff, 0x00, 0x78, 0xd7); }
        }

        private static bool IsLightTheme()
        {
            try
            {
                var color = new UISettings().GetColorValue(UIColorType.Background);
                return color.R > 127 && color.G > 127 && color.B > 127;
            }
            catch
            {
                return false;
            }
        }

        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    public sealed class ChatMediaAlbumRowViewModel
    {
        public ChatMediaAlbumRowViewModel()
        {
            Items = new ObservableCollection<ChatMediaItemViewModel>();
        }

        public ObservableCollection<ChatMediaItemViewModel> Items { get; private set; }
    }

    public sealed class ChatMediaItemViewModel : INotifyPropertyChanged
    {
        private string _mediaFileUri;
        private string _mediaFallbackUri;
        private string _mediaFullUri;
        private string _mediaPreviewUri;
        private string _mediaTitle;
        private string _mediaPerformer;
        private string _mediaErrorText;
        private bool _isMediaDownloading;
        private bool _isFileDownloadOperationActive;
        private long _mediaDownloadBytes;
        private long _mediaDownloadTotalBytes;
        private long _displayMediaDownloadBytes;
        private long _displayMediaDownloadTotalBytes;
        private DispatcherTimer _downloadProgressTimer;
        private bool _hasPlaybackError;
        private ImageSource _mediaPreviewImageSource;
        private string _mediaPreviewImageSourceUri;
        private ImageSource _photoImageSource;
        private string _photoImageSourceUri;
        private double _mediaPreviewAspectRatio;
        private bool _albumWideTile;

        public event PropertyChangedEventHandler PropertyChanged;

        public ChatMessageViewModel OwnerMessage { get; set; }
        public int SourceMessageId { get; set; }
        public string MediaKind { get; set; }
        public string MediaFileName { get; set; }
        public string MediaPerformer
        {
            get { return _mediaPerformer; }
            set
            {
                if (_mediaPerformer == value) return;
                _mediaPerformer = value;
                OnPropertyChanged("MediaPerformer");
            }
        }
        public string MediaMimeType { get; set; }
        public long MediaId { get; set; }
        public long MediaFullId { get; set; }
        public long MediaAccessHash { get; set; }
        public int MediaDcId { get; set; }
        public byte[] MediaFileReference { get; set; }
        public long MediaPreviewId { get; set; }
        public string MediaThumbSize { get; set; }
        public string FullPhotoThumbSize { get; set; }
        public byte[] MediaThumbBytes { get; set; }
        public long MediaSize { get; set; }
        public bool MediaIsPhoto { get; set; }
        public int MediaDurationSeconds { get; set; }

        public string MediaFileUri
        {
            get { return _mediaFileUri; }
            set
            {
                if (_mediaFileUri == value) return;
                _mediaFileUri = value;
                _photoImageSource = null;
                _photoImageSourceUri = null;
                HasPlaybackError = false;
                MediaErrorText = string.Empty;
                NotifyMediaStateChanged();
            }
        }

        public string MediaFallbackUri
        {
            get { return _mediaFallbackUri; }
            set
            {
                if (_mediaFallbackUri == value) return;
                _mediaFallbackUri = value;
                NotifyMediaStateChanged();
            }
        }

        public string MediaFullUri
        {
            get { return _mediaFullUri; }
            set
            {
                if (_mediaFullUri == value) return;
                _mediaFullUri = value;
                OnPropertyChanged("MediaFullUri");
            }
        }

        public string MediaPreviewUri
        {
            get { return _mediaPreviewUri; }
            set
            {
                if (_mediaPreviewUri == value) return;
                _mediaPreviewUri = value;
                _mediaPreviewImageSource = null;
                _mediaPreviewImageSourceUri = null;
                if (string.IsNullOrEmpty(value)) _mediaPreviewAspectRatio = 0;
                OnPropertyChanged("MediaPreviewUri");
                OnPropertyChanged("MediaPreviewImageSource");
                OnPropertyChanged("MediaPreviewVisibility");
            }
        }

        public Uri MediaUri
        {
            get
            {
                if (string.IsNullOrEmpty(MediaFileUri)) return null;
                if (MediaKind != "gif") return null;
                try { return new Uri(MediaFileUri); }
                catch { return null; }
            }
        }

        public string MediaTitle
        {
            get { return _mediaTitle; }
            set
            {
                if (_mediaTitle == value) return;
                _mediaTitle = value;
                OnPropertyChanged("MediaTitle");
                OnPropertyChanged("VisibleText");
                OnPropertyChanged("UnsupportedMessageDisplayText");
                OnPropertyChanged("LooksLikeUnsupportedPollCandidate");
                OnPropertyChanged("TextVisibility");
                OnPropertyChanged("MediaTitleVisibility");
                OnPropertyChanged("DownloadButtonText");
            }
        }

        public string MediaErrorText
        {
            get { return _mediaErrorText; }
            set
            {
                if (_mediaErrorText == value) return;
                _mediaErrorText = value;
                OnPropertyChanged("MediaErrorText");
                OnPropertyChanged("MediaErrorVisibility");
            }
        }

        public bool HasPlaybackError
        {
            get { return _hasPlaybackError; }
            set
            {
                if (_hasPlaybackError == value) return;
                _hasPlaybackError = value;
                OnPropertyChanged("HasPlaybackError");
                OnPropertyChanged("MediaErrorVisibility");
                OnPropertyChanged("VideoVisibility");
                OnPropertyChanged("GifVisibility");
                OnPropertyChanged("RoundVideoVisibility");
                OnPropertyChanged("AudioVisibility");
                OnPropertyChanged("VoiceVisibility");
                OnPropertyChanged("MusicVisibility");
                OnPropertyChanged("StickerVisibility");
                OnPropertyChanged("PlayerVisibility");
            }
        }

        public bool IsMediaDownloading
        {
            get { return _isMediaDownloading; }
            set
            {
                if (_isMediaDownloading == value) return;
                _isMediaDownloading = value;
                OnPropertyChanged("IsMediaDownloading");
                OnPropertyChanged("DownloadButtonText");
                OnPropertyChanged("DownloadButtonEnabled");
                OnPropertyChanged("DownloadingVisibility");
                OnPropertyChanged("DownloadIconVisibility");
                OnPropertyChanged("DownloadProgressBarVisibility");
                OnPropertyChanged("DownloadProgressValue");
                OnPropertyChanged("DownloadProgressText");
                OnPropertyChanged("DownloadProgressIndeterminate");
                OnPropertyChanged("GroupedFileDownloadProgressVisibility");
                OnPropertyChanged("GroupedFileDownloadProgressValue");
                OnPropertyChanged("GroupedFileDownloadProgressText");
                OnPropertyChanged("GroupedFileDownloadIndeterminate");
                UpdateDisplayedDownloadProgress(true);
            }
        }

        public bool IsFileDownloadOperationActive
        {
            get { return _isFileDownloadOperationActive; }
            set
            {
                if (_isFileDownloadOperationActive == value) return;
                _isFileDownloadOperationActive = value;
                OnPropertyChanged("IsFileDownloadOperationActive");
                OnPropertyChanged("DownloadButtonEnabled");
                OnPropertyChanged("DownloadIconVisibility");
                OnPropertyChanged("DownloadProgressBarVisibility");
                OnPropertyChanged("GroupedFileIdleVisibility");
                OnPropertyChanged("GroupedFileDownloadProgressVisibility");
                OnPropertyChanged("GroupedFileDownloadIndeterminate");
                OnPropertyChanged("GroupedFileDownloadProgressValue");
                OnPropertyChanged("GroupedFileDownloadProgressText");
                UpdateDisplayedDownloadProgress(true);
            }
        }

        public long MediaDownloadBytes
        {
            get { return _mediaDownloadBytes; }
            set
            {
                if (_mediaDownloadBytes == value) return;
                _mediaDownloadBytes = value;
                OnPropertyChanged("MediaDownloadBytes");
                OnPropertyChanged("DownloadingVisibility");
                OnPropertyChanged("DownloadProgressBarVisibility");
                OnPropertyChanged("DownloadProgressIndeterminate");
                OnPropertyChanged("GroupedFileDownloadProgressVisibility");
                OnPropertyChanged("GroupedFileDownloadIndeterminate");
                UpdateDisplayedDownloadProgress(false);
            }
        }

        public long MediaDownloadTotalBytes
        {
            get { return _mediaDownloadTotalBytes; }
            set
            {
                if (_mediaDownloadTotalBytes == value) return;
                _mediaDownloadTotalBytes = value;
                OnPropertyChanged("MediaDownloadTotalBytes");
                OnPropertyChanged("DownloadingVisibility");
                OnPropertyChanged("DownloadProgressBarVisibility");
                OnPropertyChanged("DownloadProgressIndeterminate");
                OnPropertyChanged("GroupedFileDownloadProgressVisibility");
                OnPropertyChanged("GroupedFileDownloadIndeterminate");
                UpdateDisplayedDownloadProgress(false);
            }
        }

        public bool DownloadButtonEnabled
        {
            get { return !IsMediaDownloading && !IsFileDownloadOperationActive; }
        }

        public string DownloadButtonText
        {
            get
            {
                if (IsMediaDownloading)
                {
                    if (MediaKind == "video" || MediaKind == "roundvideo") return "Buffering...";
                    return "Loading...";
                }
                if (MediaKind == "photo") return "Load photo";
                if (MediaKind == "roundvideo") return "Open round video";
                if (MediaKind == "video") return "Open video";
                if (MediaKind == "gif") return "Open GIF";
                if (MediaKind == "sticker") return "Load sticker";
                if (MediaKind == "voice") return "Load voice message";
                if (MediaKind == "audio") return "Load audio";
                return "Load file";
            }
        }

        public Visibility PhotoVisibility
        {
            get { return MediaKind == "photo" && !string.IsNullOrEmpty(MediaFileUri) ? Visibility.Visible : Visibility.Collapsed; }
        }

        public ImageSource PhotoImageSource
        {
            get
            {
                if (MediaKind != "photo" || string.IsNullOrEmpty(MediaFileUri)) return null;
                try
                {
                    if (_photoImageSource != null &&
                        string.Equals(_photoImageSourceUri, MediaFileUri, StringComparison.OrdinalIgnoreCase))
                        return _photoImageSource;

                    var image = new BitmapImage();
                    image.DecodePixelWidth = 480;
                    image.UriSource = new Uri(MediaFileUri);
                    _photoImageSource = image;
                    _photoImageSourceUri = MediaFileUri;
                    return _photoImageSource;
                }
                catch
                {
                    return null;
                }
            }
        }

        public Visibility VideoVisibility
        {
            get { return MediaKind == "video" && IsPlayableVideoUri(MediaFileUri) && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility GifVisibility
        {
            get { return MediaKind == "gif" && !string.IsNullOrEmpty(MediaFileUri) && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility RoundVideoVisibility
        {
            get { return MediaKind == "roundvideo" && IsPlayableVideoUri(MediaFileUri) && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility VideoPlaceholderVisibility
        {
            get { return (MediaKind == "video" || MediaKind == "roundvideo") && string.IsNullOrEmpty(MediaFileUri) ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility AudioPlaceholderVisibility
        {
            get { return Visibility.Collapsed; }
        }

        public Visibility AudioVisibility
        {
            get { return (MediaKind == "audio" || MediaKind == "voice") && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility VoiceVisibility
        {
            get { return MediaKind == "voice" && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility MusicVisibility
        {
            get { return MediaKind == "audio" && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility StickerVisibility
        {
            get { return MediaKind == "sticker" && (!string.IsNullOrEmpty(MediaFileUri) || !string.IsNullOrEmpty(MediaFallbackUri)) && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility StaticEmojiVisibility
        {
            get { return Visibility.Collapsed; }
        }

        public Visibility PlayerVisibility
        {
            get { return (MediaKind == "video" || MediaKind == "gif" || MediaKind == "roundvideo") && !string.IsNullOrEmpty(MediaFileUri) && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility AlbumDownloadButtonVisibility
        {
            get { return MediaKind == "document" ? Visibility.Collapsed : DownloadButtonVisibility; }
        }

        public Visibility DownloadButtonVisibility
        {
            get
            {
                if (IsWebPageMedia(MediaKind)) return Visibility.Collapsed;
                if (MediaKind == "audio" || MediaKind == "voice") return Visibility.Collapsed;
                if (MediaKind == "emoji") return Visibility.Collapsed;
                if (MediaKind == "sticker" && !string.IsNullOrEmpty(MediaFallbackUri)) return Visibility.Collapsed;
                return string.IsNullOrEmpty(MediaFileUri) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility DownloadingVisibility
        {
            get
            {
                return Visibility.Collapsed;
            }
        }

        public Visibility DownloadIconVisibility
        {
            get { return IsMediaDownloading || IsFileDownloadOperationActive ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility DownloadProgressBarVisibility
        {
            get
            {
                if ((!IsMediaDownloading && !IsFileDownloadOperationActive) || !string.IsNullOrEmpty(MediaFileUri)) return Visibility.Collapsed;
                return Visibility.Visible;
            }
        }

        public double DownloadProgressValue
        {
            get
            {
                var total = _displayMediaDownloadTotalBytes > 0 ? _displayMediaDownloadTotalBytes : (MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize);
                if (total <= 0) return 0;
                var value = (double)_displayMediaDownloadBytes * 100.0 / (double)total;
                if (value < 0) return 0;
                if (value <= 0) return 0;
                if (value > 100) return 100;
                return value;
            }
        }

        public string DownloadProgressText
        {
            get
            {
                return Math.Round(DownloadProgressValue).ToString("0") + "%";
            }
        }

        public bool DownloadProgressIndeterminate
        {
            get
            {
                var total = MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize;
                return (IsMediaDownloading || IsFileDownloadOperationActive) && total <= 0;
            }
        }

        private bool IsVideoMediaKind
        {
            get { return MediaKind == "video" || MediaKind == "roundvideo"; }
        }

        public ImageSource MediaPreviewImageSource
        {
            get
            {
                if (!(MediaKind == "video" || MediaKind == "roundvideo" || MediaKind == "audio")) return null;
                if (!string.IsNullOrEmpty(MediaPreviewUri))
                {
                    try
                    {
                        if (_mediaPreviewImageSource != null &&
                            string.Equals(_mediaPreviewImageSourceUri, MediaPreviewUri, StringComparison.OrdinalIgnoreCase))
                            return _mediaPreviewImageSource;

                        var image = new BitmapImage();
                        image.DecodePixelWidth = 220;
                        image.UriSource = new Uri(MediaPreviewUri);
                        _mediaPreviewImageSource = image;
                        _mediaPreviewImageSourceUri = MediaPreviewUri;
                        return _mediaPreviewImageSource;
                    }
                    catch
                    {
                    }
                }
                EnsureMediaPreviewImageSource();
                return _mediaPreviewImageSource;
            }
        }

        public Visibility MediaPreviewVisibility
        {
            get
            {
                if (!(MediaKind == "video" || MediaKind == "roundvideo")) return Visibility.Collapsed;
                return MediaPreviewImageSource == null ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        public int AlbumColumnSpan
        {
            get { return 1; }
        }

        public int AlbumRowSpan
        {
            get { return AlbumColumnSpan; }
        }

        public bool AlbumWideTile
        {
            get { return _albumWideTile; }
            set
            {
                if (_albumWideTile == value) return;
                _albumWideTile = value;
                NotifyAlbumLayoutChanged();
            }
        }

        public double AlbumTileWidth
        {
            get
            {
                var available = OwnerMessage == null ? CalculateFallbackMediaWidth() : OwnerMessage.MediaPlaceholderWidth;
                if (MediaKind == "document")
                    return available;
                if (AlbumWideTile)
                    return available;
                return Clamp(Math.Floor((available - 3) / 2), 72, 150);
            }
        }

        public double AlbumTileHeight
        {
            get
            {
                if (MediaKind == "document")
                    return 56;
                if (AlbumWideTile)
                    return Clamp(Math.Round(AlbumTileWidth * 0.58), 108, 176);
                return AlbumTileWidth;
            }
        }

        public double AlbumDownloadTextWidth
        {
            get { return Math.Max(54, AlbumTileWidth - 18); }
        }

        public Thickness AlbumTileMargin
        {
            get { return AlbumWideTile || MediaKind == "document" ? new Thickness(0, 0, 0, 3) : new Thickness(0, 0, 3, 3); }
        }

        public Visibility GroupedFileVisibility
        {
            get { return MediaKind == "document" ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility GroupedFileIdleVisibility
        {
            get
            {
                return MediaKind == "document" && !IsMediaDownloading && !IsFileDownloadOperationActive
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        public Visibility GroupedFileDownloadProgressVisibility
        {
            get
            {
                return MediaKind == "document" && (IsMediaDownloading || IsFileDownloadOperationActive)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        public string GroupedFileDisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(MediaFileName)) return MediaFileName;
                if (!string.IsNullOrEmpty(MediaTitle)) return MediaTitle;
                return "file";
            }
        }

        public string GroupedFileSubtitle
        {
            get
            {
                var size = FormatGroupedFileSize(MediaSize);
                var extension = string.Empty;
                try
                {
                    extension = System.IO.Path.GetExtension(GroupedFileDisplayName);
                    if (!string.IsNullOrEmpty(extension))
                        extension = extension.TrimStart('.').ToUpperInvariant();
                }
                catch
                {
                }

                if (string.IsNullOrEmpty(extension)) return size;
                if (string.IsNullOrEmpty(size)) return extension;
                return size + " " + extension;
            }
        }

        public double GroupedFileDownloadProgressValue
        {
            get
            {
                var total = _displayMediaDownloadTotalBytes > 0 ? _displayMediaDownloadTotalBytes : (MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize);
                if (total <= 0) return 0;
                var value = (double)_displayMediaDownloadBytes * 100.0 / (double)total;
                if (value < 0) return 0;
                if (value <= 0) return 0;
                if (value > 100) return 100;
                return value;
            }
        }

        public bool GroupedFileDownloadIndeterminate
        {
            get { return (IsMediaDownloading || IsFileDownloadOperationActive) && MediaDownloadTotalBytes <= 0 && MediaDownloadBytes <= 0; }
        }

        public string GroupedFileDownloadProgressText
        {
            get
            {
                var total = _displayMediaDownloadTotalBytes > 0 ? _displayMediaDownloadTotalBytes : (MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize);
                if (total > 0)
                    return (_displayMediaDownloadBytes > 0 ? FormatGroupedFileSize(_displayMediaDownloadBytes) : "0 B") + " / " + FormatGroupedFileSize(total);
                return _displayMediaDownloadBytes > 0 ? FormatGroupedFileSize(_displayMediaDownloadBytes) : "Downloading...";
            }
        }

        private void UpdateDisplayedDownloadProgress(bool force)
        {
            var total = MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize;
            var active = IsMediaDownloading || IsFileDownloadOperationActive;
            if (force || !active || total <= 0 || MediaDownloadBytes <= _displayMediaDownloadBytes)
            {
                _displayMediaDownloadBytes = active ? MediaDownloadBytes : 0;
                _displayMediaDownloadTotalBytes = active ? total : 0;
                StopDownloadProgressTimer();
                NotifyDownloadProgressValuesChanged();
                return;
            }

            _displayMediaDownloadTotalBytes = total;
            StartDownloadProgressTimer();
        }

        private void StartDownloadProgressTimer()
        {
            if (_downloadProgressTimer == null)
            {
                _downloadProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                _downloadProgressTimer.Tick += DownloadProgressTimer_Tick;
            }
            if (!_downloadProgressTimer.IsEnabled) _downloadProgressTimer.Start();
        }

        private void StopDownloadProgressTimer()
        {
            if (_downloadProgressTimer != null && _downloadProgressTimer.IsEnabled)
                _downloadProgressTimer.Stop();
        }

        private void DownloadProgressTimer_Tick(object sender, object e)
        {
            var target = MediaDownloadBytes;
            var total = MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize;
            var active = IsMediaDownloading || IsFileDownloadOperationActive;
            if (!active || total <= 0 || target <= _displayMediaDownloadBytes)
            {
                _displayMediaDownloadBytes = active ? target : 0;
                _displayMediaDownloadTotalBytes = active ? total : 0;
                StopDownloadProgressTimer();
                NotifyDownloadProgressValuesChanged();
                return;
            }

            var remaining = target - _displayMediaDownloadBytes;
            var step = Math.Max(remaining / 8, Math.Max(total / 160, 32 * 1024));
            if (step <= 0) step = 1;
            _displayMediaDownloadBytes = Math.Min(target, _displayMediaDownloadBytes + step);
            _displayMediaDownloadTotalBytes = total;
            NotifyDownloadProgressValuesChanged();

            if (_displayMediaDownloadBytes >= target)
                StopDownloadProgressTimer();
        }

        private void NotifyDownloadProgressValuesChanged()
        {
            OnPropertyChanged("DownloadProgressValue");
            OnPropertyChanged("DownloadProgressText");
            OnPropertyChanged("GroupedFileDownloadProgressValue");
            OnPropertyChanged("GroupedFileDownloadProgressText");
        }

        private static string FormatGroupedFileSize(long bytes)
        {
            if (bytes <= 0) return string.Empty;
            if (bytes >= 1024L * 1024L * 1024L)
                return ((double)bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.##") + " GB";
            if (bytes >= 1024L * 1024L)
                return ((double)bytes / (1024.0 * 1024.0)).ToString("0.##") + " MB";
            if (bytes >= 1024L)
                return ((double)bytes / 1024.0).ToString("0.#") + " KB";
            return bytes.ToString() + " B";
        }

        public Visibility AlbumFallbackVisibility
        {
            get
            {
                if (MediaKind == "document") return Visibility.Collapsed;
                if (PhotoVisibility == Visibility.Visible) return Visibility.Collapsed;
                if (VideoVisibility == Visibility.Visible) return Visibility.Collapsed;
                if (GifVisibility == Visibility.Visible) return Visibility.Collapsed;
                if (RoundVideoVisibility == Visibility.Visible) return Visibility.Collapsed;
                if (AudioVisibility == Visibility.Visible) return Visibility.Collapsed;
                if (StickerVisibility == Visibility.Visible) return Visibility.Collapsed;
                if (StaticEmojiVisibility == Visibility.Visible) return Visibility.Collapsed;
                if (DownloadButtonVisibility == Visibility.Visible) return Visibility.Collapsed;
                return Visibility.Visible;
            }
        }

        public string AlbumIconGlyph
        {
            get
            {
                if (MediaKind == "video" || MediaKind == "roundvideo") return "\uE768";
                if (MediaKind == "gif") return "\uE8B9";
                if (MediaKind == "sticker") return "\uE7F4";
                if (MediaKind == "audio" || MediaKind == "voice") return "\uE189";
                return "\uE8B7";
            }
        }

        public Visibility MediaErrorVisibility
        {
            get { return string.IsNullOrEmpty(MediaErrorText) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility MediaTitleVisibility
        {
            get
            {
                if (IsWebPageMedia(MediaKind)) return Visibility.Collapsed;
                if (MediaKind == "photo" || MediaKind == "video" || MediaKind == "gif" || MediaKind == "sticker" || MediaKind == "emoji" || MediaKind == "roundvideo" || MediaKind == "audio" || MediaKind == "voice") return Visibility.Collapsed;
                return string.IsNullOrEmpty(MediaTitle) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private static bool IsWebPageMedia(string kind)
        {
            return string.Equals(kind, "webpage", StringComparison.OrdinalIgnoreCase);
        }

        public void NotifyMediaStateChanged()
        {
            OnPropertyChanged("MediaFileUri");
            OnPropertyChanged("MediaFallbackUri");
            OnPropertyChanged("MediaPreviewUri");
            OnPropertyChanged("MediaUri");
            OnPropertyChanged("FileVisibility");
            OnPropertyChanged("FileIdleVisibility");
            OnPropertyChanged("FileDownloadProgressVisibility");
            OnPropertyChanged("FileDisplayName");
            OnPropertyChanged("FileSubtitle");
            OnPropertyChanged("FileDownloadProgressValue");
            OnPropertyChanged("FileDownloadProgressText");
            OnPropertyChanged("PhotoVisibility");
            OnPropertyChanged("PhotoImageSource");
            OnPropertyChanged("VideoVisibility");
            OnPropertyChanged("GifVisibility");
            OnPropertyChanged("RoundVideoVisibility");
            OnPropertyChanged("VideoPlaceholderVisibility");
            OnPropertyChanged("AudioPlaceholderVisibility");
            OnPropertyChanged("AudioVisibility");
            OnPropertyChanged("VoiceVisibility");
            OnPropertyChanged("MusicVisibility");
            OnPropertyChanged("StickerVisibility");
            OnPropertyChanged("LocationIconVisibility");
            OnPropertyChanged("LocationIconSource");
            OnPropertyChanged("StaticEmojiVisibility");
            OnPropertyChanged("PlayerVisibility");
            OnPropertyChanged("DownloadButtonVisibility");
            OnPropertyChanged("DownloadButtonEnabled");
            OnPropertyChanged("AlbumDownloadButtonVisibility");
            OnPropertyChanged("DownloadingVisibility");
            OnPropertyChanged("DownloadIconVisibility");
            OnPropertyChanged("DownloadProgressBarVisibility");
            OnPropertyChanged("DownloadProgressValue");
            OnPropertyChanged("DownloadProgressText");
            OnPropertyChanged("DownloadProgressIndeterminate");
            OnPropertyChanged("MediaPreviewImageSource");
            OnPropertyChanged("MediaPreviewVisibility");
            OnPropertyChanged("MediaTitleVisibility");
            OnPropertyChanged("MediaErrorVisibility");
            OnPropertyChanged("AlbumFallbackVisibility");
            OnPropertyChanged("AlbumIconGlyph");
            OnPropertyChanged("GroupedFileVisibility");
            OnPropertyChanged("GroupedFileIdleVisibility");
            OnPropertyChanged("GroupedFileDownloadProgressVisibility");
            OnPropertyChanged("GroupedFileDisplayName");
            OnPropertyChanged("GroupedFileSubtitle");
            OnPropertyChanged("GroupedFileDownloadProgressValue");
            OnPropertyChanged("GroupedFileDownloadProgressText");
            OnPropertyChanged("GroupedFileDownloadIndeterminate");
            if (OwnerMessage != null) OwnerMessage.NotifyMediaCollectionStateChanged();
        }

        public void SetMediaPreviewAspectRatio(double aspectRatio)
        {
            if (aspectRatio <= 0.1) return;
            if (Math.Abs(_mediaPreviewAspectRatio - aspectRatio) < 0.01) return;
            _mediaPreviewAspectRatio = aspectRatio;
            if (OwnerMessage != null) OwnerMessage.NotifyMediaCollectionStateChanged();
        }

        public void NotifyAlbumLayoutChanged()
        {
            OnPropertyChanged("AlbumColumnSpan");
            OnPropertyChanged("AlbumRowSpan");
            OnPropertyChanged("AlbumWideTile");
            OnPropertyChanged("AlbumTileWidth");
            OnPropertyChanged("AlbumTileHeight");
            OnPropertyChanged("AlbumTileMargin");
            OnPropertyChanged("AlbumDownloadTextWidth");
        }

        private static double CalculateFallbackMediaWidth()
        {
            return ChatMessageLayoutMetrics.CalculateMediaWidth(false, true);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private void EnsureMediaPreviewImageSource()
        {
            if (_mediaPreviewImageSource != null) return;
            // Assign the field rather than going through SetMediaPreviewAspectRatio: this runs
            // inside a binding getter, so raising PropertyChanged here would re-enter layout.
            int thumbWidth;
            int thumbHeight;
            if (_mediaPreviewAspectRatio <= 0.1 &&
                ChatMediaPreviewHelper.TryReadImageSize(MediaThumbBytes, out thumbWidth, out thumbHeight))
                _mediaPreviewAspectRatio = (double)thumbWidth / thumbHeight;

            _mediaPreviewImageSource = ChatMediaPreviewHelper.CreateImageSource(MediaThumbBytes, 220, SetMediaPreviewAspectRatio);
            _mediaPreviewImageSourceUri = null;
        }

        private static bool IsPlayableVideoUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            var value = uri.Trim();
            if (EndsWithAny(value, ".jpg", ".jpeg", ".png", ".bmp", ".webp")) return false;
            return true;
        }

        private static bool EndsWithAny(string value, params string[] suffixes)
        {
            if (string.IsNullOrEmpty(value) || suffixes == null) return false;
            for (var i = 0; i < suffixes.Length; i++)
            {
                if (value.EndsWith(suffixes[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }


    public sealed class BotCommandViewModel
    {
        public string Command { get; set; }
        public string Description { get; set; }
        public string DisplayText
        {
            get
            {
                var command = string.IsNullOrWhiteSpace(Command) ? string.Empty : (Command[0] == '/' ? Command : "/" + Command);
                return string.IsNullOrWhiteSpace(Description) ? command : command + " — " + Description;
            }
        }
    }

    public sealed class BotCallbackAnswerViewModel
    {
        public string Text { get; set; }
        public bool ShowAlert { get; set; }
        public string Url { get; set; }
    }

    public sealed class BotKeyboardButtonViewModel
    {
        public int MessageId { get; set; }
        public string Text { get; set; }
        public string Type { get; set; }
        public string Data { get; set; }
        public string Url { get; set; }
        public string Query { get; set; }
        public long UserId { get; set; }
    }

    public sealed class BotKeyboardRowViewModel
    {
        public BotKeyboardRowViewModel()
        {
            Buttons = new ObservableCollection<BotKeyboardButtonViewModel>();
        }

        public ObservableCollection<BotKeyboardButtonViewModel> Buttons { get; private set; }
    }

    public sealed class ChatMessageViewModel : INotifyPropertyChanged
    {
        private const string StateSendingIconUri = "ms-appx:///Assets/Messages/message.state.sending-WXGA.png";
        private const string StateSentIconUri = "ms-appx:///Assets/Messages/message.state.sent-WXGA.png";
        private const string StateReadIconUri = "ms-appx:///Assets/Messages/message.state.read-WXGA.png";
        private static ImageSource _stateSendingIconSource;
        private static ImageSource _stateSentIconSource;
        private static ImageSource _stateReadIconSource;
        private static ImageSource _locationIconSource;

        private string _mediaFileUri;
        private string _mediaFallbackUri;
        private string _mediaFullUri;
        private string _mediaPreviewUri;
        private string _mediaTitle;
        private string _mediaPerformer;
        private string _mediaErrorText;
        private bool _isMediaDownloading;
        private bool _isFileDownloadOperationActive;
        private bool _hasPlaybackError;
        private bool _isFirstInSenderGroup = true;
        private bool _isSending;
        private bool _isRead;
        private long _mediaDownloadBytes;
        private long _mediaDownloadTotalBytes;
        private long _displayMediaDownloadBytes;
        private long _displayMediaDownloadTotalBytes;
        private DispatcherTimer _downloadProgressTimer;
        private ImageSource _mediaPreviewImageSource;
        private string _mediaPreviewImageSourceUri;
        private ImageSource _photoImageSource;
        private string _photoImageSourceUri;
        private double _mediaPreviewAspectRatio;
        private string _senderAvatarUri;
        private ImageSource _senderAvatarImageSource;
        private string _senderAvatarImageSourceUri;
        private string _forwardedAvatarUri;
        private string _text;
        private bool _isPinned;

        public event PropertyChangedEventHandler PropertyChanged;

        public ChatMessageViewModel()
        {
            MediaItems = new ObservableCollection<ChatMediaItemViewModel>();
            MediaItemRows = new ObservableCollection<ChatMediaAlbumRowViewModel>();
            Reactions = new ObservableCollection<MessageReactionViewModel>();
            CommentAvatars = new ObservableCollection<CommentAvatarViewModel>();
            ReadByUsers = new ObservableCollection<CommentAvatarViewModel>();
            ReadByPreviewUsers = new ObservableCollection<CommentAvatarViewModel>();
            TextEntities = new ObservableCollection<MessageTextEntityViewModel>();
            InlineKeyboardRows = new ObservableCollection<BotKeyboardRowViewModel>();
            ReplyKeyboardRows = new ObservableCollection<BotKeyboardRowViewModel>();
        }

        public ObservableCollection<ChatMediaItemViewModel> MediaItems { get; private set; }
        public ObservableCollection<ChatMediaAlbumRowViewModel> MediaItemRows { get; private set; }
        public ObservableCollection<MessageReactionViewModel> Reactions { get; private set; }
        public ObservableCollection<CommentAvatarViewModel> CommentAvatars { get; private set; }
        public ObservableCollection<CommentAvatarViewModel> ReadByUsers { get; private set; }
        public ObservableCollection<CommentAvatarViewModel> ReadByPreviewUsers { get; private set; }
        public ObservableCollection<MessageTextEntityViewModel> TextEntities { get; private set; }
        public ObservableCollection<BotKeyboardRowViewModel> InlineKeyboardRows { get; private set; }
        public ObservableCollection<BotKeyboardRowViewModel> ReplyKeyboardRows { get; private set; }
        public bool ReplyKeyboardOneTime { get; set; }
        public bool ReplyKeyboardPersistent { get; set; }
        public bool RemovesReplyKeyboard { get; set; }
        public string ReplyKeyboardPlaceholder { get; set; }
        public Visibility InlineKeyboardVisibility { get { return InlineKeyboardRows != null && InlineKeyboardRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed; } }
        public bool HasReplyKeyboard { get { return ReplyKeyboardRows != null && ReplyKeyboardRows.Count > 0; } }
        public long GroupedId { get; set; }
        public bool IsServiceMessage { get; set; }
        public string ServiceActionText { get; set; }
        public string DateText { get { return null; } }
        public string UnreadText { get { return null; } }

        public int Id { get; set; }
        public long SortId { get; set; }
        public int Date { get; set; }
        public int EditDate { get; set; }
        public bool IsOutgoing { get; set; }
        public bool IsGroupChat { get; set; }
        public bool IsChannelPost { get; set; }
        public string PostAuthor { get; set; }

        public void SetEditDate(int editDate)
        {
            if (EditDate == editDate) return;
            EditDate = editDate;
            OnPropertyChanged("EditDate");
            OnPropertyChanged("IsEditedMessage");
            OnPropertyChanged("FooterText");
            OnPropertyChanged("BubbleContentMargin");
        }

        public void SetPostAuthor(string postAuthor)
        {
            postAuthor = postAuthor ?? string.Empty;
            if (PostAuthor == postAuthor) return;
            PostAuthor = postAuthor;
            OnPropertyChanged("PostAuthor");
            OnPropertyChanged("FooterText");
            OnPropertyChanged("BubbleContentMargin");
        }

        public bool IsEditedMessage
        {
            get { return Date > 0 && EditDate > Date + 1; }
        }

        public bool IsFirstInSenderGroup
        {
            get { return _isFirstInSenderGroup; }
            set
            {
                if (_isFirstInSenderGroup == value) return;
                _isFirstInSenderGroup = value;
                OnPropertyChanged("IsFirstInSenderGroup");
                OnPropertyChanged("SenderVisibility");
                OnPropertyChanged("AvatarVisibility");
                OnPropertyChanged("IncomingTailVisibility");
                OnPropertyChanged("OutgoingTailVisibility");
                OnPropertyChanged("IncomingTailColumnWidth");
                OnPropertyChanged("OutgoingTailColumnWidth");
                OnPropertyChanged("BubbleMargin");
                OnPropertyChanged("BubbleContentAlignment");
                OnPropertyChanged("SenderInitialsVisibility");
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
                OnPropertyChanged("StateIconSource");
                OnPropertyChanged("OutgoingStateVisibility");
            }
        }

        public bool IsRead
        {
            get { return _isRead; }
            set
            {
                if (_isRead == value) return;
                _isRead = value;
                OnPropertyChanged("IsRead");
                OnPropertyChanged("StateIconSource");
            }
        }

        public void SetMessageState(bool isSending, bool isRead)
        {
            IsSending = isSending;
            IsRead = isRead;
        }

        public string Text
        {
            get { return _text; }
            set
            {
                if (_text == value) return;
                _text = value;
                OnPropertyChanged("Text");
                OnPropertyChanged("VisibleText");
                OnPropertyChanged("UnsupportedMessageDisplayText");
                OnPropertyChanged("LooksLikeUnsupportedPollCandidate");
                OnPropertyChanged("TextVisibility");
                OnPropertyChanged("CanCopyText");
                OnPropertyChanged("FileDisplayName");
                OnPropertyChanged("IsUnsupportedMessagePlaceholder");
                OnPropertyChanged("MessageRootVisibility");
            }
        }

        public bool HasTextEntities
        {
            get { return TextEntities != null && TextEntities.Count > 0; }
        }

        public void SetTextEntities(IEnumerable<MessageTextEntityViewModel> entities)
        {
            TextEntities.Clear();
            if (entities != null)
            {
                foreach (var entity in entities)
                {
                    if (entity == null || entity.Length <= 0) continue;
                    TextEntities.Add(entity);
                }
            }
            OnPropertyChanged("TextEntities");
            OnPropertyChanged("HasTextEntities");
        }

        public string VisibleText
        {
            get
            {
                if (IsUnsupportedMessagePlaceholder) return UnsupportedMessageDisplayText;
                if (string.Equals(MediaKind, "poll", StringComparison.OrdinalIgnoreCase) && PollVisibility != Visibility.Visible) return "Unsupported poll";
                return IsMediaFallbackText(Text) ? string.Empty : Text;
            }
        }

        public string UnsupportedMessageDisplayText
        {
            get
            {
                if (LooksLikeUnsupportedPollCandidate) return "Unsupported poll";
                return "Unsupported message";
            }
        }

        public bool LooksLikeUnsupportedPollCandidate
        {
            get
            {
                if (string.Equals(MediaKind, "poll", StringComparison.OrdinalIgnoreCase)) return true;
                if (!IsUnsupportedMessagePlaceholder) return false;
                if (string.Equals(MediaTitle, "Poll", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(MediaFileName, "Poll", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(StructuredMediaTitle, "Poll", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }

        public bool IsUnsupportedMessagePlaceholder
        {
            get
            {
                if (string.Equals(MediaKind, "unsupported", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(MediaTitle, "Unsupported message", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(MediaFileName, "Unsupported message", StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.Equals(Text, "Unsupported message", StringComparison.OrdinalIgnoreCase)) return false;
                return HasMedia || !string.IsNullOrEmpty(MediaKind) || !string.IsNullOrEmpty(MediaTitle) || !string.IsNullOrEmpty(MediaFileName);
            }
        }

        public Visibility MessageRootVisibility
        {
            get { return Visibility.Visible; }
        }

        public string SenderName { get; set; }
        public string SenderInitials { get; set; }
        public string SenderAvatarUri
        {
            get { return _senderAvatarUri; }
            set
            {
                if (_senderAvatarUri == value) return;
                _senderAvatarUri = value;
                OnPropertyChanged("SenderAvatarUri");
                OnPropertyChanged("SenderAvatarImageSource");
                OnPropertyChanged("SenderInitialsVisibility");
            }
        }

        public ImageSource SenderAvatarImageSource
        {
            get
            {
                var uri = SenderAvatarUri;
                if (string.IsNullOrEmpty(uri)) return null;
                // Cached: this getter runs for every binding pass of every realized row, and a
                // fresh BitmapImage each time means a fresh decode of the same avatar.
                if (_senderAvatarImageSource != null &&
                    string.Equals(_senderAvatarImageSourceUri, uri, StringComparison.OrdinalIgnoreCase))
                    return _senderAvatarImageSource;
                try
                {
                    var image = new BitmapImage();
                    image.DecodePixelWidth = 64;
                    image.UriSource = new Uri(uri);
                    _senderAvatarImageSource = image;
                    _senderAvatarImageSourceUri = uri;
                    return image;
                }
                catch { return null; }
            }
        }
        public string SenderPeerKey { get; set; }
        public string SenderPeerType { get; set; }
        public long SenderPeerId { get; set; }
        public long SenderAccessHash { get; set; }
        public bool SenderIsGroup { get; set; }
        public bool SenderIsChannel { get; set; }
        public bool SenderIsBroadcast { get; set; }
        public long SenderAvatarPhotoId { get; set; }
        public int SenderAvatarDcId { get; set; }
        public byte[] SenderAvatarStrippedThumb { get; set; }

        public string ForwardedFrom { get; set; }
        public string ForwardedInitials { get; set; }
        public string ForwardedAvatarUri
        {
            get { return _forwardedAvatarUri; }
            set
            {
                if (_forwardedAvatarUri == value) return;
                _forwardedAvatarUri = value;
                OnPropertyChanged("ForwardedAvatarUri");
            }
        }
        public string ForwardedPeerKey { get; set; }
        public string ForwardedPeerType { get; set; }
        public long ForwardedPeerId { get; set; }
        public long ForwardedAccessHash { get; set; }
        public long ForwardedAvatarPhotoId { get; set; }
        public int ForwardedAvatarDcId { get; set; }
        public byte[] ForwardedAvatarStrippedThumb { get; set; }
        public int ReplyToMessageId { get; set; }
        public string ReplyToSenderName { get; set; }
        public string ReplyToText { get; set; }
        public bool IsPinned
        {
            get { return _isPinned; }
            set
            {
                if (_isPinned == value) return;
                _isPinned = value;
                OnPropertyChanged("IsPinned");
            }
        }
        public bool CanReply { get; set; }
        public bool CanPin { get; set; }
        public bool CanForward { get; set; }
        public bool CanDelete { get; set; }
        public bool CanReact { get; set; }
        public bool CanGetViewers { get; set; }
        public bool HasCanGetViewersFlag { get; set; }
        public int ReadByUserCount { get; private set; }
        public int CommentsCount { get; set; }
        public long CommentsChannelId { get; set; }
        public int CommentsMaxId { get; set; }
        public int CommentsReadMaxId { get; set; }
        public string CommentsDiscussionTitle { get; set; }
        public long CommentsDiscussionAccessHash { get; set; }
        public bool CommentsDiscussionCanSend { get; set; }
        public bool CanOpenComments { get; set; }
        public bool CanCopyText
        {
            get { return HasVisibleText(VisibleText); }
        }

        public Visibility CommentsPreviewVisibility
        {
            get { return CanOpenComments ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility ReadByPreviewVisibility
        {
            get { return ReadByUsers != null && ReadByUsers.Count > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string ReadByPreviewText
        {
            get
            {
                var count = ReadByUserCount > 0 ? ReadByUserCount : (ReadByUsers == null ? 0 : ReadByUsers.Count);
                if (count <= 0) return string.Empty;
                return count == 1 ? "Read by 1" : "Read by " + count.ToString();
            }
        }

        public string CommentsCountText
        {
            get
            {
                if (CommentsCount <= 0) return "Comments";
                return CommentsCount.ToString() + " " + PluralizeComments(CommentsCount);
            }
        }

        private static string PluralizeComments(int count)
        {
            return Math.Abs(count) == 1 ? "comment" : "comments";
        }

        public Visibility CommentAvatarsVisibility
        {
            get { return CommentAvatars != null && CommentAvatars.Count > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility CommentsGlyphVisibility
        {
            get { return CommentAvatars == null || CommentAvatars.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility ReactionsVisibility
        {
            get { return Reactions != null && Reactions.Count > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public void SetReactions(IEnumerable<MessageReactionViewModel> reactions)
        {
            if (Reactions == null) Reactions = new ObservableCollection<MessageReactionViewModel>();
            var seen = new List<string>();
            var targetIndex = 0;
            if (reactions != null)
            {
                foreach (var reaction in reactions)
                {
                    if (reaction == null || reaction.Count <= 0 || string.IsNullOrEmpty(reaction.ReactionKey)) continue;

                    var key = reaction.ReactionKey;
                    seen.Add(key);

                    var existing = FindReactionByKey(key);
                    if (existing == null)
                    {
                        reaction.OwnerMessage = this;
                        Reactions.Add(reaction);
                        existing = reaction;
                    }
                    else
                    {
                        existing.Emoticon = reaction.Emoticon;
                        existing.CustomEmojiDocumentId = reaction.CustomEmojiDocumentId;
                        existing.CustomEmojiUri = reaction.CustomEmojiUri;
                        existing.Count = reaction.Count;
                        existing.IsChosen = reaction.IsChosen;
                        existing.NotifyStateChanged();
                    }

                    var currentIndex = Reactions.IndexOf(existing);
                    if (currentIndex >= 0 && currentIndex != targetIndex)
                        Reactions.Move(currentIndex, targetIndex);
                    targetIndex++;
                }
            }

            for (var i = Reactions.Count - 1; i >= 0; i--)
            {
                var reaction = Reactions[i];
                if (reaction == null || !seen.Contains(reaction.ReactionKey))
                    Reactions.RemoveAt(i);
            }

            OnPropertyChanged("Reactions");
            OnPropertyChanged("ReactionsVisibility");
        }

        public void SetCommentAvatars(IEnumerable<CommentAvatarViewModel> avatars)
        {
            if (CommentAvatars == null) CommentAvatars = new ObservableCollection<CommentAvatarViewModel>();
            CommentAvatars.Clear();
            if (avatars != null)
            {
                foreach (var avatar in avatars)
                {
                    if (avatar != null)
                        CommentAvatars.Add(avatar);
                    if (CommentAvatars.Count >= 3)
                        break;
                }
            }
            OnPropertyChanged("CommentAvatars");
            OnPropertyChanged("CommentAvatarsVisibility");
            OnPropertyChanged("CommentsGlyphVisibility");
            OnPropertyChanged("BubbleStretchContentWidth");
            OnPropertyChanged("BubbleContentAlignment");
        }

        public void SetReadByUsers(IEnumerable<CommentAvatarViewModel> users)
        {
            if (ReadByUsers == null) ReadByUsers = new ObservableCollection<CommentAvatarViewModel>();
            if (ReadByPreviewUsers == null) ReadByPreviewUsers = new ObservableCollection<CommentAvatarViewModel>();

            ReadByUsers.Clear();
            ReadByPreviewUsers.Clear();

            if (users != null)
            {
                foreach (var user in users)
                {
                    if (user == null) continue;
                    ReadByUsers.Add(user);
                    if (ReadByPreviewUsers.Count < 3)
                        ReadByPreviewUsers.Add(user);
                }
            }

            ReadByUserCount = ReadByUsers.Count;
            OnPropertyChanged("ReadByUsers");
            OnPropertyChanged("ReadByPreviewUsers");
            OnPropertyChanged("ReadByUserCount");
            OnPropertyChanged("ReadByPreviewVisibility");
            OnPropertyChanged("ReadByPreviewText");
        }

        public MessageReactionViewModel FindReaction(string emoticon)
        {
            return FindReaction(emoticon, 0);
        }

        public MessageReactionViewModel FindReaction(string emoticon, long customEmojiDocumentId)
        {
            var key = customEmojiDocumentId != 0 ? "custom:" + customEmojiDocumentId.ToString() : (emoticon ?? string.Empty);
            return FindReactionByKey(key);
        }

        private MessageReactionViewModel FindReactionByKey(string key)
        {
            if (Reactions == null || string.IsNullOrEmpty(key)) return null;
            for (var i = 0; i < Reactions.Count; i++)
            {
                var reaction = Reactions[i];
                if (reaction != null && reaction.ReactionKey == key) return reaction;
            }
            return null;
        }

        public void ApplyLocalReaction(string emoticon)
        {
            ApplyLocalReaction(emoticon, 0);
        }

        public void ApplyLocalReaction(string emoticon, long customEmojiDocumentId)
        {
            if (string.IsNullOrEmpty(emoticon) && customEmojiDocumentId == 0) return;
            if (Reactions == null) Reactions = new ObservableCollection<MessageReactionViewModel>();

            var target = FindReaction(emoticon, customEmojiDocumentId);
            var targetWasChosen = target != null && target.IsChosen;
            for (var i = Reactions.Count - 1; i >= 0; i--)
            {
                var reaction = Reactions[i];
                if (reaction == null) continue;
                if (!reaction.IsChosen) continue;
                reaction.IsChosen = false;
                reaction.Count = Math.Max(0, reaction.Count - 1);
                if (reaction.Count <= 0) Reactions.RemoveAt(i);
            }

            if (!targetWasChosen)
            {
                target = FindReaction(emoticon, customEmojiDocumentId);
                if (target == null)
                {
                    target = new MessageReactionViewModel { Emoticon = emoticon, CustomEmojiDocumentId = customEmojiDocumentId, OwnerMessage = this };
                    Reactions.Add(target);
                }
                target.Count = target.Count + 1;
                target.IsChosen = true;
            }

            OnPropertyChanged("Reactions");
            OnPropertyChanged("ReactionsVisibility");
        }

        public string MediaKind { get; set; }

        public string MediaFileUri
        {
            get { return _mediaFileUri; }
            set
            {
                if (_mediaFileUri == value) return;
                _mediaFileUri = value;
                _photoImageSource = null;
                _photoImageSourceUri = null;
                HasPlaybackError = false;
                MediaErrorText = string.Empty;
                NotifyMediaStateChanged();
            }
        }

        public string MediaFallbackUri
        {
            get { return _mediaFallbackUri; }
            set
            {
                if (_mediaFallbackUri == value) return;
                _mediaFallbackUri = value;
                NotifyMediaStateChanged();
            }
        }

        public string MediaFullUri
        {
            get { return _mediaFullUri; }
            set
            {
                if (_mediaFullUri == value) return;
                _mediaFullUri = value;
                OnPropertyChanged("MediaFullUri");
            }
        }

        public string MediaPreviewUri
        {
            get { return _mediaPreviewUri; }
            set
            {
                if (_mediaPreviewUri == value) return;
                _mediaPreviewUri = value;
                _mediaPreviewImageSource = null;
                _mediaPreviewImageSourceUri = null;
                if (string.IsNullOrEmpty(value)) _mediaPreviewAspectRatio = 0;
                OnPropertyChanged("MediaPreviewUri");
                OnPropertyChanged("MediaPreviewImageSource");
                OnPropertyChanged("MediaPreviewVisibility");
                OnPropertyChanged("MediaRenderWidth");
            OnPropertyChanged("MediaDownloadPlaceholderHeight");
            }
        }

        public Uri MediaUri
        {
            get
            {
                if (string.IsNullOrEmpty(MediaFileUri)) return null;
                if (MediaKind != "gif") return null;
                try { return new Uri(MediaFileUri); }
                catch { return null; }
            }
        }

        public ImageSource PhotoImageSource
        {
            get
            {
                if (MediaKind != "photo" || string.IsNullOrEmpty(MediaFileUri)) return null;
                try
                {
                    if (_photoImageSource != null &&
                        string.Equals(_photoImageSourceUri, MediaFileUri, StringComparison.OrdinalIgnoreCase))
                        return _photoImageSource;

                    var image = new BitmapImage();
                    image.DecodePixelWidth = 480;
                    image.UriSource = new Uri(MediaFileUri);
                    _photoImageSource = image;
                    _photoImageSourceUri = MediaFileUri;
                    return _photoImageSource;
                }
                catch
                {
                    return null;
                }
            }
        }

        public string MediaTitle
        {
            get { return _mediaTitle; }
            set
            {
                if (_mediaTitle == value) return;
                _mediaTitle = value;
                OnPropertyChanged("MediaTitle");
                OnPropertyChanged("MediaTitleVisibility");
                OnPropertyChanged("DownloadButtonText");
            }
        }

        public string MediaErrorText
        {
            get { return _mediaErrorText; }
            set
            {
                if (_mediaErrorText == value) return;
                _mediaErrorText = value;
                OnPropertyChanged("MediaErrorText");
                OnPropertyChanged("MediaErrorVisibility");
            }
        }

        public bool HasPlaybackError
        {
            get { return _hasPlaybackError; }
            set
            {
                if (_hasPlaybackError == value) return;
                _hasPlaybackError = value;
                OnPropertyChanged("HasPlaybackError");
                OnPropertyChanged("MediaErrorVisibility");
                OnPropertyChanged("VideoVisibility");
                OnPropertyChanged("GifVisibility");
                OnPropertyChanged("RoundVideoVisibility");
                OnPropertyChanged("AudioVisibility");
                OnPropertyChanged("VoiceVisibility");
                OnPropertyChanged("MusicVisibility");
                OnPropertyChanged("StickerVisibility");
                OnPropertyChanged("PlayerVisibility");
            }
        }

        public string MediaFileName { get; set; }
        public string MediaPerformer
        {
            get { return _mediaPerformer; }
            set
            {
                if (_mediaPerformer == value) return;
                _mediaPerformer = value;
                OnPropertyChanged("MediaPerformer");
            }
        }
        public string MediaMimeType { get; set; }
        public bool HasMedia { get; set; }
        public long MediaId { get; set; }
        public long MediaFullId { get; set; }
        public long MediaAccessHash { get; set; }
        public int MediaDcId { get; set; }
        public byte[] MediaFileReference { get; set; }
        public long MediaPreviewId { get; set; }
        public string MediaThumbSize { get; set; }
        public string FullPhotoThumbSize { get; set; }
        public byte[] MediaThumbBytes { get; set; }
        public long MediaSize { get; set; }
        public bool MediaIsPhoto { get; set; }
        public int MediaDurationSeconds { get; set; }
        public string StructuredMediaTitle { get; set; }
        public string StructuredMediaSubtitle { get; set; }
        public ObservableCollection<string> StructuredMediaLines { get; set; }
        public ObservableCollection<StructuredMediaItemViewModel> StructuredMediaItems { get; set; }
        public bool StructuredMediaAllowsMultiple { get; set; }
        public bool StructuredMediaIsClosed { get; set; }
        public int StructuredMediaTotalVoters { get; set; }
        public bool PollIsPublic { get; set; }
        public bool PollIsQuiz { get; set; }
        public bool PollAllowsRevoting { get; set; }
        public bool PollCanAddOption { get; set; }
        public int PollClosePeriodSeconds { get; set; }
        public int PollCloseDate { get; set; }
        public string PollRecentVotersText { get; set; }
        public string PollSolutionText { get; set; }

        public Visibility MediaItemsVisibility
        {
            get { return MediaItems != null && MediaItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public bool HasPendingMedia
        {
            get
            {
                if (IsWebPageMedia(MediaKind)) return false;
                if (string.Equals(MediaKind, "emoji", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(MediaKind, "poll", StringComparison.OrdinalIgnoreCase) || string.Equals(MediaKind, "todo", StringComparison.OrdinalIgnoreCase)) return false;
                if (MediaItems == null || MediaItems.Count == 0) return HasMedia && string.IsNullOrEmpty(MediaFileUri);
                for (var i = 0; i < MediaItems.Count; i++)
                {
                    var item = MediaItems[i];
                    if (item != null && string.IsNullOrEmpty(item.MediaFileUri)) return true;
                }
                return false;
            }
        }

        public Visibility MediaItemsLoadedVisibility
        {
            get { return MediaItems != null && MediaItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility MediaDownloadPlaceholderVisibility
        {
            get
            {
                if (MediaItems != null && MediaItems.Count > 0) return Visibility.Collapsed;
                if (string.Equals(MediaKind, "roundvideo", StringComparison.OrdinalIgnoreCase)) return Visibility.Collapsed;
                if (IsSingleFileMessage) return Visibility.Collapsed;
                if (IsWebPageMedia(MediaKind)) return Visibility.Collapsed;
                if (string.Equals(MediaKind, "emoji", StringComparison.OrdinalIgnoreCase)) return Visibility.Collapsed;
                if (string.Equals(MediaKind, "poll", StringComparison.OrdinalIgnoreCase) || string.Equals(MediaKind, "todo", StringComparison.OrdinalIgnoreCase) || MediaKind == "audio" || MediaKind == "voice") return Visibility.Collapsed;
                return HasMedia && HasPendingMedia ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility RoundVideoDownloadPlaceholderVisibility
        {
            get
            {
                if (MediaItems != null && MediaItems.Count > 0) return Visibility.Collapsed;
                if (!string.Equals(MediaKind, "roundvideo", StringComparison.OrdinalIgnoreCase)) return Visibility.Collapsed;
                return HasMedia && HasPendingMedia ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility MediaDownloadPlaceholderIconVisibility
        {
            get { return IsMediaDownloading ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility MediaDownloadPlaceholderProgressVisibility
        {
            get
            {
                return Visibility.Collapsed;
            }
        }

        public Visibility VideoDownloadProgressBarVisibility
        {
            get
            {
                if (!IsMediaDownloading) return Visibility.Collapsed;
                if (!IsVideoMediaKind) return Visibility.Collapsed;
                return Visibility.Visible;
            }
        }

        public double VideoDownloadProgressValue
        {
            get
            {
                var total = _displayMediaDownloadTotalBytes > 0 ? _displayMediaDownloadTotalBytes : (MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize);
                if (total <= 0) return 0;
                var value = (double)_displayMediaDownloadBytes * 100.0 / (double)total;
                if (value < 0) return 0;
                if (value <= 0) return 0;
                if (value > 100) return 100;
                return value;
            }
        }

        public string VideoDownloadProgressText
        {
            get
            {
                return Math.Round(VideoDownloadProgressValue).ToString("0") + "%";
            }
        }

        public bool VideoDownloadProgressIndeterminate
        {
            get
            {
                var total = MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize;
                return IsMediaDownloading && total <= 0;
            }
        }

        public Visibility MediaDownloadProgressCircleVisibility
        {
            get
            {
                if (!IsMediaDownloading) return Visibility.Collapsed;
                return Visibility.Visible;
            }
        }

        public double MediaDownloadProgressValue
        {
            get
            {
                var total = _displayMediaDownloadTotalBytes > 0 ? _displayMediaDownloadTotalBytes : (MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize);
                if (total <= 0) return 0;
                var value = (double)_displayMediaDownloadBytes * 100.0 / (double)total;
                if (value < 0) return 0;
                if (value <= 0) return 0;
                if (value > 100) return 100;
                return value;
            }
        }

        public string MediaDownloadProgressText
        {
            get { return Math.Round(MediaDownloadProgressValue).ToString("0") + "%"; }
        }

        public bool MediaDownloadProgressIndeterminate
        {
            get
            {
                var total = MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize;
                return IsMediaDownloading && total <= 0;
            }
        }

        private bool IsVideoMediaKind
        {
            get { return MediaKind == "video" || MediaKind == "roundvideo"; }
        }

        public double MediaPlaceholderWidth
        {
            get { return CalculateAvailableMediaWidth(IsOutgoing, IsGroupChat); }
        }

        public double BubbleMaxWidth
        {
            get
            {
                if (HasMedia && !IsWebPageMedia(MediaKind)) return MediaPlaceholderWidth + 20;
                return CalculateAvailableTextWidth(IsOutgoing, IsGroupChat);
            }
        }

        public double BubbleStretchContentWidth
        {
            get
            {
                if (!ShouldStretchBubbleContent) return double.NaN;
                var margin = BubbleContentMargin;
                return Math.Max(0, BubbleMaxWidth - margin.Left - margin.Right);
            }
        }

        private bool ShouldStretchBubbleContent
        {
            get
            {
                if (IsStickerMessage || IsLocationMessage || string.Equals(MediaKind, "roundvideo", StringComparison.OrdinalIgnoreCase)) return false;
                if (HasMedia && !IsWebPageMedia(MediaKind)) return true;
                if (CanOpenComments) return true;
                if (Reactions != null && Reactions.Count > 0) return true;
                if (InlineKeyboardRows != null && InlineKeyboardRows.Count > 0) return true;
                return false;
            }
        }

        public double MediaPlaceholderHeight
        {
            get { return Clamp(Math.Round(MediaPlaceholderWidth * 0.49), 108, 150); }
        }

        public double MediaRenderWidth
        {
            get
            {
                var maxWidth = MediaPlaceholderWidth;
                if (_mediaPreviewAspectRatio <= 0.1) return maxWidth;

                // GIF messages must preserve the complete animation frame.
                // Do not squeeze their width merely to satisfy the video
                // height cap; their height is allowed to grow with aspect ratio.
                if (string.Equals(MediaKind, "gif", StringComparison.OrdinalIgnoreCase))
                    return Math.Round(maxWidth);

                const double maxHeight = 360.0;
                var height = maxWidth / _mediaPreviewAspectRatio;
                if (height <= maxHeight) return Math.Round(maxWidth);

                return Math.Max(1, Math.Round(maxHeight * _mediaPreviewAspectRatio));
            }
        }

        public double GifRenderWidth
        {
            get
            {
                var width = MediaPlaceholderWidth;
                if (!string.Equals(MediaKind, "gif", StringComparison.OrdinalIgnoreCase))
                    return width;

                // Width stays within the normal message media width. Height is
                // derived from the animation's real TDLib width/height ratio.
                return Math.Max(1, Math.Round(width));
            }
        }

        public double GifRenderHeight
        {
            get
            {
                var width = GifRenderWidth;
                if (_mediaPreviewAspectRatio > 0.1)
                    return Math.Max(1, Math.Round(width / _mediaPreviewAspectRatio));

                // Never assume 16:9 for GIF. Until TDLib dimensions are known,
                // use a neutral square; SetMediaPreviewAspectRatio will resize
                // the message immediately when real animation metadata arrives.
                return Math.Max(1, Math.Round(width));
            }
        }

        public double MediaDownloadPlaceholderHeight
        {
            get
            {
                if (_mediaPreviewAspectRatio > 0.1)
                {
                    var width = MediaRenderWidth;
                    return Math.Max(1, Math.Round(width / _mediaPreviewAspectRatio));
                }
                if (!string.IsNullOrEmpty(MediaPreviewUri))
                    return Math.Round(MediaPlaceholderWidth * 0.5625);
                return MediaPlaceholderHeight;
            }
        }

        public ImageSource MediaPreviewImageSource
        {
            get
            {
                if (!(MediaKind == "video" || MediaKind == "roundvideo" || MediaKind == "audio")) return null;
                if (!string.IsNullOrEmpty(MediaPreviewUri))
                {
                    try
                    {
                        if (_mediaPreviewImageSource != null &&
                            string.Equals(_mediaPreviewImageSourceUri, MediaPreviewUri, StringComparison.OrdinalIgnoreCase))
                            return _mediaPreviewImageSource;

                        var image = new BitmapImage();
                        image.DecodePixelWidth = 320;
                        image.UriSource = new Uri(MediaPreviewUri);
                        _mediaPreviewImageSource = image;
                        _mediaPreviewImageSourceUri = MediaPreviewUri;
                        return _mediaPreviewImageSource;
                    }
                    catch
                    {
                    }
                }
                EnsureMediaPreviewImageSource();
                return _mediaPreviewImageSource;
            }
        }

        public Visibility MediaPreviewVisibility
        {
            get
            {
                if (!(MediaKind == "video" || MediaKind == "roundvideo")) return Visibility.Collapsed;
                return MediaPreviewImageSource == null ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        public double MediaDownloadTextWidth
        {
            get { return Math.Max(96, MediaPlaceholderWidth - 36); }
        }

        public Thickness CaptionMargin
        {
            get
            {
                if (HasMedia && !IsWebPageMedia(MediaKind)) return new Thickness(0, 5, 0, 0);
                return new Thickness(0);
            }
        }

        public void AddMediaItem(ChatMediaItemViewModel item)
        {
            if (item == null) return;
            if (MediaItems == null) MediaItems = new ObservableCollection<ChatMediaItemViewModel>();
            if (MediaItemRows == null) MediaItemRows = new ObservableCollection<ChatMediaAlbumRowViewModel>();
            item.OwnerMessage = this;
            MediaItems.Add(item);
            HasMedia = true;
            if (MediaItems.Count == 1) ApplyPrimaryMedia(item);
            RebuildMediaItemRows();
            OnPropertyChanged("MediaItems");
            OnPropertyChanged("MediaItemRows");
            OnPropertyChanged("MediaItemsVisibility");
            OnPropertyChanged("CaptionMargin");
            NotifyMediaCollectionStateChanged();
            NotifyMediaStateChanged();
        }

        public bool RemoveMediaItemBySourceMessageId(int messageId)
        {
            if (messageId <= 0 || MediaItems == null || MediaItems.Count == 0) return false;
            for (var i = MediaItems.Count - 1; i >= 0; i--)
            {
                var item = MediaItems[i];
                if (item == null || item.SourceMessageId != messageId) continue;
                MediaItems.RemoveAt(i);
                if (MediaItems.Count > 0) ApplyPrimaryMedia(MediaItems[0]);
                RebuildMediaItemRows();
                OnPropertyChanged("MediaItems");
                OnPropertyChanged("MediaItemRows");
                OnPropertyChanged("MediaItemsVisibility");
                OnPropertyChanged("CaptionMargin");
                NotifyMediaCollectionStateChanged();
                NotifyMediaStateChanged();
                return true;
            }
            return false;
        }

        private void RebuildMediaItemRows()
        {
            if (MediaItemRows == null) MediaItemRows = new ObservableCollection<ChatMediaAlbumRowViewModel>();
            MediaItemRows.Clear();

            if (MediaItems == null || MediaItems.Count == 0) return;
            var visualMediaCount = 0;
            for (var i = 0; i < MediaItems.Count; i++)
            {
                var item = MediaItems[i];
                if (item == null) continue;
                if (string.Equals(item.MediaKind, "document", StringComparison.OrdinalIgnoreCase)) continue;
                visualMediaCount++;
            }

            ChatMediaAlbumRowViewModel row = null;
            var visualIndex = 0;
            for (var i = 0; i < MediaItems.Count; i++)
            {
                var mediaItem = MediaItems[i];
                if (mediaItem == null) continue;

                // Documents sent together use the same Telegram grouped_id,
                // but visually they are still individual file cards. Never
                // place two documents into one square album row.
                var isDocument = string.Equals(mediaItem.MediaKind, "document", StringComparison.OrdinalIgnoreCase);
                mediaItem.AlbumWideTile = !isDocument && visualMediaCount == 3 && visualIndex == 0;

                if (isDocument || mediaItem.AlbumWideTile || row == null || row.Items.Count >= 2)
                {
                    row = new ChatMediaAlbumRowViewModel();
                    MediaItemRows.Add(row);
                }

                mediaItem.NotifyAlbumLayoutChanged();
                row.Items.Add(mediaItem);

                // Force the next item to start a new row after a document.
                if (isDocument || mediaItem.AlbumWideTile)
                    row = null;
                if (!isDocument)
                    visualIndex++;
            }
        }

        public void ApplyPrimaryMedia(ChatMediaItemViewModel item)
        {
            if (item == null) return;
            MediaKind = item.MediaKind;
            MediaFileUri = item.MediaFileUri;
            MediaFallbackUri = item.MediaFallbackUri;
            MediaFullUri = item.MediaFullUri;
            MediaTitle = item.MediaTitle;
            MediaErrorText = item.MediaErrorText;
            MediaFileName = item.MediaFileName;
            MediaPerformer = item.MediaPerformer;
            MediaMimeType = item.MediaMimeType;
            MediaId = item.MediaId;
            MediaFullId = item.MediaFullId;
            MediaAccessHash = item.MediaAccessHash;
            MediaDcId = item.MediaDcId;
            MediaFileReference = item.MediaFileReference;
            MediaPreviewId = item.MediaPreviewId;
            MediaThumbSize = item.MediaThumbSize;
            FullPhotoThumbSize = item.FullPhotoThumbSize;
            MediaThumbBytes = item.MediaThumbBytes;
            MediaPreviewUri = item.MediaPreviewUri;
            MediaSize = item.MediaSize;
            MediaIsPhoto = item.MediaIsPhoto;
            MediaDurationSeconds = item.MediaDurationSeconds;
            HasMedia = true;
        }

        public void UpdateFrom(ChatMessageViewModel source)
        {
            if (source == null) return;

            Id = source.Id;
            SortId = source.SortId;
            Date = source.Date;
            SetEditDate(source.EditDate);
            IsOutgoing = source.IsOutgoing;
            IsGroupChat = source.IsGroupChat;
            IsChannelPost = source.IsChannelPost;
            SetPostAuthor(source.PostAuthor);
            var shouldReplaceText = true;
            if (shouldReplaceText && HasVisibleText(Text) && source.IsMediaFallbackText(source.Text))
                shouldReplaceText = false;
            if (shouldReplaceText)
                Text = source.Text;
            if (shouldReplaceText || source.HasTextEntities)
                SetTextEntities(source.TextEntities);
            GroupedId = source.GroupedId;
            SetMessageState(source.IsSending, source.IsRead);

            SenderName = source.SenderName;
            SenderInitials = source.SenderInitials;
            SenderAvatarUri = source.SenderAvatarUri;
            SenderPeerKey = source.SenderPeerKey;
            SenderPeerType = source.SenderPeerType;
            SenderPeerId = source.SenderPeerId;
            SenderAccessHash = source.SenderAccessHash;
            SenderIsGroup = source.SenderIsGroup;
            SenderIsChannel = source.SenderIsChannel;
            SenderIsBroadcast = source.SenderIsBroadcast;
            SenderAvatarPhotoId = source.SenderAvatarPhotoId;
            SenderAvatarDcId = source.SenderAvatarDcId;
            SenderAvatarStrippedThumb = source.SenderAvatarStrippedThumb;

            ForwardedFrom = source.ForwardedFrom;
            ForwardedInitials = source.ForwardedInitials;
            ForwardedAvatarUri = source.ForwardedAvatarUri;
            ForwardedPeerKey = source.ForwardedPeerKey;
            ForwardedPeerType = source.ForwardedPeerType;
            ForwardedPeerId = source.ForwardedPeerId;
            ForwardedAccessHash = source.ForwardedAccessHash;
            ForwardedAvatarPhotoId = source.ForwardedAvatarPhotoId;
            ForwardedAvatarDcId = source.ForwardedAvatarDcId;
            ForwardedAvatarStrippedThumb = source.ForwardedAvatarStrippedThumb;

            ReplyToMessageId = source.ReplyToMessageId;
            ReplyToSenderName = source.ReplyToSenderName;
            ReplyToText = source.ReplyToText;
            IsPinned = source.IsPinned;
            CanReply = source.CanReply;
            CanPin = source.CanPin;
            CanForward = source.CanForward;
            CanDelete = source.CanDelete;
            CanReact = source.CanReact;
            CanGetViewers = source.CanGetViewers;
            HasCanGetViewersFlag = source.HasCanGetViewersFlag;
            if (source.CanOpenComments || source.CommentsCount > 0 || !CanOpenComments)
            {
                CommentsCount = source.CommentsCount;
                CommentsChannelId = source.CommentsChannelId;
                CommentsMaxId = source.CommentsMaxId;
                CommentsReadMaxId = source.CommentsReadMaxId;
                CommentsDiscussionTitle = source.CommentsDiscussionTitle;
                CommentsDiscussionAccessHash = source.CommentsDiscussionAccessHash;
                CommentsDiscussionCanSend = source.CommentsDiscussionCanSend;
                CanOpenComments = source.CanOpenComments;
                SetCommentAvatars(source.CommentAvatars);
            }

            if (source.HasMedia || !string.IsNullOrEmpty(source.MediaKind))
            {
                MediaKind = source.MediaKind;
                MediaTitle = source.MediaTitle;
                MediaErrorText = source.MediaErrorText;
                MediaFileName = source.MediaFileName;
                MediaPerformer = source.MediaPerformer;
                MediaMimeType = source.MediaMimeType;
                HasMedia = source.HasMedia;
                MediaId = source.MediaId;
                MediaFullId = source.MediaFullId;
                MediaAccessHash = source.MediaAccessHash;
                MediaDcId = source.MediaDcId;
                MediaFileReference = source.MediaFileReference;
                MediaPreviewId = source.MediaPreviewId;
                MediaThumbSize = source.MediaThumbSize;
                FullPhotoThumbSize = source.FullPhotoThumbSize;
                MediaThumbBytes = source.MediaThumbBytes;
                MediaPreviewUri = source.MediaPreviewUri;
                MediaSize = source.MediaSize;
                MediaIsPhoto = source.MediaIsPhoto;
                MediaDurationSeconds = source.MediaDurationSeconds;
                if (!string.IsNullOrEmpty(source.MediaFallbackUri) || string.IsNullOrEmpty(MediaFallbackUri))
                    MediaFallbackUri = source.MediaFallbackUri;
                if (!string.IsNullOrEmpty(source.MediaFileUri) || string.IsNullOrEmpty(MediaFileUri))
                    MediaFileUri = source.MediaFileUri;
                if (!string.IsNullOrEmpty(source.MediaFullUri) || string.IsNullOrEmpty(MediaFullUri))
                    MediaFullUri = source.MediaFullUri;
            }

            StructuredMediaTitle = source.StructuredMediaTitle;
            StructuredMediaSubtitle = source.StructuredMediaSubtitle;
            StructuredMediaLines = source.StructuredMediaLines;
            StructuredMediaItems = source.StructuredMediaItems;
            StructuredMediaAllowsMultiple = source.StructuredMediaAllowsMultiple;
            StructuredMediaIsClosed = source.StructuredMediaIsClosed;
            StructuredMediaTotalVoters = source.StructuredMediaTotalVoters;
            PollIsPublic = source.PollIsPublic;
            PollIsQuiz = source.PollIsQuiz;
            PollAllowsRevoting = source.PollAllowsRevoting;
            PollCanAddOption = source.PollCanAddOption;
            PollClosePeriodSeconds = source.PollClosePeriodSeconds;
            PollCloseDate = source.PollCloseDate;
            PollRecentVotersText = source.PollRecentVotersText;
            PollSolutionText = source.PollSolutionText;
            if (StructuredMediaItems != null)
            {
                for (var i = 0; i < StructuredMediaItems.Count; i++)
                {
                    if (StructuredMediaItems[i] != null)
                        StructuredMediaItems[i].OwnerMessage = this;
                }
            }

            if (source.Reactions != null && source.Reactions.Count > 0 || Reactions == null || Reactions.Count == 0)
                SetReactions(source.Reactions);
            if (source.ReadByUsers != null && source.ReadByUsers.Count > 0)
                SetReadByUsers(source.ReadByUsers);

            InlineKeyboardRows.Clear();
            if (source.InlineKeyboardRows != null)
                foreach (var row in source.InlineKeyboardRows) InlineKeyboardRows.Add(row);
            ReplyKeyboardRows.Clear();
            if (source.ReplyKeyboardRows != null)
                foreach (var row in source.ReplyKeyboardRows) ReplyKeyboardRows.Add(row);
            ReplyKeyboardOneTime = source.ReplyKeyboardOneTime;
            ReplyKeyboardPersistent = source.ReplyKeyboardPersistent;
            ReplyKeyboardPlaceholder = source.ReplyKeyboardPlaceholder;
            RemovesReplyKeyboard = source.RemovesReplyKeyboard;
            NotifyMessageStateChanged();
            NotifyMediaCollectionStateChanged();
            NotifyMediaStateChanged();
        }

        public void NotifyContentChanged()
        {
            NotifyMessageStateChanged();
            NotifyMediaCollectionStateChanged();
            NotifyMediaStateChanged();
        }

        private void NotifyMessageStateChanged()
        {
            OnPropertyChanged("Text");
            OnPropertyChanged("TextEntities");
            OnPropertyChanged("HasTextEntities");
            OnPropertyChanged("TextVisibility");
            OnPropertyChanged("SenderName");
            OnPropertyChanged("SenderVisibility");
            OnPropertyChanged("AvatarVisibility");
            OnPropertyChanged("SenderInitials");
            OnPropertyChanged("SenderInitialsVisibility");
            OnPropertyChanged("ForwardedVisibility");
            OnPropertyChanged("ForwardedAvatarVisibility");
            OnPropertyChanged("ReplyToVisibility");
            OnPropertyChanged("InlineKeyboardRows");
            OnPropertyChanged("InlineKeyboardVisibility");
            OnPropertyChanged("BubbleStretchContentWidth");
            OnPropertyChanged("ReplyKeyboardRows");
            OnPropertyChanged("HasReplyKeyboard");
            OnPropertyChanged("ReplyToSenderDisplay");
            OnPropertyChanged("ReplyToTextDisplay");
            OnPropertyChanged("CanCopyText");
            OnPropertyChanged("CanOpenComments");
            OnPropertyChanged("CommentsCount");
            OnPropertyChanged("CommentsCountText");
            OnPropertyChanged("CommentsPreviewVisibility");
            OnPropertyChanged("CommentAvatars");
            OnPropertyChanged("CommentAvatarsVisibility");
            OnPropertyChanged("CommentsGlyphVisibility");
            OnPropertyChanged("ReadByUsers");
            OnPropertyChanged("ReadByPreviewUsers");
            OnPropertyChanged("ReadByPreviewVisibility");
            OnPropertyChanged("ReadByPreviewText");
            OnPropertyChanged("BubbleStretchContentWidth");
            OnPropertyChanged("TimeText");
            OnPropertyChanged("IsEditedMessage");
            OnPropertyChanged("FooterText");
            OnPropertyChanged("FooterVisibility");
            OnPropertyChanged("FooterPadding");
            OnPropertyChanged("FooterRow");
            OnPropertyChanged("FooterCornerRadius");
            OnPropertyChanged("FooterBackground");
            OnPropertyChanged("FooterForeground");
            OnPropertyChanged("BubbleMinHeight");
            OnPropertyChanged("IsLeftAligned");
            OnPropertyChanged("BubbleAlignment");
            OnPropertyChanged("BubbleMargin");
            OnPropertyChanged("BubbleMaxWidth");
            OnPropertyChanged("BubbleContentAlignment");
            OnPropertyChanged("BubbleBackground");
            OnPropertyChanged("PollVisibility");
            OnPropertyChanged("PollStatusText");
            OnPropertyChanged("TextVisibility");
            OnPropertyChanged("BubbleContentMargin");
            OnPropertyChanged("BubbleBackground");
            OnPropertyChanged("MessageForeground");
            OnPropertyChanged("PollStatusVisibility");
            OnPropertyChanged("PollRecentVotersVisibility");
            OnPropertyChanged("PollSolutionVisibility");
            OnPropertyChanged("PollCanAddOption");
            OnPropertyChanged("PollAllowsRevoting");
            OnPropertyChanged("PollAddOptionVisibility");
            OnPropertyChanged("MessageForeground");
            OnPropertyChanged("MessageSubtleForeground");
            OnPropertyChanged("MessageAccentBrush");
            OnPropertyChanged("MediaControlAccentBrush");
            OnPropertyChanged("MediaControlAccentForegroundBrush");
            OnPropertyChanged("LocationIconVisibility");
            OnPropertyChanged("LocationIconSource");
            OnPropertyChanged("FileDownloadBackgroundBrush");
            OnPropertyChanged("FileDownloadForegroundBrush");
            OnPropertyChanged("IncomingTailVisibility");
            OnPropertyChanged("OutgoingTailVisibility");
            OnPropertyChanged("OutgoingStateVisibility");
            OnPropertyChanged("StateIconSource");
            OnPropertyChanged("IsLocationMessage");
        }

        public string TimeText
        {
            get
            {
                if (Date <= 0) return string.Empty;
                try
                {
                    var utc = new DateTime(1970, 1, 1).AddSeconds(Date);
                    return utc.ToLocalTime().ToString("H:mm");
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        public string FooterText
        {
            get
            {
                var time = TimeText;
                if (string.IsNullOrEmpty(time)) return string.Empty;

                var result = string.Empty;
                if (!string.IsNullOrWhiteSpace(PostAuthor))
                    result = PostAuthor.Trim();
                if (IsEditedMessage)
                    result = string.IsNullOrEmpty(result) ? "изменено" : result + " изменено";
                return string.IsNullOrEmpty(result) ? time : result + " " + time;
            }
        }

        public bool IsLeftAligned
        {
            get { return IsChannelPost || !IsOutgoing; }
        }

        public ImageSource StateIconSource
        {
            get
            {
                if (!IsOutgoing || IsChannelPost) return null;
                if (IsSending) return GetCachedStateIconSource(ref _stateSendingIconSource, StateSendingIconUri);
                if (IsRead) return GetCachedStateIconSource(ref _stateReadIconSource, StateReadIconUri);
                return GetCachedStateIconSource(ref _stateSentIconSource, StateSentIconUri);
            }
        }

        private static ImageSource GetCachedStateIconSource(ref ImageSource source, string uri)
        {
            if (source == null)
                source = new BitmapImage(new Uri(uri));
            return source;
        }

        public Visibility OutgoingStateVisibility
        {
            get { return IsOutgoing && !IsChannelPost ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Thickness BubbleMargin
        {
            get
            {
                if (!IsLeftAligned) return new Thickness(60, 1, 14, 1);
                if (IsGroupChat) return new Thickness(58, 1, 60, 1);
                return new Thickness(14, 1, 60, 1);
            }
        }

        public Thickness BubblePadding
        {
            get { return new Thickness(0); }
        }

        /// <summary>Unigram's ContentPanel MinHeight. Keeps a one-word bubble from collapsing
        /// around its text. Media that draws its own frame does not need it.</summary>
        public double BubbleMinHeight
        {
            get { return IsBareMediaMessage ? 0.0 : 30.0; }
        }

        public Thickness BubbleContentMargin
        {
            get
            {
                // Unigram's MessageContentPadding: 10,4,10,6. Bare media cancels the padding
                // entirely (their Media.Margin = -10,-4,-10,-6) so the picture is flush.
                // No extra bottom room is reserved any more - when the footer is not a pill it
                // occupies its own grid row instead of floating over the content.
                if (IsBareMediaMessage) return new Thickness(0);
                return new Thickness(10, 4, 10, 6);
            }
        }

        /// <summary>
        /// The time/state footer always has its own row below the content. It must never be laid
        /// over photos, stickers or video notes because late media layout can make it overlap.
        /// </summary>
        public int FooterRow
        {
            get { return 1; }
        }

        /// <summary>
        /// True when the footer sits directly on top of a picture, so it needs Unigram's dark
        /// rounded pill to stay readable. A caption pushes the footer off the media, back into
        /// the bubble, where the pill is not wanted.
        /// </summary>
        private bool HasOverlayFooter
        {
            get
            {
                if (IsServiceMessage) return false;
                if (HasVisibleText(VisibleText)) return false;

                var key = MediaTemplateKey;
                return key == "photo" || key == "video" || key == "gif" || key == "album";
            }
        }

        /// <summary>Stickers, round videos and the like have no bubble, so the footer gets the
        /// service-style pill instead.</summary>
        private bool HasServiceFooter
        {
            get { return !IsServiceMessage && IsBareMediaMessage; }
        }

        private bool HasPillFooter
        {
            get { return false; }
        }

        public Thickness FooterPadding
        {
            get { return HasPillFooter ? new Thickness(6, 2, 6, 3) : new Thickness(0); }
        }

        public CornerRadius FooterCornerRadius
        {
            get { return HasPillFooter ? new CornerRadius(12) : new CornerRadius(0); }
        }

        public Brush FooterBackground
        {
            get
            {
                if (HasOverlayFooter) return MessagePalette.OverlayBackground;
                if (HasServiceFooter) return MessagePalette.ServiceBackground;
                return MessagePalette.Transparent;
            }
        }

        public Brush FooterForeground
        {
            get { return IsBareMediaMessage ? MessagePalette.SubtleLabel(false) : MessageSubtleForeground; }
        }

        private double FooterContentRightInset
        {
            get
            {
                if (IsEditedMessage || !string.IsNullOrWhiteSpace(PostAuthor)) return 128;
                return 44;
            }
        }

        public Thickness FooterMargin
        {
            get
            {
                // Keep a visible gap after media. Bare media has no bubble padding, so it needs a
                // slightly larger explicit separation from the sticker/photo/video-note content.
                if (IsBareMediaMessage) return new Thickness(0, 6, 0, 1);
                return new Thickness(0, 4, 4, 4);
            }
        }

        public SolidColorBrush BubbleBackground
        {
            get
            {
                if (IsBareMediaMessage) return MessagePalette.Transparent;
                return MessagePalette.Background(!IsLeftAligned);
            }
        }

        public SolidColorBrush BubbleBorderBrush
        {
            get { return MessagePalette.Transparent; }
        }

        public Thickness BubbleBorderThickness
        {
            get { return new Thickness(0); }
        }

        public SolidColorBrush MessageForeground
        {
            get { return MessagePalette.Foreground(!IsLeftAligned && !IsBareStructuredMessage); }
        }

        public SolidColorBrush MessageSubtleForeground
        {
            get { return MessagePalette.SubtleLabel(!IsLeftAligned && !IsBareStructuredMessage); }
        }

        public SolidColorBrush MessageAccentBrush
        {
            get { return MessagePalette.HeaderForeground(!IsLeftAligned && !IsBareStructuredMessage); }
        }

        public SolidColorBrush ForwardedBackground
        {
            get { return BubbleBackground; }
        }

        public SolidColorBrush MediaBlockBackground
        {
            get { return BubbleBackground; }
        }

        public Visibility IncomingTailVisibility
        {
            get { return Visibility.Collapsed; }
        }

        public Visibility OutgoingTailVisibility
        {
            get { return Visibility.Collapsed; }
        }

        public GridLength IncomingTailColumnWidth
        {
            get { return !IsOutgoing && IsFirstInSenderGroup ? new GridLength(10) : new GridLength(0); }
        }

        public GridLength OutgoingTailColumnWidth
        {
            get { return !IsLeftAligned && IsFirstInSenderGroup ? new GridLength(10) : new GridLength(0); }
        }

        public Visibility FooterVisibility
        {
            get { return string.IsNullOrEmpty(FooterText) ? Visibility.Collapsed : Visibility.Visible; }
        }

        /// <summary>
        /// Deliberately conservative: anything that could ever show a media block gets the full
        /// row template. Only messages that can never be anything but text take the light one.
        /// Read once, when the list container is prepared, so it must not depend on transient
        /// state such as download progress.
        /// </summary>
        public bool HasAnyMediaContent
        {
            get
            {
                if (HasMedia) return true;
                if (MediaItems != null && MediaItems.Count > 0) return true;
                if (StructuredMediaItems != null && StructuredMediaItems.Count > 0) return true;
                if (StructuredMediaLines != null && StructuredMediaLines.Count > 0) return true;
                if (!string.IsNullOrEmpty(MediaKind)) return true;
                if (!string.IsNullOrEmpty(MediaFileUri)) return true;
                if (!string.IsNullOrEmpty(MediaFallbackUri)) return true;
                if (!string.IsNullOrEmpty(MediaPreviewUri)) return true;
                if (MediaThumbBytes != null && MediaThumbBytes.Length > 0) return true;
                if (MediaId != 0) return true;
                return false;
            }
        }

        /// <summary>
        /// Selects which media block the row renders. Album membership is taken from GroupedId
        /// as well as MediaItems, because the individual album parts arrive after the message is
        /// already on screen and the template is only chosen once.
        /// </summary>
        public string MediaTemplateKey
        {
            get
            {
                if (GroupedId != 0 || (MediaItems != null && MediaItems.Count > 0)) return "album";

                var kind = MediaKind ?? string.Empty;
                if (string.Equals(kind, "poll", StringComparison.OrdinalIgnoreCase)) return "poll";
                if (string.Equals(kind, "todo", StringComparison.OrdinalIgnoreCase)) return "todo";

                if (kind == "photo") return "photo";
                if (kind == "video") return "video";
                if (kind == "gif") return "gif";
                if (kind == "roundvideo") return "roundvideo";
                if (kind == "sticker") return "sticker";
                if (kind == "voice") return "voice";
                if (kind == "audio") return "audio";

                if (IsLocationMessage) return "location";
                if (IsSingleFileMessage) return "file";
                return "generic";
            }
        }

        public bool IsStickerMessage
        {
            get { return HasMedia && MediaKind == "sticker"; }
        }

        public bool IsLocationMessage
        {
            get { return string.Equals(MediaKind, "location", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsBareStructuredMessage
        {
            get
            {
                return string.Equals(MediaKind, "poll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(MediaKind, "todo", StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool IsBareMediaMessage
        {
            get { return IsStickerMessage || IsLocationMessage || IsBareStructuredMessage || string.Equals(MediaKind, "roundvideo", StringComparison.OrdinalIgnoreCase); }
        }

        public HorizontalAlignment BubbleContentAlignment
        {
            get
            {
                if (IsStickerMessage || IsLocationMessage || string.Equals(MediaKind, "roundvideo", StringComparison.OrdinalIgnoreCase))
                    return IsLeftAligned ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                return HorizontalAlignment.Stretch;
            }
        }

        public bool IsOnlyServiceText
        {
            get { return !HasMedia && string.IsNullOrEmpty(SenderName) && string.IsNullOrEmpty(ForwardedFrom) && !IsOutgoing; }
        }

        public bool IsMediaDownloading
        {
            get { return _isMediaDownloading; }
            set
            {
                if (_isMediaDownloading == value) return;
                _isMediaDownloading = value;
                OnPropertyChanged("IsMediaDownloading");
                OnPropertyChanged("DownloadButtonText");
                OnPropertyChanged("DownloadButtonEnabled");
                OnPropertyChanged("PendingMediaFallbackText");
                OnPropertyChanged("PendingMediaFallbackVisibility");
                OnPropertyChanged("MediaDownloadPlaceholderIconVisibility");
                OnPropertyChanged("MediaDownloadPlaceholderProgressVisibility");
                OnPropertyChanged("MediaDownloadProgressCircleVisibility");
                OnPropertyChanged("MediaDownloadProgressValue");
                OnPropertyChanged("MediaDownloadProgressText");
                OnPropertyChanged("MediaDownloadProgressIndeterminate");
                OnPropertyChanged("FileIdleVisibility");
                OnPropertyChanged("FileDownloadProgressVisibility");
                OnPropertyChanged("FileDownloadIndeterminate");
                OnPropertyChanged("FileDownloadProgressText");
                OnPropertyChanged("VideoDownloadProgressBarVisibility");
                OnPropertyChanged("VideoDownloadProgressValue");
                OnPropertyChanged("VideoDownloadProgressText");
                OnPropertyChanged("VideoDownloadProgressIndeterminate");
                OnPropertyChanged("FileVisibility");
                OnPropertyChanged("FileDownloadProgressValue");
                UpdateDisplayedDownloadProgress(true);
            }
        }

        public bool IsFileDownloadOperationActive
        {
            get { return _isFileDownloadOperationActive; }
            set
            {
                if (_isFileDownloadOperationActive == value) return;
                _isFileDownloadOperationActive = value;
                OnPropertyChanged("IsFileDownloadOperationActive");
                OnPropertyChanged("DownloadButtonEnabled");
                OnPropertyChanged("FileIdleVisibility");
                OnPropertyChanged("FileDownloadProgressVisibility");
                OnPropertyChanged("FileDownloadIndeterminate");
                OnPropertyChanged("FileDownloadProgressValue");
                OnPropertyChanged("FileDownloadProgressText");
                OnPropertyChanged("MediaDownloadProgressCircleVisibility");
                OnPropertyChanged("MediaDownloadProgressValue");
                OnPropertyChanged("MediaDownloadProgressText");
                OnPropertyChanged("MediaDownloadProgressIndeterminate");
                OnPropertyChanged("VideoDownloadProgressBarVisibility");
                OnPropertyChanged("VideoDownloadProgressValue");
                OnPropertyChanged("VideoDownloadProgressText");
                OnPropertyChanged("VideoDownloadProgressIndeterminate");
                UpdateDisplayedDownloadProgress(true);
            }
        }

        public long MediaDownloadBytes
        {
            get { return _mediaDownloadBytes; }
            set
            {
                if (_mediaDownloadBytes == value) return;
                _mediaDownloadBytes = value;
                OnPropertyChanged("MediaDownloadBytes");
                OnPropertyChanged("FileDownloadProgressValue");
                OnPropertyChanged("FileDownloadIndeterminate");
                OnPropertyChanged("FileDownloadProgressText");
                OnPropertyChanged("MediaDownloadPlaceholderProgressVisibility");
                OnPropertyChanged("MediaDownloadProgressCircleVisibility");
                OnPropertyChanged("MediaDownloadProgressValue");
                OnPropertyChanged("MediaDownloadProgressText");
                OnPropertyChanged("MediaDownloadProgressIndeterminate");
                OnPropertyChanged("VideoDownloadProgressBarVisibility");
                OnPropertyChanged("VideoDownloadProgressValue");
                OnPropertyChanged("VideoDownloadProgressText");
                OnPropertyChanged("VideoDownloadProgressIndeterminate");
                UpdateDisplayedDownloadProgress(false);
            }
        }

        public long MediaDownloadTotalBytes
        {
            get { return _mediaDownloadTotalBytes; }
            set
            {
                if (_mediaDownloadTotalBytes == value) return;
                _mediaDownloadTotalBytes = value;
                OnPropertyChanged("MediaDownloadTotalBytes");
                OnPropertyChanged("FileDownloadProgressValue");
                OnPropertyChanged("FileDownloadIndeterminate");
                OnPropertyChanged("FileDownloadProgressText");
                OnPropertyChanged("MediaDownloadPlaceholderProgressVisibility");
                OnPropertyChanged("MediaDownloadProgressCircleVisibility");
                OnPropertyChanged("MediaDownloadProgressValue");
                OnPropertyChanged("MediaDownloadProgressText");
                OnPropertyChanged("MediaDownloadProgressIndeterminate");
                OnPropertyChanged("VideoDownloadProgressBarVisibility");
                OnPropertyChanged("VideoDownloadProgressValue");
                OnPropertyChanged("VideoDownloadProgressText");
                OnPropertyChanged("VideoDownloadProgressIndeterminate");
                UpdateDisplayedDownloadProgress(false);
            }
        }

        public bool DownloadButtonEnabled
        {
            get { return !IsMediaDownloading && !IsFileDownloadOperationActive; }
        }

        public string DownloadButtonText
        {
            get
            {
                if (IsMediaDownloading) return "Loading...";
                if (MediaKind == "emoji") return "";
                if (MediaKind == "photo") return "Load photo";
                if (MediaKind == "roundvideo") return "Load round video";
                if (MediaKind == "video") return "Load video";
                if (MediaKind == "gif") return "Load GIF";
                if (MediaKind == "sticker") return "Load sticker";
                if (MediaKind == "voice") return "Load voice message";
                if (MediaKind == "audio") return "Load audio";
                return "Load file";
            }
        }

        public string PendingMediaFallbackText
        {
            get
            {
                if (!HasPendingMedia) return string.Empty;
                if (MediaKind == "grouped") return IsMediaDownloading ? "Loading album..." : "Album";
                if (MediaKind == "photo") return IsMediaDownloading ? "Loading photo..." : "Photo";
                if (MediaKind == "sticker") return IsMediaDownloading ? "Loading sticker..." : "Sticker";
                if (MediaKind == "gif") return IsMediaDownloading ? "Loading GIF..." : "GIF";
                if (MediaKind == "video") return IsMediaDownloading ? "Loading video..." : "Video";
                if (MediaKind == "roundvideo") return IsMediaDownloading ? "Loading round video..." : "Round video";
                if (MediaKind == "voice") return IsMediaDownloading ? "Loading voice message..." : "Voice message";
                if (MediaKind == "audio") return IsMediaDownloading ? "Loading audio..." : "Audio";
                if (!string.IsNullOrEmpty(MediaTitle)) return MediaTitle;
                if (!string.IsNullOrEmpty(MediaFileName)) return MediaFileName;
                return IsMediaDownloading ? "Loading media..." : "Media";
            }
        }

        public Visibility PendingMediaFallbackVisibility
        {
            get { return MediaDownloadPlaceholderVisibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed; }
        }

        public HorizontalAlignment BubbleAlignment
        {
            get { return IsLeftAligned ? HorizontalAlignment.Left : HorizontalAlignment.Right; }
        }

        public Visibility SenderVisibility
        {
            get { return !IsOutgoing && (IsGroupChat || IsChannelPost) && IsFirstInSenderGroup && !string.IsNullOrEmpty(SenderName) ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility AvatarVisibility
        {
            get { return !IsOutgoing && IsGroupChat && !IsChannelPost && IsFirstInSenderGroup && !string.IsNullOrEmpty(SenderName) ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility SenderInitialsVisibility
        {
            get { return string.IsNullOrEmpty(SenderAvatarUri) ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility TextVisibility
        {
            get
            {
            if (IsServiceMessage) return Visibility.Collapsed;
                if (string.Equals(MediaKind, "todo", StringComparison.OrdinalIgnoreCase)) return Visibility.Collapsed;
                if (string.Equals(MediaKind, "poll", StringComparison.OrdinalIgnoreCase))
                    return PollVisibility == Visibility.Visible ? Visibility.Collapsed : (HasVisibleText(VisibleText) ? Visibility.Visible : Visibility.Collapsed);
                if (IsSingleFileNameText(VisibleText)) return Visibility.Collapsed;
                return HasVisibleText(VisibleText) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility ServiceMessageVisibility
        {
            get { return IsServiceMessage && !string.IsNullOrEmpty(ServiceActionText) ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility BubbleVisibility
        {
            get { return IsServiceMessage ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility FileVisibility
        {
            get
            {
                return IsSingleFileMessage
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        public Visibility FileIdleVisibility
        {
            get
            {
                return IsSingleFileMessage && !IsMediaDownloading && !IsFileDownloadOperationActive
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        public Visibility FileDownloadProgressVisibility
        {
            get
            {
                return IsSingleFileMessage && (IsMediaDownloading || IsFileDownloadOperationActive)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private bool IsSingleFileDownloadInProgress
        {
            get { return IsSingleFileMessage && (IsMediaDownloading || IsFileDownloadOperationActive); }
        }

        private bool IsSingleFileNameText(string text)
        {
            if (!IsSingleFileMessage || !HasVisibleText(text)) return false;

            var value = NormalizeFileNameText(text);
            if (string.IsNullOrEmpty(value)) return false;
            if (IsSameFileNameText(value, MediaFileName)) return true;
            if (IsSameFileNameText(value, MediaTitle)) return true;

            return IsSameFileNameText(value, FileDisplayName);
        }

        private static bool IsSameFileNameText(string normalizedText, string fileName)
        {
            var normalizedName = NormalizeFileNameText(fileName);
            if (string.IsNullOrEmpty(normalizedName)) return false;
            return string.Equals(normalizedText, normalizedName, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeFileNameText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            value = value.Trim();
            try
            {
                var name = System.IO.Path.GetFileName(value);
                if (!string.IsNullOrEmpty(name)) value = name;
            }
            catch
            {
            }
            return value.Trim();
        }

        public string FileDisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(MediaFileName)) return MediaFileName;
                if (!string.IsNullOrEmpty(MediaTitle)) return MediaTitle;
                if (HasVisibleText(VisibleText)) return VisibleText;
                return "file";
            }
        }

        public string FileSubtitle
        {
            get
            {
                var size = FormatFileSize(MediaSize);
                var type = FileTypeLabel;
                if (string.IsNullOrEmpty(type)) return size;
                return size + " " + type;
            }
        }

        public double FileDownloadProgressValue
        {
            get
            {
                var total = _displayMediaDownloadTotalBytes > 0 ? _displayMediaDownloadTotalBytes : (MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize);
                if (total <= 0) return 0;
                var value = (double)_displayMediaDownloadBytes * 100.0 / (double)total;
                if (value < 0) return 0;
                if (value <= 0) return 0;
                if (value > 100) return 100;
                return value;
            }
        }

        public bool FileDownloadIndeterminate
        {
            get { return (IsMediaDownloading || IsFileDownloadOperationActive) && MediaDownloadTotalBytes <= 0 && MediaDownloadBytes <= 0; }
        }

        public string FileDownloadProgressText
        {
            get
            {
                var total = _displayMediaDownloadTotalBytes > 0 ? _displayMediaDownloadTotalBytes : (MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize);
                if (_displayMediaDownloadBytes <= 0)
                {
                    if (MediaDownloadTotalBytes > 0) return "0 B / " + FormatFileSize(total);
                    if (total > 0) return "Downloading " + FormatFileSize(total);
                    return "Downloading...";
                }
                if (total > 0) return FormatFileSize(_displayMediaDownloadBytes) + " / " + FormatFileSize(total);
                return FormatFileSize(_displayMediaDownloadBytes);
            }
        }

        private void UpdateDisplayedDownloadProgress(bool force)
        {
            var total = MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize;
            var active = IsMediaDownloading || IsFileDownloadOperationActive;
            if (force || !active || total <= 0 || MediaDownloadBytes <= _displayMediaDownloadBytes)
            {
                _displayMediaDownloadBytes = active ? MediaDownloadBytes : 0;
                _displayMediaDownloadTotalBytes = active ? total : 0;
                StopDownloadProgressTimer();
                NotifyDownloadProgressValuesChanged();
                return;
            }

            _displayMediaDownloadTotalBytes = total;
            StartDownloadProgressTimer();
        }

        private void StartDownloadProgressTimer()
        {
            if (_downloadProgressTimer == null)
            {
                _downloadProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                _downloadProgressTimer.Tick += DownloadProgressTimer_Tick;
            }
            if (!_downloadProgressTimer.IsEnabled) _downloadProgressTimer.Start();
        }

        private void StopDownloadProgressTimer()
        {
            if (_downloadProgressTimer != null && _downloadProgressTimer.IsEnabled)
                _downloadProgressTimer.Stop();
        }

        private void DownloadProgressTimer_Tick(object sender, object e)
        {
            var target = MediaDownloadBytes;
            var total = MediaDownloadTotalBytes > 0 ? MediaDownloadTotalBytes : MediaSize;
            var active = IsMediaDownloading || IsFileDownloadOperationActive;
            if (!active || total <= 0 || target <= _displayMediaDownloadBytes)
            {
                _displayMediaDownloadBytes = active ? target : 0;
                _displayMediaDownloadTotalBytes = active ? total : 0;
                StopDownloadProgressTimer();
                NotifyDownloadProgressValuesChanged();
                return;
            }

            var remaining = target - _displayMediaDownloadBytes;
            var step = Math.Max(remaining / 8, Math.Max(total / 160, 32 * 1024));
            if (step <= 0) step = 1;
            _displayMediaDownloadBytes = Math.Min(target, _displayMediaDownloadBytes + step);
            _displayMediaDownloadTotalBytes = total;
            NotifyDownloadProgressValuesChanged();

            if (_displayMediaDownloadBytes >= target)
                StopDownloadProgressTimer();
        }

        private void NotifyDownloadProgressValuesChanged()
        {
            OnPropertyChanged("FileDownloadProgressValue");
            OnPropertyChanged("FileDownloadProgressText");
            OnPropertyChanged("MediaDownloadProgressValue");
            OnPropertyChanged("MediaDownloadProgressText");
            OnPropertyChanged("VideoDownloadProgressValue");
            OnPropertyChanged("VideoDownloadProgressText");
        }

        public SolidColorBrush FileDownloadBackgroundBrush
        {
            get { return MediaControlAccentBrush; }
        }

        public SolidColorBrush FileDownloadForegroundBrush
        {
            get { return MediaControlAccentForegroundBrush; }
        }

        public SolidColorBrush MediaControlAccentBrush
        {
            get
            {
                if (!IsLeftAligned) return new SolidColorBrush(Colors.White);
                return new SolidColorBrush(GetAccentColor());
            }
        }

        public SolidColorBrush MediaControlAccentForegroundBrush
        {
            get
            {
                if (!IsLeftAligned) return new SolidColorBrush(GetAccentColor());
                var color = GetAccentColor();
                var luminance = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
                return new SolidColorBrush(luminance > 186 ? Colors.Black : Colors.White);
            }
        }

        public Visibility PhotoVisibility
        {
            get { return (MediaItems == null || MediaItems.Count == 0) && MediaKind == "photo" && !string.IsNullOrEmpty(MediaFileUri) ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility VideoVisibility
        {
            get { return (MediaItems == null || MediaItems.Count == 0) && MediaKind == "video" && IsPlayableVideoUri(MediaFileUri) && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility GifVisibility
        {
            get { return (MediaItems == null || MediaItems.Count == 0) && MediaKind == "gif" && !string.IsNullOrEmpty(MediaFileUri) && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility RoundVideoVisibility
        {
            get { return (MediaItems == null || MediaItems.Count == 0) && MediaKind == "roundvideo" && IsPlayableVideoUri(MediaFileUri) && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility VideoPlaceholderVisibility
        {
            get { return (MediaItems == null || MediaItems.Count == 0) && (MediaKind == "video" || MediaKind == "roundvideo" || MediaKind == "gif") && HasMedia && string.IsNullOrEmpty(MediaFileUri) ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility AudioPlaceholderVisibility
        {
            get { return Visibility.Collapsed; }
        }

        public Visibility AudioVisibility
        {
            get { return (MediaItems == null || MediaItems.Count == 0) && (MediaKind == "audio" || MediaKind == "voice") && HasMedia && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility VoiceVisibility
        {
            get { return (MediaItems == null || MediaItems.Count == 0) && MediaKind == "voice" && HasMedia && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility MusicVisibility
        {
            get { return (MediaItems == null || MediaItems.Count == 0) && MediaKind == "audio" && HasMedia && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility StickerVisibility
        {
            get { return (MediaItems == null || MediaItems.Count == 0) && MediaKind == "sticker" && (!string.IsNullOrEmpty(MediaFileUri) || !string.IsNullOrEmpty(MediaFallbackUri)) && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility LocationIconVisibility
        {
            get { return (MediaItems == null || MediaItems.Count == 0) && IsLocationMessage ? Visibility.Visible : Visibility.Collapsed; }
        }

        public ImageSource LocationIconSource
        {
            get { return GetCachedStateIconSource(ref _locationIconSource, "ms-appx:///Assets/Maps/Map_Pin.png"); }
        }

        public Visibility StaticEmojiVisibility
        {
            get { return Visibility.Collapsed; }
        }

        public Visibility PlayerVisibility
        {
            get
            {
                if (MediaItems != null && MediaItems.Count > 0) return Visibility.Collapsed;
                if (MediaKind == "gif") return !string.IsNullOrEmpty(MediaFileUri) && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed;
                if (MediaKind == "video" || MediaKind == "roundvideo") return IsPlayableVideoUri(MediaFileUri) && !HasPlaybackError ? Visibility.Visible : Visibility.Collapsed;
                return Visibility.Collapsed;
            }
        }

        public Visibility DownloadButtonVisibility
        {
            get
            {
                if (IsSingleFileMessage) return Visibility.Collapsed;
                if (IsWebPageMedia(MediaKind)) return Visibility.Collapsed;
                if (MediaKind == "emoji") return Visibility.Collapsed;
                if (string.Equals(MediaKind, "poll", StringComparison.OrdinalIgnoreCase) || string.Equals(MediaKind, "todo", StringComparison.OrdinalIgnoreCase) || MediaKind == "audio" || MediaKind == "voice") return Visibility.Collapsed;
                if (MediaKind == "sticker" && !string.IsNullOrEmpty(MediaFallbackUri)) return Visibility.Collapsed;
                if (IsLocationMessage) return Visibility.Collapsed;
                return HasMedia && string.IsNullOrEmpty(MediaFileUri) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility StructuredMediaVisibility
        {
            get
            {
                var hasItems = StructuredMediaItems != null && StructuredMediaItems.Count > 0;
                var hasLines = StructuredMediaLines != null && StructuredMediaLines.Count > 0;
                return string.Equals(MediaKind, "todo", StringComparison.OrdinalIgnoreCase) && (hasItems || hasLines) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility PollVisibility
        {
            get
            {
                if (!string.Equals(MediaKind, "poll", StringComparison.OrdinalIgnoreCase)) return Visibility.Collapsed;
                var hasItems = StructuredMediaItems != null && StructuredMediaItems.Count > 0;
                var hasTitle = !string.IsNullOrEmpty(StructuredMediaTitle);
                var hasStatus = !string.IsNullOrEmpty(PollStatusText);
                return hasItems || hasTitle || hasStatus ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public string PollStatusText
        {
            get { return StructuredMediaSubtitle ?? string.Empty; }
        }

        public Visibility PollStatusVisibility
        {
            get { return string.IsNullOrEmpty(PollStatusText) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility PollRecentVotersVisibility
        {
            get { return string.IsNullOrEmpty(PollRecentVotersText) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility PollSolutionVisibility
        {
            get { return string.IsNullOrEmpty(PollSolutionText) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility PollAddOptionVisibility
        {
            get
            {
                return string.Equals(MediaKind, "poll", StringComparison.OrdinalIgnoreCase) && PollCanAddOption && !StructuredMediaIsClosed
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        public void NotifyPollDataChanged()
        {
            OnPropertyChanged("StructuredMediaTotalVoters");
            OnPropertyChanged("PollStatusText");
            OnPropertyChanged("PollStatusVisibility");
            OnPropertyChanged("PollRecentVotersText");
            OnPropertyChanged("PollRecentVotersVisibility");
            OnPropertyChanged("PollSolutionText");
            OnPropertyChanged("PollSolutionVisibility");
            OnPropertyChanged("PollAddOptionVisibility");
            if (StructuredMediaItems != null)
            {
                for (var i = 0; i < StructuredMediaItems.Count; i++)
                    if (StructuredMediaItems[i] != null) StructuredMediaItems[i].NotifyPollVisualStateChanged();
            }
        }

        public Visibility StructuredMediaSubtitleVisibility
        {
            get { return string.IsNullOrEmpty(StructuredMediaSubtitle) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string StructuredMediaText
        {
            get
            {
                if (StructuredMediaLines == null || StructuredMediaLines.Count == 0) return string.Empty;
                return string.Join(Environment.NewLine, StructuredMediaLines);
            }
        }

        public Visibility StructuredMediaItemsVisibility
        {
            get { return StructuredMediaItems != null && StructuredMediaItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility StructuredMediaTextVisibility
        {
            get
            {
                var hasItems = StructuredMediaItems != null && StructuredMediaItems.Count > 0;
                return !hasItems && !string.IsNullOrEmpty(StructuredMediaText) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility MediaErrorVisibility
        {
            get { return Visibility.Collapsed; }
        }

        public Visibility ForwardedVisibility
        {
            get { return string.IsNullOrEmpty(ForwardedFrom) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility ForwardedAvatarVisibility
        {
            get { return string.IsNullOrEmpty(ForwardedFrom) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility ReplyToVisibility
        {
            get { return ReplyToMessageId > 0 || !string.IsNullOrWhiteSpace(ReplyToText) ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string ReplyToSenderDisplay
        {
            get { return string.IsNullOrEmpty(ReplyToSenderName) ? "Message" : ReplyToSenderName; }
        }

        public string ReplyToTextDisplay
        {
            get
            {
                var text = ReplyToText;
                if (string.IsNullOrWhiteSpace(text)) return "Media";
                text = text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length > 90) text = text.Substring(0, 90) + "...";
                return text;
            }
        }

        public void SetReplyPreview(string senderName, string text)
        {
            ReplyToSenderName = senderName;
            ReplyToText = text;
            OnPropertyChanged("ReplyToSenderName");
            OnPropertyChanged("ReplyToText");
            OnPropertyChanged("ReplyToSenderDisplay");
            OnPropertyChanged("ReplyToTextDisplay");
            OnPropertyChanged("ReplyToVisibility");
        }

        public Visibility MediaTitleVisibility
        {
            get
            {
                if (IsSingleFileMessage) return Visibility.Collapsed;
                if (MediaKind == "photo" || MediaKind == "video" || MediaKind == "gif" || MediaKind == "sticker" || MediaKind == "emoji" || MediaKind == "roundvideo" || MediaKind == "audio" || MediaKind == "voice") return Visibility.Collapsed;
                if (IsLocationMessage) return Visibility.Collapsed;
                if (IsWebPageMedia(MediaKind)) return Visibility.Collapsed;
                if (string.Equals(MediaKind, "poll", StringComparison.OrdinalIgnoreCase) || string.Equals(MediaKind, "todo", StringComparison.OrdinalIgnoreCase)) return Visibility.Collapsed;
                return HasMedia && !string.IsNullOrEmpty(MediaTitle) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public void NotifyMediaCollectionStateChanged()
        {
            OnPropertyChanged("HasPendingMedia");
            OnPropertyChanged("MediaItemsVisibility");
            OnPropertyChanged("MediaItemsLoadedVisibility");
            OnPropertyChanged("MediaDownloadPlaceholderVisibility");
            OnPropertyChanged("RoundVideoDownloadPlaceholderVisibility");
            OnPropertyChanged("MediaDownloadPlaceholderIconVisibility");
            OnPropertyChanged("MediaDownloadPlaceholderProgressVisibility");
            OnPropertyChanged("VideoDownloadProgressBarVisibility");
            OnPropertyChanged("VideoDownloadProgressValue");
            OnPropertyChanged("VideoDownloadProgressText");
            OnPropertyChanged("VideoDownloadProgressIndeterminate");
            OnPropertyChanged("MediaRenderWidth");
            OnPropertyChanged("MediaDownloadPlaceholderHeight");
            OnPropertyChanged("GifRenderWidth");
            OnPropertyChanged("GifRenderHeight");
            OnPropertyChanged("MediaPreviewImageSource");
            OnPropertyChanged("MediaPreviewVisibility");
            OnPropertyChanged("PendingMediaFallbackText");
            OnPropertyChanged("PendingMediaFallbackVisibility");
            OnPropertyChanged("DownloadButtonVisibility");
            OnPropertyChanged("FileVisibility");
            OnPropertyChanged("FileIdleVisibility");
            OnPropertyChanged("FileDownloadProgressVisibility");
            OnPropertyChanged("FileDisplayName");
            OnPropertyChanged("FileSubtitle");
            OnPropertyChanged("FileDownloadProgressValue");
            OnPropertyChanged("FileDownloadIndeterminate");
            OnPropertyChanged("FileDownloadProgressText");
            OnPropertyChanged("VideoDownloadProgressBarVisibility");
            OnPropertyChanged("VideoDownloadProgressValue");
            OnPropertyChanged("VideoDownloadProgressText");
            OnPropertyChanged("VideoDownloadProgressIndeterminate");
            OnPropertyChanged("StructuredMediaVisibility");
            OnPropertyChanged("StructuredMediaSubtitleVisibility");
            OnPropertyChanged("StructuredMediaText");
            OnPropertyChanged("StructuredMediaItemsVisibility");
            OnPropertyChanged("StructuredMediaTextVisibility");
            OnPropertyChanged("DownloadButtonEnabled");
            OnPropertyChanged("CaptionMargin");
            OnPropertyChanged("BubbleContentMargin");
            OnPropertyChanged("BubbleStretchContentWidth");
            OnPropertyChanged("BubbleContentAlignment");
            OnPropertyChanged("FooterMargin");
            OnPropertyChanged("FooterPadding");
            OnPropertyChanged("FooterRow");
            OnPropertyChanged("FooterCornerRadius");
            OnPropertyChanged("FooterBackground");
            OnPropertyChanged("FooterForeground");
        }

        public void NotifyLayoutMetricsChanged()
        {
            OnPropertyChanged("BubbleMaxWidth");
            OnPropertyChanged("BubbleStretchContentWidth");
            OnPropertyChanged("BubbleContentAlignment");
            OnPropertyChanged("MediaPlaceholderWidth");
            OnPropertyChanged("MediaPlaceholderHeight");
            OnPropertyChanged("MediaRenderWidth");
            OnPropertyChanged("MediaDownloadPlaceholderHeight");
            OnPropertyChanged("MediaDownloadTextWidth");
            OnPropertyChanged("GifRenderWidth");
            OnPropertyChanged("GifRenderHeight");
            OnPropertyChanged("BubbleContentMargin");
            OnPropertyChanged("CaptionMargin");
            OnPropertyChanged("FooterMargin");
            OnPropertyChanged("FooterPadding");
            OnPropertyChanged("FooterRow");
            OnPropertyChanged("FooterCornerRadius");
            OnPropertyChanged("FooterBackground");
            OnPropertyChanged("FooterForeground");

            if (MediaItems != null)
            {
                for (var i = 0; i < MediaItems.Count; i++)
                {
                    if (MediaItems[i] != null)
                        MediaItems[i].NotifyAlbumLayoutChanged();
                }
            }
        }

        public void SetMediaPreviewAspectRatio(double aspectRatio)
        {
            if (aspectRatio <= 0.1) return;
            if (Math.Abs(_mediaPreviewAspectRatio - aspectRatio) < 0.01) return;
            _mediaPreviewAspectRatio = aspectRatio;
            OnPropertyChanged("MediaRenderWidth");
            OnPropertyChanged("MediaDownloadPlaceholderHeight");
            OnPropertyChanged("GifRenderWidth");
            OnPropertyChanged("GifRenderHeight");
            OnPropertyChanged("BubbleMaxWidth");
        }

        private static Color GetAccentColor()
        {
            try
            {
                return new UISettings().GetColorValue(UIColorType.Accent);
            }
            catch
            {
                return Color.FromArgb(255, 0, 132, 211);
            }
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

        private static double CalculateAvailableMediaWidth(bool isOutgoing, bool isGroupChat)
        {
            return ChatMessageLayoutMetrics.CalculateMediaWidth(isOutgoing, isGroupChat);
        }

        private static double CalculateAvailableTextWidth(bool isOutgoing, bool isGroupChat)
        {
            return ChatMessageLayoutMetrics.CalculateTextWidth(isOutgoing, isGroupChat);
        }

        private bool IsSingleFileMessage
        {
            get { return HasMedia && (MediaItems == null || MediaItems.Count == 0) && IsFileKind(MediaKind); }
        }

        private static bool IsFileKind(string kind)
        {
            return kind != "photo" &&
                kind != "video" &&
                kind != "gif" &&
                kind != "roundvideo" &&
                kind != "sticker" &&
                kind != "audio" &&
                kind != "voice" &&
                kind != "location" &&
                kind != "poll" &&
                kind != "todo" &&
                kind != "emoji" &&
                kind != "webpage" &&
                kind != "grouped";
        }

        private static bool IsWebPageMedia(string kind)
        {
            return string.Equals(kind, "webpage", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPlayableVideoUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            var value = uri.Trim();
            if (EndsWithAny(value, ".jpg", ".jpeg", ".png", ".bmp", ".webp")) return false;
            return true;
        }

        private static bool EndsWithAny(string value, params string[] suffixes)
        {
            if (string.IsNullOrEmpty(value) || suffixes == null) return false;
            for (var i = 0; i < suffixes.Length; i++)
            {
                if (value.EndsWith(suffixes[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void EnsureMediaPreviewImageSource()
        {
            if (_mediaPreviewImageSource != null) return;
            // Assign the field rather than going through SetMediaPreviewAspectRatio: this runs
            // inside a binding getter, so raising PropertyChanged here would re-enter layout.
            int thumbWidth;
            int thumbHeight;
            if (_mediaPreviewAspectRatio <= 0.1 &&
                ChatMediaPreviewHelper.TryReadImageSize(MediaThumbBytes, out thumbWidth, out thumbHeight))
                _mediaPreviewAspectRatio = (double)thumbWidth / thumbHeight;

            _mediaPreviewImageSource = ChatMediaPreviewHelper.CreateImageSource(MediaThumbBytes, 320, SetMediaPreviewAspectRatio);
            _mediaPreviewImageSourceUri = null;
        }

        private bool IsMediaFallbackText(string text)
        {
            if (!HasMedia || string.IsNullOrWhiteSpace(text)) return false;
            var value = text.Trim();
            if (string.Equals(MediaKind, "photo", StringComparison.OrdinalIgnoreCase))
                return string.Equals(value, "Photo", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(MediaKind, "video", StringComparison.OrdinalIgnoreCase))
                return string.Equals(value, "Video", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(MediaKind, "roundvideo", StringComparison.OrdinalIgnoreCase))
                return string.Equals(value, "Round video", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "Video", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(MediaKind, "gif", StringComparison.OrdinalIgnoreCase))
                return string.Equals(value, "GIF", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(MediaKind, "sticker", StringComparison.OrdinalIgnoreCase))
                return string.Equals(value, "Sticker", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(MediaKind, "voice", StringComparison.OrdinalIgnoreCase))
                return string.Equals(value, "Voice message", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(MediaKind, "audio", StringComparison.OrdinalIgnoreCase))
                return string.Equals(value, "Audio", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(MediaKind, "grouped", StringComparison.OrdinalIgnoreCase))
                return string.Equals(value, "Album", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "Photo", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "Video", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private static bool HasVisibleText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (char.IsWhiteSpace(ch)) continue;
                if (ch == '\u200b' || ch == '\u200c' || ch == '\u200d' || ch == '\ufeff') continue;
                return true;
            }
            return false;
        }

        private string FileTypeLabel
        {
            get
            {
                var extension = string.Empty;
                if (!string.IsNullOrEmpty(MediaFileName))
                {
                    try { extension = System.IO.Path.GetExtension(MediaFileName); }
                    catch { extension = string.Empty; }
                }

                if (!string.IsNullOrEmpty(extension))
                    return extension.TrimStart('.').ToUpperInvariant();

                if (!string.IsNullOrEmpty(MediaMimeType))
                {
                    var slash = MediaMimeType.LastIndexOf('/');
                    if (slash >= 0 && slash < MediaMimeType.Length - 1)
                        return MediaMimeType.Substring(slash + 1).ToUpperInvariant();
                }

                return "FILE";
            }
        }

        private static string FormatFileSize(long size)
        {
            if (size <= 0) return "0 MB";
            var value = (double)size;
            var units = new[] { "B", "KB", "MB", "GB" };
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            if (unit == 0) return ((long)value).ToString() + " " + units[unit];
            return value.ToString(value >= 10 ? "0.#" : "0.0") + " " + units[unit];
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private void NotifyMediaStateChanged()
        {
            OnPropertyChanged("MediaFileUri");
            OnPropertyChanged("MediaFallbackUri");
            OnPropertyChanged("MediaUri");
            OnPropertyChanged("PhotoImageSource");
            OnPropertyChanged("PhotoVisibility");
            OnPropertyChanged("VideoVisibility");
            OnPropertyChanged("GifVisibility");
            OnPropertyChanged("RoundVideoVisibility");
            OnPropertyChanged("VideoPlaceholderVisibility");
            OnPropertyChanged("AudioPlaceholderVisibility");
            OnPropertyChanged("AudioVisibility");
            OnPropertyChanged("VoiceVisibility");
            OnPropertyChanged("MusicVisibility");
            OnPropertyChanged("StickerVisibility");
            OnPropertyChanged("StaticEmojiVisibility");
            OnPropertyChanged("PlayerVisibility");
            OnPropertyChanged("DownloadButtonVisibility");
            OnPropertyChanged("DownloadButtonEnabled");
            OnPropertyChanged("MediaTitleVisibility");
            OnPropertyChanged("VisibleText");
            OnPropertyChanged("TextVisibility");
            OnPropertyChanged("StructuredMediaVisibility");
            OnPropertyChanged("StructuredMediaSubtitleVisibility");
            OnPropertyChanged("StructuredMediaText");
            OnPropertyChanged("StructuredMediaItemsVisibility");
            OnPropertyChanged("StructuredMediaTextVisibility");
            OnPropertyChanged("PollVisibility");
            OnPropertyChanged("PollStatusText");
            OnPropertyChanged("PollStatusVisibility");
            OnPropertyChanged("PollRecentVotersVisibility");
            OnPropertyChanged("PollSolutionVisibility");
            OnPropertyChanged("MediaErrorVisibility");
            OnPropertyChanged("MediaItemsVisibility");
            OnPropertyChanged("MediaItemsLoadedVisibility");
            OnPropertyChanged("MediaDownloadPlaceholderVisibility");
            OnPropertyChanged("RoundVideoDownloadPlaceholderVisibility");
            OnPropertyChanged("MediaDownloadPlaceholderIconVisibility");
            OnPropertyChanged("MediaDownloadPlaceholderProgressVisibility");
            OnPropertyChanged("MediaDownloadProgressCircleVisibility");
            OnPropertyChanged("MediaDownloadProgressValue");
            OnPropertyChanged("MediaDownloadProgressText");
            OnPropertyChanged("MediaDownloadProgressIndeterminate");
            OnPropertyChanged("MediaRenderWidth");
            OnPropertyChanged("MediaDownloadPlaceholderHeight");
            OnPropertyChanged("GifRenderWidth");
            OnPropertyChanged("GifRenderHeight");
            OnPropertyChanged("MediaPreviewImageSource");
            OnPropertyChanged("MediaPreviewVisibility");
            OnPropertyChanged("PendingMediaFallbackText");
            OnPropertyChanged("PendingMediaFallbackVisibility");
            OnPropertyChanged("CaptionMargin");
            OnPropertyChanged("BubbleContentMargin");
            OnPropertyChanged("FooterMargin");
            OnPropertyChanged("FooterPadding");
            OnPropertyChanged("FooterRow");
            OnPropertyChanged("FooterCornerRadius");
            OnPropertyChanged("FooterBackground");
            OnPropertyChanged("FooterForeground");
            OnPropertyChanged("BubbleBackground");
            OnPropertyChanged("IsStickerMessage");
            OnPropertyChanged("IsLocationMessage");
            OnPropertyChanged("IsBareStructuredMessage");
            OnPropertyChanged("IsBareMediaMessage");
            OnPropertyChanged("BubbleContentAlignment");
            OnPropertyChanged("MediaControlAccentBrush");
            OnPropertyChanged("MediaControlAccentForegroundBrush");
        }

        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    public class DateSeparatorItem
    {
        public string DateText { get; set; }
        public int DateUnix { get; set; }
        public virtual string UnreadText { get { return null; } }

        // Stub properties to prevent binding errors when the inline message
        // template is applied to date separator items. These return safe defaults
        // so the template renders without crashes or visual artifacts.
        public string ServiceActionText { get { return null; } }
        public Visibility ServiceMessageVisibility { get { return Visibility.Collapsed; } }
        public Visibility BubbleVisibility { get { return Visibility.Collapsed; } }
        public Visibility AvatarVisibility { get { return Visibility.Collapsed; } }
        public string SenderAvatarImageSource { get { return null; } }
        public string SenderInitials { get { return null; } }
        public Visibility SenderInitialsVisibility { get { return Visibility.Collapsed; } }
        public Thickness BubbleMargin { get { return new Thickness(0); } }
        public HorizontalAlignment BubbleAlignment { get { return HorizontalAlignment.Left; } }
        public HorizontalAlignment BubbleContentAlignment { get { return HorizontalAlignment.Stretch; } }
        public Brush BubbleBackground { get { return null; } }
        public Visibility IncomingTailVisibility { get { return Visibility.Collapsed; } }
        public Visibility OutgoingTailVisibility { get { return Visibility.Collapsed; } }
        public double BubbleMaxWidth { get { return 300; } }
        public double BubbleStretchContentWidth { get { return double.NaN; } }
        public Thickness BubblePadding { get { return new Thickness(8, 6, 8, 6); } }
        public Brush BubbleBorderBrush { get { return null; } }
        public Thickness BubbleBorderThickness { get { return new Thickness(0); } }
        public Thickness BubbleContentMargin { get { return new Thickness(0); } }
        public string SenderName { get { return null; } }
        public Visibility SenderVisibility { get { return Visibility.Collapsed; } }
        public Brush MessageAccentBrush { get { return null; } }
        public Visibility ForwardedVisibility { get { return Visibility.Collapsed; } }
        public Brush MessageSubtleForeground { get { return null; } }
        public string ForwardedFrom { get { return null; } }
        public Visibility ReplyToVisibility { get { return Visibility.Collapsed; } }
        public string ReplyToSenderDisplay { get { return null; } }
        public Visibility PollVisibility { get { return Visibility.Collapsed; } }
        public double MediaPlaceholderWidth { get { return 0; } }
        public string StructuredMediaTitle { get { return null; } }
        public Brush MessageForeground { get { return null; } }
        public string PollStatusText { get { return null; } }
        public Visibility PollStatusVisibility { get { return Visibility.Collapsed; } }
        public Visibility PollAddOptionVisibility { get { return Visibility.Collapsed; } }
        public object StructuredMediaItems { get { return null; } }
        public Visibility StructuredMediaItemsVisibility { get { return Visibility.Collapsed; } }
        public string PollRecentVotersText { get { return null; } }
        public Visibility PollRecentVotersVisibility { get { return Visibility.Collapsed; } }
        public string PollSolutionText { get { return null; } }
        public Visibility PollSolutionVisibility { get { return Visibility.Collapsed; } }
        public Visibility StructuredMediaVisibility { get { return Visibility.Collapsed; } }
        public string StructuredMediaSubtitle { get { return null; } }
        public Visibility StructuredMediaSubtitleVisibility { get { return Visibility.Collapsed; } }
        public Visibility StructuredMediaTextVisibility { get { return Visibility.Collapsed; } }
        public Visibility FileVisibility { get { return Visibility.Collapsed; } }
        public Visibility FileIdleVisibility { get { return Visibility.Collapsed; } }
        public bool DownloadButtonEnabled { get { return false; } }
        public Brush FileDownloadBackgroundBrush { get { return null; } }
        public Brush FileDownloadForegroundBrush { get { return null; } }
        public string FileDisplayName { get { return null; } }
        public string FileSubtitle { get { return null; } }
        public Visibility FileDownloadProgressVisibility { get { return Visibility.Collapsed; } }
        public string FileDownloadProgressText { get { return null; } }
        public bool FileDownloadIndeterminate { get { return false; } }
        public double FileDownloadProgressValue { get { return 0; } }
        public double MediaRenderWidth { get { return 0; } }
        public double MediaDownloadPlaceholderHeight { get { return 0; } }
        public Visibility MediaDownloadPlaceholderVisibility { get { return Visibility.Collapsed; } }
        public Visibility MediaDownloadPlaceholderProgressVisibility { get { return Visibility.Collapsed; } }
        public Visibility MediaDownloadProgressCircleVisibility { get { return Visibility.Collapsed; } }
        public string MediaDownloadProgressText { get { return null; } }
        public double MediaDownloadProgressValue { get { return 0; } }
        public bool MediaDownloadProgressIndeterminate { get { return false; } }
        public Visibility VideoDownloadProgressBarVisibility { get { return Visibility.Collapsed; } }
        public string VideoDownloadProgressText { get { return null; } }
        public double VideoDownloadProgressValue { get { return 0; } }
        public Visibility MediaDownloadPlaceholderIconVisibility { get { return Visibility.Collapsed; } }
        public double MediaDownloadTextWidth { get { return 0; } }
        public Visibility RoundVideoDownloadPlaceholderVisibility { get { return Visibility.Collapsed; } }
        public ImageSource MediaPreviewImageSource { get { return null; } }
        public Visibility MediaPreviewVisibility { get { return Visibility.Collapsed; } }
        public Brush MediaControlAccentBrush { get { return null; } }
        public Brush MediaControlAccentForegroundBrush { get { return null; } }
        public string DownloadButtonText { get { return null; } }
        public ImageSource PhotoImageSource { get { return null; } }
        public Visibility PhotoVisibility { get { return Visibility.Collapsed; } }
        public bool IsMediaDownloading { get { return false; } }
        public string MediaKind { get { return null; } }
        public string MediaFileUri { get { return null; } }
        public Visibility VideoVisibility { get { return Visibility.Collapsed; } }
        public double GifRenderWidth { get { return 0; } }
        public double GifRenderHeight { get { return 0; } }
        public Uri MediaUri { get { return null; } }
        public Visibility GifVisibility { get { return Visibility.Collapsed; } }
        public Visibility RoundVideoVisibility { get { return Visibility.Collapsed; } }
        public string MediaFallbackUri { get { return null; } }
        public Visibility StaticEmojiVisibility { get { return Visibility.Collapsed; } }
        public int MediaDurationSeconds { get { return 0; } }
        public Visibility StickerVisibility { get { return Visibility.Collapsed; } }
        public Visibility VoiceVisibility { get { return Visibility.Collapsed; } }
        public Visibility MusicVisibility { get { return Visibility.Collapsed; } }
        public object MediaItemRows { get { return null; } }
        public Visibility MediaItemsLoadedVisibility { get { return Visibility.Collapsed; } }
        public Thickness CaptionMargin { get { return new Thickness(0); } }
        public Visibility TextVisibility { get { return Visibility.Collapsed; } }
        public object Reactions { get { return null; } }
        public Visibility ReactionsVisibility { get { return Visibility.Collapsed; } }
        public object InlineKeyboardRows { get { return null; } }
        public Visibility InlineKeyboardVisibility { get { return Visibility.Collapsed; } }
        public Visibility CommentsPreviewVisibility { get { return Visibility.Collapsed; } }
        public object CommentAvatars { get { return null; } }
        public Visibility CommentAvatarsVisibility { get { return Visibility.Collapsed; } }
        public Visibility CommentsGlyphVisibility { get { return Visibility.Collapsed; } }
        public string CommentsCountText { get { return null; } }
        public object ReadByPreviewUsers { get { return null; } }
        public Visibility ReadByPreviewVisibility { get { return Visibility.Collapsed; } }
        public string ReadByPreviewText { get { return null; } }
        public Thickness FooterMargin { get { return new Thickness(0); } }
        public Visibility FooterVisibility { get { return Visibility.Collapsed; } }
        public ImageSource StateIconSource { get { return null; } }
        public Visibility OutgoingStateVisibility { get { return Visibility.Collapsed; } }
        public string FooterText { get { return null; } }
    }

    public sealed class UnreadSeparatorItem : DateSeparatorItem
    {
        public override string UnreadText { get { return "Unread messages"; } }
    }
}
