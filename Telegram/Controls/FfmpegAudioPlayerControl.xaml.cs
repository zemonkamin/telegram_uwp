using System;
using Windows.Foundation;
using FFmpegInterop;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Telegram.Models;

namespace Telegram.Controls
{
    public sealed class FfmpegAudioSourceRequestedEventArgs : EventArgs
    {
        public FfmpegAudioSourceRequestedEventArgs(object dataContext)
        {
            DataContext = dataContext;
        }

        public object DataContext { get; private set; }
        public System.Threading.Tasks.Task<bool> ReloadTask { get; set; }
    }

    public sealed class FfmpegAudioPlaybackEndedEventArgs : EventArgs
    {
        public FfmpegAudioPlaybackEndedEventArgs(object dataContext)
        {
            DataContext = dataContext;
        }

        public object DataContext { get; private set; }
    }

    public sealed partial class FfmpegAudioPlayerControl : UserControl
    {
        public static readonly DependencyProperty SourceUriProperty =
            DependencyProperty.Register("SourceUri", typeof(string), typeof(FfmpegAudioPlayerControl), new PropertyMetadata(null, OnSourceUriChanged));

        public static readonly DependencyProperty IsDownloadingProperty =
            DependencyProperty.Register("IsDownloading", typeof(bool), typeof(FfmpegAudioPlayerControl), new PropertyMetadata(false, OnIsDownloadingChanged));

        public static readonly DependencyProperty DurationSecondsProperty =
            DependencyProperty.Register("DurationSeconds", typeof(int), typeof(FfmpegAudioPlayerControl), new PropertyMetadata(0, OnDurationSecondsChanged));

        public static readonly DependencyProperty MediaKindProperty =
            DependencyProperty.Register("MediaKind", typeof(string), typeof(FfmpegAudioPlayerControl), new PropertyMetadata(null, OnMediaKindChanged));

        public static readonly DependencyProperty AccentBrushProperty =
            DependencyProperty.Register("AccentBrush", typeof(Brush), typeof(FfmpegAudioPlayerControl), new PropertyMetadata(null, OnAccentBrushChanged));

        public static readonly DependencyProperty AccentForegroundBrushProperty =
            DependencyProperty.Register("AccentForegroundBrush", typeof(Brush), typeof(FfmpegAudioPlayerControl), new PropertyMetadata(null, OnAccentBrushChanged));

        private static WeakReference _activeControl;

        private readonly DispatcherTimer _timer;
        private string _preparedUri;
        private int _version;
        private bool _isPrepared;
        private bool _isMediaOpened;
        private bool _isPreparing;
        private bool _isPlaying;
        private bool _playbackRequested;
        private bool _ended;
        private double? _pendingSeekRatio;
        private bool _isSeekDragging;
        private double _dragSeekRatio;
        private bool _ignoreSeekSliderValueChanged;
        private TimeSpan _logicalPosition;
        private bool _hasLogicalPosition;
        private bool _logicalClockRunning;
        private DateTime _logicalClockStartedUtc;
        private TimeSpan _logicalClockStartPosition;
        private object _interopObject;

        public event EventHandler<FfmpegAudioSourceRequestedEventArgs> SourceRequested;
        public event EventHandler<FfmpegAudioPlaybackEndedEventArgs> PlaybackEnded;

        public FfmpegAudioPlayerControl()
        {
            InitializeComponent();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(180);
            _timer.Tick += PlaybackTimer_Tick;
        }

        public string SourceUri
        {
            get { return (string)GetValue(SourceUriProperty); }
            set { SetValue(SourceUriProperty, value); }
        }

        public bool IsDownloading
        {
            get { return (bool)GetValue(IsDownloadingProperty); }
            set { SetValue(IsDownloadingProperty, value); }
        }

        public int DurationSeconds
        {
            get { return (int)GetValue(DurationSecondsProperty); }
            set { SetValue(DurationSecondsProperty, value); }
        }

        public string MediaKind
        {
            get { return (string)GetValue(MediaKindProperty); }
            set { SetValue(MediaKindProperty, value); }
        }

