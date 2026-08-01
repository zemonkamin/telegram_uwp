using System;
using System.Collections.Generic;
using Telegram.Models;
using Telegram.Services;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Telegram
{
    public sealed partial class ContactsPage : Page
    {
        private static List<ChatViewModel> _cachedContacts;
        private static int _cacheResetVersion;
        private int _appliedCacheResetVersion;
        private bool _backRequestedAttached;

        public static void ClearCache()
        {
            _cachedContacts = null;
            _cacheResetVersion++;
        }

        public ContactsPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ConfigureSystemBackButton(true);
            ApplyExternalCacheReset();
            ApplyCachedContacts();
            await LoadContactsAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StatusBarLoadingIndicator.Hide();
            ConfigureSystemBackButton(false);
            base.OnNavigatedFrom(e);
        }

        private void ConfigureSystemBackButton(bool visible)
        {
            var manager = SystemNavigationManager.GetForCurrentView();
            if (visible)
            {
                if (!_backRequestedAttached)
                {
                    manager.BackRequested += SystemBackButton_BackRequested;
                    _backRequestedAttached = true;
                }
                manager.AppViewBackButtonVisibility = AppViewBackButtonVisibility.Visible;
            }
            else
            {
                if (_backRequestedAttached)
                {
                    manager.BackRequested -= SystemBackButton_BackRequested;
                    _backRequestedAttached = false;
                }
                manager.AppViewBackButtonVisibility = AppViewBackButtonVisibility.Collapsed;
            }
        }

        private void SystemBackButton_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (Frame == null) return;
            e.Handled = true;
            if (Frame.CanGoBack) Frame.GoBack();
            else Frame.Navigate(typeof(Chats));
        }

        private async System.Threading.Tasks.Task LoadContactsAsync()
        {
            SetLoading(true);
            HideStatus();
            try
            {
                var contacts = await TelegramService.Instance.GetContactsAsync();
                SortContacts(contacts);
                _cachedContacts = CopyContacts(contacts);
                ContactList.ItemsSource = contacts;
                if (contacts == null || contacts.Count == 0)
                    ShowStatus("No contacts.");
                else
                    await OfferWindowsContactImportAsync(contacts);
            }
            catch (Exception ex)
            {
                if (_cachedContacts == null || _cachedContacts.Count == 0)
                    ContactList.ItemsSource = null;
                ShowStatus("Contacts error: " + ex.Message);
            }
            SetLoading(false);
        }

        private void ApplyCachedContacts()
        {
            if (_cachedContacts == null || _cachedContacts.Count == 0) return;
            ContactList.ItemsSource = CopyContacts(_cachedContacts);
            HideStatus();
        }

        private void ContactList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var chat = e.ClickedItem as ChatViewModel;
            if (chat == null || Frame == null) return;
            if (AdaptiveShellNavigationService.NavigateChat(chat))
                return;
            Frame.Navigate(typeof(ChatPage), chat);
        }

        private void ListItem_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState == HoldingState.Started)
                e.Handled = true;
        }

        private void ListItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void SetLoading(bool active)
        {
            StatusBarLoadingIndicator.SetActive(active, TopLoadingBar);
        }

        private void ShowStatus(string text)
        {
            StatusText.Text = text ?? string.Empty;
            StatusText.Visibility = string.IsNullOrEmpty(StatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void HideStatus()
        {
            StatusText.Text = string.Empty;
            StatusText.Visibility = Visibility.Collapsed;
        }

        private void ApplyExternalCacheReset()
        {
            if (_appliedCacheResetVersion == _cacheResetVersion) return;
            _appliedCacheResetVersion = _cacheResetVersion;
            if (ContactList != null)
                ContactList.ItemsSource = null;
            HideStatus();
        }

        private async System.Threading.Tasks.Task OfferWindowsContactImportAsync(IList<ChatViewModel> contacts)
        {
            if (!TelegramAppSettings.ContactSyncPromptEnabled)
                return;

            try
            {
                var candidates = await WindowsContactImportService.FindMissingContactsAsync(contacts);
                if (candidates == null || candidates.Count == 0)
                    return;

                var dialog = new ContentDialog();
                dialog.Title = "Add Telegram contacts?";
                dialog.PrimaryButtonText = "Add";
                dialog.SecondaryButtonText = "Not now";
                dialog.Content = BuildContactImportDialogContent(candidates);

                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary)
                    return;

                var added = await WindowsContactImportService.SaveContactsAsync(candidates);
                if (added > 0)
                    ShowStatus("Added " + added.ToString() + " contact" + (added == 1 ? string.Empty : "s") + " to Windows.");
            }
            catch (Exception ex)
            {
                ShowStatus("Windows contacts error: " + ex.Message);
            }
        }

        private static FrameworkElement BuildContactImportDialogContent(IList<SystemContactImportCandidate> contacts)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "These Telegram contacts are not in Windows contacts yet.",
                TextWrapping = TextWrapping.WrapWholeWords,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var listPanel = new StackPanel();
            for (var i = 0; i < contacts.Count; i++)
            {
                var row = BuildContactImportRow(contacts[i]);
                if (row != null)
                    listPanel.Children.Add(row);
            }

            panel.Children.Add(new ScrollViewer
            {
                MaxHeight = 320,
                Content = listPanel
            });

            return panel;
        }

        private static FrameworkElement BuildContactImportRow(SystemContactImportCandidate contact)
        {
            if (contact == null) return null;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var avatar = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(Color.FromArgb(255, 0, 120, 215)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(contact.Initials) ? "?" : contact.Initials,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            var avatarBrush = BuildContactAvatarBrush(contact);
            if (avatarBrush != null)
            {
                avatar.Background = avatarBrush;
                avatar.Child = null;
            }
            Grid.SetColumn(avatar, 0);
            row.Children.Add(avatar);

            var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(contact.DisplayName) ? "Telegram contact" : contact.DisplayName,
                FontSize = 15,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = BuildContactImportSubtitle(contact),
                FontSize = 12,
                Opacity = 0.72,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(textPanel, 1);
            row.Children.Add(textPanel);

            return row;
        }

        private static Brush BuildContactAvatarBrush(SystemContactImportCandidate contact)
        {
            if (contact == null || contact.Chat == null || string.IsNullOrWhiteSpace(contact.Chat.AvatarUri))
                return null;

            try
            {
                var image = new BitmapImage();
                image.DecodePixelWidth = 64;
                image.UriSource = new Uri(contact.Chat.AvatarUri);
                return new ImageBrush
                {
                    ImageSource = image,
                    Stretch = Stretch.UniformToFill
                };
            }
            catch
            {
                return null;
            }
        }

        private static string BuildContactImportSubtitle(SystemContactImportCandidate contact)
        {
            if (contact == null) return string.Empty;
            var subtitle = contact.Phone ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(contact.Username))
                subtitle += "  @" + contact.Username.TrimStart('@');
            return subtitle.Trim();
        }

        private static void SortContacts(IList<ChatViewModel> contacts)
        {
            var list = contacts as List<ChatViewModel>;
            if (list == null || list.Count < 2) return;
            list.Sort(delegate(ChatViewModel a, ChatViewModel b)
            {
                var at = a == null ? string.Empty : (a.Title ?? string.Empty);
                var bt = b == null ? string.Empty : (b.Title ?? string.Empty);
                return string.Compare(at, bt, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static List<ChatViewModel> CopyContacts(IList<ChatViewModel> source)
        {
            var result = new List<ChatViewModel>();
            if (source == null) return result;
            for (var i = 0; i < source.Count; i++)
                if (source[i] != null) result.Add(source[i]);
            return result;
        }
    }
}
