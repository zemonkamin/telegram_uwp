using System;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Controls
{
    /// <summary>
    /// Simple wrap panel for the sticker picker.
    /// Unlike ItemsWrapGrid, every row (including the last incomplete row)
    /// is horizontally centered inside the available picker width.
    /// </summary>
    public sealed class CenteredStickerWrapPanel : Panel
    {
        public static readonly DependencyProperty ItemWidthProperty =
            DependencyProperty.Register(
                "ItemWidth",
                typeof(double),
                typeof(CenteredStickerWrapPanel),
                new PropertyMetadata(76.0, OnLayoutPropertyChanged));

        public static readonly DependencyProperty ItemHeightProperty =
            DependencyProperty.Register(
                "ItemHeight",
                typeof(double),
                typeof(CenteredStickerWrapPanel),
                new PropertyMetadata(76.0, OnLayoutPropertyChanged));

        private double _measuredWidth;

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
            var panel = d as CenteredStickerWrapPanel;
            if (panel != null)
                panel.InvalidateMeasure();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var itemWidth = Normalize(ItemWidth, 76.0);
            var itemHeight = Normalize(ItemHeight, 76.0);

            var width = availableSize.Width;
            if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0)
            {
                width = ActualWidth;
                if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0)
                    width = itemWidth;
            }

            _measuredWidth = width;

            var columns = Math.Max(1, (int)Math.Floor(width / itemWidth));
            var childSize = new Size(itemWidth, itemHeight);

            foreach (var child in Children)
                child.Measure(childSize);

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
            if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0)
                width = _measuredWidth;
            if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0)
                width = itemWidth;

            var columns = Math.Max(1, (int)Math.Floor(width / itemWidth));
            var index = 0;
            var y = 0.0;

            while (index < Children.Count)
            {
                var countInRow = Math.Min(columns, Children.Count - index);

                // Center the actual stickers in this row, not only the panel itself.
                // This is the important difference from ItemsWrapGrid.
                var rowWidth = countInRow * itemWidth;
                var x = Math.Max(0.0, (width - rowWidth) / 2.0);

                for (var i = 0; i < countInRow; i++)
                {
                    Children[index].Arrange(new Rect(x, y, itemWidth, itemHeight));
                    x += itemWidth;
                    index++;
                }

                y += itemHeight;
            }

            return new Size(width, y);
        }

        private static double Normalize(double value, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return fallback;
            return value;
        }
    }
}
