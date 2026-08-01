using System;
using System.ComponentModel;
using System.IO;
using FFmpegInterop;
using Telegram.Models;
using Windows.Foundation;
using Windows.Media;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Telegram.Controls
{
    public sealed partial class FfmpegMusicPlayerControl : UserControl
    {
        public static readonly DependencyProperty SourceUriProperty =
            DependencyProperty.Register("SourceUri", typeof(string), typeof(FfmpegMusicPlayerControl), new PropertyMetadata(null, OnSourceUriChanged));

        public static readonly DependencyProperty IsDownloadingProperty =
            DependencyProperty.Register("IsDownloading", typeof(bool), typeof(FfmpegMusicPlayerControl), new PropertyMetadata(false, OnIsDownloadingChanged));

        public static readonly DependencyProperty DurationSecondsProperty =
            DependencyProperty.Register("DurationSeconds", typeof(int), typeof(FfmpegMusicPlayerControl), new PropertyMetadata(0, OnDurationSecondsChanged));

        public static readonly DependencyProperty MediaKindProperty =
            DependencyProperty.Register("MediaKind", typeof(string), typeof(FfmpegMusicPlayerControl), new PropertyMetadata(null, OnMediaKindChanged));

        public static readonly DependencyProperty AccentBrushProperty =
            DependencyProperty.Register("AccentBrush", typeof(Brush), typeof(FfmpegMusicPlayerControl), new PropertyMetadata(null, OnAccentBrushChanged));

        public static readonly DependencyProperty AccentForegroundBrushProperty =
            DependencyProperty.Register("AccentForegroundBrush", typeof(Brush), typeof(FfmpegMusicPlayerControl), new PropertyMetadata(null, OnAccentBrushChanged));

        private static WeakReference _activeControl;
        private static WeakReference _transportOwner;

        private readonly DispatcherTimer _timer;
        private string _preparedUri;
        private int _version;
        private bool _isPrepared;
        private bool _isMediaOpened;
        private bool _isPreparing;
        private bool _isPlaying;
        private bool _playbackRequested;
        private bool _playbackStarted;
        private bool _ended;
        private double? _pendingSeekRatio;
        private bool _isSeekDragging;
        private double _dragSeekRatio;
        private bool _ignoreSeekSliderValueChanged;
        private object _interopObject;
        private SystemMediaTransportControls _systemControls;
        private bool _systemControlsAttached;
        private INotifyPropertyChanged _notifyingDataContext;

        public event EventHandler<FfmpegAudioSourceRequestedEventArgs> SourceRequested;
        public event EventHandler<FfmpegAudioPlaybackEndedEventArgs> PlaybackEnded;
        public event EventHandler<FfmpegAudioPlaybackEndedEventArgs> PlaybackStarted;
        public event EventHandler<FfmpegAudioPlaybackEndedEventArgs> NextRequested;
        public event EventHandler<FfmpegAudioPlaybackEndedEventArgs> PreviousRequested;

        public FfmpegMusicPlayerControl()
        {
            InitializeComponent();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(220);
            _timer.Tick += PlaybackTimer_Tick;
            DataContextChanged += MusicPlayer_DataContextChanged;
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

        public async System.Threading.Tasks.Task PlayAsync()
        {
            if (_isPlaying || _playbackRequested) return;
            await StartPlaybackInternalAsync();
        }

        private static FfmpegMusicPlayerControl GetActiveControl()
        {
            return _activeControl == null ? null : _activeControl.Target as FfmpegMusicPlayerControl;
        }

        private static FfmpegMusicPlayerControl GetTransportOwner()
        {
            return _transportOwner == null ? null : _transportOwner.Target as FfmpegMusicPlayerControl;
        }

        private static void OnSourceUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegMusicPlayerControl;
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
            var control = d as FfmpegMusicPlayerControl;
            if (control == null) return;
            control.UpdatePlaybackUi();
        }

        private static void OnDurationSecondsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegMusicPlayerControl;
            if (control == null) return;
            control.UpdatePlaybackUi();
        }

        private static void OnMediaKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegMusicPlayerControl;
            if (control == null) return;
            if (!control.IsAllowedMediaKind())
            {
                control.StopPlayback(true);
                control.ResetPreparedSource();
            }
            control.UpdateTrackTexts();
            control.UpdatePlaybackUi();
        }

        private static void OnAccentBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegMusicPlayerControl;
            if (control == null) return;
            control.ApplyAccentBrush();
        }

        private void MusicRoot_Loaded(object sender, RoutedEventArgs e)
        {
            AttachDataContextNotifications();
            UpdateTrackTexts();
            ApplyAccentBrush();
            UpdatePlaybackUi();
        }

        private void MusicRoot_Unloaded(object sender, RoutedEventArgs e)
        {
            if (GetActiveControl() == this)
            {
                StopPlayback(true);
                _activeControl = null;
            }
            if (GetTransportOwner() == this)
                DetachSystemMediaControls();
            DetachDataContextNotifications();
        }

        private void MusicPlayer_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            AttachDataContextNotifications();
            UpdateTrackTexts();
            UpdatePlaybackUi();
        }

        private void AttachDataContextNotifications()
        {
            var notifying = DataContext as INotifyPropertyChanged;
            if (object.ReferenceEquals(_notifyingDataContext, notifying)) return;

            DetachDataContextNotifications();
            _notifyingDataContext = notifying;
            if (_notifyingDataContext != null)
                _notifyingDataContext.PropertyChanged += DataContext_PropertyChanged;
        }

        private void DetachDataContextNotifications()
        {
            if (_notifyingDataContext != null)
                _notifyingDataContext.PropertyChanged -= DataContext_PropertyChanged;
            _notifyingDataContext = null;
        }

        private void DataContext_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var name = e == null ? null : e.PropertyName;
            if (string.IsNullOrEmpty(name) ||
                name == "MediaPreviewUri" ||
                name == "MediaPreviewImageSource" ||
                name == "IsMediaDownloading" ||
                name == "MediaFileUri")
            {
                UpdatePlaybackUi();
                return;
            }

            if (name == "MediaTitle" || name == "MediaPerformer" || name == "MediaFileName")
            {
                UpdateTrackTexts();
            }
        }

        private async void MusicPlayButton_Click(object sender, RoutedEventArgs e)
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

            FfmpegAudioPlayerControl.StopAnyPlayback();
            AttachSystemMediaControls();
            UpdateSystemMediaDisplay();

            var active = GetActiveControl();
            if (active != null && active != this)
                active.StopPlayback(true);
            _activeControl = new WeakReference(this);

            var restartFromBeginning = _ended || IsPlaybackAtEnd();
            if (restartFromBeginning)
            {
                ResetPreparedSource();
                _ended = false;
                _pendingSeekRatio = 0;
                _isSeekDragging = false;
                _dragSeekRatio = 0;
                SetMusicSeekSliderValue(0);
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
                    SetMusicSeekSliderValue(0);
                }

                Player.Play();
                _isPlaying = true;
                _playbackStarted = true;
                if (!_timer.IsEnabled) _timer.Start();
                UpdateSystemMediaPlaybackStatus();
                RaisePlaybackStarted();
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
                Player.Source = new Uri(uri);
                _preparedUri = uri;
                _isPrepared = true;
                _isMediaOpened = false;
                await System.Threading.Tasks.Task.Yield();
                return version == _version;
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
                if (sourceUri.Scheme == "ms-appdata" || sourceUri.Scheme == "ms-appx")
                    file = await StorageFile.GetFileFromApplicationUriAsync(sourceUri);
                else if (sourceUri.Scheme == "file")
                    file = await StorageFile.GetFileFromPathAsync(sourceUri.LocalPath);
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
                    _playbackStarted = true;
                    if (!_timer.IsEnabled) _timer.Start();
                    UpdateSystemMediaPlaybackStatus();
                }
                catch
                {
                    StopPlayback(true);
                }
            }
            UpdatePlaybackUi();
            UpdateSystemMediaPlaybackStatus();
        }

        private void Player_CurrentStateChanged(object sender, RoutedEventArgs e)
        {
            var isNowPlaying = Player.CurrentState == MediaElementState.Playing;
            if (isNowPlaying)
            {
                _isPlaying = true;
                _playbackRequested = true;
                _playbackStarted = true;
                if (!_timer.IsEnabled) _timer.Start();
            }
            else if (Player.CurrentState == MediaElementState.Paused)
            {
                _isPlaying = false;
                _playbackRequested = false;
            }
            else if (Player.CurrentState == MediaElementState.Stopped)
            {
                _isPlaying = false;
            }
            UpdatePlaybackUi();
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            _isPlaying = false;
            _playbackRequested = false;
            _playbackStarted = false;
            _ended = true;
            _pendingSeekRatio = null;
            _isSeekDragging = false;
            _dragSeekRatio = 0;
            SetMusicSeekSliderValue(0);

            ResetPreparedSource();
            _ended = true;

            if (GetActiveControl() == this) _activeControl = null;
            UpdatePlaybackUi();
            UpdateSystemMediaPlaybackStatus();
            RaisePlaybackEnded();
        }

        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            StopPlayback(true);
            ResetPreparedSource();
            UpdatePlaybackUi();
            UpdateSystemMediaPlaybackStatus();
        }

        private void PlaybackTimer_Tick(object sender, object e)
        {
            if (!_playbackRequested && !_isPlaying && !_playbackStarted)
            {
                _timer.Stop();
                return;
            }
            UpdatePlaybackUi();
        }

        private void MusicSeekHitTarget_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var ratio = CalculateSeekRatioFromPointer(e);
            if (!ratio.HasValue) return;

            e.Handled = true;
            _isSeekDragging = true;
            _dragSeekRatio = ratio.Value;
            SetMusicSeekSliderValue(ratio.Value);

            try { MusicSeekHitTarget.CapturePointer(e.Pointer); }
            catch { }
            UpdatePlaybackUi();
        }

        private void MusicSeekHitTarget_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isSeekDragging) return;

            var ratio = CalculateSeekRatioFromPointer(e);
            if (!ratio.HasValue) return;

            e.Handled = true;
            _dragSeekRatio = ratio.Value;
            SetMusicSeekSliderValue(ratio.Value);
            UpdatePlaybackUi();
        }

        private void MusicSeekHitTarget_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isSeekDragging) return;

            var ratio = CalculateSeekRatioFromPointer(e);
            if (ratio.HasValue)
            {
                _dragSeekRatio = ratio.Value;
                SetMusicSeekSliderValue(ratio.Value);
            }

            e.Handled = true;
            _isSeekDragging = false;
            try { MusicSeekHitTarget.ReleasePointerCapture(e.Pointer); }
            catch { }

            ApplySeekRatioFromUser(ratio.HasValue ? ratio.Value : _dragSeekRatio);
        }

        private void MusicSeekHitTarget_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            CancelSeekDrag(e);
        }

        private void MusicSeekHitTarget_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
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
            if (e == null || MusicSeekHitTarget == null) return null;
            return CalculateSeekRatio(e.GetCurrentPoint(MusicSeekHitTarget).Position);
        }

        private double? CalculateSeekRatio(Point point)
        {
            var width = 0d;
            if (MusicSeekHitTarget != null) width = MusicSeekHitTarget.ActualWidth;
            if (width <= 0 && MusicSeekRoot != null) width = MusicSeekRoot.ActualWidth;
            if (width <= 0 && MusicSeekSlider != null) width = MusicSeekSlider.ActualWidth;
            if (width <= 0) return null;

            return ClampRatio(point.X / width);
        }

        private async void ApplySeekRatioFromUser(double ratio)
        {
            ratio = ClampRatio(ratio);

            var duration = GetPlaybackDuration();
            var targetPosition = GetPositionFromRatio(ratio, duration);
            var resumePlayback = _isPlaying || _playbackRequested;

            _ended = false;
            _pendingSeekRatio = ratio;
            SetMusicSeekSliderValue(ratio);
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
                else if (duration.TotalMilliseconds > 0)
                    Player.Position = targetPosition;

                if (resumePlayback)
                {
                    _playbackRequested = true;
                    try { Player.Play(); }
                    catch { }
                    _isPlaying = true;
                    _playbackStarted = true;
                    if (!_timer.IsEnabled) _timer.Start();
                }
                else
                {
                    _playbackRequested = false;
                    _isPlaying = false;
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
                SetMusicSeekSliderValue(ratio);

                if (resumePlayback)
                {
                    Player.Play();
                    _isPlaying = true;
                    _playbackRequested = true;
                    _playbackStarted = true;
                    UpdateSystemMediaPlaybackStatus();
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                UpdatePlaybackUi();
            }
        }

        private TimeSpan GetPlaybackDuration()
        {
            try
            {
                if (Player != null && Player.NaturalDuration.HasTimeSpan && Player.NaturalDuration.TimeSpan.TotalMilliseconds > 0)
                    return Player.NaturalDuration.TimeSpan;
            }
            catch
            {
            }

            if (DurationSeconds > 0) return TimeSpan.FromSeconds(DurationSeconds);
            return TimeSpan.Zero;
        }

        private TimeSpan GetPlaybackPosition()
        {
            try
            {
                if (Player != null) return Player.Position;
            }
            catch
            {
            }
            return TimeSpan.Zero;
        }

        private TimeSpan GetPositionFromRatio(double ratio, TimeSpan duration)
        {
            ratio = ClampRatio(ratio);
            if (duration.TotalMilliseconds <= 0) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(duration.TotalMilliseconds * ratio);
        }

        private bool IsPlaybackAtEnd()
        {
            var duration = GetPlaybackDuration();
            if (duration.TotalMilliseconds <= 0) return false;
            var position = GetPlaybackPosition();
            return position.TotalMilliseconds >= Math.Max(0, duration.TotalMilliseconds - 400);
        }

        private void PausePlayback()
        {
            _playbackRequested = false;
            _isPlaying = false;
            try { Player.Pause(); }
            catch { }
            UpdatePlaybackUi();
            UpdateSystemMediaPlaybackStatus();
        }

        private void StopPlayback(bool resetPosition)
        {
            _version++;
            _timer.Stop();
            _isPlaying = false;
            _playbackRequested = false;
            _isPreparing = false;
            _pendingSeekRatio = null;
            _isSeekDragging = false;
            _dragSeekRatio = 0;
            if (resetPosition)
            {
                _playbackStarted = false;
                _ended = false;
                SetMusicSeekSliderValue(0);
            }

            try { Player.Stop(); }
            catch { }
            UpdatePlaybackUi();
            if (GetTransportOwner() == this)
            {
                UpdateSystemMediaPlaybackStatus();
                if (resetPosition) DetachSystemMediaControls();
            }
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
        }

        private void UpdatePlaybackUi()
        {
            UpdateTrackTexts();
            ApplyAccentBrush();

            var loading = IsDownloading || _isPreparing;
            var hasCover = UpdateCoverImage();
            MusicLoadingRing.IsActive = loading;
            MusicLoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            MusicPlayGlyph.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            MusicBackgroundGlyph.Visibility = Visibility.Collapsed;
            if (MusicCoverShade != null)
            {
                MusicCoverShade.Visibility = hasCover ? Visibility.Visible : Visibility.Collapsed;
                MusicCoverShade.Opacity = hasCover ? 1 : 0;
            }
            MusicCoverOverlay.Visibility = hasCover ? Visibility.Visible : Visibility.Collapsed;
            MusicCoverOverlay.Opacity = hasCover ? 1 : 0;
            MusicPlayGlyph.Text = _isPlaying || _playbackRequested ? "\uE769" : "\uE768";

            var showSeek = _playbackStarted && !_ended;
            MusicSeekLine.Visibility = showSeek ? Visibility.Visible : Visibility.Collapsed;
            MusicAuthorText.Visibility = showSeek ? Visibility.Collapsed : Visibility.Visible;

            var duration = GetPlaybackDuration();
            var position = _isSeekDragging ? GetPositionFromRatio(_dragSeekRatio, duration) : GetPlaybackPosition();
            var ratio = 0d;
            if (duration.TotalMilliseconds > 0)
                ratio = ClampRatio(position.TotalMilliseconds / duration.TotalMilliseconds);

            if (_isSeekDragging)
                ratio = _dragSeekRatio;

            SetMusicSeekSliderValue(ratio);

            var shownTime = position;
            if (shownTime.TotalMilliseconds < 0) shownTime = TimeSpan.Zero;
            if (duration.TotalMilliseconds > 0 && shownTime > duration) shownTime = duration;
            MusicDurationText.Text = FormatDuration(shownTime);
            UpdateSystemMediaPlaybackStatus();
        }

        private void ApplyAccentBrush()
        {
            var brush = AccentBrush ?? ResolveAccentBrush();
            var buttonBackground = new SolidColorBrush(Color.FromArgb(184, 255, 255, 255));
            var buttonForeground = new SolidColorBrush(Color.FromArgb(221, 0, 0, 0));

            if (MusicAccentBackground != null) MusicAccentBackground.Fill = buttonBackground;
            if (MusicSeekSlider != null) MusicSeekSlider.Foreground = brush;
            if (MusicPlayGlyph != null) MusicPlayGlyph.Foreground = buttonForeground;
            if (MusicLoadingRing != null) MusicLoadingRing.Foreground = buttonForeground;
            if (MusicBackgroundGlyph != null) MusicBackgroundGlyph.Foreground = buttonForeground;
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
                if (brush != null) return brush;
            }

            if (resources != null && resources.TryGetValue("SystemAccentColor", out value))
            {
                if (value is Color) return new SolidColorBrush((Color)value);
            }

            return new SolidColorBrush(Color.FromArgb(255, 0, 120, 215));
        }

        private void UpdateTrackTexts()
        {
            if (MusicTitleText == null || MusicAuthorText == null) return;
            var title = GetDisplayTitle();
            MusicTitleText.Text = title;
            MusicAuthorText.Text = GetDisplayAuthor(title);
            if (GetTransportOwner() == this)
                UpdateSystemMediaDisplay();
        }

        private bool UpdateCoverImage()
        {
            var source = GetPreviewImageSource();
            if (MusicCoverBrush != null) MusicCoverBrush.ImageSource = source;
            if (MusicCoverEllipse != null) MusicCoverEllipse.Opacity = source == null ? 0 : 1;
            return source != null;
        }

        private void AttachSystemMediaControls()
        {
            try
            {
                var owner = GetTransportOwner();
                if (owner != null && owner != this)
                    owner.DetachSystemMediaControls();

                _transportOwner = new WeakReference(this);
                _systemControls = SystemMediaTransportControls.GetForCurrentView();
                if (_systemControls == null) return;

                if (!_systemControlsAttached)
                {
                    _systemControls.ButtonPressed += SystemControls_ButtonPressed;
                    _systemControlsAttached = true;
                }

                _systemControls.IsEnabled = true;
                _systemControls.IsPlayEnabled = true;
                _systemControls.IsPauseEnabled = true;
                _systemControls.IsStopEnabled = true;
                _systemControls.IsNextEnabled = true;
                _systemControls.IsPreviousEnabled = true;
                UpdateSystemMediaPlaybackStatus();
            }
            catch
            {
            }
        }

        private void DetachSystemMediaControls()
        {
            try
            {
                if (_systemControls != null && _systemControlsAttached)
                    _systemControls.ButtonPressed -= SystemControls_ButtonPressed;

                if (_systemControls != null)
                {
                    _systemControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
                    _systemControls.IsPlayEnabled = false;
                    _systemControls.IsPauseEnabled = false;
                    _systemControls.IsStopEnabled = false;
                    _systemControls.IsNextEnabled = false;
                    _systemControls.IsPreviousEnabled = false;
                }
            }
            catch
            {
            }

            _systemControlsAttached = false;
            _systemControls = null;
            if (GetTransportOwner() == this) _transportOwner = null;
        }

        private async void SystemControls_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            if (args == null || Dispatcher == null) return;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async delegate
            {
                if (GetTransportOwner() != this) return;

                if (args.Button == SystemMediaTransportControlsButton.Play)
                {
                    if (!_isPlaying && !_playbackRequested)
                        await StartPlaybackInternalAsync();
                }
                else if (args.Button == SystemMediaTransportControlsButton.Pause)
                {
                    if (_isPlaying || _playbackRequested)
                        PausePlayback();
                }
                else if (args.Button == SystemMediaTransportControlsButton.Stop)
                {
                    StopPlayback(true);
                }
                else if (args.Button == SystemMediaTransportControlsButton.Next)
                {
                    RaiseNextRequested();
                }
                else if (args.Button == SystemMediaTransportControlsButton.Previous)
                {
                    RaisePreviousRequested();
                }
            });
        }

        private void UpdateSystemMediaDisplay()
        {
            try
            {
                var controls = _systemControls;
                if (controls == null) return;

                var title = GetDisplayTitle();
                var author = GetDisplayAuthor(title);
                controls.DisplayUpdater.Type = MediaPlaybackType.Music;
                controls.DisplayUpdater.AppMediaId = SourceUri ?? string.Empty;
                controls.DisplayUpdater.MusicProperties.Title = title;
                controls.DisplayUpdater.MusicProperties.Artist = author;
                controls.DisplayUpdater.Update();
            }
            catch
            {
            }
        }

        private void UpdateSystemMediaPlaybackStatus()
        {
            try
            {
                if (_systemControls == null) return;

                if (_isPlaying)
                    _systemControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                else if (_isPreparing || IsDownloading)
                    _systemControls.PlaybackStatus = MediaPlaybackStatus.Changing;
                else if (_playbackStarted)
                    _systemControls.PlaybackStatus = MediaPlaybackStatus.Paused;
                else
                    _systemControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
            }
            catch
            {
            }
        }

        private string GetDisplayTitle()
        {
            var message = DataContext as ChatMessageViewModel;
            if (message != null)
                return FirstNonEmpty(UsefulTitle(message.MediaTitle), FileTitle(message.MediaFileName), "Audio");

            var item = DataContext as ChatMediaItemViewModel;
            if (item != null)
                return FirstNonEmpty(UsefulTitle(item.MediaTitle), FileTitle(item.MediaFileName), "Audio");

            return "Audio";
        }

        private string GetDisplayAuthor(string title)
        {
            var message = DataContext as ChatMessageViewModel;
            if (message != null)
                return FirstNonEmpty(message.MediaPerformer, ExtractAuthorFromFileName(message.MediaFileName, title), "Unknown artist");

            var item = DataContext as ChatMediaItemViewModel;
            if (item != null)
                return FirstNonEmpty(item.MediaPerformer, ExtractAuthorFromFileName(item.MediaFileName, title), "Unknown artist");

            return "Unknown artist";
        }

        private ImageSource GetPreviewImageSource()
        {
            var message = DataContext as ChatMessageViewModel;
            if (message != null) return message.MediaPreviewImageSource;

            var item = DataContext as ChatMediaItemViewModel;
            if (item != null) return item.MediaPreviewImageSource;

            return null;
        }

        private static string UsefulTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            if (string.Equals(value, "Audio", StringComparison.OrdinalIgnoreCase)) return null;
            if (string.Equals(value, "Voice message", StringComparison.OrdinalIgnoreCase)) return null;
            return value;
        }

        private static string FileTitle(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            try
            {
                var name = Path.GetFileNameWithoutExtension(fileName.Trim());
                return string.IsNullOrWhiteSpace(name) ? fileName.Trim() : name;
            }
            catch
            {
                return fileName.Trim();
            }
        }

        private static string ExtractAuthorFromFileName(string fileName, string title)
        {
            var name = FileTitle(fileName);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(title)) return null;

            var separator = name.IndexOf(" - ", StringComparison.Ordinal);
            if (separator <= 0) return null;

            var possibleAuthor = name.Substring(0, separator).Trim();
            var possibleTitle = name.Substring(separator + 3).Trim();
            if (possibleAuthor.Length == 0 || possibleTitle.Length == 0) return null;

            if (string.Equals(possibleTitle, title, StringComparison.OrdinalIgnoreCase))
                return possibleAuthor;

            return null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return string.Empty;
            for (var i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i])) return values[i].Trim();
            }
            return string.Empty;
        }

        private static string FormatDuration(TimeSpan value)
        {
            if (value.TotalHours >= 1)
                return ((int)value.TotalHours).ToString() + ":" + value.Minutes.ToString("00") + ":" + value.Seconds.ToString("00");
            return ((int)value.TotalMinutes).ToString() + ":" + value.Seconds.ToString("00");
        }

        private double ClampRatio(double ratio)
        {
            if (ratio < 0) return 0;
            if (ratio > 1) return 1;
            return ratio;
        }

        private void SetMusicSeekSliderValue(double value)
        {
            value = ClampRatio(value);
            _ignoreSeekSliderValueChanged = true;
            try { MusicSeekSlider.Value = value; }
            finally { _ignoreSeekSliderValueChanged = false; }
        }

        private void RaisePlaybackEnded()
        {
            var handler = PlaybackEnded;
            if (handler != null) handler(this, new FfmpegAudioPlaybackEndedEventArgs(DataContext));
        }

        private void RaisePlaybackStarted()
        {
            var handler = PlaybackStarted;
            if (handler != null) handler(this, new FfmpegAudioPlaybackEndedEventArgs(DataContext));
        }

        private void RaiseNextRequested()
        {
            var handler = NextRequested;
            if (handler != null) handler(this, new FfmpegAudioPlaybackEndedEventArgs(DataContext));
        }

        private void RaisePreviousRequested()
        {
            var handler = PreviousRequested;
            if (handler != null) handler(this, new FfmpegAudioPlaybackEndedEventArgs(DataContext));
        }

        private bool IsAllowedMediaKind()
        {
            return string.Equals(MediaKind, "audio", StringComparison.OrdinalIgnoreCase);
        }
    }
}
