using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;

namespace Telegram
{
    internal static class HeaderGlassEffectHelper
    {
        private const float HeaderBlurAmount = 36.0f;
        private const float HeaderDarkOverlayOpacity = 0.32f;
        private const float AppBarBlurAmount = 26.0f;
        private const float AppBarDarkOverlayOpacity = 0.18f;
        private const double MinimumSurfaceSize = 1.0;
        private const double TopMaskOpacity = 0.00;
        private const double BottomMaskOpacity = 1.00;

        public static void AttachGradient(Grid gradientHost)
        {
            AttachGlassEffect(gradientHost, HeaderBlurAmount, true, HeaderDarkOverlayOpacity);
        }

        public static void AttachUniform(Grid target)
        {
            AttachGlassEffect(target, AppBarBlurAmount, false, AppBarDarkOverlayOpacity);
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

        private static void AttachGlassEffect(Grid target, float blurAmount, bool useGradientMask, float darkOverlayOpacity)
        {
            if (target == null) return;

            try
            {
                var targetVisual = ElementCompositionPreview.GetElementVisual(target);
                var compositor = targetVisual.Compositor;

                var backdropBrush = compositor.CreateBackdropBrush();

                var blurEffect = new GaussianBlurEffect
                {
                    Name = "GlassEffect",
                    BlurAmount = blurAmount,
                    BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Balanced,
                    Source = new CompositionEffectSourceParameter("source")
                };

                var effectFactory = compositor.CreateEffectFactory(blurEffect);
                var effectBrush = effectFactory.CreateBrush();
                effectBrush.SetSourceParameter("source", backdropBrush);

                var containerVisual = compositor.CreateContainerVisual();

                var blurVisual = compositor.CreateSpriteVisual();
                var darkVisual = compositor.CreateSpriteVisual();
                darkVisual.Opacity = darkOverlayOpacity;

                var sizeAnimation = compositor.CreateExpressionAnimation("target.Size");
                sizeAnimation.SetReferenceParameter("target", targetVisual);
                containerVisual.StartAnimation("Size", sizeAnimation);
                blurVisual.StartAnimation("Size", sizeAnimation);
                darkVisual.StartAnimation("Size", sizeAnimation);

                CompositionGraphicsDevice graphicsDevice = null;
                CompositionMaskBrush blurMaskBrush = null;
                CompositionMaskBrush darkMaskBrush = null;

                if (useGradientMask)
                {
                    var canvasDevice = CanvasDevice.GetSharedDevice();
                    graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, canvasDevice);

                    blurMaskBrush = compositor.CreateMaskBrush();
                    blurMaskBrush.Source = effectBrush;
                    blurVisual.Brush = blurMaskBrush;

                    darkMaskBrush = compositor.CreateMaskBrush();
                    darkMaskBrush.Source = compositor.CreateColorBrush(Colors.Black);
                    darkVisual.Brush = darkMaskBrush;
                }
                else
                {
                    blurVisual.Brush = effectBrush;
                    darkVisual.Brush = compositor.CreateColorBrush(Colors.Black);
                }

                containerVisual.Children.InsertAtBottom(blurVisual);
                containerVisual.Children.InsertAtTop(darkVisual);

                ElementCompositionPreview.SetElementChildVisual(target, containerVisual);

                if (useGradientMask)
                {
                    UpdateGradientMask(target, compositor, graphicsDevice, blurMaskBrush, darkMaskBrush);

                    target.SizeChanged += delegate
                    {
                        UpdateGradientMask(target, compositor, graphicsDevice, blurMaskBrush, darkMaskBrush);
                    };
                }
            }
            catch
            {
                AttachFallbackGlassEffect(target, blurAmount, darkOverlayOpacity);
            }
        }

