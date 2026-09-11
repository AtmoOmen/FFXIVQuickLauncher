using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace XIVLauncher.Xaml.Components;

public sealed class LoadingAnimation : FrameworkElement
{
    private const int    FRAME_COUNT   = 720;
    private const int    ATLAS_COLUMNS = 20;
    private const double LOOP_SECONDS  = 3;

    private static readonly BitmapSource Atlas = LoadAtlas();

    private static readonly DependencyProperty FrameIndexProperty = DependencyProperty.Register
    (
        "FrameIndex",
        typeof(int),
        typeof(LoadingAnimation),
        new PropertyMetadata(0, OnFrameIndexChanged)
    );

    private readonly ImageBrush frameBrush = new(Atlas)
    {
        Viewbox      = new Rect(0, 0, 1d / ATLAS_COLUMNS, (double)ATLAS_COLUMNS / FRAME_COUNT),
        ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
        Stretch      = Stretch.Fill
    };

    private Window?         hostWindow;
    private bool            isPlaying;
    private AnimationClock? playbackClock;
    private TimeSpan        position;

    public LoadingAnimation()
    {
        IsHitTestVisible =  false;
        Loaded           += OnLoaded;
        Unloaded         += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdatePlayback(this, EventArgs.Empty);
        IsEnabledChanged += (_, _) => UpdatePlayback(this, EventArgs.Empty);
    }

    protected override void OnRender
    (
        DrawingContext drawingContext
    ) =>
        drawingContext.DrawRectangle(frameBrush, null, new Rect(RenderSize));

    private static void OnFrameIndexChanged
    (
        DependencyObject                   d,
        DependencyPropertyChangedEventArgs e
    )
    {
        var animation = (LoadingAnimation)d;
        var frame     = (int)e.NewValue % FRAME_COUNT;
        animation.frameBrush.Viewbox = new Rect
        (
            (double)(frame % ATLAS_COLUMNS)                 / ATLAS_COLUMNS,
            (double)(frame / ATLAS_COLUMNS) * ATLAS_COLUMNS / FRAME_COUNT,
            1d                                              / ATLAS_COLUMNS,
            (double)ATLAS_COLUMNS                           / FRAME_COUNT
        );
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
            var animation = new Int32Animation
            (
                0,
                FRAME_COUNT,
                TimeSpan.FromSeconds(LOOP_SECONDS)
            )
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            Timeline.SetDesiredFrameRate(animation, (int)(FRAME_COUNT / LOOP_SECONDS));
            playbackClock = (AnimationClock)animation.CreateClock(true);
            ApplyAnimationClock(FrameIndexProperty, playbackClock);
            playbackClock.Controller!.SeekAlignedToLastTick(position, TimeSeekOrigin.BeginTime);
        }
        else if (playbackClock != null)
        {
            position = playbackClock.CurrentTime ?? TimeSpan.Zero;
            var frame = (int)GetValue(FrameIndexProperty);
            playbackClock.Controller!.Remove();
            ApplyAnimationClock(FrameIndexProperty, null);
            SetCurrentValue(FrameIndexProperty, frame);
            playbackClock = null;
        }
    }

    private static BitmapImage LoadAtlas()
    {
        var atlas = new BitmapImage();
        atlas.BeginInit();
        atlas.CacheOption = BitmapCacheOption.OnLoad;
        atlas.UriSource   = new Uri("pack://application:,,,/XIVLauncherCN;component/Resources/loading-spinner.png");
        atlas.EndInit();
        atlas.Freeze();

        return atlas;
    }
}
