using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Telegram.Controls
{
    public sealed class LocalEmojiTextBlock : Panel
    {
        private sealed class Token
        {
            public FrameworkElement Element;
            public bool LineBreak;
        }

        private readonly List<Token> _tokens = new List<Token>();

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register("Text", typeof(string), typeof(LocalEmojiTextBlock), new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

        public static readonly DependencyProperty ForegroundProperty =
            DependencyProperty.Register("Foreground", typeof(Brush), typeof(LocalEmojiTextBlock), new PropertyMetadata(null, OnVisualPropertyChanged));

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register("FontSize", typeof(double), typeof(LocalEmojiTextBlock), new PropertyMetadata(16.0, OnVisualPropertyChanged));

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        public Brush Foreground
        {
            get { return (Brush)GetValue(ForegroundProperty); }
            set { SetValue(ForegroundProperty, value); }
        }

        public double FontSize
        {
            get { return (double)GetValue(FontSizeProperty); }
            set { SetValue(FontSizeProperty, value); }
        }

        private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as LocalEmojiTextBlock;
            if (control == null) return;
            control.Rebuild();
        }

        public LocalEmojiTextBlock()
        {
            Loaded += delegate { Rebuild(); };
        }

        private void Rebuild()
        {
            Children.Clear();
            _tokens.Clear();

            var text = Text ?? string.Empty;
            var buffer = new System.Text.StringBuilder();
            var index = 0;
            while (index < text.Length)
            {
                string emoji;
                string uri;
                int length;
                if (Telegram.ChatPage.TryReadLocalEmojiAsset(text, index, out emoji, out uri, out length))
                {
                    FlushText(buffer);
                    AddEmoji(uri);
                    index += length;
                    continue;
                }

                var ch = text[index];
                if (ch == '\r')
                {
                    index++;
                    continue;
                }

                if (ch == '\n')
                {
                    FlushText(buffer);
                    _tokens.Add(new Token { LineBreak = true });
                    index++;
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                {
                    FlushText(buffer);
                    AddText(ch.ToString());
                    index++;
                    continue;
                }

                buffer.Append(ch);
                index++;
            }

            FlushText(buffer);
            InvalidateMeasure();
        }

        private void FlushText(System.Text.StringBuilder buffer)
        {
            if (buffer == null || buffer.Length == 0) return;
            AddText(buffer.ToString());
            buffer.Length = 0;
        }

        private void AddText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var block = new TextBlock
            {
                Text = text,
                FontSize = FontSize,
                Foreground = Foreground,
                TextWrapping = TextWrapping.NoWrap,
                IsHitTestVisible = false
            };
            Children.Add(block);
            _tokens.Add(new Token { Element = block });
        }

        private void AddEmoji(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return;
            var size = Math.Max(16.0, FontSize + 4.0);
            var image = new Image
            {
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                Source = new BitmapImage(new Uri(uri)),
                IsHitTestVisible = false
            };
            Children.Add(image);
            _tokens.Add(new Token { Element = image });
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var availableWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0 ? double.MaxValue : availableSize.Width;
            var x = 0.0;
            var y = 0.0;
            var lineHeight = 0.0;
            var maxWidth = 0.0;

            for (var i = 0; i < _tokens.Count; i++)
            {
                var token = _tokens[i];
                if (token == null) continue;
                if (token.LineBreak)
                {
                    maxWidth = Math.Max(maxWidth, x);
                    y += Math.Max(lineHeight, FontSize + 4.0);
                    x = 0;
                    lineHeight = 0;
                    continue;
                }

                var child = token.Element;
                if (child == null) continue;
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var desired = child.DesiredSize;
                if (x > 0 && x + desired.Width > availableWidth)
                {
                    maxWidth = Math.Max(maxWidth, x);
                    y += Math.Max(lineHeight, FontSize + 4.0);
                    x = 0;
                    lineHeight = 0;
                }

                x += desired.Width;
                lineHeight = Math.Max(lineHeight, desired.Height);
            }

            maxWidth = Math.Max(maxWidth, x);
            y += Math.Max(lineHeight, _tokens.Count == 0 ? 0 : FontSize + 4.0);
            return new Size(double.IsInfinity(availableSize.Width) ? maxWidth : Math.Min(maxWidth, availableSize.Width), y);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var availableWidth = finalSize.Width <= 0 ? double.MaxValue : finalSize.Width;
            var x = 0.0;
            var y = 0.0;
            var lineHeight = 0.0;
            var lineStart = 0;

            for (var i = 0; i <= _tokens.Count; i++)
            {
                var flush = i == _tokens.Count || (_tokens[i] != null && _tokens[i].LineBreak);
                if (!flush)
                {
                    var child = _tokens[i].Element;
                    if (child != null)
                    {
                        var desired = child.DesiredSize;
                        if (x > 0 && x + desired.Width > availableWidth)
                        {
                            ArrangeLine(lineStart, i, y, lineHeight);
                            y += Math.Max(lineHeight, FontSize + 4.0);
                            x = 0;
                            lineHeight = 0;
                            lineStart = i;
                        }
                        x += desired.Width;
                        lineHeight = Math.Max(lineHeight, desired.Height);
                    }
                    continue;
                }

                ArrangeLine(lineStart, i, y, lineHeight);
                y += Math.Max(lineHeight, FontSize + 4.0);
                x = 0;
                lineHeight = 0;
                lineStart = i + 1;
            }

            return finalSize;
        }

        private void ArrangeLine(int start, int end, double y, double lineHeight)
        {
            var x = 0.0;
            for (var i = start; i < end; i++)
            {
                var token = i >= 0 && i < _tokens.Count ? _tokens[i] : null;
                var child = token == null ? null : token.Element;
                if (child == null) continue;
                var desired = child.DesiredSize;
                var childY = y + Math.Max(0, lineHeight - desired.Height);
                child.Arrange(new Rect(x, childY, desired.Width, desired.Height));
                x += desired.Width;
            }
        }
    }
}
