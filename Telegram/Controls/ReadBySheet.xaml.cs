using System;
using System.Collections.Generic;
using Telegram.Models;
using Telegram.Services;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Telegram.Controls
{
    public sealed partial class ReadBySheet : UserControl
    {
        private const double MaxSheetHeight = 440.0;
        private const double MinSheetHeight = 260.0;

        public static readonly DependencyProperty HeaderTextProperty =
            DependencyProperty.Register("HeaderText", typeof(string), typeof(ReadBySheet), new PropertyMetadata("Read by"));

        private readonly List<ReadBySheetUserItem> _allUsers = new List<ReadBySheetUserItem>();
        private double _sheetHeight = MaxSheetHeight;
        private Popup _popup;
        private System.Threading.Tasks.TaskCompletionSource<CommentAvatarViewModel> _completion;
        private bool _isShown;
        private bool _isDragging;
        private double _dragStartY;
        private double _dragStartTransformY;
        private Storyboard _sheetStoryboard;
        private double _popupLeft;
        private double _popupTop;

        public ReadBySheet()
        {
            InitializeComponent();
        }

        public string HeaderText
        {
            get { return (string)GetValue(HeaderTextProperty); }
            set { SetValue(HeaderTextProperty, value); }
        }

        public System.Threading.Tasks.Task<CommentAvatarViewModel> ShowAsync(ChatViewModel chat, int messageId, string title, IList<CommentAvatarViewModel> cachedUsers)
        {
            if (_isShown && _completion != null)
                return _completion.Task;

            _completion = new System.Threading.Tasks.TaskCompletionSource<CommentAvatarViewModel>();
            HeaderText = string.IsNullOrWhiteSpace(title) ? "Read by" : title;
            SearchBox.Text = string.Empty;
            SetUsers(cachedUsers);
            EmptyText.Text = _allUsers.Count == 0 ? "Loading..." : "";
            EmptyText.Visibility = _allUsers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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
            var ignored = LoadUsersAsync(chat, messageId);
            return _completion.Task;
        }

        private async System.Threading.Tasks.Task LoadUsersAsync(ChatViewModel chat, int messageId)
        {
            if (chat == null || messageId <= 0)
            {
                ApplyFilter();
                return;
            }

            try
            {
                var users = await TelegramService.Instance.GetMessageViewersAsync(chat, messageId, 50);
                if (users != null)
                    SetUsers(users);
            }
            catch
            {
                if (_allUsers.Count == 0)
                {
                    EmptyText.Text = "Could not load viewers.";
                    EmptyText.Visibility = Visibility.Visible;
                }
            }

            ApplyFilter();
        }

        private void SetUsers(IList<CommentAvatarViewModel> users)
        {
            _allUsers.Clear();
            if (users != null)
            {
                for (var i = 0; i < users.Count; i++)
                {
                    var user = users[i];
                    if (user == null || user.PeerId == 0) continue;
                    _allUsers.Add(new ReadBySheetUserItem(user));
                }
            }

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var query = SearchBox != null ? SearchBox.Text : string.Empty;
            List<ReadBySheetUserItem> source;
            if (string.IsNullOrWhiteSpace(query))
            {
                source = new List<ReadBySheetUserItem>(_allUsers);
            }
            else
            {
                source = new List<ReadBySheetUserItem>();
                for (var i = 0; i < _allUsers.Count; i++)
                {
                    var item = _allUsers[i];
                    if (item != null && item.Title != null && item.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        source.Add(item);
                }
            }

            UserList.ItemsSource = source;
            EmptyText.Text = string.IsNullOrWhiteSpace(query) ? "No viewers yet." : "No users found.";
            EmptyText.Visibility = source == null || source.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UserList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as ReadBySheetUserItem;
            if (item == null || item.User == null) return;
            Hide(item.User);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            Hide(null);
            if (e != null) e.Handled = true;
        }

        private void Hide(CommentAvatarViewModel result)
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

        private void Complete(CommentAvatarViewModel result)
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
                StopSheetStoryboard();
                SheetTransform.Y = targetY;
                if (completed != null) completed(sender, e);
            };

            _sheetStoryboard = storyboard;
            storyboard.Begin();
        }

        private sealed class ReadBySheetUserItem
        {
            private readonly CommentAvatarViewModel _user;

            public ReadBySheetUserItem(CommentAvatarViewModel user)
            {
                _user = user;
            }

            public CommentAvatarViewModel User { get { return _user; } }
            public string Title { get { return _user == null || string.IsNullOrWhiteSpace(_user.Title) ? "User" : _user.Title; } }
            public string Initials { get { return _user == null || string.IsNullOrWhiteSpace(_user.Initials) ? "?" : _user.Initials; } }
            public ImageSource AvatarImageSource { get { return _user == null ? null : _user.AvatarImageSource; } }
        }
    }
}
