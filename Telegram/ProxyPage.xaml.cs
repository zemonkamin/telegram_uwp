using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Telegram.Services;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Telegram
{
    public sealed partial class ProxyPage : Page
    {
        private readonly ObservableCollection<ProxyProfile> _proxyProfiles = new ObservableCollection<ProxyProfile>();
        private bool _proxyUiLoading;
        private bool _backRequestedAttached;

        public ProxyPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ConfigureSystemBackButton(true);
            LoadProxyUi();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            PersistProxyProfiles();
            ConfigureSystemBackButton(false);
            base.OnNavigatedFrom(e);
        }

        private void LoadProxyUi()
        {
            _proxyUiLoading = true;
            try
            {
                _proxyProfiles.Clear();
                var stored = ProxyStore.LoadProfiles();
                for (var i = 0; i < stored.Count; i++)
                    _proxyProfiles.Add(stored[i]);

                if (ProxyListView.ItemsSource == null)
                    ProxyListView.ItemsSource = _proxyProfiles;

                ProxyEnabledToggle.IsOn = ProxyStore.Enabled;
                ProxyListSection.Visibility = ProxyStore.Enabled ? Visibility.Visible : Visibility.Collapsed;
                MarkActiveProfile();
            }
            finally
            {
                _proxyUiLoading = false;
            }
        }

        private void ProxyEnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_proxyUiLoading)
                return;

            ProxyStore.Enabled = ProxyEnabledToggle.IsOn;
            ProxyListSection.Visibility = ProxyEnabledToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;

            if (ProxyEnabledToggle.IsOn && string.IsNullOrEmpty(ProxyStore.SelectedId) && _proxyProfiles.Count > 0)
                ProxyStore.SelectedId = _proxyProfiles[0].Id;

            ApplyActiveProxy();
        }

        private void ProxyListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var profile = e.ClickedItem as ProxyProfile;
            if (profile == null)
                return;

            ProxyStore.SelectedId = profile.Id;
            ApplyActiveProxy();
        }

        private async void ProxyAddButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new ProxyEditDialog(null);
                await dialog.ShowAsync();
                if (dialog.Saved && dialog.Result != null)
                {
                    _proxyProfiles.Add(dialog.Result);
                    PersistProxyProfiles();
                    if (string.IsNullOrEmpty(ProxyStore.SelectedId))
                    {
                        ProxyStore.SelectedId = dialog.Result.Id;
                        ApplyActiveProxy();
                    }
                }
            }
            catch (Exception ex)
            {
                ProxyStatusText.Text = "Could not open the proxy editor: " + ex.Message;
            }
        }

        private void ProxyItem_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started)
                return;

            var element = sender as FrameworkElement;
            if (element != null)
            {
                ShowProxyItemMenu(element, element.DataContext as ProxyProfile);
                e.Handled = true;
            }
        }

        private void ProxyItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element != null)
            {
                ShowProxyItemMenu(element, element.DataContext as ProxyProfile);
                e.Handled = true;
            }
        }

        private void ShowProxyItemMenu(FrameworkElement anchor, ProxyProfile profile)
        {
            if (profile == null)
                return;

            var menu = new MenuFlyout();

            var edit = new MenuFlyoutItem { Text = "Edit" };
            edit.Click += async (s, args) => await EditProxyAsync(profile);
            menu.Items.Add(edit);

            var delete = new MenuFlyoutItem { Text = "Delete" };
            delete.Click += async (s, args) => await DeleteProxyAsync(profile);
            menu.Items.Add(delete);

            menu.ShowAt(anchor);
        }

        private async Task EditProxyAsync(ProxyProfile profile)
        {
            var index = _proxyProfiles.IndexOf(profile);
            if (index < 0)
                return;

            try
            {
                var dialog = new ProxyEditDialog(profile);
                await dialog.ShowAsync();
                if (dialog.Saved && dialog.Result != null)
                {
                    _proxyProfiles[index] = dialog.Result;
                    PersistProxyProfiles();
                    if (ProxyStore.SelectedId == dialog.Result.Id)
                        ApplyActiveProxy();
                }
            }
            catch (Exception ex)
            {
                ProxyStatusText.Text = "Could not open the proxy editor: " + ex.Message;
            }
        }

        private async Task DeleteProxyAsync(ProxyProfile profile)
        {
            var confirm = new ContentDialog
            {
                Title = "Delete proxy",
                Content = "Remove " + profile.Title + "?",
                PrimaryButtonText = "Delete",
                SecondaryButtonText = "Cancel"
            };

            var result = await confirm.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            var wasActive = ProxyStore.SelectedId == profile.Id;
            _proxyProfiles.Remove(profile);
            PersistProxyProfiles();

            if (wasActive)
            {
                ProxyStore.SelectedId = _proxyProfiles.Count > 0 ? _proxyProfiles[0].Id : "";
                ApplyActiveProxy();
            }
        }

        private void ApplyActiveProxy()
        {
            if (ProxyStore.Enabled)
            {
                var profile = FindSelectedProfile();
                if (profile != null)
                {
                    TelegramService.Instance.ApplyProxySettings(profile.ToSettings());
                    ProxyStatusText.Text = "Connecting through " + profile.Title + "...";
                }
                else
                {
                    TelegramService.Instance.ApplyProxySettings(new ProxySettings());
                    ProxyStatusText.Text = string.Empty;
                }
            }
            else
            {
                TelegramService.Instance.ApplyProxySettings(new ProxySettings());
                ProxyStatusText.Text = "Using system proxy / VPN";
            }

            MarkActiveProfile();
            var ignored = TelegramService.Instance.ApplyConnectionSettingsAsync();
        }

        private ProxyProfile FindSelectedProfile()
        {
            var id = ProxyStore.SelectedId;
            if (string.IsNullOrEmpty(id))
                return null;

            for (var i = 0; i < _proxyProfiles.Count; i++)
            {
                if (_proxyProfiles[i].Id == id)
                    return _proxyProfiles[i];
            }
            return null;
        }

        private void MarkActiveProfile()
        {
            var activeId = ProxyStore.Enabled ? ProxyStore.SelectedId : null;
            for (var i = 0; i < _proxyProfiles.Count; i++)
                _proxyProfiles[i].IsActive = _proxyProfiles[i].Id == activeId;
        }

        private void PersistProxyProfiles()
        {
            ProxyStore.SaveProfiles(_proxyProfiles);
        }

        private void ConfigureSystemBackButton(bool enabled)
        {
            var navigation = SystemNavigationManager.GetForCurrentView();
            if (navigation == null) return;
            navigation.AppViewBackButtonVisibility = enabled ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
            if (enabled && !_backRequestedAttached)
            {
                navigation.BackRequested += SystemNavigation_BackRequested;
                _backRequestedAttached = true;
            }
            else if (!enabled && _backRequestedAttached)
            {
                navigation.BackRequested -= SystemNavigation_BackRequested;
                _backRequestedAttached = false;
            }
        }

        private void SystemNavigation_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }
    }
}
