using System;
using System.Collections.Generic;
using System.Diagnostics;
using FFmpegInterop;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Telegram.Controls
{
    public sealed partial class FfmpegVideoPlayerControl : UserControl
    {
        public static readonly DependencyProperty SourceUriProperty =
            DependencyProperty.Register("SourceUri", typeof(string), typeof(FfmpegVideoPlayerControl), new PropertyMetadata(null, OnSourceUriChanged));

        public static readonly DependencyProperty IsDownloadingProperty =
            DependencyProperty.Register("IsDownloading", typeof(bool), typeof(FfmpegVideoPlayerControl), new PropertyMetadata(false, OnIsDownloadingChanged));

        public static readonly DependencyProperty PlayerStretchProperty =
            DependencyProperty.Register("PlayerStretch", typeof(Stretch), typeof(FfmpegVideoPlayerControl), new PropertyMetadata(Stretch.Uniform, OnPlayerStretchChanged));

        public static readonly DependencyProperty MediaKindProperty =
            DependencyProperty.Register("MediaKind", typeof(string), typeof(FfmpegVideoPlayerControl), new PropertyMetadata(null, OnMediaKindChanged));

        private readonly Dictionary<string, object> _interopObjects = new Dictionary<string, object>();
        private string _preparedKey;
        private const bool ForceAudioDecodeForMobile = true;
        private const bool ForceVideoDecodeForMobile = true;
        private int _blankRetries;
        private int _version;
        private bool _isPrepared;
        private bool _isPreparing;
        private bool _isPlaying;
        private bool _playWhenPrepared;
        private readonly DispatcherTimer _progressTimer;

        public FfmpegVideoPlayerControl()
        {
            this.InitializeComponent();
            _progressTimer = new DispatcherTimer();
            _progressTimer.Interval = TimeSpan.FromMilliseconds(180);
            _progressTimer.Tick += ProgressTimer_Tick;
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

        public Stretch PlayerStretch
        {
            get { return (Stretch)GetValue(PlayerStretchProperty); }
            set { SetValue(PlayerStretchProperty, value); }
        }

        public string MediaKind
        {
            get { return (string)GetValue(MediaKindProperty); }
            set { SetValue(MediaKindProperty, value); }
        }

        private static void OnSourceUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegVideoPlayerControl;
            if (control == null) return;
            control.ResetPlayer(true);
            control.ApplyVisualMode();
            if (control.IsAllowedMediaKind())
                control.PrepareSource(false);
        }

        private static void OnIsDownloadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegVideoPlayerControl;
            if (control == null) return;
            control.UpdateRoundControls();
            if (control.IsDownloading || control._isPrepared || string.IsNullOrEmpty(control.SourceUri) || !control.IsAllowedMediaKind()) return;
            control.PrepareSource(false);
        }

        private static void OnPlayerStretchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegVideoPlayerControl;
            if (control == null) return;
            control.Player.Stretch = control.PlayerStretch;
            control.ApplyVisualMode();
        }

        private static void OnMediaKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegVideoPlayerControl;
            if (control == null) return;
            control.ResetPlayer(true);
            control.ApplyVisualMode();
            if (control.IsAllowedMediaKind() && !string.IsNullOrEmpty(control.SourceUri))
                control.PrepareSource(false);
        }

        private void Player_Loaded(object sender, RoutedEventArgs e)
        {
            Player.Stretch = PlayerStretch;
            ApplyVisualMode();
            if (!_isPrepared && !string.IsNullOrEmpty(SourceUri) && IsAllowedMediaKind())
                PrepareSource(false);
        }

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("TG_FFMPEG_PLAYER opened uri=" + Safe(SourceUri) +
                " video=" + Player.NaturalVideoWidth.ToString() + "x" + Player.NaturalVideoHeight.ToString() +
                " forceVideoDecode=" + ForceVideoDecodeForMobile.ToString());

            if (Player.NaturalVideoWidth > 0 && Player.NaturalVideoHeight > 0)
            {
                _isPrepared = true;
                _isPreparing = false;
                UpdateRoundControls();
                return;
            }

            if (_blankRetries >= 2) return;
            _blankRetries++;
            ResetPlayer(false);
            PrepareSource(_playWhenPrepared);
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            _isPlaying = false;
            StopProgressTimer();
            UpdateRoundProgress();
            UpdateRoundControls();
            if (!IsDownloading) return;
            ResetPlayer(false);
            PrepareSource(false);
        }

        private void Player_CurrentStateChanged(object sender, RoutedEventArgs e)
        {
            _isPlaying = Player.CurrentState == MediaElementState.Playing;
            if (_isPlaying) StartProgressTimer();
            else StopProgressTimer();
            UpdateRoundControls();
        }

        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            Debug.WriteLine("TG_FFMPEG_PLAYER failed uri=" + Safe(SourceUri) +
                " forceVideoDecode=" + ForceVideoDecodeForMobile.ToString() +
                " error=" + Safe(e == null ? null : e.ErrorMessage));

            if (_blankRetries >= 3) return;

            _blankRetries++;
            ResetPlayer(false);
            PrepareSource(_playWhenPrepared);
        }

        public void RefreshGrowingFile()
        {
            if (string.IsNullOrEmpty(SourceUri)) return;
            var resume = _isPlaying;
            ResetPlayer(false);
            PrepareSource(resume);
        }

        public void PlayWhenReady()
        {
            if (string.IsNullOrEmpty(SourceUri)) return;
            if (!IsAllowedMediaKind()) return;
            if (IsPosterImageUri(SourceUri)) return;
            ResetPlayer(false);
            PrepareSource(true);
        }

        public void Play()
        {
            if (string.IsNullOrEmpty(SourceUri) || !IsAllowedMediaKind()) return;
            if (!_isPrepared)
            {
                PrepareSource(true);
                return;
            }

            try
            {
                Player.Play();
                _isPlaying = true;
                StartProgressTimer();
            }
            catch
            {
            }
            UpdateRoundControls();
        }

        public void Pause()
        {
            try { Player.Pause(); }
            catch { }
            _isPlaying = false;
            StopProgressTimer();
            UpdateRoundControls();
        }

        private async void PrepareSource(bool playWhenReady)
        {
            var uri = SourceUri;
            if (string.IsNullOrEmpty(uri)) return;
            if (!IsAllowedMediaKind()) return;
            if (IsPosterImageUri(uri))
            {
                Debug.WriteLine("TG_FFMPEG_PLAYER skip-poster uri=" + Safe(uri));
                return;
            }

            _playWhenPrepared = playWhenReady;
            var version = ++_version;
            _isPreparing = true;
            UpdateRoundControls();

            try
            {
                var file = await GetStorageFileFromUriAsync(uri);
                if (version != _version || file == null) return;

                var stream = await file.OpenReadAsync();
                if (version != _version || stream == null) return;

                var interop = FFmpegInteropMSS.CreateFFmpegInteropMSSFromStream(stream, ForceAudioDecodeForMobile, ForceVideoDecodeForMobile);
                if (version != _version || interop == null) return;

                var source = interop.GetMediaStreamSource();
                if (version != _version || source == null) return;

                var key = uri + "|mobile-nv12-pcm";
                _interopObjects[key] = interop;
                _preparedKey = key;
                Player.Source = null;
                Player.SetMediaStreamSource(source);
                _isPrepared = true;
                _isPreparing = false;

                if (_playWhenPrepared)
                {
                    Player.Play();
                    _isPlaying = true;
                    StartProgressTimer();
                }
                else
                {
                    _isPlaying = false;
                    StopProgressTimer();
                }

                _playWhenPrepared = false;
                UpdateRoundControls();
                Debug.WriteLine("TG_FFMPEG_PLAYER prepared uri=" + Safe(uri) + " forceVideoDecode=" + ForceVideoDecodeForMobile.ToString());
            }
            catch (Exception ex)
            {
                _isPreparing = false;
                UpdateRoundControls();
                Debug.WriteLine("TG_FFMPEG_PLAYER exception uri=" + Safe(uri) + " error=" + ex.GetType().Name);
            }
        }

        private static async System.Threading.Tasks.Task<StorageFile> GetStorageFileFromUriAsync(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return null;

            var sourceUri = new Uri(uri);
            if (sourceUri.Scheme == "file")
                return await StorageFile.GetFileFromPathAsync(sourceUri.LocalPath);
            if (sourceUri.Scheme == "ms-appdata" || sourceUri.Scheme == "ms-appx")
                return await StorageFile.GetFileFromApplicationUriAsync(sourceUri);

            return null;
        }

        private void ResetPlayer(bool resetMode)
        {
            _version++;
            try { Player.Stop(); }
            catch { }
            Player.Source = null;
            _preparedKey = null;
            _isPrepared = false;
            _isPreparing = false;
            _isPlaying = false;
            _playWhenPrepared = false;
            StopProgressTimer();
            _interopObjects.Clear();
            if (resetMode)
            {
                _blankRetries = 0;
            }
            UpdateRoundControls();
        }

        private bool IsAllowedMediaKind()
        {
            return string.Equals(MediaKind, "video", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(MediaKind, "roundvideo", StringComparison.OrdinalIgnoreCase);
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            return value.Replace("\r", " ").Replace("\n", " ");
        }

        private static bool IsPosterImageUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            var value = uri.Trim();
            var cut = value.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) value = value.Substring(0, cut);
            return value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
        }

        private void Root_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (!IsRoundVideo()) return;
            if (IsDownloading || _isPreparing) return;
            if (_isPlaying) Pause();
            else Play();
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsRoundVideo()) return;
            UpdateRoundMask();
            UpdateRoundProgress();
        }

        private void ApplyVisualMode()
        {
            var round = IsRoundVideo();
            Player.AreTransportControlsEnabled = !round;
            var placeholder = GetLocalBrush("TelegramMediaPlaceholderBrush") ?? new SolidColorBrush(Windows.UI.Colors.Transparent);
            Root.Background = round ? new SolidColorBrush(Windows.UI.Colors.Transparent) : placeholder;
            if (round)
                RoundMask.Fill = ResolveRoundMaskBrush();
            RoundMask.Visibility = round ? Visibility.Visible : Visibility.Collapsed;
            RoundBorder.Visibility = round ? Visibility.Visible : Visibility.Collapsed;
            RoundControls.Visibility = round ? Visibility.Visible : Visibility.Collapsed;
            RoundProgressArc.Visibility = round ? Visibility.Visible : Visibility.Collapsed;
            UpdateRoundMask();
            UpdateRoundControls();
        }

        private bool IsRoundVideo()
        {
            return string.Equals(MediaKind, "roundvideo", StringComparison.OrdinalIgnoreCase);
        }

        private Brush ResolveRoundMaskBrush()
        {
            var pageBrush = FindResourceBrushInParents("TelegramPageBackgroundBrush");
            if (pageBrush != null) return pageBrush;

            var backgroundBrush = FindNearestBackgroundBrush();
            if (backgroundBrush != null) return backgroundBrush;

            return GetLocalBrush("TelegramRoundMaskFallbackBrush") ??
                new SolidColorBrush(Windows.UI.Colors.Black);
        }

        private Brush FindResourceBrushInParents(string key)
        {
            var current = this as DependencyObject;
            for (var i = 0; i < 32 && current != null; i++)
            {
                var element = current as FrameworkElement;
                var brush = GetResourceBrush(element, key);
                if (brush != null) return brush;

                try { current = VisualTreeHelper.GetParent(current); }
                catch { current = null; }
            }

            return null;
        }

        private Brush FindNearestBackgroundBrush()
        {
            DependencyObject current = this;
            for (var i = 0; i < 32 && current != null; i++)
            {
                var panel = current as Panel;
                if (panel != null && IsUsableBrush(panel.Background)) return panel.Background;

                var border = current as Border;
                if (border != null && IsUsableBrush(border.Background)) return border.Background;

                var control = current as Control;
                if (control != null && IsUsableBrush(control.Background)) return control.Background;

                try { current = VisualTreeHelper.GetParent(current); }
                catch { current = null; }
            }

            return null;
        }

        // TryGetValue, not the indexer.
        //
        // ResourceDictionary's indexer is the WinRT IMap.Lookup, which throws when the key is
        // absent - "Cannot find a resource with the given key: ...". FindResourceBrushInParents
        // walks up to 32 ancestors and asks each one, and almost none of them carry the key, so a
        // single round-video mask used to raise up to 32 interop exceptions. That was the burst of
        // 'System.Exception in Telegram.McgInterop.dll'. Missing keys are the normal case here, so
        // they must not be signalled with exceptions.
        private static Brush TryGetResourceBrush(ResourceDictionary resources, string key)
        {
            if (resources == null || string.IsNullOrEmpty(key)) return null;

            object value;
            if (!resources.TryGetValue(key, out value)) return null;
            return value as Brush;
        }

        private Brush GetLocalBrush(string key)
        {
            return TryGetResourceBrush(Resources, key);
        }

        private static Brush GetResourceBrush(FrameworkElement element, string key)
        {
            if (element == null) return null;
            return TryGetResourceBrush(element.Resources, key);
        }

        private static bool IsUsableBrush(Brush brush)
        {
            if (brush == null) return false;
            var solid = brush as SolidColorBrush;
            return solid == null || solid.Color.A > 0;
        }

        private void ProgressTimer_Tick(object sender, object e)
        {
            UpdateRoundProgress();
            UpdateRoundControls();
        }

        private void StartProgressTimer()
        {
            if (!_progressTimer.IsEnabled) _progressTimer.Start();
        }

        private void StopProgressTimer()
        {
            if (_progressTimer.IsEnabled) _progressTimer.Stop();
        }

        private void UpdateRoundControls()
        {
            if (!IsRoundVideo() || RoundControls == null) return;
            var loading = IsDownloading || _isPreparing;
            RoundLoadingRing.IsActive = loading;
            RoundLoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            RoundPlayGlyph.Visibility = Visibility.Collapsed;
            RoundCenterButtonBackground.Visibility = Visibility.Collapsed;
            RoundPlayGlyph.Text = _isPlaying ? "\uE769" : "\uE768";
            RoundPlayGlyph.Opacity = 0.0;
            RoundTimeText.Text = FormatRoundTime();
            UpdateRoundProgress();
        }

        private string FormatRoundTime()
        {
            var duration = GetDuration();
            var position = GetPosition();
            if (_isPlaying && position.TotalMilliseconds > 0) return FormatTime(position);
            if (duration.TotalMilliseconds > 0) return FormatTime(duration);
            return "0:00";
        }

        private TimeSpan GetDuration()
        {
            try
            {
                if (Player.NaturalDuration.HasTimeSpan) return Player.NaturalDuration.TimeSpan;
            }
            catch
            {
            }
            return TimeSpan.Zero;
        }

        private TimeSpan GetPosition()
        {
            try { return Player.Position; }
            catch { return TimeSpan.Zero; }
        }

        private static string FormatTime(TimeSpan value)
        {
            if (value.TotalHours >= 1)
                return ((int)value.TotalHours).ToString() + ":" + value.Minutes.ToString("00") + ":" + value.Seconds.ToString("00");
            return ((int)value.TotalMinutes).ToString() + ":" + value.Seconds.ToString("00");
        }

        private void UpdateRoundProgress()
        {
            if (!IsRoundVideo() || RoundProgressArc == null) return;
            var duration = GetDuration();
            var position = GetPosition();
            var progress = 0d;
            if (duration.TotalMilliseconds > 0)
                progress = Math.Max(0, Math.Min(1, position.TotalMilliseconds / duration.TotalMilliseconds));

            RoundProgressArc.Data = BuildArcGeometry(progress);
            RoundProgressArc.Visibility = progress > 0.001 ? Visibility.Visible : Visibility.Collapsed;
        }

        private Geometry BuildArcGeometry(double progress)
        {
            var width = ActualWidth > 0 ? ActualWidth : Width;
            var height = ActualHeight > 0 ? ActualHeight : Height;
            if (width <= 0) width = 196;
            if (height <= 0) height = 196;
            var radius = Math.Max(1, Math.Min(width, height) / 2 - 3);
            var center = new Point(width / 2, height / 2);
            if (progress >= 0.999) progress = 0.999;
            var angle = -90 + progress * 360;
            var radians = angle * Math.PI / 180.0;
            var start = new Point(center.X, center.Y - radius);
            var end = new Point(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);

            var figure = new PathFigure();
            figure.StartPoint = start;
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = progress > 0.5
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        private void UpdateRoundMask()
        {
            if (RoundMask == null) return;
            RoundMask.Data = BuildRoundMaskGeometry();
        }

        private Geometry BuildRoundMaskGeometry()
        {
            var width = ActualWidth > 0 ? ActualWidth : Width;
            var height = ActualHeight > 0 ? ActualHeight : Height;
            if (width <= 0) width = 196;
            if (height <= 0) height = 196;

            var radius = Math.Min(width, height) / 2;
            var center = new Point(width / 2, height / 2);

            var outer = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true };
            outer.Segments.Add(new LineSegment { Point = new Point(width, 0) });
            outer.Segments.Add(new LineSegment { Point = new Point(width, height) });
            outer.Segments.Add(new LineSegment { Point = new Point(0, height) });

            var inner = new PathFigure { StartPoint = new Point(center.X, center.Y - radius), IsClosed = true };
            inner.Segments.Add(new ArcSegment { Point = new Point(center.X, center.Y + radius), Size = new Size(radius, radius), IsLargeArc = true, SweepDirection = SweepDirection.Clockwise });
            inner.Segments.Add(new ArcSegment { Point = new Point(center.X, center.Y - radius), Size = new Size(radius, radius), IsLargeArc = true, SweepDirection = SweepDirection.Clockwise });

            var geometry = new PathGeometry();
            geometry.FillRule = FillRule.EvenOdd;
            geometry.Figures.Add(outer);
            geometry.Figures.Add(inner);
            return geometry;
        }
    }
}