        private static void UpdateGradientMask(
            Grid target,
            Compositor compositor,
            CompositionGraphicsDevice graphicsDevice,
            CompositionMaskBrush blurMaskBrush,
            CompositionMaskBrush darkMaskBrush)
        {
            if (target == null || compositor == null || graphicsDevice == null || blurMaskBrush == null || darkMaskBrush == null) return;

            var width = Math.Max(target.ActualWidth, MinimumSurfaceSize);
            var height = Math.Max(target.ActualHeight, MinimumSurfaceSize);

            if (width <= MinimumSurfaceSize || height <= MinimumSurfaceSize) return;

            try
            {
                var surface = graphicsDevice.CreateDrawingSurface(
                    new Size(width, height),
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    DirectXAlphaMode.Premultiplied);

                using (var session = CanvasComposition.CreateDrawingSession(surface))
                {
                    session.Clear(Colors.Transparent);

                    var pixelHeight = Math.Max(1, (int)Math.Ceiling(height));
                    for (var y = 0; y < pixelHeight; y++)
                    {
                        var position = pixelHeight <= 1 ? 1.0 : (double)y / (double)(pixelHeight - 1);
                        var smooth = SmoothStep(0.00, 0.72, position);
                        var maskOpacity = TopMaskOpacity + (BottomMaskOpacity - TopMaskOpacity) * smooth;
                        var alpha = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(255.0 * maskOpacity)));

                        session.FillRectangle(
                            0,
                            y,
                            (float)width,
                            1.0f,
                            Color.FromArgb(alpha, 255, 255, 255));
                    }
                }

                var blurSurfaceBrush = compositor.CreateSurfaceBrush(surface);
                blurSurfaceBrush.Stretch = CompositionStretch.Fill;
                blurMaskBrush.Mask = blurSurfaceBrush;

                var darkSurfaceBrush = compositor.CreateSurfaceBrush(surface);
                darkSurfaceBrush.Stretch = CompositionStretch.Fill;
                darkMaskBrush.Mask = darkSurfaceBrush;
            }
            catch
            {
                blurMaskBrush.Mask = null;
                darkMaskBrush.Mask = null;
            }
        }

        private static double SmoothStep(double edge0, double edge1, double value)
        {
            var t = (value - edge0) / Math.Max(edge1 - edge0, 0.0001);
            t = Math.Max(0.0, Math.Min(1.0, t));
            return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
        }

        private static void AttachFallbackGlassEffect(UIElement target, float blurAmount, float darkOverlayOpacity)
        {
            try
            {
                var targetVisual = ElementCompositionPreview.GetElementVisual(target);
                var compositor = targetVisual.Compositor;
                var backdropBrush = compositor.CreateBackdropBrush();

                var blurEffect = new GaussianBlurEffect
                {
                    Name = "GlassEffectFallback",
                    BlurAmount = blurAmount,
                    BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Balanced,
                    Source = new CompositionEffectSourceParameter("source")
                };

                var effectFactory = compositor.CreateEffectFactory(blurEffect);
                var effectBrush = effectFactory.CreateBrush();
                effectBrush.SetSourceParameter("source", backdropBrush);

                var containerVisual = compositor.CreateContainerVisual();

                var blurVisual = compositor.CreateSpriteVisual();
                blurVisual.Brush = effectBrush;
                blurVisual.Opacity = 0.96f;

                var darkVisual = compositor.CreateSpriteVisual();
                darkVisual.Brush = compositor.CreateColorBrush(Colors.Black);
                darkVisual.Opacity = darkOverlayOpacity;

                var sizeAnimation = compositor.CreateExpressionAnimation("target.Size");
                sizeAnimation.SetReferenceParameter("target", targetVisual);
                containerVisual.StartAnimation("Size", sizeAnimation);
                blurVisual.StartAnimation("Size", sizeAnimation);
                darkVisual.StartAnimation("Size", sizeAnimation);

                containerVisual.Children.InsertAtBottom(blurVisual);
                containerVisual.Children.InsertAtTop(darkVisual);

                ElementCompositionPreview.SetElementChildVisual(target, containerVisual);
            }
            catch
            {
                Detach(target);
            }
        }
    }
}
