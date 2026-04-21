using System;
using System.Windows;
using System.Windows.Media.Animation;
using MaterialDesignThemes.Wpf.Transitions;

namespace XIVLauncher.Windows.Transitions;

public sealed class StableFadeWipe : ITransitionWipe
{
    private readonly SineEase sineEase = new();
    private readonly KeyTime zeroKeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero);

    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(0.5);

    public void Wipe(TransitionerSlide fromSlide, TransitionerSlide toSlide, Point origin, IZIndexController zIndexController)
    {
        ArgumentNullException.ThrowIfNull(fromSlide);
        ArgumentNullException.ThrowIfNull(toSlide);
        ArgumentNullException.ThrowIfNull(zIndexController);

        var midpointKeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Duration.TotalSeconds / 2));

        var fromAnimation = new DoubleAnimationUsingKeyFrames();
        fromAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(1, zeroKeyTime));
        fromAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0, midpointKeyTime, sineEase));

        var toAnimation = new DoubleAnimationUsingKeyFrames();
        toAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0, zeroKeyTime));
        toAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, midpointKeyTime, sineEase));

        fromSlide.Opacity = 1;
        toSlide.Opacity = 0;

        toAnimation.Completed += (_, _) =>
        {
            toSlide.BeginAnimation(UIElement.OpacityProperty, null);
            fromSlide.Opacity = 0;
            toSlide.Opacity = 1;
        };

        fromAnimation.Completed += (_, _) =>
        {
            fromSlide.BeginAnimation(UIElement.OpacityProperty, null);
            fromSlide.Opacity = 0;
            toSlide.BeginAnimation(UIElement.OpacityProperty, toAnimation);
        };

        fromSlide.BeginAnimation(UIElement.OpacityProperty, fromAnimation);
        zIndexController.Stack(toSlide, fromSlide);
    }
}
