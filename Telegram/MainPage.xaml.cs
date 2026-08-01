using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Services;
using Windows.Graphics.Display;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Shapes;

namespace Telegram
{
    public sealed partial class MainPage : Page
    {
        private CancellationTokenSource _loginLoopCts;
        private Task _loginLoopTask;
        private bool _isNavigatingToChats;
        private bool _isStartingQrLogin;
        private bool _isSendingCode;
        private bool _isSigningIn;
        private bool _isCheckingCloudPassword;
        private bool _isConnecting;
        private string _currentQrLink = string.Empty;
        private CountryInfo _selectedCountry;
        private readonly ObservableCollection<ProxyProfile> _proxyProfiles = new ObservableCollection<ProxyProfile>();
        private bool _proxyUiLoading;
        private Grid _currentPanel;
        private bool _backRegistered;

        public MainPage()
        {
            InitializeComponent();
            InitializeCountryList();
            Loaded += MainPage_Loaded;
            Unloaded += Login_Unloaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isNavigatingToChats)
                return;

            RegisterBackButton();
            ShowView(PhonePanel);
            SetQrLoading(true);
            _currentQrLink = string.Empty;
            QrStatusText.Text = string.Empty;
            TelegramService.Instance.Start();
            ApplyStartupProxy();
            SetPhoneCodeEntryMode(CodePanel.Visibility == Visibility.Visible);
            UpdateAdaptiveLayout();
            var ignored = PrewarmConnectionAsync();
            try
            {
                PhoneNumberBox.Focus(FocusState.Programmatic);
            }
            catch
            {
            }
        }

