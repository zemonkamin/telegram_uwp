using System;
using Telegram.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Controls.Messages
{
    /// <summary>
    /// Hosts the media part of a message row.
    /// </summary>
    /// <remarks>
    /// Modelled on Unigram's MessageBubble, which keeps the media area as a plain Border and
    /// swaps its Child in code, reusing the existing child whenever a recycled row happens to
    /// carry a message of the same kind (its IContent.IsValid check).
    ///
    /// That reuse is the whole point. A ContentControl with a ContentTemplateSelector re-inflates
    /// its template every single time the row is recycled, so scrolling through a run of photos
    /// rebuilds the same subtree over and over. Here a run of same-kind messages costs one
    /// inflation in total - afterwards the child simply inherits the new DataContext and its
    /// bindings refresh.
    /// </remarks>
    public sealed class MessageMediaHost : UserControl
    {
        public DataTemplate PhotoTemplate { get; set; }
        public DataTemplate VideoTemplate { get; set; }
        public DataTemplate GifTemplate { get; set; }
        public DataTemplate RoundVideoTemplate { get; set; }
        public DataTemplate StickerTemplate { get; set; }
        public DataTemplate LocationTemplate { get; set; }
        public DataTemplate VoiceTemplate { get; set; }
        public DataTemplate MusicTemplate { get; set; }
        public DataTemplate PollTemplate { get; set; }
        public DataTemplate TodoTemplate { get; set; }
        public DataTemplate FileTemplate { get; set; }
        public DataTemplate AlbumTemplate { get; set; }
        public DataTemplate GenericTemplate { get; set; }

        private string _currentKey;

        public MessageMediaHost()
        {
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Covers the first realization, where DataContext can arrive before the templates
            // have been assigned by the XAML parser.
            if (Content == null) Apply(DataContext as ChatMessageViewModel);
        }

        private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            Apply(args.NewValue as ChatMessageViewModel);
        }

        private void Apply(ChatMessageViewModel message)
        {
            var key = message == null ? null : message.MediaTemplateKey;

            // Same kind as the message this row previously showed: keep the subtree, the child
            // inherits the new DataContext and every binding in it re-evaluates on its own.
            if (Content != null && string.Equals(key, _currentKey, StringComparison.Ordinal)) return;

            _currentKey = key;

            var template = SelectTemplate(key);
            if (template == null)
            {
                Content = null;
                return;
            }

            Content = template.LoadContent() as UIElement;
        }

        private DataTemplate SelectTemplate(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            switch (key)
            {
                case "album": return AlbumTemplate;
                case "photo": return PhotoTemplate;
                case "video": return VideoTemplate;
                case "gif": return GifTemplate;
                case "roundvideo": return RoundVideoTemplate;
                case "sticker": return StickerTemplate;
                case "location": return LocationTemplate;
                case "voice": return VoiceTemplate;
                case "audio": return MusicTemplate;
                case "poll": return PollTemplate;
                case "todo": return TodoTemplate;
                case "file": return FileTemplate;
                default: return GenericTemplate;
            }
        }
    }
}
