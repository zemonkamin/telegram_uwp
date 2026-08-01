using System;
using Telegram.Models;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Telegram.Controls
{
    public sealed partial class ChatPickerSheet : UserControl
    {
        private const double MaxSheetHeight = 440.0;
        private const double MinSheetHeight = 260.0;
        private double _sheetHeight = MaxSheetHeight;

        private Popup _popup;
        private System.Threading.Tasks.TaskCompletionSource<ChatViewModel> _completion;
        private bool _isShown;
        private bool _isDragging;
        private double _dragStartY;
        private double _dragStartTransformY;
        private Storyboard _sheetStoryboard;

        public ChatPickerSheet()
        {
            InitializeComponent();
            Picker.ChatSelected += Picker_ChatSelected;
        }

        public System.Threading.Tasks.Task<ChatViewModel> ShowAsync(string title)
        {
            if (_isShown && _completion != null)
                return _completion.Task;

            _completion = new System.Threading.Tasks.TaskCompletionSource<ChatViewModel>();
            if (!string.IsNullOrWhiteSpace(title))
                Picker.HeaderText = title;

            UpdatePopupSize();

            _popup = new Popup
            {
                Width = Width,
                Height = Height,
                HorizontalOffset = _popupLeft,
                VerticalOffset = _popupTop,
                Child = this,
                IsLightDismissEnabled = false
            };

            _isShown = true;
            SheetTransform.Y = _sheetHeight;
            _popup.IsOpen = true;

            Window.Current.SizeChanged += Window_SizeChanged;

            var nav = SystemNavigationManager.GetForCurrentView();
            nav.BackRequested += OnBackRequested;

            AnimateTo(0, false, null);
            return _completion.Task;
        }

        private void Picker_ChatSelected(object sender, ChatViewModel chat)
        {
            Hide(chat);
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            Hide(null);
            if (e != null) e.Handled = true;
        }

        private void Hide(ChatViewModel result)
        {
            if (!_isShown) return;
            _isShown = false;

            var nav = SystemNavigationManager.GetForCurrentView();
            nav.BackRequested -= OnBackRequested;

            AnimateTo(_sheetHeight, true, delegate
            {
                ClosePopup();
                Complete(result);
            });
        }

        private void ClosePopup()
        {
            Window.Current.SizeChanged -= Window_SizeChanged;

            if (_popup != null)
            {
                _popup.IsOpen = false;
                _popup.Child = null;
                _popup = null;
            }
        }

        private void Complete(ChatViewModel result)
        {
            var completion = _completion;
            _completion = null;
            if (completion != null)
                completion.TrySetResult(result);
        }

        private void DragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element == null || e == null) return;

            if (!element.CapturePointer(e.Pointer)) return;

            _isDragging = true;
            _dragStartY = e.GetCurrentPoint(element).Position.Y;
            _dragStartTransformY = SheetTransform.Y;
            e.Handled = true;
        }

        private void DragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging || e == null) return;
            var element = sender as UIElement;
            if (element == null) return;

            var currentY = e.GetCurrentPoint(element).Position.Y;
            var delta = currentY - _dragStartY;
            var newY = _dragStartTransformY + delta;

            if (newY < 0) newY = 0;
            if (newY > _sheetHeight) newY = _sheetHeight;

            SheetTransform.Y = newY;
            e.Handled = true;
        }

        private void DragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;

            var element = sender as UIElement;
            if (element != null && e != null)
            {
                try { element.ReleasePointerCapture(e.Pointer); }
                catch { }
            }

            var closeThreshold = Math.Min(120.0, _sheetHeight * 0.35);
            if (SheetTransform.Y >= closeThreshold)
                Hide(null);
            else
                AnimateTo(0, false, null);

            if (e != null) e.Handled = true;
        }

        private void Overlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            Hide(null);
            if (e != null) e.Handled = true;
        }

        private void SheetPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (e != null) e.Handled = true;
        }

        private void StopSheetStoryboard()
        {
            if (_sheetStoryboard != null)
            {
                _sheetStoryboard.Stop();
                _sheetStoryboard = null;
            }
        }

        private void Window_SizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            UpdatePopupSize();
            if (_popup != null)
            {
                _popup.Width = Width;
                _popup.Height = Height;
                _popup.HorizontalOffset = _popupLeft;
                _popup.VerticalOffset = _popupTop;
            }
        }

        private void UpdatePopupSize()
        {
            var bounds = GetVisibleBounds();
            _popupLeft = bounds.Left;
            _popupTop = bounds.Top;

            Width = bounds.Width;
            Height = bounds.Height;
            RootGrid.Width = bounds.Width;
            RootGrid.Height = bounds.Height;

            var availableHeight = bounds.Height - 8.0;
            if (availableHeight < 1) availableHeight = bounds.Height;
            if (availableHeight < 1) availableHeight = MaxSheetHeight;

            _sheetHeight = Math.Min(MaxSheetHeight, availableHeight);
            if (_sheetHeight < MinSheetHeight)
                _sheetHeight = Math.Max(1.0, availableHeight);

            SheetPanel.Height = _sheetHeight;
            if (_isShown && !_isDragging && SheetTransform.Y > 0)
                SheetTransform.Y = _sheetHeight;
        }

        private double _popupLeft;
        private double _popupTop;

        private Rect GetVisibleBounds()
        {
            try
            {
                var visible = ApplicationView.GetForCurrentView().VisibleBounds;
                if (visible.Width > 0 && visible.Height > 0)
                    return visible;
            }
            catch
            {
            }

            return Window.Current.Bounds;
        }

        private void AnimateTo(double targetY, bool closeAfter, EventHandler<object> completed)
        {
            StopSheetStoryboard();

            var fromY = SheetTransform.Y;
            SheetTransform.Y = targetY;

            var animation = new DoubleAnimation
            {
                From = fromY,
                To = targetY,
                Duration = new Duration(TimeSpan.FromMilliseconds(190)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };

            Storyboard.SetTarget(animation, SheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Completed += delegate(object sender, object e)
            {
                if (closeAfter)
                    SheetTransform.Y = _sheetHeight;
                else
                    SheetTransform.Y = targetY;

                if (completed != null) completed(sender, e);
            };

            _sheetStoryboard = storyboard;
            storyboard.Begin();
        }
    }
}
