// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;

namespace Kit.Settings.UI.Helpers;

/// <summary>
/// Fluent 2 GPU-composited motion primitives shared across Kit module pages.
/// Respects Windows accessibility settings (<see cref="UISettings.AnimationsEnabled"/>)
/// and leaves XAML layout metrics untouched for deterministic responsive layout.
/// </summary>
public static class KitMotionHelper
{
    private static readonly UISettings UiSettings = new();

    public static bool IsAnimationsEnabled
    {
        get
        {
            try
            {
                return UiSettings.AnimationsEnabled;
            }
            catch
            {
                return true;
            }
        }
    }

    public static void PlayFadeIn(UIElement? element, float fromOpacity = 0.45f, int durationMs = 170)
    {
        if (element == null)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!IsAnimationsEnabled)
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Opacity = 1f;
            return;
        }

        var compositor = visual.Compositor;
        var easing = CreateFluentDecelerate(compositor);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, fromOpacity);
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation(nameof(Visual.Opacity), fade);
    }

    public static void PlaySlideFadeIn(UIElement? element, float offsetX = 0f, float offsetY = 8f, int durationMs = 200)
    {
        if (element == null)
        {
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!IsAnimationsEnabled)
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.StopAnimation("Translation");
            visual.Opacity = 1f;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }

        var compositor = visual.Compositor;
        var easing = CreateFluentDecelerate(compositor);
        var duration = TimeSpan.FromMilliseconds(durationMs);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, easing);
        fade.Duration = duration;

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(offsetX, offsetY, 0f));
        slide.InsertKeyFrame(1f, Vector3.Zero, easing);
        slide.Duration = duration;

        visual.StartAnimation(nameof(Visual.Opacity), fade);
        visual.StartAnimation("Translation", slide);
    }

    public static void PlayBadgePop(FrameworkElement? element, double size = 32d, int durationMs = 200)
    {
        if (element == null)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(element);
        float half = (float)(size / 2d);
        visual.CenterPoint = new Vector3(half, half, 0f);
        if (!IsAnimationsEnabled)
        {
            visual.StopAnimation(nameof(Visual.Scale));
            visual.Scale = Vector3.One;
            return;
        }

        var compositor = visual.Compositor;
        var easing = CreateFluentDecelerate(compositor);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(0.86f, 0.86f, 1f));
        scale.InsertKeyFrame(1f, Vector3.One, easing);
        scale.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation(nameof(Visual.Scale), scale);
    }

    public static void StartPulseRing(FrameworkElement? ring, double centerSize, float maxScale = 2.15f, float startOpacity = 0.6f, int durationMs = 1750)
    {
        if (ring == null)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(ring);
        float half = (float)(centerSize / 2d);
        visual.CenterPoint = new Vector3(half, half, 0f);

        if (!IsAnimationsEnabled)
        {
            StopPulseRing(ring);
            return;
        }

        var compositor = visual.Compositor;
        var easing = CreateFluentDecelerate(compositor);
        var duration = TimeSpan.FromMilliseconds(durationMs);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, Vector3.One);
        scale.InsertKeyFrame(0.82f, new Vector3(maxScale, maxScale, 1f), easing);
        scale.InsertKeyFrame(1f, new Vector3(maxScale, maxScale, 1f));
        scale.Duration = duration;
        scale.IterationBehavior = AnimationIterationBehavior.Forever;

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, startOpacity);
        opacity.InsertKeyFrame(0.82f, 0f, easing);
        opacity.InsertKeyFrame(1f, 0f);
        opacity.Duration = duration;
        opacity.IterationBehavior = AnimationIterationBehavior.Forever;

        visual.StartAnimation(nameof(Visual.Scale), scale);
        visual.StartAnimation(nameof(Visual.Opacity), opacity);
    }

    public static void StopPulseRing(FrameworkElement? ring)
    {
        if (ring == null)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(ring);
        visual.StopAnimation(nameof(Visual.Scale));
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.Scale = Vector3.One;
        visual.Opacity = 0f;
    }

    public static void AttachHoverLift(FrameworkElement? element, float liftY = -1.5f)
    {
        if (element == null)
        {
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        element.PointerEntered += (_, _) => AnimateTranslationY(element, liftY, 130);
        element.PointerExited += (_, _) => AnimateTranslationY(element, 0f, 150);
        element.PointerCaptureLost += (_, _) => AnimateTranslationY(element, 0f, 150);
        element.Unloaded += (_, _) => ResetTranslation(element);
    }

    public static Storyboard? StartDashFlow(Shape? shape, double cycleLength = 14d, double durationSeconds = 1.6)
    {
        if (shape == null || !IsAnimationsEnabled)
        {
            return null;
        }

        var animation = new DoubleAnimation
        {
            From = cycleLength,
            To = 0d,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(animation, shape);
        Storyboard.SetTargetProperty(animation, nameof(Shape.StrokeDashOffset));
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
        return storyboard;
    }

    private static void AnimateTranslationY(UIElement element, float targetY, int durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        if (!IsAnimationsEnabled)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }

        var compositor = visual.Compositor;
        var easing = CreateFluentDecelerate(compositor);
        var anim = compositor.CreateVector3KeyFrameAnimation();
        anim.InsertKeyFrame(1f, new Vector3(0f, targetY, 0f), easing);
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Translation", anim);
    }

    private static void ResetTranslation(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private static CubicBezierEasingFunction CreateFluentDecelerate(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));
}
