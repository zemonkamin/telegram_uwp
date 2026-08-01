using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.ViewManagement;

namespace Telegram
{
    public sealed partial class Welcome : Page
    {
        private SolidColorBrush _selectedBrush;
        private SolidColorBrush _normalBrush;

        public Welcome()
        {
            InitializeComponent();
            Loaded += Welcome_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (Frame != null)
                Frame.BackStack.Clear();
        }

        private void Welcome_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateThemeBrushes();
            UpdateAdaptiveLayout();
            UpdateDots();
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateAdaptiveLayout();
        }

        private void StartView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDots();
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(MainPage));
        }

        private void UpdateAdaptiveLayout()
        {
            var width = RootGrid == null ? ActualWidth : RootGrid.ActualWidth;
            var height = RootGrid == null ? ActualHeight : RootGrid.ActualHeight;
            if (width <= 0 || height <= 0) return;

            var buttonHeight = 72.0;
            var imageSize = 150.0;
            var titleFontSize = 34.0;
            var bodyFontSize = 18.0;
            var titleTopMargin = 26.0;
            var titleBottomMargin = 8.0;
            var panelTopMargin = -30.0;
            var dotsBottomMargin = 24.0;

            if (height < 560)
            {
                buttonHeight = 64.0;
                imageSize = 124.0;
                titleFontSize = 31.0;
                bodyFontSize = 17.0;
                titleTopMargin = 20.0;
                panelTopMargin = -12.0;
                dotsBottomMargin = 18.0;
            }

            if (height < 480)
            {
                buttonHeight = 58.0;
                imageSize = 96.0;
                titleFontSize = 27.0;
                bodyFontSize = 15.0;
                titleTopMargin = 14.0;
                titleBottomMargin = 5.0;
                panelTopMargin = 0.0;
                dotsBottomMargin = 12.0;
            }

            if (height < 420)
            {
                buttonHeight = 52.0;
                imageSize = 76.0;
                titleFontSize = 24.0;
                bodyFontSize = 14.0;
                titleTopMargin = 10.0;
                titleBottomMargin = 4.0;
                panelTopMargin = 0.0;
                dotsBottomMargin = 8.0;
            }

            var panelWidth = Math.Min(320.0, Math.Max(220.0, width - 32.0));
            StartButtonRow.Height = new GridLength(buttonHeight);
            StartButton.FontSize = height < 420 ? 18.0 : (height < 480 ? 20.0 : 22.0);
            DotsPanel.Margin = new Thickness(0, 0, 0, dotsBottomMargin);

            ApplyIntroPanelLayout(IntroPanel0, IntroImage0, IntroTitle0, IntroText0, panelWidth, panelTopMargin, imageSize, titleFontSize, bodyFontSize, titleTopMargin, titleBottomMargin);
            ApplyIntroPanelLayout(IntroPanel1, IntroImage1, IntroTitle1, IntroText1, panelWidth, panelTopMargin, imageSize, titleFontSize, bodyFontSize, titleTopMargin, titleBottomMargin);
            ApplyIntroPanelLayout(IntroPanel2, IntroImage2, IntroTitle2, IntroText2, panelWidth, panelTopMargin, imageSize, titleFontSize, bodyFontSize, titleTopMargin, titleBottomMargin);
            ApplyIntroPanelLayout(IntroPanel3, IntroImage3, IntroTitle3, IntroText3, panelWidth, panelTopMargin, imageSize, titleFontSize, bodyFontSize, titleTopMargin, titleBottomMargin);
            ApplyIntroPanelLayout(IntroPanel4, IntroImage4, IntroTitle4, IntroText4, panelWidth, panelTopMargin, imageSize, titleFontSize, bodyFontSize, titleTopMargin, titleBottomMargin);
        }

        private static void ApplyIntroPanelLayout(StackPanel panel, Image image, TextBlock title, TextBlock text, double panelWidth, double panelTopMargin, double imageSize, double titleFontSize, double bodyFontSize, double titleTopMargin, double titleBottomMargin)
        {
            if (panel != null)
            {
                panel.Width = panelWidth;
                panel.Margin = new Thickness(0, panelTopMargin, 0, 0);
            }

            if (image != null)
            {
                image.Width = imageSize;
                image.Height = imageSize;
                image.MaxWidth = imageSize;
                image.MaxHeight = imageSize;
            }

            if (title != null)
            {
                title.FontSize = titleFontSize;
                title.Margin = new Thickness(0, titleTopMargin, 0, titleBottomMargin);
            }

            if (text != null)
                text.FontSize = bodyFontSize;
        }

        private void UpdateDots()
        {
            if (_selectedBrush == null || _normalBrush == null)
                UpdateThemeBrushes();

            var index = StartView == null ? 0 : StartView.SelectedIndex;

            Dot0.Fill = index == 0 ? _selectedBrush : _normalBrush;
            Dot1.Fill = index == 1 ? _selectedBrush : _normalBrush;
            Dot2.Fill = index == 2 ? _selectedBrush : _normalBrush;
            Dot3.Fill = index == 3 ? _selectedBrush : _normalBrush;
            Dot4.Fill = index == 4 ? _selectedBrush : _normalBrush;
        }

        private void UpdateThemeBrushes()
        {
            if (IsLightTheme())
            {
                _selectedBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 17, 17, 17));
                _normalBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 170, 170, 170));
            }
            else
            {
                _selectedBrush = new SolidColorBrush(Windows.UI.Colors.White);
                _normalBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 77, 77, 77));
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
    }
}
