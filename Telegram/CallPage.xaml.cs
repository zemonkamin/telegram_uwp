using System;
using System.Diagnostics;
using Telegram.Models;
using Telegram.Services;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Telegram
{
    public sealed partial class CallPage : Page
    {
        private ChatViewModel _peer;
        private TelegramCallInfo _call;
        private DispatcherTimer _pollTimer;
        private DispatcherTimer _durationTimer;
        private DateTime _callStartedUtc;
        private bool _ending;
        private bool _backRequestedAttached;
        private int _callProtocolAttempt;
        private bool _retryingProtocol;

        public CallPage()
        {
            InitializeComponent();

            _pollTimer = new DispatcherTimer();
            _pollTimer.Interval = TimeSpan.FromSeconds(2);
            _pollTimer.Tick += PollTimer_Tick;

            _durationTimer = new DispatcherTimer();
            _durationTimer.Interval = TimeSpan.FromSeconds(1);
            _durationTimer.Tick += DurationTimer_Tick;

            Loaded += CallPage_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ConfigureSystemBackButton(true);
            _peer = e.Parameter as ChatViewModel;
            ApplyPeer(_peer);
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ConfigureSystemBackButton(false);
            StopTimers();

            if (!_ending && _call != null && !_call.IsDiscarded)
            {
                try
                {
                    _ending = true;
                    var duration = GetCurrentDurationSeconds();
                    await TelegramService.Instance.DiscardCallAsync(_call, duration);
                }
                catch
                {
                }
            }
        }

        private async void CallPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= CallPage_Loaded;

            if (_peer == null || _peer.PeerType != "user")
            {
                CallStatusText.Text = "Call unavailable";
                HangupButton.Content = "close";
                return;
            }

            try
            {
                CallStatusText.Text = "Connecting";
                HangupButton.IsEnabled = true;

                _callProtocolAttempt = 0;
                _call = await TelegramService.Instance.RequestCallAsync(_peer, _callProtocolAttempt);
                if (_call == null)
                {
                    CallStatusText.Text = "Failed to start call";
                    HangupButton.Content = "close";
                    return;
                }

                // phone.requestCall may already start the outgoing call on Telegram, but older
                // layers/devices can return a wrapper we cannot fully parse immediately. Treat
                // a non-error RPC response as a started outgoing call and keep the UI in the
                // dialing state instead of showing a false failure. Poll/discard will still work
                // as soon as the call id/access_hash are present.
                ApplyCallState(_call);

                if (!_call.IsActive && !_call.IsDiscarded)
                    _pollTimer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_CALL_PAGE_START_ERROR " + ex.GetType().Name + ": " + ex.Message);
                CallStatusText.Text = BuildErrorStatus(ex);
                HangupButton.Content = "close";
            }
        }

        private async void PollTimer_Tick(object sender, object e)
        {
            if (_call == null || _ending) return;

            try
            {
                var fresh = await TelegramService.Instance.GetCallAsync(_call);
                if (fresh == null) return;

                _call = fresh;

                if (await RetryWithNextProtocolIfUsefulAsync(_call))
                    return;

                ApplyCallState(_call);

                if (_call.IsActive || _call.IsDiscarded)
                    _pollTimer.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_CALL_PAGE_POLL_ERROR " + ex.GetType().Name + ": " + ex.Message);
                // Keep the visible call state stable. Polling is opportunistic and must not blank the page.
            }
        }

        private async System.Threading.Tasks.Task<bool> RetryWithNextProtocolIfUsefulAsync(TelegramCallInfo call)
        {
            if (_retryingProtocol || _ending || call == null || !call.IsDiscarded)
                return false;
            if (!string.Equals(call.DiscardReason, "missed", StringComparison.OrdinalIgnoreCase))
                return false;

            var max = TelegramService.Instance.CallProtocolVariantCount;
            if (_callProtocolAttempt + 1 >= max)
                return false;

            _retryingProtocol = true;
            try
            {
                _pollTimer.Stop();
                _callProtocolAttempt++;
                CallStatusText.Text = "Retrying connection";
                Debug.WriteLine("TG_CALL_PROTOCOL_RETRY next=" + _callProtocolAttempt.ToString());
                _call = await TelegramService.Instance.RequestCallAsync(_peer, _callProtocolAttempt);
                ApplyCallState(_call);
                if (_call != null && !_call.IsActive && !_call.IsDiscarded)
                    _pollTimer.Start();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_CALL_PROTOCOL_RETRY_ERROR " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
            finally
            {
                _retryingProtocol = false;
            }
        }

        private void DurationTimer_Tick(object sender, object e)
        {
            CallStatusText.Text = FormatDuration(DateTime.UtcNow - _callStartedUtc);
        }

        private async void HangupButton_Click(object sender, RoutedEventArgs e)
        {
            await EndCallAndCloseAsync();
        }

        private async void SystemBackButton_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (_ending) return;
            e.Handled = true;
            await EndCallAndCloseAsync();
        }

        private async System.Threading.Tasks.Task EndCallAndCloseAsync()
        {
            if (_ending)
                return;

            _ending = true;
            HangupButton.IsEnabled = false;
            StopTimers();
            CallStatusText.Text = "Ending";

            try
            {
                if (_call != null && !_call.IsDiscarded)
                    await TelegramService.Instance.DiscardCallAsync(_call, GetCurrentDurationSeconds());
            }
            catch
            {
            }

            if (Frame != null && Frame.CanGoBack)
                Frame.GoBack();
            else if (Frame != null)
                Frame.Navigate(typeof(Chats));
        }

        private void ApplyPeer(ChatViewModel peer)
        {
            if (peer == null)
            {
                PeerNameText.Text = "Call";
                AvatarInitials.Text = "?";
                AvatarImage.Opacity = 0;
                return;
            }

            PeerNameText.Text = string.IsNullOrEmpty(peer.Title) ? "Call" : peer.Title;
            AvatarInitials.Text = string.IsNullOrEmpty(peer.IconText) ? "?" : peer.IconText;

            if (!string.IsNullOrEmpty(peer.AvatarUri))
            {
                try
                {
                    var image = new BitmapImage();
                    image.DecodePixelWidth = 264;
                    image.UriSource = new Uri(peer.AvatarUri);
                    AvatarBrush.ImageSource = image;
                    AvatarImage.Opacity = 1;
                    AvatarInitials.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    AvatarImage.Opacity = 0;
                    AvatarInitials.Visibility = Visibility.Visible;
                }
            }
            else
            {
                AvatarImage.Opacity = 0;
                AvatarInitials.Visibility = Visibility.Visible;
            }
        }

        private void ApplyCallState(TelegramCallInfo call)
        {
            if (call == null)
            {
                CallStatusText.Text = "Call";
                return;
            }

            if (call.IsDiscarded)
            {
                StopTimers();
                CallStatusText.Text = FormatDiscardStatus(call.DiscardReason);
                HangupButton.Content = "close";
                return;
            }

            if (call.IsActive)
            {
                if (_callStartedUtc == DateTime.MinValue)
                {
                    _callStartedUtc = call.StartDate > 0
                        ? FromUnixTimeSecondsUtc(call.StartDate)
                        : DateTime.UtcNow;
                    _durationTimer.Start();
                }

                CallStatusText.Text = FormatDuration(DateTime.UtcNow - _callStartedUtc);
                return;
            }

            if (call.IsAccepted)
            {
                CallStatusText.Text = "Connecting";
                return;
            }

            CallStatusText.Text = "Call";
        }

        private string FormatDiscardStatus(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "Ended";
            if (string.Equals(reason, "missed", StringComparison.OrdinalIgnoreCase))
                return "Ended: not accepted";
            if (string.Equals(reason, "busy", StringComparison.OrdinalIgnoreCase))
                return "Busy";
            if (string.Equals(reason, "disconnect", StringComparison.OrdinalIgnoreCase))
                return "Ended: disconnected";
            if (string.Equals(reason, "hangup", StringComparison.OrdinalIgnoreCase))
                return "Ended";
            return "Ended: " + reason;
        }

        private int GetCurrentDurationSeconds()
        {
            if (_callStartedUtc == DateTime.MinValue) return 0;
            var seconds = (int)(DateTime.UtcNow - _callStartedUtc).TotalSeconds;
            return seconds < 0 ? 0 : seconds;
        }

        private void StopTimers()
        {
            if (_pollTimer != null) _pollTimer.Stop();
            if (_durationTimer != null) _durationTimer.Stop();
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
            }
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            if (value.TotalHours >= 1)
                return ((int)value.TotalHours).ToString() + ":" + value.Minutes.ToString("00") + ":" + value.Seconds.ToString("00");
            return value.Minutes.ToString() + ":" + value.Seconds.ToString("00");
        }

        private static DateTime FromUnixTimeSecondsUtc(int seconds)
        {
            try
            {
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
            }
            catch
            {
                return DateTime.UtcNow;
            }
        }

        private static string BuildErrorStatus(Exception ex)
        {
            if (ex == null || string.IsNullOrWhiteSpace(ex.Message))
                return "Call error";

            var message = ex.Message;
            if (message.IndexOf("USER_PRIVACY", StringComparison.OrdinalIgnoreCase) >= 0)
                return "User does not allow calls";
            if (message.IndexOf("USER_IS_BLOCKED", StringComparison.OrdinalIgnoreCase) >= 0)
                return "User is blocked";
            if (message.IndexOf("PARTICIPANT_VERSION_OUTDATED", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The other user's client does not support calls";
            if (message.IndexOf("CALL_PROTOCOL_COMPAT_LAYER_INVALID", StringComparison.OrdinalIgnoreCase) >= 0)
                return "No compatible VoIP protocol";
            if (message.IndexOf("CALL_PROTOCOL_LAYER_INVALID", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Invalid VoIP layer";
            if (message.IndexOf("CALL_PROTOCOL_FLAGS_INVALID", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Invalid VoIP protocol flags";
            if (message.IndexOf("CALL_ALREADY", StringComparison.OrdinalIgnoreCase) >= 0)
                return "There is already an active call";

            return "Call error: " + message;
        }
    }
}
