using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace XIVLauncher.Xaml.Components;

public sealed class LoadingAnimation : FrameworkElement
{
    private const double DESIGN_SIZE       = 160;
    private const double RING_RADIUS       = 50;
    private const double RING_THICKNESS    = 6;
    private const double RIPPLE_RADIUS     = 47;
    private const double RIPPLE_MAX_RADIUS = 80;

    private const double ARC_START_ANGLE = 356.7;
    private const double ARC_SWEEP_ANGLE = 256.9;

    private const double LOOP_SECONDS = 3;
    private const double LOOP_DEGREES = 720;

    private const double TRACK_OPACITY      = 38d / 255d;
    private const double RIPPLE_OPACITY     = 64d / 255d;
    private const double RIPPLE_MIN_OPACITY = 13d / 255d;

    private static readonly Point CENTER = new(DESIGN_SIZE / 2, DESIGN_SIZE / 2);
    private static readonly Color TINT   = Color.FromRgb(0xFF, 0x97, 0x00);

    private static readonly Geometry RIPPLE_GEOMETRY = Freeze(new EllipseGeometry(CENTER, RIPPLE_RADIUS, RIPPLE_RADIUS));
    private static readonly Geometry TRACK_GEOMETRY  = Freeze(new EllipseGeometry(CENTER, RING_RADIUS,   RING_RADIUS));
    private static readonly Geometry ARC_GEOMETRY    = Freeze(CreateArcGeometry());

    private static readonly Pen TRACK_PEN = CreatePen(TRACK_OPACITY);
    private static readonly Pen ARC_PEN   = CreatePen(1);

    private readonly DrawingVisual   contentVisual = new();
    private readonly ScaleTransform  sizeTransform = new();
    private readonly ScaleTransform  rippleScale   = new(1, 1, CENTER.X, CENTER.Y);
    private readonly RotateTransform arcRotation   = new(0, CENTER.X, CENTER.Y);
    private readonly SolidColorBrush rippleBrush   = new(TINT) { Opacity = RIPPLE_OPACITY };

    private List<AnimationClock>? clocks;
    private Window?               hostWindow;
    private bool                  isPlaying;