        // Connect to Telegram servers eagerly while the user is still typing so that
        // "Continue" and "Use QR code" do not block on the first network round-trip.
        private async Task PrewarmConnectionAsync()
        {
            if (_isConnecting)
                return;

            _isConnecting = true;
            try
            {
                var authorized = await Task.Run(async () => await TelegramService.Instance.IsAuthorizedAsync());
                if (authorized && !_isNavigatingToChats)
                    NavigateToChatsOnce();
            }
            catch
            {
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private void ShowView(Grid view)
        {
            PhonePanel.Visibility = view == PhonePanel ? Visibility.Visible : Visibility.Collapsed;
            QrPanel.Visibility = view == QrPanel ? Visibility.Visible : Visibility.Collapsed;
            PasswordPanel.Visibility = view == PasswordPanel ? Visibility.Visible : Visibility.Collapsed;
            ProxyPanel.Visibility = view == ProxyPanel ? Visibility.Visible : Visibility.Collapsed;

            _currentPanel = view;
            AnimateIn(view);
            UpdateBackButton();
        }

        // Slide + fade the incoming panel so switching views feels like a page transition.
        private void AnimateIn(Grid view)
        {
            if (view == null)
                return;

            var transform = view.RenderTransform as TranslateTransform;
            if (transform == null)
            {
                transform = new TranslateTransform();
                view.RenderTransform = transform;
            }

            var storyboard = new Storyboard();

            var fade = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200))
            };
            Storyboard.SetTarget(fade, view);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);

            var slide = new DoubleAnimation
            {
                From = 32.0,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(260)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slide, transform);
            Storyboard.SetTargetProperty(slide, "X");
            storyboard.Children.Add(slide);

            try
            {
                storyboard.Begin();
            }
            catch
            {
                view.Opacity = 1.0;
                transform.X = 0.0;
            }
        }

        private void RegisterBackButton()
        {
            if (_backRegistered)
                return;

            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            _backRegistered = true;
        }

        private void UnregisterBackButton()
        {
            if (!_backRegistered)
                return;

            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            _backRegistered = false;
        }

        private void UpdateBackButton()
        {
            try
            {
                var canGoBack = (_currentPanel != null && _currentPanel != PhonePanel) ||
                                (Frame != null && Frame.CanGoBack);
                SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                    canGoBack ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
            }
            catch
            {
            }
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled || _isNavigatingToChats)
                return;

            if (_currentPanel != null && _currentPanel != PhonePanel)
            {
                e.Handled = true;
                if (_currentPanel == ProxyPanel)
                    PersistProxyProfiles();
                if (_currentPanel == QrPanel)
                    StopLoginLoop();
                GoToPhonePanel();
                return;
            }

            if (Frame != null && Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }

        private void GoToPhonePanel()
        {
            ShowView(PhonePanel);
            SetPhoneCodeEntryMode(false);
            CloudPasswordBox.Password = string.Empty;
            QrStatusText.Text = string.Empty;
            try
            {
                PhoneNumberBox.Focus(FocusState.Programmatic);
            }
            catch
            {
            }
        }

        private void SetQrLoading(bool loading)
        {
            QrProgressRing.IsActive = loading;
            QrLoadingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateAdaptiveLayout();
        }

        private void UpdateAdaptiveLayout()
        {
            var height = ActualHeight;
            var width = ActualWidth;
            if (height <= 0 || width <= 0)
                return;

            // Shrink the QR block and headers on short screens so the layout never
            // overflows the phone canvas.
            var qrSize = 280.0;
            if (height < 560)
                qrSize = 240.0;
            if (height < 480)
                qrSize = 210.0;
            if (height < 420)
                qrSize = 180.0;

            var available = Math.Max(140.0, width - 60.0);
            if (qrSize > available)
                qrSize = available;

            if (QrContainer != null)
            {
                QrContainer.Width = qrSize;
                QrContainer.Height = qrSize;
            }
            if (QrCodeCanvas != null)
            {
                QrCodeCanvas.Width = qrSize;
                QrCodeCanvas.Height = qrSize;
            }

            if (!string.IsNullOrEmpty(_currentQrLink) && _currentQrLink != "authorized")
                LocalQrBitmap.Draw(QrCodeCanvas, _currentQrLink, (int)qrSize);
        }

        private void Login_Unloaded(object sender, RoutedEventArgs e)
        {
            StopLoginLoop();
            UnregisterBackButton();
        }

        private void PhoneNumberBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                SendCodeButton_Click(sender, e);
            }
        }

        private void CodeBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                var ignored = SignInWithCodeAsync();
            }
        }

        private void CloudPasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                CloudPasswordButton_Click(sender, e);
            }
        }

        private async void SendCodeButton_Click(object sender, RoutedEventArgs e)
        {
            if (CodePanel.Visibility == Visibility.Visible)
            {
                await SignInWithCodeAsync();
                return;
            }

            if (_isNavigatingToChats || _isSendingCode)
                return;

            StopLoginLoop();
            ShowView(PhonePanel);
            CloudPasswordBox.Password = string.Empty;
            QrStatusText.Text = string.Empty;

            var phoneNumber = BuildPhoneNumber();
            if (string.IsNullOrEmpty(phoneNumber) || phoneNumber.Length < 8 || phoneNumber[0] != '+')
            {
                StatusText.Text = "Enter the phone number with country code, for example +33123456789.";
                return;
            }

            _isSendingCode = true;
            SendCodeButton.IsEnabled = false;
            SignInButton.IsEnabled = false;
            ShowQrButton.IsEnabled = false;
            StatusText.Text = "Sending Telegram auth.sendCode request...";

            try
            {
                var sent = await Task.Run(async () => await TelegramService.Instance.SendPhoneCodeAsync(phoneNumber));
                if (sent != null && sent.Authorized)
                {
                    StatusText.Text = "This session is already authorized. Loading chats...";
                    NavigateToChatsOnce();
                    return;
                }

                SetPhoneCodeEntryMode(true);
                CloudPasswordBox.Password = string.Empty;
                SignInButton.IsEnabled = true;
                StatusText.Text = sent == null || string.IsNullOrEmpty(sent.Message)
                    ? "Code sent. Enter the code from Telegram."
                    : sent.Message;

                try
                {
                    CodeBox.Focus(FocusState.Programmatic);
                }
                catch
                {
                }
            }
            catch (TelegramCloudPasswordRequiredException ex)
            {
                ShowPasswordView(ex.Message);
            }
            catch (Exception ex)
            {
                if (!_isNavigatingToChats)
                    StatusText.Text = "Code request error: " + ToUserMessage(ex);
            }
            finally
            {
                _isSendingCode = false;
                if (!_isNavigatingToChats)
                {
                    SendCodeButton.IsEnabled = true;
                    ShowQrButton.IsEnabled = true;
                }
            }
        }

        private async void SignInButton_Click(object sender, RoutedEventArgs e)
        {
            await SignInWithCodeAsync();
        }

        private async Task SignInWithCodeAsync()
        {
            if (_isNavigatingToChats || _isSigningIn)
                return;

            var code = NormalizeCode(CodeBox.Text);
            if (string.IsNullOrEmpty(code))
            {
                StatusText.Text = "Enter the code from Telegram.";
                return;
            }

            _isSigningIn = true;
            SignInButton.IsEnabled = false;
            SendCodeButton.IsEnabled = false;
            ShowQrButton.IsEnabled = false;
            StatusText.Text = "Checking the code through Telegram auth.signIn...";

            try
            {
                await Task.Run(async () => await TelegramService.Instance.SignInWithPhoneCodeAsync(code));
                StatusText.Text = "Login completed. Loading chats...";
                NavigateToChatsOnce();
            }
            catch (TelegramCloudPasswordRequiredException ex)
            {
                ShowPasswordView(ex.Message);
            }
            catch (Exception ex)
            {
                if (!_isNavigatingToChats)
                    StatusText.Text = "Login error: " + ToUserMessage(ex);
            }
            finally
            {
                _isSigningIn = false;
                if (!_isNavigatingToChats)
                {
                    SignInButton.IsEnabled = true;
                    SendCodeButton.IsEnabled = true;
                    ShowQrButton.IsEnabled = true;
                }
            }
        }

        private void ShowPasswordView(string message)
        {
            StopLoginLoop();
            ShowView(PasswordPanel);
            CloudPasswordButton.IsEnabled = true;
            PasswordStatusText.Text = string.IsNullOrEmpty(message)
                ? "Two-step verification is enabled. Enter your Telegram cloud password."
                : message;
            try
            {
                CloudPasswordBox.Focus(FocusState.Programmatic);
            }
            catch
            {
            }
        }

        private void SetPhoneCodeEntryMode(bool codeEntry)
        {
            CodePanel.Visibility = codeEntry ? Visibility.Visible : Visibility.Collapsed;
            SignInButton.Visibility = Visibility.Collapsed;
            SendCodeButton.Content = codeEntry ? "Sign in" : "Continue";
        }

        private async void CloudPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isNavigatingToChats || _isCheckingCloudPassword)
                return;

            var password = CloudPasswordBox.Password;
            if (string.IsNullOrWhiteSpace(password))
            {
                PasswordStatusText.Text = "Enter the Telegram cloud password.";
                return;
            }

            _isCheckingCloudPassword = true;
            CloudPasswordButton.IsEnabled = false;
            PasswordStatusText.Text = "Checking cloud password...";

            try
            {
                await Task.Run(async () => await TelegramService.Instance.SignInWithCloudPasswordAsync(password));
                CloudPasswordBox.Password = string.Empty;
                PasswordStatusText.Text = "Login completed. Loading chats...";
                NavigateToChatsOnce();
            }
            catch (Exception ex)
            {
                if (!_isNavigatingToChats)
                    PasswordStatusText.Text = "Cloud password error: " + ToUserMessage(ex);
            }
            finally
            {
                _isCheckingCloudPassword = false;
                if (!_isNavigatingToChats)
                    CloudPasswordButton.IsEnabled = true;
            }
        }

        private void ForgotPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            PasswordStatusText.Text = "To reset the cloud password, open Telegram on another logged-in device, or wait for account recovery. You can also go back and sign in with a QR code.";
        }

        private async void ShowQrButton_Click(object sender, RoutedEventArgs e)
        {
            await StartQrLoginAsync();
        }

        private void UsePhoneButton_Click(object sender, RoutedEventArgs e)
        {
            StopLoginLoop();
            ShowView(PhonePanel);
            SetPhoneCodeEntryMode(false);
            CodeBox.Text = string.Empty;
            CloudPasswordBox.Password = string.Empty;
            QrStatusText.Text = string.Empty;
            try
            {
                PhoneNumberBox.Focus(FocusState.Programmatic);
            }
            catch
            {
            }
        }

        private void ProxyOpenButton_Click(object sender, RoutedEventArgs e)
        {
            LoadProxyUi();
            ProxyStatusText.Text = string.Empty;
            ShowView(ProxyPanel);
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
                ProxyStatusText.Text = "Could not open the proxy editor: " + ToUserMessage(ex);
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
                ProxyStatusText.Text = "Could not open the proxy editor: " + ToUserMessage(ex);
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
            var ignored = PrewarmConnectionAsync();
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

        private void ApplyStartupProxy()
        {
            if (ProxyStore.Enabled)
            {
                var profiles = ProxyStore.LoadProfiles();
                var id = ProxyStore.SelectedId;
                ProxyProfile selected = null;
                for (var i = 0; i < profiles.Count; i++)
                {
                    if (profiles[i].Id == id)
                    {
                        selected = profiles[i];
                        break;
                    }
                }
                if (selected == null && profiles.Count > 0)
                    selected = profiles[0];

                if (selected != null)
                {
                    TelegramService.Instance.ApplyProxySettings(selected.ToSettings());
                    return;
                }
            }

            TelegramService.Instance.ApplyProxySettings(new ProxySettings());
        }

        private async Task StartQrLoginAsync()
        {
            if (_isNavigatingToChats || _isStartingQrLogin)
                return;

            _isStartingQrLogin = true;
            StopLoginLoop();
            ShowView(QrPanel);
            _currentQrLink = string.Empty;
            LocalQrBitmap.Clear(QrCodeCanvas);
            SetQrLoading(true);
            QrStatusText.Text = "Connecting to Telegram...";
            // Keep "Use phone number" enabled so the screen is never stuck while the
            // first server round-trip is still in flight.

            try
            {
                var qr = await Task.Run(async () => await TelegramService.Instance.CreateQrLoginAsync());

                if (qr != null && qr.LoginUrl == "authorized")
                {
                    NavigateToChatsOnce();
                    return;
                }

                ShowQr(qr);
                QrStatusText.Text = "Scan the QR code from Telegram.";

                _loginLoopCts = new CancellationTokenSource();
                var token = _loginLoopCts.Token;
                _loginLoopTask = PollLoginAsync(token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TelegramCloudPasswordRequiredException ex)
            {
                ShowPasswordView(ex.Message);
            }
            catch (Exception ex)
            {
                if (!_isNavigatingToChats)
                {
                    SetQrLoading(false);
                    QrStatusText.Text = "QR login error: " + ToUserMessage(ex);
                }
            }
            finally
            {
                _isStartingQrLogin = false;
            }
        }

        private async Task PollLoginAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    token.ThrowIfCancellationRequested();

                    var status = await Task.Run(async () => await TelegramService.Instance.CheckQrLoginAsync());
                    token.ThrowIfCancellationRequested();

                    if (status == QrLoginState.Accepted)
                    {
                        NavigateToChatsOnce();
                        return;
                    }

                    var currentQr = TelegramService.Instance.CurrentQr;
                    if (currentQr != null && currentQr.LoginUrl != _currentQrLink)
                    {
                        ShowQr(currentQr);
                    }

                    if (status == QrLoginState.TokenExpired)
                    {
                        var qr = await Task.Run(async () => await TelegramService.Instance.CreateQrLoginAsync());
                        token.ThrowIfCancellationRequested();
                        ShowQr(qr);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (TelegramCloudPasswordRequiredException ex)
            {
                if (!token.IsCancellationRequested)
                    ShowPasswordView(ex.Message);
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    QrStatusText.Text = "QR login polling error: " + ToUserMessage(ex);
            }
        }

        private void ShowQr(QrLoginInfo qr)
        {
            if (qr == null)
            {
                _currentQrLink = string.Empty;
                LocalQrBitmap.Clear(QrCodeCanvas);
                return;
            }

            _currentQrLink = qr.LoginUrl;

            if (string.IsNullOrEmpty(qr.LoginUrl) || qr.LoginUrl == "authorized")
            {
                LocalQrBitmap.Clear(QrCodeCanvas);
                return;
            }

            // Render the QR as XAML vector rectangles instead of WriteableBitmap.
            // Windows 10 Mobile can corrupt or resample WriteableBitmap QR modules on some devices.
            var size = QrCodeCanvas.Width > 0 ? (int)QrCodeCanvas.Width : 280;
            LocalQrBitmap.Draw(QrCodeCanvas, qr.LoginUrl, size);
            SetQrLoading(false);
        }

        private void StopLoginLoop()
        {
            if (_loginLoopCts != null)
            {
                _loginLoopCts.Cancel();
                _loginLoopCts.Dispose();
                _loginLoopCts = null;
            }

            _loginLoopTask = null;
        }

        private void NavigateToChatsOnce()
        {
            if (_isNavigatingToChats)
                return;

            _isNavigatingToChats = true;
            StopLoginLoop();

            SendCodeButton.IsEnabled = false;
            SignInButton.IsEnabled = false;
            CloudPasswordButton.IsEnabled = false;
            ShowQrButton.IsEnabled = false;
            UsePhoneButton.IsEnabled = false;

            if (Frame != null)
            {
                Frame.Navigate(typeof(AdaptiveShellPage));
            }
        }

        private void InitializeCountryList()
        {
            var country = CountryCatalog.FindByName("France");
            if (country == null && CountryCatalog.All.Count > 0)
                country = CountryCatalog.All[0];
            ApplyCountry(country);
        }

        private async void CountrySelectButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CountryPickerDialog(_selectedCountry);
            await dialog.ShowAsync();
            if (dialog.Picked && dialog.SelectedCountry != null)
            {
                ApplyCountry(dialog.SelectedCountry);
                try
                {
                    PhoneNumberBox.Focus(FocusState.Programmatic);
                }
                catch
                {
                }
            }
        }

        private void ApplyCountry(CountryInfo country)
        {
            if (country == null)
                return;

            _selectedCountry = country;
            CountrySelectText.Text = country.Name;
            PhoneCodeBox.Text = country.PhoneCode;
            PhoneNumberBox.PlaceholderText = country.Example;
        }

        private string BuildPhoneNumber()
        {
            var countryCode = NormalizePhoneNumber(PhoneCodeBox.Text);
            var localNumber = NormalizePhoneNumber(PhoneNumberBox.Text);

            if (string.IsNullOrEmpty(localNumber))
                return string.Empty;

            if (localNumber[0] == '+')
                return localNumber;

            if (string.IsNullOrEmpty(countryCode))
                return localNumber;

            if (countryCode[0] != '+')
                countryCode = "+" + countryCode;

            return countryCode + localNumber;
        }

        private static string NormalizePhoneNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var sb = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsDigit(c)) sb.Append(c);
                else if (c == '+' && sb.Length == 0) sb.Append(c);
            }

            return sb.ToString();
        }

        private static string NormalizeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var sb = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsDigit(c) || char.IsLetter(c)) sb.Append(c);
            }

            return sb.ToString();
        }

        private static string ToUserMessage(Exception ex)
        {
            if (ex == null) return "unknown error";
            return ex.Message;
        }

        private static class LocalQrBitmap
        {
            private const int QuietZone = 4;

            private static readonly QrSpec[] Specs = new[]
            {
                new QrSpec(4, 80, 1, 80, 20, new[] { 6, 26 }, 78),
                new QrSpec(5, 108, 1, 108, 26, new[] { 6, 30 }, 106),
                new QrSpec(6, 136, 2, 68, 18, new[] { 6, 34 }, 134)
            };

            public static void Draw(Canvas canvas, string text, int layoutPixelSize)
            {
                if (canvas == null)
                    return;

                var dataBytes = Encoding.UTF8.GetBytes(text);
                var spec = ChooseSpec(dataBytes.Length);
                var modules = EncodeBytes(dataBytes, spec);
                Render(canvas, modules, spec, layoutPixelSize);
            }

            public static void Clear(Canvas canvas)
            {
                if (canvas != null)
                    canvas.Children.Clear();
            }

            private static QrSpec ChooseSpec(int dataLength)
            {
                for (var i = 0; i < Specs.Length; i++)
                {
                    if (dataLength <= Specs[i].MaxInputBytes)
                        return Specs[i];
                }

                throw new InvalidOperationException("QR token is too long for the built-in QR renderer.");
            }

            private static bool[,] EncodeBytes(byte[] dataBytes, QrSpec spec)
            {
                var dataCodewords = CreateDataCodewords(dataBytes, spec);
                var allCodewords = AddErrorCorrection(dataCodewords, spec);

                var modules = new bool[spec.Size, spec.Size];
                var isFunction = new bool[spec.Size, spec.Size];
                DrawFunctionPatterns(modules, isFunction, spec);
                DrawCodewords(modules, isFunction, allCodewords, spec);

                // Use one deterministic mask instead of choosing a mask by penalty.
                // On Windows 10 Mobile some scanner/camera combinations fail on dense auto-selected
                // masks even when the QR payload itself is correct. Mask 0 keeps the matrix stable
                // across desktop and ARM/mobile builds.
                const int fixedMask = 0;
                ApplyMask(modules, isFunction, fixedMask, spec);
                DrawFormatBits(modules, fixedMask, spec);
                return modules;
            }

            private static byte[] CreateDataCodewords(byte[] dataBytes, QrSpec spec)
            {
                var bits = new BitBuffer();
                bits.AppendBits(0x4, 4); // byte mode
                bits.AppendBits(dataBytes.Length, 8); // versions 1-9 use 8 length bits in byte mode

                for (var i = 0; i < dataBytes.Length; i++)
                    bits.AppendBits(dataBytes[i] & 0xFF, 8);

                var capacityBits = spec.DataCodewords * 8;
                if (bits.Count > capacityBits)
                    throw new InvalidOperationException("QR token is too long for the built-in QR renderer.");

                var terminator = Math.Min(4, capacityBits - bits.Count);
                bits.AppendBits(0, terminator);

                while (bits.Count % 8 != 0)
                    bits.AppendBits(0, 1);

                var result = bits.ToByteArray();
                var codewords = new List<byte>(result);
                var pad = true;
                while (codewords.Count < spec.DataCodewords)
                {
                    codewords.Add((byte)(pad ? 0xEC : 0x11));
                    pad = !pad;
                }

                return codewords.ToArray();
            }

            private static byte[] AddErrorCorrection(byte[] dataCodewords, QrSpec spec)
            {
                var divisor = ReedSolomonComputeDivisor(spec.EccCodewordsPerBlock);
                var dataBlocks = new byte[spec.BlockCount][];
                var eccBlocks = new byte[spec.BlockCount][];

                for (var block = 0; block < spec.BlockCount; block++)
                {
                    dataBlocks[block] = new byte[spec.DataCodewordsPerBlock];
                    Array.Copy(dataCodewords, block * spec.DataCodewordsPerBlock, dataBlocks[block], 0, spec.DataCodewordsPerBlock);
                    eccBlocks[block] = ReedSolomonComputeRemainder(dataBlocks[block], divisor);
                }

                var result = new List<byte>();
                for (var i = 0; i < spec.DataCodewordsPerBlock; i++)
                {
                    for (var block = 0; block < spec.BlockCount; block++)
                        result.Add(dataBlocks[block][i]);
                }

                for (var i = 0; i < spec.EccCodewordsPerBlock; i++)
                {
                    for (var block = 0; block < spec.BlockCount; block++)
                        result.Add(eccBlocks[block][i]);
                }

                return result.ToArray();
            }

            private static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunction, QrSpec spec)
            {
                DrawFinderPattern(modules, isFunction, 0, 0, spec);
                DrawFinderPattern(modules, isFunction, spec.Size - 7, 0, spec);
                DrawFinderPattern(modules, isFunction, 0, spec.Size - 7, spec);

                for (var i = 8; i < spec.Size - 8; i++)
                {
                    var dark = i % 2 == 0;
                    SetFunctionModule(modules, isFunction, 6, i, dark);
                    SetFunctionModule(modules, isFunction, i, 6, dark);
                }

                for (var i = 0; i < spec.AlignmentCenters.Length; i++)
                {
                    for (var j = 0; j < spec.AlignmentCenters.Length; j++)
                    {
                        var x = spec.AlignmentCenters[i];
                        var y = spec.AlignmentCenters[j];
                        if (!isFunction[y, x])
                            DrawAlignmentPattern(modules, isFunction, x, y);
                    }
                }

                ReserveFormatBits(modules, isFunction, spec);
            }

            private static void DrawFinderPattern(bool[,] modules, bool[,] isFunction, int left, int top, QrSpec spec)
            {
                for (var dy = -1; dy <= 7; dy++)
                {
                    for (var dx = -1; dx <= 7; dx++)
                    {
                        var x = left + dx;
                        var y = top + dy;
                        if (x < 0 || x >= spec.Size || y < 0 || y >= spec.Size)
                            continue;

                        var dark = dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6 &&
                                   (dx == 0 || dx == 6 || dy == 0 || dy == 6 ||
                                    (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4));
                        SetFunctionModule(modules, isFunction, x, y, dark);
                    }
                }
            }

            private static void DrawAlignmentPattern(bool[,] modules, bool[,] isFunction, int centerX, int centerY)
            {
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        SetFunctionModule(modules, isFunction, centerX + dx, centerY + dy, distance != 1);
                    }
                }
            }

            private static void ReserveFormatBits(bool[,] modules, bool[,] isFunction, QrSpec spec)
            {
                for (var i = 0; i <= 8; i++)
                {
                    if (i != 6)
                    {
                        SetFunctionModule(modules, isFunction, 8, i, false);
                        SetFunctionModule(modules, isFunction, i, 8, false);
                    }
                }

                for (var i = 0; i < 8; i++)
                {
                    SetFunctionModule(modules, isFunction, spec.Size - 1 - i, 8, false);
                    SetFunctionModule(modules, isFunction, 8, spec.Size - 1 - i, false);
                }

                SetFunctionModule(modules, isFunction, 8, spec.Size - 8, true);
            }

            private static void DrawFormatBits(bool[,] modules, int mask, QrSpec spec)
            {
                var data = (1 << 3) | mask; // error correction level L
                var rem = data;
                for (var i = 0; i < 10; i++)
                    rem = (rem << 1) ^ (((rem >> 9) & 1) != 0 ? 0x537 : 0);

                var bits = ((data << 10) | rem) ^ 0x5412;

                for (var i = 0; i <= 5; i++)
                    SetModule(modules, 8, i, GetBit(bits, i));
                SetModule(modules, 8, 7, GetBit(bits, 6));
                SetModule(modules, 8, 8, GetBit(bits, 7));
                SetModule(modules, 7, 8, GetBit(bits, 8));
                for (var i = 9; i < 15; i++)
                    SetModule(modules, 14 - i, 8, GetBit(bits, i));

                for (var i = 0; i < 8; i++)
                    SetModule(modules, spec.Size - 1 - i, 8, GetBit(bits, i));
                for (var i = 8; i < 15; i++)
                    SetModule(modules, 8, spec.Size - 15 + i, GetBit(bits, i));
                SetModule(modules, 8, spec.Size - 8, true);
            }

            private static void DrawCodewords(bool[,] modules, bool[,] isFunction, byte[] data, QrSpec spec)
            {
                var bitIndex = 0;
                var upward = true;

                for (var right = spec.Size - 1; right >= 1; right -= 2)
                {
                    if (right == 6)
                        right = 5;

                    for (var vertical = 0; vertical < spec.Size; vertical++)
                    {
                        var y = upward ? spec.Size - 1 - vertical : vertical;
                        for (var j = 0; j < 2; j++)
                        {
                            var x = right - j;
                            if (isFunction[y, x])
                                continue;

                            var dark = false;
                            if (bitIndex < data.Length * 8)
                                dark = ((data[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;

                            modules[y, x] = dark;
                            bitIndex++;
                        }
                    }

                    upward = !upward;
                }
            }

            private static void ApplyMask(bool[,] modules, bool[,] isFunction, int mask, QrSpec spec)
            {
                for (var y = 0; y < spec.Size; y++)
                {
                    for (var x = 0; x < spec.Size; x++)
                    {
                        if (!isFunction[y, x] && GetMaskBit(mask, x, y))
                            modules[y, x] = !modules[y, x];
                    }
                }
            }

            private static bool GetMaskBit(int mask, int x, int y)
            {
                switch (mask)
                {
                    case 0: return (x + y) % 2 == 0;
                    case 1: return y % 2 == 0;
                    case 2: return x % 3 == 0;
                    case 3: return (x + y) % 3 == 0;
                    case 4: return (x / 3 + y / 2) % 2 == 0;
                    case 5: return x * y % 2 + x * y % 3 == 0;
                    case 6: return (x * y % 2 + x * y % 3) % 2 == 0;
                    case 7: return ((x + y) % 2 + x * y % 3) % 2 == 0;
                    default: throw new ArgumentOutOfRangeException("mask");
                }
            }

            private static int GetPenaltyScore(bool[,] modules, QrSpec spec)
            {
                var result = 0;

                for (var y = 0; y < spec.Size; y++)
                {
                    var runColor = false;
                    var runLength = 0;
                    for (var x = 0; x < spec.Size; x++)
                    {
                        if (x == 0 || modules[y, x] != runColor)
                        {
                            if (runLength >= 5)
                                result += runLength - 2;
                            runColor = modules[y, x];
                            runLength = 1;
                        }
                        else
                        {
                            runLength++;
                        }
                    }
                    if (runLength >= 5)
                        result += runLength - 2;
                }

                for (var x = 0; x < spec.Size; x++)
                {
                    var runColor = false;
                    var runLength = 0;
                    for (var y = 0; y < spec.Size; y++)
                    {
                        if (y == 0 || modules[y, x] != runColor)
                        {
                            if (runLength >= 5)
                                result += runLength - 2;
                            runColor = modules[y, x];
                            runLength = 1;
                        }
                        else
                        {
                            runLength++;
                        }
                    }
                    if (runLength >= 5)
                        result += runLength - 2;
                }

                for (var y = 0; y < spec.Size - 1; y++)
                {
                    for (var x = 0; x < spec.Size - 1; x++)
                    {
                        var color = modules[y, x];
                        if (color == modules[y, x + 1] && color == modules[y + 1, x] && color == modules[y + 1, x + 1])
                            result += 3;
                    }
                }

                for (var y = 0; y < spec.Size; y++)
                {
                    for (var x = 0; x <= spec.Size - 7; x++)
                    {
                        if (HasFinderLikePattern(modules, x, y, true, spec))
                            result += 40;
                    }
                }

                for (var x = 0; x < spec.Size; x++)
                {
                    for (var y = 0; y <= spec.Size - 7; y++)
                    {
                        if (HasFinderLikePattern(modules, x, y, false, spec))
                            result += 40;
                    }
                }

                var dark = 0;
                for (var y = 0; y < spec.Size; y++)
                {
                    for (var x = 0; x < spec.Size; x++)
                    {
                        if (modules[y, x])
                            dark++;
                    }
                }

                var total = spec.Size * spec.Size;
                var k = Math.Abs(dark * 20 - total * 10) / total;
                result += k * 10;

                return result;
            }

            private static bool HasFinderLikePattern(bool[,] modules, int x, int y, bool horizontal, QrSpec spec)
            {
                if (!GetPatternModule(modules, x, y, 0, horizontal)) return false;
                if (GetPatternModule(modules, x, y, 1, horizontal)) return false;
                if (!GetPatternModule(modules, x, y, 2, horizontal)) return false;
                if (!GetPatternModule(modules, x, y, 3, horizontal)) return false;
                if (!GetPatternModule(modules, x, y, 4, horizontal)) return false;
                if (GetPatternModule(modules, x, y, 5, horizontal)) return false;
                if (!GetPatternModule(modules, x, y, 6, horizontal)) return false;

                var beforeWhite = true;
                for (var i = 1; i <= 4; i++)
                {
                    var xx = horizontal ? x - i : x;
                    var yy = horizontal ? y : y - i;
                    if (xx >= 0 && yy >= 0 && modules[yy, xx])
                        beforeWhite = false;
                }

                var afterWhite = true;
                for (var i = 7; i <= 10; i++)
                {
                    var xx = horizontal ? x + i : x;
                    var yy = horizontal ? y : y + i;
                    if (xx < spec.Size && yy < spec.Size && modules[yy, xx])
                        afterWhite = false;
                }

                return beforeWhite || afterWhite;
            }

            private static bool GetPatternModule(bool[,] modules, int x, int y, int offset, bool horizontal)
            {
                return horizontal ? modules[y, x + offset] : modules[y + offset, x];
            }

            private static void Render(Canvas canvas, bool[,] modules, QrSpec spec, int layoutPixelSize)
            {
                var qrSize = spec.Size + QuietZone * 2;
                var rawScale = GetRawPixelsPerViewPixelSafe();
                var physicalSize = Math.Max(1, (int)Math.Round(layoutPixelSize * rawScale));
                var modulePixels = Math.Max(1, physicalSize / qrSize);
                var imagePixels = qrSize * modulePixels;
                var offsetPixels = Math.Max(0, (physicalSize - imagePixels) / 2);

                var moduleSize = modulePixels / rawScale;
                var offset = offsetPixels / rawScale;
                // Inverted look: white modules on a black background.
                var moduleBrush = new SolidColorBrush(Colors.White);

                canvas.Children.Clear();
                canvas.Width = layoutPixelSize;
                canvas.Height = layoutPixelSize;
                canvas.Background = new SolidColorBrush(Colors.Black);
                canvas.UseLayoutRounding = true;

                for (var y = 0; y < spec.Size; y++)
                {
                    for (var x = 0; x < spec.Size; x++)
                    {
                        if (!modules[y, x])
                            continue;

                        var rect = new Rectangle
                        {
                            Width = moduleSize,
                            Height = moduleSize,
                            Fill = moduleBrush,
                            UseLayoutRounding = true
                        };

                        Canvas.SetLeft(rect, offset + ((x + QuietZone) * modulePixels) / rawScale);
                        Canvas.SetTop(rect, offset + ((y + QuietZone) * modulePixels) / rawScale);
                        canvas.Children.Add(rect);
                    }
                }
            }

            private static double GetRawPixelsPerViewPixelSafe()
            {
                try
                {
                    var scale = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
                    if (scale > 0.0 && !double.IsNaN(scale) && !double.IsInfinity(scale))
                        return scale;
                }
                catch
                {
                }

                return 1.0;
            }

            private static byte[] ReedSolomonComputeDivisor(int degree)
            {
                var result = new byte[degree];
                result[degree - 1] = 1;
                var root = 1;

                for (var i = 0; i < degree; i++)
                {
                    for (var j = 0; j < degree; j++)
                    {
                        result[j] = (byte)ReedSolomonMultiply(result[j] & 0xFF, root);
                        if (j + 1 < degree)
                            result[j] ^= result[j + 1];
                    }
                    root = ReedSolomonMultiply(root, 0x02);
                }

                return result;
            }

            private static byte[] ReedSolomonComputeRemainder(byte[] data, byte[] divisor)
            {
                var result = new byte[divisor.Length];

                for (var i = 0; i < data.Length; i++)
                {
                    var factor = (data[i] & 0xFF) ^ (result[0] & 0xFF);
                    Array.Copy(result, 1, result, 0, result.Length - 1);
                    result[result.Length - 1] = 0;

                    for (var j = 0; j < result.Length; j++)
                        result[j] = (byte)((result[j] & 0xFF) ^ ReedSolomonMultiply(divisor[j] & 0xFF, factor));
                }

                return result;
            }

            private static int ReedSolomonMultiply(int x, int y)
            {
                var z = 0;
                for (var i = 7; i >= 0; i--)
                {
                    z = ((z << 1) ^ (((z >> 7) & 1) * 0x11D)) & 0xFF;
                    if (((y >> i) & 1) != 0)
                        z ^= x;
                }
                return z;
            }

            private static bool GetBit(int value, int index)
            {
                return ((value >> index) & 1) != 0;
            }

            private static void SetFunctionModule(bool[,] modules, bool[,] isFunction, int x, int y, bool dark)
            {
                modules[y, x] = dark;
                isFunction[y, x] = true;
            }

            private static void SetModule(bool[,] modules, int x, int y, bool dark)
            {
                modules[y, x] = dark;
            }

            private sealed class QrSpec
            {
                public readonly int Version;
                public readonly int Size;
                public readonly int DataCodewords;
                public readonly int BlockCount;
                public readonly int DataCodewordsPerBlock;
                public readonly int EccCodewordsPerBlock;
                public readonly int[] AlignmentCenters;
                public readonly int MaxInputBytes;

                public QrSpec(int version, int dataCodewords, int blockCount, int dataCodewordsPerBlock, int eccCodewordsPerBlock, int[] alignmentCenters, int maxInputBytes)
                {
                    Version = version;
                    Size = 21 + (version - 1) * 4;
                    DataCodewords = dataCodewords;
                    BlockCount = blockCount;
                    DataCodewordsPerBlock = dataCodewordsPerBlock;
                    EccCodewordsPerBlock = eccCodewordsPerBlock;
                    AlignmentCenters = alignmentCenters;
                    MaxInputBytes = maxInputBytes;
                }
            }

            private sealed class BitBuffer
            {
                private readonly List<int> _bits = new List<int>();

                public int Count
                {
                    get { return _bits.Count; }
                }

                public void AppendBits(int value, int length)
                {
                    if (length < 0 || length > 31)
                        throw new ArgumentOutOfRangeException("length");

                    for (var i = length - 1; i >= 0; i--)
                        _bits.Add((value >> i) & 1);
                }

                public byte[] ToByteArray()
                {
                    var result = new byte[(_bits.Count + 7) / 8];
                    for (var i = 0; i < _bits.Count; i++)
                    {
                        if (_bits[i] != 0)
                            result[i >> 3] |= (byte)(1 << (7 - (i & 7)));
                    }
                    return result;
                }
            }
        }
    }

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var flag = value is bool && (bool)value;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return value is Visibility && (Visibility)value == Visibility.Visible;
        }
    }
}
