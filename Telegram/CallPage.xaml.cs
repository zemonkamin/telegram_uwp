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
        private CallInfo _call;
        private DispatcherTimer _durationTimer;
        private DateTime _callStartedUtc;
        private bool _ending;
        private bool _backRequestedAttached;
        private bool _subscribed;
        private CallInfo _incoming;

        public CallPage()
        {
            InitializeComponent();

            _durationTimer = new DispatcherTimer();
            _durationTimer.Interval = TimeSpan.FromSeconds(1);
            _durationTimer.Tick += DurationTimer_Tick;

            Loaded += CallPage_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ConfigureSystemBackButton(true);
            // Two ways in, and they differ only at the start. An outgoing call is
            // navigated to with the chat and places itself; an incoming one is
            // navigated to with the call that has already arrived, and waits to be
            // answered. Everything after that is the same screen in the same state.
            _peer = e.Parameter as ChatViewModel;
            _incoming = e.Parameter as CallInfo;

            if (_incoming != null)
            {
                _call = _incoming;
                _peer = ResolveIncomingPeer(_incoming);
            }

            ApplyPeer(_peer);

            // An incoming call carries a user id and no name, so the page opens with a
            // placeholder and the real one arrives a moment later. Not awaited: a
            // ringing phone must be on screen now, not after a round trip.
            if (_incoming != null)
            {
                var ignoredName = ResolveIncomingNameAsync(_incoming.UserId);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            ConfigureSystemBackButton(false);
            StopTimers();

            if (_subscribed)
            {
                TelegramService.Instance.CallStateChanged -= OnCallStateChanged;
                _subscribed = false;
            }

            if (!_ending && _call != null && !_call.IsDiscarded)
            {
                try
                {
                    _ending = true;
                    TelegramService.Instance.HangUpCall();
                }
                catch
                {
                }
            }
        }

        private void CallPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= CallPage_Loaded;

            if (_incoming == null && (_peer == null || _peer.PeerType != "user"))
            {
                CallStatusText.Text = "Call unavailable";
                HangupButton.Content = "close";
                return;
            }

            if (!TelegramService.Instance.CallsSupported)
            {
                // Honest rather than hopeful. libtgvoip is ARM only, so on a desktop
                // build there is nothing behind this page and saying "connecting"
                // would be a lie that never resolves.
                CallStatusText.Text = "Calls are not available in this build";
                HangupButton.Content = "close";
                return;
            }

            try
            {
                CallStatusText.Text = "Connecting";
                HangupButton.IsEnabled = true;

                TelegramService.Instance.CallStateChanged += OnCallStateChanged;
                _subscribed = true;

                if (_incoming != null)
                {
                    ShowAnswerButton(true);
                    ApplyCallState(_incoming);
                    return;
                }

                // Nothing is awaited and nothing comes back. TDLib answers with
                // updateCall, the same way it reports every later change, so the
                // page waits on the event rather than on the request - which is also
                // what makes the polling this replaced unnecessary.
                TelegramService.Instance.StartCall(_peer.UserId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_CALL_PAGE_START_ERROR " + ex.GetType().Name + ": " + ex.Message);
                CallStatusText.Text = BuildErrorStatus(ex);
                HangupButton.Content = "close";
            }
        }

        /// <summary>
        /// Every change to the call, pushed rather than polled.
        ///
        /// Already on the UI thread: the client marshals both TDLib updates and
        /// libtgvoip's own state before raising this.
        /// </summary>
        private void OnCallStateChanged(object sender, CallInfo call)
        {
            if (call == null || _ending) return;

            _call = call;

            // Nothing left to answer once it is up or over.
            if (call.IsActive || call.IsDiscarded) ShowAnswerButton(false);

            ApplyCallState(call);
        }

        private void AnswerButton_Click(object sender, RoutedEventArgs e)
        {
            ShowAnswerButton(false);
            CallStatusText.Text = "Connecting";

            TelegramService.Instance.AcceptCall();
        }

        private void ShowAnswerButton(bool visible)
        {
            AnswerButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            AnswerColumn.Width = new GridLength(visible ? 1 : 0, visible ? GridUnitType.Star : GridUnitType.Pixel);
        }

        /// <summary>
        /// A stand-in peer for an incoming call.
        ///
        /// The call carries a user id and nothing else - no name, no avatar - so the
        /// page shows what it has rather than waiting for a lookup that would delay
        /// a ringing phone.
        /// </summary>
        private static ChatViewModel ResolveIncomingPeer(CallInfo call)
        {
            return new ChatViewModel
            {
                PeerType = "user",
                UserId = call.UserId,
                Title = "Incoming call",
                IconText = "?",
            };
        }

        /// <summary>
        /// Replaces the placeholder with whoever is actually calling.
        ///
        /// Nothing depends on this succeeding - the call works either way, and the
        /// page is already usable - so a failed lookup leaves the placeholder rather
        /// than reporting anything.
        /// </summary>
        private async System.Threading.Tasks.Task ResolveIncomingNameAsync(long userId)
        {
            if (userId == 0) return;

            ChatViewModel peer = null;
            try
            {
                peer = await TelegramService.Instance.GetPrivateChatAsync(userId);
            }
            catch (Exception)
            {
            }

            if (peer == null || string.IsNullOrEmpty(peer.Title)) return;

            // The call may have been answered, declined or replaced meanwhile.
            if (_incoming == null || _incoming.UserId != userId) return;

            _peer = peer;
            ApplyPeer(peer);

            // The ringing notification is showing the same placeholder. Re-showing it
            // under the same tag replaces it rather than stacking a second one.
            if (_incoming.State == "callStatePending" && !_incoming.IsOutgoing)
            {
                Notifications.IncomingCallToast.Refresh(_incoming.Id, peer.Title);
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
                    TelegramService.Instance.HangUpCall();
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

        private void ApplyCallState(CallInfo call)
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
                // Ready means Telegram considers the call up. That is not the same
                // as audio flowing, and showing a running duration before the media
                // layer has connected tells the user the call is working when it may
                // not be - so the timer starts on Established, not before.
                if (!string.Equals(call.MediaState, "Established", StringComparison.OrdinalIgnoreCase))
                {
                    CallStatusText.Text = string.IsNullOrEmpty(call.MediaState)
                        ? "Connecting"
                        : "Connecting - " + call.MediaState.ToLowerInvariant();
                    return;
                }

                if (_callStartedUtc == DateTime.MinValue)
                {
                    _callStartedUtc = DateTime.UtcNow;
                    _durationTimer.Start();
                }

                CallStatusText.Text = FormatDuration(DateTime.UtcNow - _callStartedUtc);
                return;
            }

            if (call.State == "callStatePending")
            {
                CallStatusText.Text = call.IsOutgoing ? "Ringing" : "Incoming call";
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

        private void StopTimers()
        {
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
