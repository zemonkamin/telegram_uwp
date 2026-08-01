using System;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Controls
{
    public sealed class ReactionWrapPanel : Panel
    {
        public double ItemSpacing
        {
            get { return (double)GetValue(ItemSpacingProperty); }
            set { SetValue(ItemSpacingProperty, value); }
        }

        public static readonly DependencyProperty ItemSpacingProperty =
            DependencyProperty.Register("ItemSpacing", typeof(double), typeof(ReactionWrapPanel), new PropertyMetadata(6.0, OnLayoutPropertyChanged));

        public double LineSpacing
        {
            get { return (double)GetValue(LineSpacingProperty); }
            set { SetValue(LineSpacingProperty, value); }
        }

        public static readonly DependencyProperty LineSpacingProperty =
            DependencyProperty.Register("LineSpacing", typeof(double), typeof(ReactionWrapPanel), new PropertyMetadata(6.0, OnLayoutPropertyChanged));

        private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var panel = d as ReactionWrapPanel;
            if (panel != null)
                panel.InvalidateMeasure();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var maxWidth = NormalizeWidth(availableSize.Width);
            var x = 0.0;
            var rowHeight = 0.0;
            var totalHeight = 0.0;
            var usedWidth = 0.0;

            for (var i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                if (child == null) continue;

                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                var childWidth = child.DesiredSize.Width;
                var childHeight = child.DesiredSize.Height;

                if (x > 0 && x + ItemSpacing + childWidth > maxWidth)
                {
                    usedWidth = Math.Max(usedWidth, x);
                    totalHeight += rowHeight + LineSpacing;
                    x = 0;
                    rowHeight = 0;
                }

                if (x > 0) x += ItemSpacing;
                x += childWidth;
                rowHeight = Math.Max(rowHeight, childHeight);
            }

            usedWidth = Math.Max(usedWidth, x);
            totalHeight += rowHeight;
            return new Size(Math.Min(usedWidth, maxWidth), totalHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var maxWidth = NormalizeWidth(finalSize.Width);
            var x = 0.0;
            var y = 0.0;
            var rowHeight = 0.0;

            for (var i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                if (child == null) continue;

                var childWidth = child.DesiredSize.Width;
                var childHeight = child.DesiredSize.Height;

                if (x > 0 && x + ItemSpacing + childWidth > maxWidth)
                {
                    x = 0;
                    y += rowHeight + LineSpacing;
                    rowHeight = 0;
                }

                if (x > 0) x += ItemSpacing;
                child.Arrange(new Rect(x, y, childWidth, childHeight));
                x += childWidth;
                rowHeight = Math.Max(rowHeight, childHeight);
            }

            return finalSize;
        }

        private double NormalizeWidth(double width)
        {
            if (!double.IsNaN(width) && !double.IsInfinity(width) && width > 0)
                return Math.Max(1, width);

            // A StackPanel can measure children with infinity. Using the whole window here
            // made reaction chips arrange outside the message bubble. Prefer the panel's
            // actual constrained width; XAML also caps the ItemsControl to BubbleMaxWidth.
            if (!double.IsNaN(ActualWidth) && !double.IsInfinity(ActualWidth) && ActualWidth > 0)
                return Math.Max(1, ActualWidth);

            return 280;
        }
    }
}