        public Brush AccentBrush
        {
            get { return (Brush)GetValue(AccentBrushProperty); }
            set { SetValue(AccentBrushProperty, value); }
        }

        public Brush AccentForegroundBrush
        {
            get { return (Brush)GetValue(AccentForegroundBrushProperty); }
            set { SetValue(AccentForegroundBrushProperty, value); }
        }

        public static void StopAnyPlayback()
        {
            var active = GetActiveControl();
            if (active != null) active.StopPlayback(true);
            _activeControl = null;
        }

        private static FfmpegAudioPlayerControl GetActiveControl()
        {
            return _activeControl == null ? null : _activeControl.Target as FfmpegAudioPlayerControl;
        }

        private static void OnSourceUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegAudioPlayerControl;
            if (control == null) return;

            var oldValue = e.OldValue as string;
            var newValue = e.NewValue as string;
            if (string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase)) return;

            var keepPendingPlayback = string.IsNullOrEmpty(oldValue) &&
                !string.IsNullOrEmpty(newValue) &&
                control._playbackRequested;
            if (keepPendingPlayback)
            {
                control._isPreparing = false;
                control.UpdatePlaybackUi();
                return;
            }

            control.StopPlayback(true);
            control.ResetPreparedSource();
            control.UpdatePlaybackUi();
        }

        private static void OnIsDownloadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegAudioPlayerControl;
            if (control == null) return;
            control.UpdatePlaybackUi();
        }

        private static void OnDurationSecondsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegAudioPlayerControl;
            if (control == null) return;
            control.UpdatePlaybackUi();
        }

        private static void OnMediaKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegAudioPlayerControl;
            if (control == null) return;
            if (!control.IsAllowedMediaKind())
            {
                control.StopPlayback(true);
                control.ResetPreparedSource();
            }
            control.UpdatePlaybackUi();
        }

        private static void OnAccentBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegAudioPlayerControl;
            if (control == null) return;
            control.ApplyAccentBrush();
        }

        private void AudioBubbleRoot_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyAccentBrush();
            UpdatePlaybackUi();
        }

        private void AudioBubbleRoot_Unloaded(object sender, RoutedEventArgs e)
        {
            if (GetActiveControl() == this)
            {
                StopPlayback(true);
                _activeControl = null;
            }
        }

        public async void StartPlaybackFromExternal()
        {
            if (!IsAllowedMediaKind()) return;
            if (_isPlaying || _playbackRequested) return;
            await StartPlaybackInternalAsync();
        }

        private async void AudioPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAllowedMediaKind()) return;

            if (_isPlaying || _playbackRequested)
            {
                PausePlayback();
                return;
            }

            await StartPlaybackInternalAsync();
        }

        private async System.Threading.Tasks.Task StartPlaybackInternalAsync()
        {
            if (!IsAllowedMediaKind()) return;

            FfmpegMusicPlayerControl.StopAnyPlayback();

            var active = GetActiveControl();
            if (active != null && active != this)
                active.StopPlayback(true);
            _activeControl = new WeakReference(this);

            var restartFromBeginning = _ended || IsLogicalPlaybackAtEnd();
            if (restartFromBeginning)
            {
                ResetPreparedSource();
                _ended = false;
                _pendingSeekRatio = 0;
                _isSeekDragging = false;
                _dragSeekRatio = 0;
                SetLogicalPosition(TimeSpan.Zero, false);
                SetAudioSeekSliderValue(0);
            }

            _playbackRequested = true;
            UpdatePlaybackUi();

            var version = ++_version;
            try
            {
                if (!await EnsureSourceAvailableAsync(version))
                {
                    StopPlayback(true);
                    return;
                }

                if (!await PrepareSourceAsync(version))
                {
                    StopPlayback(true);
                    return;
                }

                if (version != _version || !_playbackRequested) return;

                if (restartFromBeginning)
                {
                    _pendingSeekRatio = 0;
                    TryApplyPendingSeek();
                    _ended = false;
                    SetLogicalPosition(TimeSpan.Zero, false);
                    SetAudioSeekSliderValue(0);
                }

                Player.Play();
                _isPlaying = true;
                StartLogicalClock();
                if (!_timer.IsEnabled) _timer.Start();
            }
            catch
            {
                StopPlayback(true);
            }

            UpdatePlaybackUi();
        }

        private async System.Threading.Tasks.Task<bool> EnsureSourceAvailableAsync(int version)
        {
            if (!IsAllowedMediaKind()) return false;
            if (!string.IsNullOrEmpty(SourceUri)) return true;

            var handler = SourceRequested;
            if (handler == null) return false;

            var args = new FfmpegAudioSourceRequestedEventArgs(DataContext);
            handler(this, args);
            if (args.ReloadTask != null)
            {
                _isPreparing = true;
                UpdatePlaybackUi();
                var reloaded = true;
                try { reloaded = await args.ReloadTask; }
                catch { return false; }
                finally
                {
                    _isPreparing = false;
                    UpdatePlaybackUi();
                }
                if (!reloaded) return false;
            }

            if (version != _version) return false;
            return !string.IsNullOrEmpty(SourceUri);
        }

        private async System.Threading.Tasks.Task<bool> PrepareSourceAsync(int version)
        {
            var uri = SourceUri;
            if (string.IsNullOrEmpty(uri)) return false;
            if (!IsAllowedMediaKind()) return false;
            if (_isPrepared && string.Equals(_preparedUri, uri, StringComparison.OrdinalIgnoreCase)) return true;

            var playbackRequestedBeforeReset = _playbackRequested;
            ResetPreparedSource();
            _playbackRequested = playbackRequestedBeforeReset;
            if (version != _version) return false;

            _isPreparing = true;
            UpdatePlaybackUi();

            try
            {
                if (await TryPrepareFfmpegSourceAsync(uri, version))
                    return true;

                if (version != _version) return false;
                _preparedUri = uri;
                _isPrepared = true;
                _isMediaOpened = false;
                Player.Source = new Uri(uri);

                await System.Threading.Tasks.Task.Yield();

                if (version != _version) return false;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                _isPreparing = false;
                UpdatePlaybackUi();
            }
        }

        private async System.Threading.Tasks.Task<bool> TryPrepareFfmpegSourceAsync(string uri, int version)
        {
            StorageFile file = null;
            try
            {
                var sourceUri = new Uri(uri);
                if (sourceUri.Scheme == "file")
                    file = await StorageFile.GetFileFromPathAsync(sourceUri.LocalPath);
                else if (sourceUri.Scheme == "ms-appdata" || sourceUri.Scheme == "ms-appx")
                    file = await StorageFile.GetFileFromApplicationUriAsync(sourceUri);
                else
                    return false;

                if (version != _version || file == null) return false;

                var stream = await file.OpenReadAsync();
                if (version != _version || stream == null) return false;

                var interop = FFmpegInteropMSS.CreateFFmpegInteropMSSFromStream(stream, true, false);
                if (version != _version || interop == null) return false;

                var source = interop.GetMediaStreamSource();
                if (version != _version || source == null) return false;

                _interopObject = interop;
                Player.Source = null;
                Player.SetMediaStreamSource(source);
                _preparedUri = uri;
                _isPrepared = true;
                _isMediaOpened = false;
                return true;
            }
            catch
            {
                _interopObject = null;
                return false;
            }
        }

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            _isPrepared = true;
            _isMediaOpened = true;
            TryApplyPendingSeek();
            if (_playbackRequested)
            {
                try
                {
                    Player.Play();
                    _isPlaying = true;
                    StartLogicalClock();
                    if (!_timer.IsEnabled) _timer.Start();
                }
                catch
                {
                    StopPlayback(true);
                }
            }
            UpdatePlaybackUi();
        }

        private void Player_CurrentStateChanged(object sender, RoutedEventArgs e)
        {
            var isNowPlaying = Player.CurrentState == MediaElementState.Playing;
            if (isNowPlaying)
            {
                _isPlaying = true;
                _playbackRequested = true;
                StartLogicalClock();
                if (!_timer.IsEnabled) _timer.Start();
            }
            else
            {
                if (_isPlaying) StopLogicalClock(true);
                _isPlaying = false;
            }
            UpdatePlaybackUi();
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            _isPlaying = false;
            _playbackRequested = false;
            _ended = true;
            _pendingSeekRatio = null;
            _isSeekDragging = false;
            _dragSeekRatio = 0;
            StopLogicalClock(false);
            SetLogicalPosition(TimeSpan.Zero, false);
            SetAudioSeekSliderValue(0);

            ResetPreparedSource();
            _ended = true;

            if (GetActiveControl() == this) _activeControl = null;
            UpdatePlaybackUi();
            RaisePlaybackEnded();
        }

        private void RaisePlaybackEnded()
        {
            var handler = PlaybackEnded;
            if (handler != null) handler(this, new FfmpegAudioPlaybackEndedEventArgs(DataContext));
        }

        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _isMediaOpened = false;
            StopPlayback(true);
            ResetPreparedSource();
            UpdatePlaybackUi();
        }

        private void PlaybackTimer_Tick(object sender, object e)
        {
            if (!_playbackRequested && !_isPlaying)
            {
                _timer.Stop();
                return;
            }
            UpdatePlaybackUi();
        }

        private void AudioSeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_ignoreSeekSliderValueChanged) return;

            var ratio = ClampRatio(e.NewValue);
            if (_isSeekDragging)
            {
                _dragSeekRatio = ratio;
                UpdatePlaybackUi();
            }
        }

        private void AudioSeekHitTarget_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var ratio = CalculateSeekRatioFromPointer(e);
            if (!ratio.HasValue) return;

            e.Handled = true;
            _isSeekDragging = true;
            _dragSeekRatio = ratio.Value;
            SetAudioSeekSliderValue(ratio.Value);

            try { AudioSeekHitTarget.CapturePointer(e.Pointer); }
            catch { }
            UpdatePlaybackUi();
        }

        private void AudioSeekHitTarget_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isSeekDragging) return;

            var ratio = CalculateSeekRatioFromPointer(e);
            if (!ratio.HasValue) return;

            e.Handled = true;
            _dragSeekRatio = ratio.Value;
            SetAudioSeekSliderValue(ratio.Value);
            UpdatePlaybackUi();
        }

        private void AudioSeekHitTarget_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isSeekDragging) return;

            var ratio = CalculateSeekRatioFromPointer(e);
            if (ratio.HasValue)
            {
                _dragSeekRatio = ratio.Value;
                SetAudioSeekSliderValue(ratio.Value);
            }

            e.Handled = true;
            _isSeekDragging = false;
            try { AudioSeekHitTarget.ReleasePointerCapture(e.Pointer); }
            catch { }

            ApplySeekRatioFromUser(ratio.HasValue ? ratio.Value : _dragSeekRatio);
        }

        private void AudioSeekHitTarget_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            CancelSeekDrag(e);
        }

        private void AudioSeekHitTarget_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            CancelSeekDrag(e);
        }

        private void CancelSeekDrag(PointerRoutedEventArgs e)
        {
            if (!_isSeekDragging) return;

            _isSeekDragging = false;
            if (e != null) e.Handled = true;
            UpdatePlaybackUi();
        }

        private double? CalculateSeekRatioFromPointer(PointerRoutedEventArgs e)
        {
            if (e == null || AudioSeekHitTarget == null) return null;
            return CalculateSeekRatio(e.GetCurrentPoint(AudioSeekHitTarget).Position);
        }

        private double? CalculateSeekRatio(Point point)
        {
            var width = 0d;
            if (AudioSeekHitTarget != null) width = AudioSeekHitTarget.ActualWidth;
            if (width <= 0 && AudioSeekRoot != null) width = AudioSeekRoot.ActualWidth;
            if (width <= 0 && AudioSeekSlider != null) width = AudioSeekSlider.ActualWidth;
            if (width <= 0) return null;

            return ClampRatio(point.X / width);
        }

        private double ClampRatio(double ratio)
        {
            if (ratio < 0) return 0;
            if (ratio > 1) return 1;
            return ratio;
        }

        private async void ApplySeekRatioFromUser(double ratio)
        {
            ratio = ClampRatio(ratio);

            var duration = GetPlaybackDuration();
            var targetPosition = GetPositionFromRatio(ratio, duration);
            var resumePlayback = _isPlaying || _playbackRequested;

            _ended = false;
            _pendingSeekRatio = ratio;
            SetLogicalPosition(targetPosition, resumePlayback);
            SetAudioSeekSliderValue(ratio);
            UpdatePlaybackUi();

            if (!IsAllowedMediaKind()) return;

            var version = _version;
            try
            {
                if (string.IsNullOrEmpty(SourceUri))
                {
                    version = ++_version;
                    if (!await EnsureSourceAvailableAsync(version)) return;
                }

                if (!_isPrepared || !string.Equals(_preparedUri, SourceUri, StringComparison.OrdinalIgnoreCase))
                {
                    version = ++_version;
                    var playbackRequestedBeforePrepare = _playbackRequested;
                    _playbackRequested = resumePlayback;
                    if (!await PrepareSourceAsync(version))
                    {
                        _playbackRequested = playbackRequestedBeforePrepare;
                        return;
                    }
                    _playbackRequested = resumePlayback;
                    _pendingSeekRatio = ratio;
                }

                if (version != _version && _isPreparing) return;

                if (_isMediaOpened)
                    TryApplyPendingSeek();

                if (resumePlayback)
                {
                    _playbackRequested = true;
                    try { Player.Play(); }
                    catch { }
                    _isPlaying = true;
                    StartLogicalClock();
                    if (!_timer.IsEnabled) _timer.Start();
                }
                else
                {
                    _playbackRequested = false;
                    _isPlaying = false;
                    StopLogicalClock(false);
                }

                UpdatePlaybackUi();
            }
            catch
            {
            }
        }

        private bool TryApplyPendingSeek()
        {
            if (!_pendingSeekRatio.HasValue) return false;
            var ratio = _pendingSeekRatio.Value;
            if (ApplySeekRatio(ratio))
            {
                _pendingSeekRatio = null;
                return true;
            }
            return false;
        }

        private bool ApplySeekRatio(double ratio)
        {
            ratio = ClampRatio(ratio);

            var duration = GetPlaybackDuration();
            if (duration.TotalMilliseconds <= 0) return false;

            var targetPosition = GetPositionFromRatio(ratio, duration);
            var resumePlayback = _isPlaying || _playbackRequested;

            try
            {
                try { Player.Pause(); }
                catch { }

                Player.Position = targetPosition;
                SetLogicalPosition(targetPosition, resumePlayback);

                var seekVersion = _version;
                if (resumePlayback)
                {
                    Player.Play();
                    _isPlaying = true;
                    _playbackRequested = true;
                    StartLogicalClock();
                    if (!_timer.IsEnabled) _timer.Start();
                }

                ConfirmPlayerSeekAsync(targetPosition, resumePlayback, seekVersion);
                return true;
            }
            catch
            {
                SetLogicalPosition(targetPosition, resumePlayback);
                return false;
            }
        }

        private async void ConfirmPlayerSeekAsync(TimeSpan targetPosition, bool resumePlayback, int version)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(80);
                if (version != _version || !_isPrepared || !_isMediaOpened) return;

                Player.Position = targetPosition;
                if (resumePlayback && (_playbackRequested || _isPlaying))
                {
                    Player.Play();
                    _isPlaying = true;
                    StartLogicalClock();
                    if (!_timer.IsEnabled) _timer.Start();
                }
            }
            catch
            {
            }
        }

        private bool TrySetPlayerPosition(TimeSpan position)
        {
            try
            {
                Player.Position = position;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsLogicalPlaybackAtEnd()
        {
            var duration = GetPlaybackDuration();
            if (duration.TotalMilliseconds <= 0) return false;

            var position = GetLogicalPosition(duration);
            return position >= duration - TimeSpan.FromMilliseconds(250);
        }

        private void PausePlayback()
        {
            _version++;
            _playbackRequested = false;
            _isPreparing = false;
            StopLogicalClock(true);
            try { Player.Pause(); }
            catch { }
            _isPlaying = false;
            _timer.Stop();
            if (GetActiveControl() == this) _activeControl = null;
            UpdatePlaybackUi();
        }

        private void StopPlayback(bool resetProgress)
        {
            _version++;
            _playbackRequested = false;
            _isPreparing = false;
            _isPlaying = false;
            _timer.Stop();
            if (resetProgress)
                SetLogicalPosition(TimeSpan.Zero, false);
            else
                StopLogicalClock(true);
            try
            {
                Player.Pause();
                if (resetProgress) TrySetPlayerPosition(TimeSpan.Zero);
            }
            catch
            {
            }
            if (resetProgress)
            {
                _ended = false;
                _pendingSeekRatio = null;
                _isSeekDragging = false;
                _dragSeekRatio = 0;
            }
            UpdatePlaybackUi();
        }

        private void ResetPreparedSource()
        {
            try { Player.Stop(); }
            catch { }
            Player.Source = null;
            _interopObject = null;
            _preparedUri = null;
            _isPrepared = false;
            _isMediaOpened = false;
            _isPlaying = false;
            _playbackRequested = false;
            StopLogicalClock(false);
            _pendingSeekRatio = null;
            _isSeekDragging = false;
            _dragSeekRatio = 0;
        }

        private void UpdatePlaybackUi()
        {
            var loading = IsDownloading || _isPreparing;
            AudioPlayLoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            AudioPlayLoadingRing.IsActive = loading;
            AudioPlayGlyph.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            AudioPlayGlyph.Text = (_isPlaying || _playbackRequested) ? "\uE769" : "\uE768";
            ApplyAccentBrush();

            var duration = GetPlaybackDuration();
            var position = TimeSpan.Zero;
            var ratio = 0d;

            if (_isSeekDragging)
            {
                ratio = ClampRatio(_dragSeekRatio);
                position = GetPositionFromRatio(ratio, duration);
            }
            else
            {
                position = GetLogicalPosition(duration);
                if (duration.TotalMilliseconds > 0)
                    ratio = ClampRatio(position.TotalMilliseconds / duration.TotalMilliseconds);
            }

            if (duration.TotalMilliseconds > 0)
                AudioDurationText.Text = FormatAudioTime(position) + " / " + FormatAudioTime(duration);
            else
                AudioDurationText.Text = FormatAudioTime(position);

            if (!_isSeekDragging)
                SetAudioSeekSliderValue(ratio);
        }

        private TimeSpan GetPlaybackDuration()
        {
            var declaredDuration = GetDeclaredPlaybackDuration();
            if (declaredDuration > TimeSpan.Zero) return declaredDuration;

            try
            {
                var naturalDuration = Player.NaturalDuration;
                if (naturalDuration.HasTimeSpan && IsUsableNaturalDuration(naturalDuration.TimeSpan))
                    return naturalDuration.TimeSpan;
            }
            catch
            {
            }

            return TimeSpan.Zero;
        }

        private TimeSpan GetDeclaredPlaybackDuration()
        {
            if (DurationSeconds > 0) return TimeSpan.FromSeconds(DurationSeconds);

            var message = DataContext as ChatMessageViewModel;
            if (message != null && message.MediaDurationSeconds > 0)
                return TimeSpan.FromSeconds(message.MediaDurationSeconds);

            var mediaItem = DataContext as ChatMediaItemViewModel;
            if (mediaItem != null && mediaItem.MediaDurationSeconds > 0)
                return TimeSpan.FromSeconds(mediaItem.MediaDurationSeconds);

            return TimeSpan.Zero;
        }

        private bool IsUsableNaturalDuration(TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero) return false;

            // Native WebM/voice playback may expose an invalid duration derived from
            // container timestamps. Voice/audio messages should prefer the Telegram
            // declared duration; NaturalDuration is only a conservative fallback.
            if (duration.TotalHours > 12) return false;

            return true;
        }

        private TimeSpan GetPositionFromRatio(double ratio, TimeSpan duration)
        {
            ratio = ClampRatio(ratio);
            if (duration.TotalMilliseconds <= 0) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(duration.TotalMilliseconds * ratio);
        }

        private TimeSpan GetLogicalPosition(TimeSpan duration)
        {
            if (_ended) return TimeSpan.Zero;

            TimeSpan position;
            if (_logicalClockRunning)
            {
                position = _logicalClockStartPosition + (DateTime.UtcNow - _logicalClockStartedUtc);
                _logicalPosition = ClampPosition(position, duration);
                _hasLogicalPosition = true;
                return _logicalPosition;
            }

            if (_hasLogicalPosition)
                return ClampPosition(_logicalPosition, duration);

            return TimeSpan.Zero;
        }

        private TimeSpan ClampPosition(TimeSpan position, TimeSpan duration)
        {
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (duration > TimeSpan.Zero && position > duration) position = duration;
            return position;
        }

        private void SetLogicalPosition(TimeSpan position, bool keepClockRunning)
        {
            var duration = GetPlaybackDuration();
            _logicalPosition = ClampPosition(position, duration);
            _hasLogicalPosition = true;

            if (keepClockRunning)
            {
                _logicalClockStartPosition = _logicalPosition;
                _logicalClockStartedUtc = DateTime.UtcNow;
                _logicalClockRunning = true;
            }
            else
            {
                _logicalClockRunning = false;
            }
        }

        private void StartLogicalClock()
        {
            if (_logicalClockRunning) return;
            var duration = GetPlaybackDuration();
            _logicalClockStartPosition = GetLogicalPosition(duration);
            _logicalClockStartedUtc = DateTime.UtcNow;
            _logicalClockRunning = true;
            _hasLogicalPosition = true;
        }

        private void StopLogicalClock(bool updatePosition)
        {
            if (updatePosition && _logicalClockRunning)
            {
                var duration = GetPlaybackDuration();
                _logicalPosition = GetLogicalPosition(duration);
                _hasLogicalPosition = true;
            }
            _logicalClockRunning = false;
        }

        private void SetAudioSeekSliderValue(double ratio)
        {
            if (AudioSeekSlider == null) return;
            ratio = ClampRatio(ratio);

            try
            {
                _ignoreSeekSliderValueChanged = true;
                AudioSeekSlider.Value = ratio;
            }
            catch
            {
            }
            finally
            {
                _ignoreSeekSliderValueChanged = false;
            }
        }

        private bool IsAllowedMediaKind()
        {
            return string.Equals(MediaKind, "voice", StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyAccentBrush()
        {
            var brush = AccentBrush ?? ResolveAccentBrush();
            var foreground = AccentForegroundBrush ?? new SolidColorBrush(Colors.White);
            AudioPlayCircle.Fill = brush;
            AudioPlayGlyph.Foreground = foreground;
            AudioPlayLoadingRing.Foreground = foreground;
            if (AudioSeekSlider != null)
                AudioSeekSlider.Foreground = brush;
        }

        private Brush ResolveAccentBrush()
        {
            // TryGetValue: the ResourceDictionary indexer throws on a missing key, and a throw per
            // control is not an acceptable way to discover that a resource is absent.
            var resources = Application.Current == null ? null : Application.Current.Resources;
            object value;

            if (resources != null && resources.TryGetValue("SystemControlHighlightAccentBrush", out value))
            {
                var brush = value as SolidColorBrush;
                if (brush != null) return new SolidColorBrush(MakeAudioAccentColor(brush.Color));
            }

            if (resources != null && resources.TryGetValue("SystemAccentColor", out value))
            {
                if (value is Color) return new SolidColorBrush(MakeAudioAccentColor((Color)value));
            }

            return new SolidColorBrush(MakeAudioAccentColor(Color.FromArgb(255, 0, 120, 215)));
        }

        private Color MakeAudioAccentColor(Color color)
        {
            return Color.FromArgb(
                color.A,
                LightenColorComponent(color.R),
                LightenColorComponent(color.G),
                LightenColorComponent(color.B));
        }

        private byte LightenColorComponent(byte value)
        {
            var result = value + (255 - value) * 32 / 100;
            if (result > 255) result = 255;
            if (result < 0) result = 0;
            return (byte)result;
        }

        private string FormatAudioTime(TimeSpan time)
        {
            if (time.TotalSeconds < 0) time = TimeSpan.Zero;
            var totalSeconds = (int)Math.Floor(time.TotalSeconds);
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            return minutes.ToString() + ":" + seconds.ToString("00");
        }
    }
}
