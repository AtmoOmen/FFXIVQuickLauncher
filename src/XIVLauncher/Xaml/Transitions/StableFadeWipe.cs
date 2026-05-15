using System.Windows;
using System.Windows.Media.Animation;
using MaterialDesignThemes.Wpf.Transitions;

namespace XIVLauncher.Xaml.Transitions;

public sealed class StableFadeWipe : ITransitionWipe
{
    private readonly IEasingFunction modernEasing = new CubicEase { EasingMode = EasingMode.EaseOut };

    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(0.4);

    public void Wipe(TransitionerSlide fromSlide, TransitionerSlide toSlide, Point origin, IZIndexController zIndexController)
    {
        ArgumentNullException.ThrowIfNull(fromSlide);
        ArgumentNullException.ThrowIfNull(toSlide);
        ArgumentNullException.ThrowIfNull(zIndexController);

        var durationKeyTime = KeyTime.FromTimeSpan(Duration);
        var zeroKeyTime     = KeyTime.FromTimeSpan(TimeSpan.Zero);

        var fadeOutEndPoint  = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Duration.TotalSeconds * 0.55));
        var fadeInStartPoint = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Duration.TotalSeconds * 0.45));

        var outAnimation = new DoubleAnimationUsingKeyFrames();
        outAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, zeroKeyTime));
        outAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, fadeOutEndPoint, modernEasing));
        outAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, durationKeyTime));

        var inAnimation = new DoubleAnimationUsingKeyFrames();
        inAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, zeroKeyTime));
        inAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, fadeInStartPoint));
        inAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, durationKeyTime, modernEasing));

        zIndexController.Stack(toSlide, fromSlide);
        fromSlide.Opacity = 1.0;
        toSlide.Opacity   = 0.0;

        inAnimation.Completed += (_, _) =>
        {
            fromSlide.BeginAnimation(UIElement.OpacityProperty, null);
            toSlide.BeginAnimation(UIElement.OpacityProperty, null);
            fromSlide.Opacity = 0.0;
            toSlide.Opacity   = 1.0;
        };

        fromSlide.BeginAnimation(UIElement.OpacityProperty, outAnimation);
        toSlide.BeginAnimation(UIElement.OpacityProperty, inAnimation);
    }
}