    public LoadingAnimation()
    {
        IsHitTestVisible = false;

        contentVisual.Transform = sizeTransform;
        using (var drawingContext = contentVisual.RenderOpen())
            drawingContext.DrawDrawing(CreateContent());

        AddVisualChild(contentVisual);

        Loaded           += OnLoaded;
        Unloaded         += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdatePlayback(this, EventArgs.Empty);
        IsEnabledChanged += (_, _) => UpdatePlayback(this, EventArgs.Empty);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild
    (
        int index
    ) =>
        index == 0 ?
            contentVisual :
            throw new ArgumentOutOfRangeException(nameof(index));

    protected override void OnRenderSizeChanged
    (
        SizeChangedInfo sizeInfo
    )
    {
        base.OnRenderSizeChanged(sizeInfo);

        sizeTransform.ScaleX = sizeInfo.NewSize.Width  / DESIGN_SIZE;
        sizeTransform.ScaleY = sizeInfo.NewSize.Height / DESIGN_SIZE;
    }

    private static DoubleAnimationUsingKeyFrames CreatePulseAnimation
    (
        double from,
        double to
    )
    {
        var easing = new SineEase { EasingMode = EasingMode.EaseInOut };

        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration       = TimeSpan.FromSeconds(LOOP_SECONDS),
            RepeatBehavior = RepeatBehavior.Forever
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to,   KeyTime.FromTimeSpan(TimeSpan.FromSeconds(LOOP_SECONDS / 2))) { EasingFunction = easing });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(LOOP_SECONDS))) { EasingFunction     = easing });

        return animation;
    }

    private static Geometry CreateArcGeometry()
    {
        var figure = new PathFigure
        {
            StartPoint = PointOnRing(ARC_START_ANGLE),
            IsClosed   = false,
            IsFilled   = false
        };
        figure.Segments.Add
        (
            new ArcSegment
            (
                PointOnRing(ARC_START_ANGLE + ARC_SWEEP_ANGLE),
                new Size(RING_RADIUS, RING_RADIUS),
                0,
                ARC_SWEEP_ANGLE > 180,
                SweepDirection.Clockwise,
                true
            )
        );

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        return geometry;
    }

    private static Point PointOnRing
    (
        double degrees
    )
    {
        var radians = degrees * Math.PI / 180;

        return new Point(CENTER.X + (RING_RADIUS * Math.Sin(radians)), CENTER.Y - (RING_RADIUS * Math.Cos(radians)));
    }

    private static Pen CreatePen
    (
        double opacity
    ) =>
        Freeze
        (
            new Pen
            (
                Freeze(new SolidColorBrush(TINT) { Opacity = opacity }),
                RING_THICKNESS
            )
        );

    private static T Freeze<T>
    (
        T freezable
    )
        where T : Freezable
    {
        freezable.Freeze();

        return freezable;
    }

    private DrawingGroup CreateContent()
    {
        var ripple = new DrawingGroup { Transform = rippleScale };
        ripple.Children.Add(new GeometryDrawing(rippleBrush, null, RIPPLE_GEOMETRY));

        var arc = new DrawingGroup { Transform = arcRotation };
        arc.Children.Add(new GeometryDrawing(null, ARC_PEN, ARC_GEOMETRY));

        var content = new DrawingGroup();
        content.Children.Add(ripple);
        content.Children.Add(new GeometryDrawing(null, TRACK_PEN, TRACK_GEOMETRY));
        content.Children.Add(arc);

        return content;
    }

    private static AnimationClock Apply
    (
        Animatable         target,
        DependencyProperty property,
        AnimationTimeline  animation
    )
    {
        var clock = (AnimationClock)animation.CreateClock(true);
        clock.Controller!.Begin();
        target.ApplyAnimationClock(property, clock);

        return clock;
    }

    private List<AnimationClock> StartPlayback()
    {
        var rotation = new DoubleAnimation(0, LOOP_DEGREES, TimeSpan.FromSeconds(LOOP_SECONDS))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        return
        [
            Apply(arcRotation, RotateTransform.AngleProperty, rotation),
            Apply(rippleScale, ScaleTransform.ScaleXProperty, CreatePulseAnimation(1,              RIPPLE_MAX_RADIUS / RIPPLE_RADIUS)),
            Apply(rippleScale, ScaleTransform.ScaleYProperty, CreatePulseAnimation(1,              RIPPLE_MAX_RADIUS / RIPPLE_RADIUS)),
            Apply(rippleBrush, Brush.OpacityProperty,         CreatePulseAnimation(RIPPLE_OPACITY, RIPPLE_MIN_OPACITY))
        ];
    }

    private void OnLoaded
    (
        object          sender,
        RoutedEventArgs e
    )
    {
        hostWindow = Window.GetWindow(this);
        if (hostWindow != null)
            hostWindow.StateChanged += UpdatePlayback;

        UpdatePlayback(sender, e);
    }

    private void OnUnloaded
    (
        object          sender,
        RoutedEventArgs e
    )
    {
        if (hostWindow != null)
            hostWindow.StateChanged -= UpdatePlayback;

        hostWindow = null;
        UpdatePlayback(sender, e);

        if (clocks == null)
            return;

        foreach (var clock in clocks)
            clock.Controller!.Remove();

        clocks = null;
    }

    private void UpdatePlayback
    (
        object?   sender,
        EventArgs e
    )
    {
        var shouldPlay = IsLoaded  &&
                         IsVisible &&
                         IsEnabled &&
                         hostWindow is
                         {
                             IsVisible: true,
                             WindowState: not WindowState.Minimized
                         };
        if (shouldPlay == isPlaying)
            return;

        isPlaying = shouldPlay;

        if (isPlaying)
        {
            clocks ??= StartPlayback();

            foreach (var clock in clocks)
                clock.Controller!.Resume();
        }
        else if (clocks != null)
        {
            foreach (var clock in clocks)
                clock.Controller!.Pause();
        }
    }
}
