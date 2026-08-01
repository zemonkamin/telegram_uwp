using System;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Controls
{
    public sealed class CenteredStickerPackPanel : Panel
    {
        public static readonly DependencyProperty ItemWidthProperty =
            DependencyProperty.Register(
                "ItemWidth",
                typeof(double),
                typeof(CenteredStickerPackPanel),
                new PropertyMetadata(76.0, OnLayoutPropertyChanged));

        public static readonly DependencyProperty ItemHeightProperty =
            DependencyProperty.Register(
                "ItemHeight",
                typeof(double),
                typeof(CenteredStickerPackPanel),
                new PropertyMetadata(76.0, OnLayoutPropertyChanged));

        private double _lastFiniteWidth;

        public double ItemWidth
        {
            get { return (double)GetValue(ItemWidthProperty); }
            set { SetValue(ItemWidthProperty, value); }
        }

        public double ItemHeight
        {
            get { return (double)GetValue(ItemHeightProperty); }
            set { SetValue(ItemHeightProperty, value); }
        }

        private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var panel = d as CenteredStickerPackPanel;
            if (panel == null)
                return;

            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var itemWidth = Normalize(ItemWidth, 76.0);
            var itemHeight = Normalize(ItemHeight, 76.0);

            var width = availableSize.Width;
            if (IsFinitePositive(width))
                _lastFiniteWidth = width;
            else if (IsFinitePositive(_lastFiniteWidth))
                width = _lastFiniteWidth;
            else
                width = itemWidth;

            var columns = Math.Max(1, (int)Math.Floor(width / itemWidth));

            for (var i = 0; i < Children.Count; i++)
                Children[i].Measure(new Size(itemWidth, itemHeight));

            var rows = Children.Count == 0
                ? 0
                : (int)Math.Ceiling((double)Children.Count / columns);

            return new Size(width, rows * itemHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var itemWidth = Normalize(ItemWidth, 76.0);
            var itemHeight = Normalize(ItemHeight, 76.0);

            var width = finalSize.Width;
            if (!IsFinitePositive(width))
                width = IsFinitePositive(_lastFiniteWidth) ? _lastFiniteWidth : itemWidth;
            else
                _lastFiniteWidth = width;

            var columns = Math.Max(1, (int)Math.Floor(width / itemWidth));

            var childIndex = 0;
            var y = 0.0;

            while (childIndex < Children.Count)
            {
                var countInRow = Math.Min(columns, Children.Count - childIndex);

                // Center the ACTUAL row of stickers. For example, if the panel
                // can fit four stickers but the last row contains two, those
                // two are placed in the middle rather than at the left edge.
                var rowWidth = countInRow * itemWidth;
                var x = Math.Max(0.0, Math.Floor((width - rowWidth) / 2.0));

                for (var i = 0; i < countInRow; i++)
                {
                    Children[childIndex].Arrange(
                        new Rect(x, y, itemWidth, itemHeight));

                    x += itemWidth;
                    childIndex++;
                }

                y += itemHeight;
            }

            return new Size(width, y);
        }

        private static bool IsFinitePositive(double value)
        {
            return !double.IsNaN(value) &&
                   !double.IsInfinity(value) &&
                   value > 0;
        }

        private static double Normalize(double value, double fallback)
        {
            return IsFinitePositive(value) ? value : fallback;
        }
    }
}
