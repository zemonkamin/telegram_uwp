using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Telegram.Models
{
    /// <summary>
    /// Shared message brushes. The base colours match TelegramBACKUP.zip;
    /// outgoing bubbles use the current Windows accent colour, slightly darkened.
    /// </summary>
    internal static class MessagePalette
    {
        private static bool _cachedIsLight;
        private static Color _cachedAccent;
        private static bool _cacheValid;

        private static SolidColorBrush _transparent;
        private static SolidColorBrush _backgroundIn;
        private static SolidColorBrush _backgroundOut;
        private static SolidColorBrush _foregroundIn;
        private static SolidColorBrush _foregroundOut;
        private static SolidColorBrush _subtleIn;
        private static SolidColorBrush _subtleOut;
        private static SolidColorBrush _headerIn;
        private static SolidColorBrush _headerOut;
        private static SolidColorBrush _overlayBackground;
        private static SolidColorBrush _serviceBackground;
        private static SolidColorBrush _mediaForeground;

        public static SolidColorBrush OverlayBackground
        {
            get
            {
                EnsureCache();
                return _overlayBackground;
            }
        }

        public static SolidColorBrush ServiceBackground
        {
            get
            {
                EnsureCache();
                return _serviceBackground;
            }
        }

        public static SolidColorBrush MediaForeground
        {
            get
            {
                EnsureCache();
                return _mediaForeground;
            }
        }

        public static SolidColorBrush Transparent
        {
            get
            {
                EnsureCache();
                return _transparent;
            }
        }

        public static SolidColorBrush Background(bool outgoing)
        {
            EnsureCache();
            return outgoing ? _backgroundOut : _backgroundIn;
        }

        public static SolidColorBrush Foreground(bool outgoing)
        {
            EnsureCache();
            return outgoing ? _foregroundOut : _foregroundIn;
        }

        public static SolidColorBrush SubtleLabel(bool outgoing)
        {
            EnsureCache();
            return outgoing ? _subtleOut : _subtleIn;
        }

        public static SolidColorBrush HeaderForeground(bool outgoing)
        {
            EnsureCache();
            return outgoing ? _headerOut : _headerIn;
        }

        private static void EnsureCache()
        {
            var isLight = IsLightTheme();
            var accent = GetAccentColor();
            if (_cacheValid && _cachedIsLight == isLight && ColorsEqual(_cachedAccent, accent)) return;

            _cachedIsLight = isLight;
            _cachedAccent = accent;
            _cacheValid = true;

            _transparent = new SolidColorBrush(Colors.Transparent);

            // Footer pills are retained for the current ChatPage media layout.
            _overlayBackground = Brush(0x54, 0x00, 0x00, 0x00);
            _serviceBackground = Brush(0x66, 0x7A, 0x8A, 0x96);
            _mediaForeground = Brush(0xFF, 0xFF, 0xFF, 0xFF);

            // TelegramBACKUP.zip message palette.
            _backgroundIn = isLight
                ? Brush(0xFF, 0xF3, 0xF3, 0xF3)
                : Brush(0xFF, 0x2D, 0x2D, 0x2D);

            // Keep the user's Windows accent, but make our bubbles 10% darker.
            _backgroundOut = new SolidColorBrush(Darken(accent, 0.90));

            _foregroundIn = isLight
                ? Brush(0xFF, 0x11, 0x11, 0x11)
                : Brush(0xFF, 0xFF, 0xFF, 0xFF);
            _foregroundOut = Brush(0xFF, 0xFF, 0xFF, 0xFF);

            _subtleIn = isLight
                ? Brush(0xFF, 0x66, 0x66, 0x66)
                : Brush(0xFF, 0xB2, 0xB2, 0xB2);
            _subtleOut = Brush(0xCD, 0xE6, 0xF5, 0xFF);

            _headerIn = new SolidColorBrush(accent);
            _headerOut = Brush(0xEB, 0xFF, 0xFF, 0xFF);
        }

        private static Color Darken(Color color, double factor)
        {
            if (factor < 0) factor = 0;
            if (factor > 1) factor = 1;

            return Color.FromArgb(
                color.A,
                (byte)(color.R * factor),
                (byte)(color.G * factor),
                (byte)(color.B * factor));
        }

        private static bool ColorsEqual(Color first, Color second)
        {
            return first.A == second.A &&
                   first.R == second.R &&
                   first.G == second.G &&
                   first.B == second.B;
        }

        private static Color GetAccentColor()
        {
            try
            {
                return new UISettings().GetColorValue(UIColorType.Accent);
            }
            catch
            {
                return Color.FromArgb(0xFF, 0x00, 0x84, 0xD3);
            }
        }

        private static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
        {
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        }

        private static bool IsLightTheme()
        {
            try
            {
                var color = new UISettings().GetColorValue(UIColorType.Background);
                return color.R + color.G + color.B > 384;
            }
            catch
            {
                try
                {
                    if (Application.Current != null)
                        return Application.Current.RequestedTheme == ApplicationTheme.Light;
                }
                catch
                {
                }
            }

            return false;
        }
    }
}
