using System;
using Microsoft.Graphics.Canvas.Effects;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace Telegram
{
    internal static class FluentGlassEffectHelper
    {
        private const string SourceParameterName = "source";
        private const float TopBlurAmount = 8.0f;
        private const float BottomBlurAmount = 9.0f;
        private const float LightTintOpacity = 0.92f;
        private const float DarkTintOpacity = 0.88f;
        private const float LightLuminosityOpacity = 0.03f;
        private const float DarkLuminosityOpacity = 0.06f;
        private const double MinimumSize = 1.0;

        // The glass used to be built from a Win2D PixelShaderEffect. That can never work here:
        // Windows.UI.Composition accepts only the fixed set of effects its own engine implements,
        // and a custom pixel shader is not one of them, so Compositor.CreateEffectFactory always
        // rejected it with ArgumentException "Unsupported effect type".
        //
        // PixelShaderEffect.IsSupported did not guard against it either - that call only answers
        // whether the Win2D device can run the shader, not whether the compositor will take it.
        // So every attach decoded and compiled the shader, spun up a Win2D device, threw, and
        // fell through to the blur below, which is what had been rendering all along. With glass
        // re-applied on navigation and on every pinned-bar resize, that produced the repeated
        // bursts of 'System.ArgumentException in Telegram.McgInterop.dll'.
        //
        // The shader path, its 5 KB of embedded bytecode and the Win2D device are gone; only the
        // blur remains, so there is nothing left to throw.

        public static void AttachTopBar(FrameworkElement target)
        {
            Attach(target, TopBlurAmount, GetDefaultTopBarColor());
        }

        public static void AttachTopBar(FrameworkElement target, Brush baseBrush)
        {
            Attach(target, TopBlurAmount, ResolveBrushColor(baseBrush, GetDefaultTopBarColor()));
        }

        public static void AttachBottomBar(FrameworkElement target)
        {
            Attach(target, BottomBlurAmount, GetDefaultBottomBarColor());
        }

        public static void AttachBottomBar(FrameworkElement target, Brush baseBrush)
        {
            Attach(target, BottomBlurAmount, ResolveBrushColor(baseBrush, GetDefaultBottomBarColor()));
        }

        public static void Detach(UIElement target)
        {
            if (target == null) return;

            try
            {
                ElementCompositionPreview.SetElementChildVisual(target, null);
            }
            catch
            {
            }
        }

        private static void Attach(FrameworkElement target, float blurAmount, Color tintColor)
        {
            if (target == null) return;

            var width = Math.Max(target.ActualWidth, MinimumSize);
            var height = Math.Max(target.ActualHeight, MinimumSize);
            if (width <= MinimumSize || height <= MinimumSize) return;

            AttachBlur(target, blurAmount, tintColor);
        }

        private static void AttachBlur(FrameworkElement target, float blurAmount, Color tintColor)
        {
            try
            {
                var targetVisual = ElementCompositionPreview.GetElementVisual(target);
                var compositor = targetVisual.Compositor;
                var backdropBrush = compositor.CreateBackdropBrush();

                var blurEffect = new GaussianBlurEffect
                {
                    Name = "FluentGlassFallback",
                    BlurAmount = blurAmount * 4.0f,
                    BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Balanced,
                    Source = new CompositionEffectSourceParameter(SourceParameterName)
                };

                var effectFactory = compositor.CreateEffectFactory(blurEffect);
                var effectBrush = effectFactory.CreateBrush();
                effectBrush.SetSourceParameter(SourceParameterName, backdropBrush);

                var containerVisual = compositor.CreateContainerVisual();
                var blurVisual = compositor.CreateSpriteVisual();
                var tintVisual = compositor.CreateSpriteVisual();

                blurVisual.Brush = effectBrush;
                blurVisual.Opacity = 1.0f;
                tintVisual.Brush = compositor.CreateColorBrush(tintColor);
                tintVisual.Opacity = GetTintOpacity(tintColor);

                var sizeAnimation = compositor.CreateExpressionAnimation("target.Size");
                sizeAnimation.SetReferenceParameter("target", targetVisual);
                containerVisual.StartAnimation("Size", sizeAnimation);
                blurVisual.StartAnimation("Size", sizeAnimation);
                tintVisual.StartAnimation("Size", sizeAnimation);

                containerVisual.Children.InsertAtBottom(blurVisual);
                containerVisual.Children.InsertAtTop(tintVisual);

                ElementCompositionPreview.SetElementChildVisual(target, containerVisual);
            }
            catch
            {
                Detach(target);
            }
        }

        private static Color ResolveBrushColor(Brush brush, Color fallback)
        {
            var solid = brush as SolidColorBrush;
            if (solid == null)
                return fallback;

            var color = solid.Color;
            return Color.FromArgb(255, color.R, color.G, color.B);
        }

        private static float GetTintOpacity(Color color)
        {
            return IsLightColor(color) ? LightTintOpacity : DarkTintOpacity;
        }

        private static float GetLuminosityOpacity(Color color)
        {
            return IsLightColor(color) ? LightLuminosityOpacity : DarkLuminosityOpacity;
        }

        private static bool IsLightColor(Color color)
        {
            return GetLuminance(color) >= 0.5f;
        }

        private static float GetLuminance(Color color)
        {
            return (color.R * 0.2126f + color.G * 0.7152f + color.B * 0.0722f) / 255.0f;
        }

        private static Color GetDefaultTopBarColor()
        {
            return IsDarkTheme()
                ? Color.FromArgb(255, 32, 32, 32)
                : Color.FromArgb(255, 243, 243, 243);
        }

        private static Color GetDefaultBottomBarColor()
        {
            return IsDarkTheme()
                ? Color.FromArgb(255, 31, 31, 31)
                : Color.FromArgb(255, 247, 247, 247);
        }

        private static bool IsDarkTheme()
        {
            try
            {
                return Application.Current.RequestedTheme == ApplicationTheme.Dark;
            }
            catch
            {
                return true;
            }
        }

    }
}
