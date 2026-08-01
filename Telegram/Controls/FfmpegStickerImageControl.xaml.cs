using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FFmpegInterop;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Telegram.Controls
{
    public sealed partial class FfmpegStickerImageControl : UserControl
    {
        public static readonly DependencyProperty SourceUriProperty =
            DependencyProperty.Register("SourceUri", typeof(string), typeof(FfmpegStickerImageControl), new PropertyMetadata(null, OnSourceUriChanged));

        public static readonly DependencyProperty FallbackUriProperty =
            DependencyProperty.Register("FallbackUri", typeof(string), typeof(FfmpegStickerImageControl), new PropertyMetadata(null, OnFallbackUriChanged));

        public static readonly DependencyProperty ImageStretchProperty =
            DependencyProperty.Register("ImageStretch", typeof(Stretch), typeof(FfmpegStickerImageControl), new PropertyMetadata(Stretch.Uniform, OnImageStretchChanged));

        public static readonly DependencyProperty ExpectedDurationSecondsProperty =
            DependencyProperty.Register("ExpectedDurationSeconds", typeof(int), typeof(FfmpegStickerImageControl), new PropertyMetadata(0, OnExpectedDurationSecondsChanged));

        public static readonly DependencyProperty MediaKindProperty =
            DependencyProperty.Register("MediaKind", typeof(string), typeof(FfmpegStickerImageControl), new PropertyMetadata(null, OnMediaKindChanged));

        private const double MaxStickerEdge = 196.0;
        private const double MinStickerEdge = 1.0;

        private object _interopObject;
        private object _streamObject;
        private int _version;
        private bool _isPrepared;
        private bool _isShowingFallback;
        private DateTime _lastMediaOpenedUtc;
        private TimeSpan _currentLoopDuration = TimeSpan.Zero;

        public FfmpegStickerImageControl()
        {
            this.InitializeComponent();
            MaxWidth = MaxStickerEdge;
            MaxHeight = MaxStickerEdge;

            // Keep the actual sticker renderer centered inside its requested cell.
            // This is explicit because older Windows 10 Mobile builds may arrange
            // Image/MediaElement at the leading edge when only MaxWidth/MaxHeight are set.
            HorizontalContentAlignment = HorizontalAlignment.Center;
            VerticalContentAlignment = VerticalAlignment.Center;
            RasterImage.HorizontalAlignment = HorizontalAlignment.Center;
            RasterImage.VerticalAlignment = VerticalAlignment.Center;
            FfmpegHost.HorizontalAlignment = HorizontalAlignment.Center;
            FfmpegHost.VerticalAlignment = VerticalAlignment.Center;
            FfmpegImagePlayer.HorizontalAlignment = HorizontalAlignment.Center;
            FfmpegImagePlayer.VerticalAlignment = VerticalAlignment.Center;

            SizeChanged += FfmpegStickerImageControl_SizeChanged;
        }

        private void FfmpegStickerImageControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var width = ActualWidth;
            var height = ActualHeight;

            if (IsUsableFinite(width) && IsUsableFinite(height))
            {
                width = ClampStickerDimension(width);
                height = ClampStickerDimension(height);

                RasterImage.Width = width;
                RasterImage.Height = height;
                FfmpegHost.Width = width;
                FfmpegHost.Height = height;
                FfmpegImagePlayer.Width = width;
                FfmpegImagePlayer.Height = height;
            }

            UpdateClip();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var target = GetSafeMeasureSize(availableSize);
            base.MeasureOverride(target);
            return target;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var target = GetSafeArrangeSize(finalSize);
            var arranged = base.ArrangeOverride(target);
            UpdateClip();
            return arranged;
        }

        private Size GetSafeMeasureSize(Size availableSize)
        {
            var width = GetExplicitOrAvailableSize(Width, availableSize.Width);
            var height = GetExplicitOrAvailableSize(Height, availableSize.Height);

            if (double.IsNaN(Width) && double.IsNaN(Height))
            {
                width = ClampStickerDimension(width);
                height = ClampStickerDimension(height);
                if (width <= MinStickerEdge) width = MaxStickerEdge;
                if (height <= MinStickerEdge) height = MaxStickerEdge;
            }
            else if (double.IsNaN(Width))
            {
                width = height;
            }
            else if (double.IsNaN(Height))
            {
                height = width;
            }

            return new Size(ClampStickerDimension(width), ClampStickerDimension(height));
        }

        private Size GetSafeArrangeSize(Size finalSize)
        {
            var width = double.IsNaN(Width) ? finalSize.Width : Width;
            var height = double.IsNaN(Height) ? finalSize.Height : Height;
            if (!IsUsableFinite(width)) width = MaxStickerEdge;
            if (!IsUsableFinite(height)) height = MaxStickerEdge;
            return new Size(ClampStickerDimension(width), ClampStickerDimension(height));
        }

        private static double GetExplicitOrAvailableSize(double explicitSize, double availableSize)
        {
            if (IsUsableFinite(explicitSize)) return explicitSize;
            if (IsUsableFinite(availableSize)) return availableSize;
            return MaxStickerEdge;
        }

        private static bool IsUsableFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > MinStickerEdge;
        }

        private static double ClampStickerDimension(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return MaxStickerEdge;
            if (value < MinStickerEdge) return MinStickerEdge;
            if (value > MaxStickerEdge) return MaxStickerEdge;
            return value;
        }

        private void UpdateClip()
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (!IsUsableFinite(width) || !IsUsableFinite(height))
            {
                Clip = null;
                return;
            }

            width = ClampStickerDimension(width);
            height = ClampStickerDimension(height);
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
        }

        public string SourceUri
        {
            get { return (string)GetValue(SourceUriProperty); }
            set { SetValue(SourceUriProperty, value); }
        }

        public string FallbackUri
        {
            get { return (string)GetValue(FallbackUriProperty); }
            set { SetValue(FallbackUriProperty, value); }
        }

        public Stretch ImageStretch
        {
            get { return (Stretch)GetValue(ImageStretchProperty); }
            set { SetValue(ImageStretchProperty, value); }
        }

        public int ExpectedDurationSeconds
        {
            get { return (int)GetValue(ExpectedDurationSecondsProperty); }
            set { SetValue(ExpectedDurationSecondsProperty, value); }
        }

        public string MediaKind
        {
            get { return (string)GetValue(MediaKindProperty); }
            set { SetValue(MediaKindProperty, value); }
        }

        private static void OnSourceUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegStickerImageControl;
            if (control == null) return;
            control.ResetSource();
            if (control.IsAllowedMediaKind())
                control.PrepareSource();
        }

        private static void OnFallbackUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegStickerImageControl;
            if (control == null) return;
            if (!string.IsNullOrEmpty(control.SourceUri) || !string.IsNullOrEmpty(control.FallbackUri))
            {
                control.ResetSource();
                if (control.IsAllowedMediaKind())
                    control.PrepareSource();
            }
        }

        private static void OnImageStretchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegStickerImageControl;
            if (control == null) return;
            control.RasterImage.Stretch = control.ImageStretch;
            control.FfmpegImagePlayer.Stretch = control.ImageStretch;
        }

        private static void OnExpectedDurationSecondsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegStickerImageControl;
            if (control == null) return;
            control.ApplyPlaybackSpeed();
        }

        private static void OnMediaKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as FfmpegStickerImageControl;
            if (control == null) return;
            control.ResetSource();
            if (control.IsAllowedMediaKind() && (!string.IsNullOrEmpty(control.SourceUri) || !string.IsNullOrEmpty(control.FallbackUri)))
                control.PrepareSource();
        }

        private void FfmpegImagePlayer_Loaded(object sender, RoutedEventArgs e)
        {
            RasterImage.Stretch = ImageStretch;
            FfmpegImagePlayer.Stretch = ImageStretch;
            if (!_isPrepared && !string.IsNullOrEmpty(SourceUri) && IsAllowedMediaKind())
                PrepareSource();
        }

        private void FfmpegImagePlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            _isPrepared = true;
            _isShowingFallback = false;
            _lastMediaOpenedUtc = DateTime.UtcNow;
            ShowLoading(false);
            RasterImage.Source = null;
            RasterImage.Visibility = Visibility.Collapsed;
            FfmpegHost.Visibility = Visibility.Visible;
            FfmpegImagePlayer.Visibility = Visibility.Visible;
            ApplyPlaybackSpeed();
            try { FfmpegImagePlayer.Play(); }
            catch { }
        }

        private async void FfmpegImagePlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            var version = _version;
            try
            {
                if (ShouldLoopFfmpeg(SourceUri))
                {
                    var delay = GetLoopDelay();
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay);

                    if (version != _version) return;
                    FfmpegImagePlayer.Position = TimeSpan.Zero;
                    ApplyPlaybackSpeed();
                    _lastMediaOpenedUtc = DateTime.UtcNow;
                    FfmpegImagePlayer.Play();
                }
                else
                {
                    FfmpegImagePlayer.Pause();
                }
            }
            catch
            {
            }
        }

        private async void FfmpegImagePlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            var version = _version;
            Debug.WriteLine("TG_FFMPEG_STICKER failed uri=" + Safe(SourceUri) + " error=" + Safe(e == null ? null : e.ErrorMessage));
            FfmpegImagePlayer.Visibility = Visibility.Collapsed;
            FfmpegHost.Visibility = Visibility.Collapsed;
            ShowLoading(false);

            if (version == _version && !_isShowingFallback)
                await TryShowFallbackAsync(version, false);
        }

        private async void PrepareSource()
        {
            var uri = SourceUri;
            var version = ++_version;

            if (!IsAllowedMediaKind())
            {
                ShowLoading(false);
                return;
            }

            if (string.IsNullOrEmpty(uri))
            {
                await TryShowFallbackAsync(version, true);
                return;
            }

            // Do not leave virtualized sticker cells empty while WEBP/WEBM is being prepared.
            // The JPG/PNG thumbnail is opaque, but it is stable and prevents blank stickers on W10M.
            await TryShowFallbackAsync(version, true);
            ShowLoading(!_isShowingFallback);

            if (IsTgsUri(uri))
            {
                // TDLib animated emoji often arrive as TGS (Lottie). This build has no TGS renderer,
                // so keep the static emoji/sticker fallback visible instead of flashing an empty player.
                ShowLoading(false);
                return;
            }

            if (IsWebpUri(uri))
            {
                if (await TryShowWebpWithFfmpegBitmapDecoderAsync(uri, version)) return;
                if (await TryShowRasterAsync(uri, version, false, true)) return;

                // MediaElement/MediaStreamSource can show WEBP on some builds, but it is the path
                // that produced dirty transparent pixels. Use it only if there is no thumbnail
                // fallback at all; otherwise keep the stable fallback visible.
                if (!_isShowingFallback && await TryShowFfmpegMediaAsync(uri, version)) return;

                if (version == _version)
                {
                    Debug.WriteLine("TG_FFMPEG_STICKER static webp decode failed uri=" + Safe(uri));
                    ShowLoading(false);
                    if (!_isShowingFallback)
                        await TryShowFallbackAsync(version, false);
                }
                return;
            }

            if (ShouldUseFfmpeg(uri))
            {
                if (await TryShowFfmpegMediaAsync(uri, version)) return;
                if (version == _version)
                {
                    ShowLoading(false);
                    if (!_isShowingFallback)
                        await TryShowFallbackAsync(version, false);
                }
                return;
            }

            if (await TryShowRasterAsync(uri, version, true, false)) return;
            if (version == _version)
            {
                ShowLoading(false);
                if (!_isShowingFallback)
                    await TryShowFallbackAsync(version, false);
            }
        }

        private async Task<bool> TryShowWebpWithFfmpegBitmapDecoderAsync(string uri, int version)
        {
            try
            {
                var file = await GetStorageFileAsync(uri);
                if (version != _version || file == null) return false;

                var bitmap = await FfmpegWebpBitmapDecoder.TryDecodeWebpAsync(file);
                if (version != _version || bitmap == null) return false;

                await ShowRasterBitmapAsync(bitmap, version, false);
                Debug.WriteLine("TG_FFMPEG_STICKER webp decoded by ffmpeg uri=" + Safe(uri));
                return version == _version;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_FFMPEG_STICKER webp ffmpeg bitmap failed " + ex.GetType().Name);
                return false;
            }
        }

        private async Task<bool> TryShowFfmpegMediaAsync(string uri, int version)
        {
            try
            {
                var file = await GetStorageFileAsync(uri);
                if (version != _version || file == null) return false;

                var stream = await file.OpenReadAsync();
                if (version != _version || stream == null) return false;

                var interop = FFmpegInteropMSS.CreateFFmpegInteropMSSFromStream(stream, false, true);
                if (version != _version || interop == null) return false;

                var source = interop.GetMediaStreamSource();
                if (version != _version || source == null) return false;

                _streamObject = stream;
                _interopObject = interop;
                FfmpegImagePlayer.Stretch = ImageStretch;
                FfmpegImagePlayer.Source = null;
                FfmpegImagePlayer.PlaybackRate = 1.0;
                FfmpegHost.Visibility = Visibility.Collapsed;
                FfmpegImagePlayer.Visibility = Visibility.Collapsed;
                FfmpegImagePlayer.SetMediaStreamSource(source);
                FfmpegImagePlayer.Play();
                Debug.WriteLine("TG_FFMPEG_STICKER ffmpeg prepared uri=" + Safe(uri));
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("TG_FFMPEG_STICKER ffmpeg prepare exception uri=" + Safe(uri) + " error=" + ex.GetType().Name);
                return false;
            }
        }

        private async Task<bool> TryShowFallbackAsync(int version, bool quiet)
        {
            var fallback = FallbackUri;
            if (string.IsNullOrEmpty(fallback)) return false;
            if (!string.IsNullOrEmpty(SourceUri) && SameUri(fallback, SourceUri)) return false;

            var shown = await TryShowRasterAsync(fallback, version, true, quiet);
            if (shown)
            {
                _isShowingFallback = true;
                ShowLoading(false);
            }
            return shown;
        }

        private async Task<bool> TryShowRasterAsync(string uri, int version, bool allowBitmapImageFallback, bool keepExistingOnFailure)
        {
            try
            {
                var file = await GetStorageFileAsync(uri);
                if (version != _version || file == null) return false;

                using (var stream = await file.OpenReadAsync())
                {
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    if (version != _version || bitmap == null) return false;

                    var bitmapSource = new SoftwareBitmapSource();
                    await bitmapSource.SetBitmapAsync(bitmap);
                    if (version != _version) return false;

                    await ShowRasterBitmapAsync(bitmapSource, version, false);
                    return version == _version;
                }
            }
            catch
            {
                if (!allowBitmapImageFallback)
                {
                    if (!keepExistingOnFailure)
                    {
                        RasterImage.Source = null;
                        RasterImage.Visibility = Visibility.Collapsed;
                        _isShowingFallback = false;
                    }
                    return false;
                }

                try
                {
                    var image = new BitmapImage(new Uri(uri));
                    image.DecodePixelWidth = (int)MaxStickerEdge;
                    image.DecodePixelHeight = (int)MaxStickerEdge;
                    await ShowRasterBitmapAsync(image, version, false);
                    return version == _version;
                }
                catch
                {
                    if (!keepExistingOnFailure)
                    {
                        RasterImage.Source = null;
                        RasterImage.Visibility = Visibility.Collapsed;
                        _isShowingFallback = false;
                    }
                    return false;
                }
            }
        }

        private async Task ShowRasterBitmapAsync(ImageSource source, int version, bool isFallback)
        {
            if (version != _version || source == null) return;

            try { FfmpegImagePlayer.Stop(); }
            catch { }
            FfmpegImagePlayer.Source = null;
            FfmpegImagePlayer.Visibility = Visibility.Collapsed;
            FfmpegHost.Visibility = Visibility.Collapsed;

            // Force the virtualized Image element to drop the old GPU texture before assigning a new alpha bitmap.
            RasterImage.Visibility = Visibility.Collapsed;
            RasterImage.Source = null;
            await Task.Yield();
            if (version != _version) return;

            RasterImage.Stretch = ImageStretch;
            RasterImage.MaxWidth = MaxStickerEdge;
            RasterImage.MaxHeight = MaxStickerEdge;
            FfmpegHost.MaxWidth = MaxStickerEdge;
            FfmpegHost.MaxHeight = MaxStickerEdge;
            FfmpegImagePlayer.MaxWidth = MaxStickerEdge;
            FfmpegImagePlayer.MaxHeight = MaxStickerEdge;
            RasterImage.Source = source;
            RasterImage.Visibility = Visibility.Visible;
            _isShowingFallback = isFallback;
            ShowLoading(false);
            _isPrepared = true;
        }

        private static async Task<StorageFile> GetStorageFileAsync(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return null;
            var parsed = new Uri(uri);
            if (parsed.IsFile)
                return await StorageFile.GetFileFromPathAsync(parsed.LocalPath);
            return await StorageFile.GetFileFromApplicationUriAsync(parsed);
        }

        private void ApplyPlaybackSpeed()
        {
            try
            {
                if (!ShouldLoopFfmpeg(SourceUri))
                {
                    FfmpegImagePlayer.PlaybackRate = 1.0;
                    _currentLoopDuration = TimeSpan.Zero;
                    return;
                }

                var target = GetTargetAnimationDuration();
                var natural = GetNaturalDuration();
                _currentLoopDuration = target;

                if (target <= TimeSpan.Zero || natural <= TimeSpan.Zero)
                {
                    FfmpegImagePlayer.PlaybackRate = 1.0;
                    return;
                }

                var rate = natural.TotalMilliseconds / target.TotalMilliseconds;
                if (rate < 0.25) rate = 0.25;
                if (rate > 1.0) rate = 1.0;
                FfmpegImagePlayer.PlaybackRate = rate;
            }
            catch
            {
            }
        }

        private TimeSpan GetLoopDelay()
        {
            if (_currentLoopDuration <= TimeSpan.Zero || _lastMediaOpenedUtc == DateTime.MinValue) return TimeSpan.Zero;
            var elapsed = DateTime.UtcNow - _lastMediaOpenedUtc;
            if (elapsed >= _currentLoopDuration) return TimeSpan.Zero;
            return _currentLoopDuration - elapsed;
        }

        private TimeSpan GetTargetAnimationDuration()
        {
            if (ExpectedDurationSeconds > 0)
                return TimeSpan.FromSeconds(ExpectedDurationSeconds);

            if (IsAnimatedStickerUri(SourceUri))
                return TimeSpan.FromSeconds(3);

            return TimeSpan.Zero;
        }

        private TimeSpan GetNaturalDuration()
        {
            try
            {
                var duration = FfmpegImagePlayer.NaturalDuration;
                if (duration.HasTimeSpan) return duration.TimeSpan;
            }
            catch
            {
            }
            return TimeSpan.Zero;
        }

        private void ResetSource()
        {
            _version++;
            try { FfmpegImagePlayer.Stop(); }
            catch { }
            FfmpegImagePlayer.Source = null;
            FfmpegImagePlayer.Visibility = Visibility.Collapsed;
            FfmpegHost.Visibility = Visibility.Collapsed;
            RasterImage.Source = null;
            RasterImage.Visibility = Visibility.Collapsed;
            _interopObject = null;
            _streamObject = null;
            _isPrepared = false;
            _isShowingFallback = false;
            _lastMediaOpenedUtc = DateTime.MinValue;
            _currentLoopDuration = TimeSpan.Zero;
            ShowLoading(false);
        }

        private void ShowLoading(bool show)
        {
            LoadingRing.IsActive = show;
            LoadingRing.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool IsAllowedMediaKind()
        {
            return string.Equals(MediaKind, "sticker", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldUseFfmpeg(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            var value = NormalizeUriForExtension(uri);
            return value.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWebpUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            var value = NormalizeUriForExtension(uri);
            return value.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTgsUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            var value = NormalizeUriForExtension(uri);
            return value.EndsWith(".tgs", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldLoopFfmpeg(string uri)
        {
            return IsAnimatedStickerUri(uri);
        }

        private static bool IsAnimatedStickerUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;
            var value = NormalizeUriForExtension(uri);
            return value.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeUriForExtension(string uri)
        {
            var value = uri.Trim();
            var cut = value.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) value = value.Substring(0, cut);
            return value;
        }

        private static bool SameUri(string a, string b)
        {
            return string.Equals(NormalizeUriForExtension(a ?? string.Empty), NormalizeUriForExtension(b ?? string.Empty), StringComparison.OrdinalIgnoreCase);
        }

        private static string Safe(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            return value.Replace("\r", " ").Replace("\n", " ");
        }
    }
}
