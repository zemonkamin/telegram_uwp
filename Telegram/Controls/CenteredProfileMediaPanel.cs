using System;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Controls
{
    /// <summary>
    /// Wraps shared-media tiles and centers every actual row inside the profile width.
    /// Keeps tile dimensions unchanged.
    /// </summary>
    public sealed class CenteredProfileMediaPanel : Panel
    {
        public static readonly DependencyProperty ItemWidthProperty =
            DependencyProperty.Register(
                "ItemWidth",
                typeof(double),
                typeof(CenteredProfileMediaPanel),
                new PropertyMetadata(104.0, OnLayoutPropertyChanged));

        public static readonly DependencyProperty ItemHeightProperty =
            DependencyProperty.Register(
                "ItemHeight",
                typeof(double),
                typeof(CenteredProfileMediaPanel),
                new PropertyMetadata(104.0, OnLayoutPropertyChanged));

        private double _lastWidth;

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
            var panel = d as CenteredProfileMediaPanel;
            if (panel == null) return;
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var itemWidth = Normalize(ItemWidth, 104.0);
            var itemHeight = Normalize(ItemHeight, 104.0);

            var width = availableSize.Width;
            if (IsFinitePositive(width))
                _lastWidth = width;
            else if (IsFinitePositive(_lastWidth))
                width = _lastWidth;
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
            var itemWidth = Normalize(ItemWidth, 104.0);
            var itemHeight = Normalize(ItemHeight, 104.0);

            var width = finalSize.Width;
            if (!IsFinitePositive(width))
                width = IsFinitePositive(_lastWidth) ? _lastWidth : itemWidth;
            else
                _lastWidth = width;

            var columns = Math.Max(1, (int)Math.Floor(width / itemWidth));
            var index = 0;
            var y = 0.0;

            while (index < Children.Count)
            {
                var count = Math.Min(columns, Children.Count - index);
                var rowWidth = count * itemWidth;
                var x = Math.Max(0.0, Math.Floor((width - rowWidth) / 2.0));

                for (var i = 0; i < count; i++)
                {
                    Children[index].Arrange(new Rect(x, y, itemWidth, itemHeight));
                    x += itemWidth;
                    index++;
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
