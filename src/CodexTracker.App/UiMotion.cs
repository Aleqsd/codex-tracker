using System.Runtime.CompilerServices;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexTracker.App;

/// <summary>Short, decorative transitions; never delay navigation or change application state.</summary>
public static class UiMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(UiMotion), new FrameworkPropertyMetadata(true,
            FrameworkPropertyMetadataOptions.Inherits, EnabledChanged));
    public static readonly DependencyProperty FadeOnShowProperty = DependencyProperty.RegisterAttached(
        "FadeOnShow", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, FadeOnShowChanged));
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(double), typeof(UiMotion), new PropertyMetadata(1d, ScaleChanged));
    public static readonly DependencyProperty AngleProperty = DependencyProperty.RegisterAttached(
        "Angle", typeof(double), typeof(UiMotion), new PropertyMetadata(0d, AngleChanged));
    public static readonly DependencyProperty AnimateTabsProperty = DependencyProperty.RegisterAttached(
        "AnimateTabs", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, AnimateTabsChanged));
    /// <summary>Vertical distance travelled while an element fades in; staggered panels apply it to their children.</summary>
    public static readonly DependencyProperty RiseProperty = DependencyProperty.RegisterAttached(
        "Rise", typeof(double), typeof(UiMotion), new PropertyMetadata(0d));
    /// <summary>The children of this panel enter one after another when it is shown or explicitly faded in.</summary>
    public static readonly DependencyProperty StaggerProperty = DependencyProperty.RegisterAttached(
        "Stagger", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, StaggerChanged));
    public static readonly DependencyProperty StaggerDelayProperty = DependencyProperty.RegisterAttached(
        "StaggerDelay", typeof(double), typeof(UiMotion), new PropertyMetadata(0d));
    /// <summary>Horizontal position, animated with a slight overshoot (switch thumbs).</summary>
    public static readonly DependencyProperty OffsetXProperty = DependencyProperty.RegisterAttached(
        "OffsetX", typeof(double), typeof(UiMotion), new PropertyMetadata(0d, OffsetChanged));
    /// <summary>Cross-fades an overlay whose opacity the same trigger switches between 0 and 1.</summary>
    public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached(
        "Reveal", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, RevealChanged));
    /// <summary>Springs from a smaller scale each time the element appears (check marks).</summary>
    public static readonly DependencyProperty PopProperty = DependencyProperty.RegisterAttached(
        "Pop", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, ShowEffectChanged));
    /// <summary>Grows horizontally from its left edge each time it appears (gauge fills).</summary>
    public static readonly DependencyProperty GrowProperty = DependencyProperty.RegisterAttached(
        "Grow", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, ShowEffectChanged));
    /// <summary>Repeating halo while true (collection in progress).</summary>
    public static readonly DependencyProperty PulseProperty = DependencyProperty.RegisterAttached(
        "Pulse", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, PulseChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(MotionState), typeof(UiMotion));
    private static readonly ConditionalWeakTable<TabControl, SelectionPill> TabPills = new();

    // Static previews must show the final state, including dialogs captured immediately after opening.
    internal static bool Suppressed { get; set; }
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetFadeOnShow(DependencyObject element) => (bool)element.GetValue(FadeOnShowProperty);
    public static void SetFadeOnShow(DependencyObject element, bool value) => element.SetValue(FadeOnShowProperty, value);
    public static double GetScale(DependencyObject element) => (double)element.GetValue(ScaleProperty);
    public static void SetScale(DependencyObject element, double value) => element.SetValue(ScaleProperty, value);
    public static double GetAngle(DependencyObject element) => (double)element.GetValue(AngleProperty);
    public static void SetAngle(DependencyObject element, double value) => element.SetValue(AngleProperty, value);
    public static bool GetAnimateTabs(DependencyObject element) => (bool)element.GetValue(AnimateTabsProperty);
    public static void SetAnimateTabs(DependencyObject element, bool value) => element.SetValue(AnimateTabsProperty, value);
    public static double GetRise(DependencyObject element) => (double)element.GetValue(RiseProperty);
    public static void SetRise(DependencyObject element, double value) => element.SetValue(RiseProperty, value);
    public static bool GetStagger(DependencyObject element) => (bool)element.GetValue(StaggerProperty);
    public static void SetStagger(DependencyObject element, bool value) => element.SetValue(StaggerProperty, value);
    public static double GetStaggerDelay(DependencyObject element) => (double)element.GetValue(StaggerDelayProperty);
    public static void SetStaggerDelay(DependencyObject element, double value) => element.SetValue(StaggerDelayProperty, value);
    public static double GetOffsetX(DependencyObject element) => (double)element.GetValue(OffsetXProperty);
    public static void SetOffsetX(DependencyObject element, double value) => element.SetValue(OffsetXProperty, value);
    public static bool GetReveal(DependencyObject element) => (bool)element.GetValue(RevealProperty);
    public static void SetReveal(DependencyObject element, bool value) => element.SetValue(RevealProperty, value);
    public static bool GetPop(DependencyObject element) => (bool)element.GetValue(PopProperty);
    public static void SetPop(DependencyObject element, bool value) => element.SetValue(PopProperty, value);
    public static bool GetGrow(DependencyObject element) => (bool)element.GetValue(GrowProperty);
    public static void SetGrow(DependencyObject element, bool value) => element.SetValue(GrowProperty, value);
    public static bool GetPulse(DependencyObject element) => (bool)element.GetValue(PulseProperty);
    public static void SetPulse(DependencyObject element, bool value) => element.SetValue(PulseProperty, value);

    internal static bool Allowed(DependencyObject element) => !Suppressed && GetEnabled(element)
        && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    internal static void FadeIn(FrameworkElement element)
    {
        if (GetStagger(element)) { StaggerChildren(element); return; }
        var state = State(element);
        state.StopFade();
        if (!element.IsLoaded || !element.IsVisible || !Allowed(element)) return;
        // Preserve opacity bindings and disabled-state triggers; animate only the presentation.
        var opacity = element.Opacity;
        var animation = Animation(opacity * 0.55, opacity, 200);
        state.Fade = animation;
        animation.Completed += (_, _) => { if (ReferenceEquals(state.Fade, animation)) state.StopFade(); };
        element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        state.Rise(GetRise(element), 0, 300);
    }

    /// <summary>One full turn of an icon, for an action the user just started.</summary>
    internal static void Spin(FrameworkElement element)
    {
        if (element.IsLoaded && element.IsVisible && Allowed(element)) State(element).Spin();
    }

    private static void StaggerChildren(FrameworkElement host)
    {
        if (!host.IsLoaded || !host.IsVisible || !Allowed(host)) return;
        double rise = GetRise(host), start = GetStaggerDelay(host);
        int index = 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(host); i++)
            if (VisualTreeHelper.GetChild(host, i) is FrameworkElement child && child.Visibility == Visibility.Visible)
                State(child).Enter(start + Math.Min(index++, 9) * 38, rise);
    }

    private static DoubleAnimation Animation(double from, double to, int milliseconds, IEasingFunction? easing = null) => new(
        from, to, TimeSpan.FromMilliseconds(milliseconds))
    {
        EasingFunction = easing ?? new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop
    };
    /// <summary>Holds <paramref name="from"/> during the delay, then eases to <paramref name="to"/>; nothing flashes before it starts.</summary>
    private static DoubleAnimationUsingKeyFrames Delayed(double from, double to, double delay, int milliseconds, IEasingFunction easing)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(delay + milliseconds), FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + milliseconds)), easing));
        return animation;
    }

    private static MotionState State(FrameworkElement element)
    {
        if (element.GetValue(StateProperty) is MotionState state) return state;
        state = new MotionState(element);
        element.SetValue(StateProperty, state);
        return state;
    }
    private static void EnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && sender.GetValue(StateProperty) is MotionState state) state.Reset();
    }
    private static void FadeOnShowChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if ((bool)e.NewValue) FadeIn(element);
        else if (element.GetValue(StateProperty) is MotionState state) state.StopFade();
    }
    private static void StaggerChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element && (bool)e.NewValue) State(element);
    }
    private static void ShowEffectChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element && (bool)e.NewValue) State(element).Appear();
    }
    private static void ScaleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element) State(element).ChangeScale((double)e.NewValue);
    }
    private static void AngleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element) State(element).ChangeAngle((double)e.NewValue);
    }
    private static void OffsetChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element) State(element).ChangeOffset((double)e.NewValue);
    }
    private static void RevealChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element) State(element).ChangeReveal((bool)e.NewValue);
    }
    private static void PulseChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element) State(element).SyncPulse();
    }
    private static void AnimateTabsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TabControl tabs) return;
        if ((bool)e.NewValue) { tabs.SelectionChanged += TabChanged; tabs.Loaded += TabsLoaded; }
        else { tabs.SelectionChanged -= TabChanged; tabs.Loaded -= TabsLoaded; }
    }
    private static void TabsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TabControl tabs || TabPills.TryGetValue(tabs, out _)) return;
        if (tabs.Template?.FindName("SelectionTrack", tabs) is FrameworkElement track && tabs.Template.FindName("SelectionPill", tabs) is Border pill)
            TabPills.Add(tabs, new SelectionPill(track, pill, () => tabs.SelectedItem as FrameworkElement));
    }
    private static void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabs || !ReferenceEquals(e.OriginalSource, tabs)) return;
        if (TabPills.TryGetValue(tabs, out var pill)) pill.Move();
        if (tabs.IsLoaded && tabs.Template?.FindName("PART_SelectedContentHost", tabs) is FrameworkElement content)
            FadeIn(content);
    }

    private sealed class MotionState
    {
        private readonly FrameworkElement _element;
        private ScaleTransform? _scale, _pop, _grow;
        private RotateTransform? _rotation, _spin;
        private TranslateTransform? _rise, _offset;
        private AnimationTimeline? _scaleAnimation, _angleAnimation, _riseAnimation, _offsetAnimation, _revealAnimation, _showAnimation, _spinAnimation;
        private bool _pulsing;
        internal AnimationTimeline? Fade;

        internal MotionState(FrameworkElement element)
        {
            _element = element;
            element.Loaded += (_, _) => Show();
            element.Unloaded += (_, _) => Reset();
            element.IsVisibleChanged += (_, _) =>
            {
                if (!element.IsVisible) Reset();
                else Show();
            };
        }
        private void Show()
        {
            if (GetStagger(_element)) StaggerChildren(_element);
            else if (GetFadeOnShow(_element)) FadeIn(_element);
            Appear(); SyncPulse();
        }
        private bool CanAnimate => Allowed(_element) && _element.IsVisible && _element.IsLoaded;
        internal void StopFade()
        {
            if (Fade is null) return;
            Fade = null;
            _element.BeginAnimation(UIElement.OpacityProperty, null);
        }
        internal void Reset()
        {
            StopFade();
            _scaleAnimation = _angleAnimation = _riseAnimation = _offsetAnimation = _showAnimation = _spinAnimation = null;
            if (_scale is not null)
            {
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _scale.ScaleX = _scale.ScaleY = 1;
            }
            if (_rotation is not null)
            {
                _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
                _rotation.Angle = GetAngle(_element);
            }
            foreach (var transform in new[] { _pop, _grow })
            {
                transform?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                transform?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }
            _rise?.BeginAnimation(TranslateTransform.YProperty, null);
            _spin?.BeginAnimation(RotateTransform.AngleProperty, null);
            if (_offset is not null) { _offset.BeginAnimation(TranslateTransform.XProperty, null); _offset.X = GetOffsetX(_element); }
            if (_revealAnimation is not null) { _revealAnimation = null; _element.BeginAnimation(UIElement.OpacityProperty, null); }
            StopPulse();
        }
        private void AddTransform(Transform transform)
        {
            var group = new TransformGroup();
            group.Children.Add(_element.RenderTransform);
            group.Children.Add(transform);
            _element.SetCurrentValue(UIElement.RenderTransformProperty, group);
        }
        // Each effect owns one transform and releases its clock when finished.
        private static void Run(Animatable target, AnimationTimeline animation, Action<AnimationTimeline?> remember, Func<AnimationTimeline?> current, params DependencyProperty[] properties)
        {
            remember(animation);
            animation.Completed += (_, _) =>
            {
                if (!ReferenceEquals(current(), animation)) return;
                remember(null);
                foreach (var property in properties) target.BeginAnimation(property, null);
            };
            foreach (var property in properties) target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }
        internal void Enter(double delay, double rise)
        {
            StopFade();
            if (!CanAnimate) return;
            var fade = Delayed(0, _element.Opacity, delay, 300, new CubicEase { EasingMode = EasingMode.EaseOut });
            Fade = fade;
            fade.Completed += (_, _) => { if (ReferenceEquals(Fade, fade)) StopFade(); };
            _element.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
            Rise(rise, delay, 420);
        }
        internal void Rise(double distance, double delay, int milliseconds)
        {
            if (distance == 0 || !CanAnimate) return;
            if (_rise is null) { _rise = new TranslateTransform(); AddTransform(_rise); }
            Run(_rise, Delayed(distance, 0, delay, milliseconds, new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }),
                a => _riseAnimation = a, () => _riseAnimation, TranslateTransform.YProperty);
        }
        internal void Appear()
        {
            if (!CanAnimate) return;
            if (GetPop(_element))
            {
                if (_pop is null) { _pop = new ScaleTransform(); _element.RenderTransformOrigin = new Point(0.5, 0.5); AddTransform(_pop); }
                Run(_pop, Animation(0.35, 1, 260, new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut }),
                    a => _showAnimation = a, () => _showAnimation, ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty);
            }
            if (GetGrow(_element))
            {
                if (_grow is null) { _grow = new ScaleTransform(); _element.RenderTransformOrigin = new Point(0, 0.5); AddTransform(_grow); }
                Run(_grow, Delayed(0, 1, 120, 750, new QuinticEase { EasingMode = EasingMode.EaseOut }), a => _showAnimation = a, () => _showAnimation, ScaleTransform.ScaleXProperty);
            }
        }
        internal void Spin()
        {
            if (_spin is null) { _spin = new RotateTransform(); _element.RenderTransformOrigin = new Point(0.5, 0.5); AddTransform(_spin); }
            Run(_spin, Animation(0, 360, 650, new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }),
                a => _spinAnimation = a, () => _spinAnimation, RotateTransform.AngleProperty);
        }
        internal void ChangeOffset(double value)
        {
            if (_offset is null) { _offset = new TranslateTransform(); AddTransform(_offset); }
            var from = _offset.X;
            _offset.BeginAnimation(TranslateTransform.XProperty, null);
            _offset.X = value; _offsetAnimation = null;
            if (CanAnimate && from != value)
                Run(_offset, Animation(from, value, 220, new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }),
                    a => _offsetAnimation = a, () => _offsetAnimation, TranslateTransform.XProperty);
        }
        internal void ChangeReveal(bool shown)
        {
            _element.BeginAnimation(UIElement.OpacityProperty, null); _revealAnimation = null;
            if (!CanAnimate) return;
            var animation = Animation(shown ? 0 : 1, shown ? 1 : 0, 180);
            _revealAnimation = animation;
            animation.Completed += (_, _) => { if (ReferenceEquals(_revealAnimation, animation)) { _revealAnimation = null; _element.BeginAnimation(UIElement.OpacityProperty, null); } };
            _element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        internal void SyncPulse()
        {
            if (GetPulse(_element) && CanAnimate) StartPulse(); else StopPulse();
        }
        private void StartPulse()
        {
            if (_pulsing) return;
            _pulsing = true;
            if (_pop is null) { _pop = new ScaleTransform(); _element.RenderTransformOrigin = new Point(0.5, 0.5); AddTransform(_pop); }
            var duration = TimeSpan.FromMilliseconds(1100);
            var grow = new DoubleAnimation(1, 2.6, duration) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _pop.BeginAnimation(ScaleTransform.ScaleXProperty, grow); _pop.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.6, 0, duration) { RepeatBehavior = RepeatBehavior.Forever });
        }
        private void StopPulse()
        {
            if (!_pulsing) return;
            _pulsing = false;
            _pop?.BeginAnimation(ScaleTransform.ScaleXProperty, null); _pop?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _element.BeginAnimation(UIElement.OpacityProperty, null);
        }
        internal void ChangeScale(double value)
        {
            if (_scale is null) { _scale = new ScaleTransform(); AddTransform(_scale); }
            var from = _scale.ScaleX;
            var animate = CanAnimate;
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _scale.ScaleX = _scale.ScaleY = animate ? value : 1;
            _scaleAnimation = null;
            if (!animate) return;
            var animation = Animation(from, value, value < 1 ? 80 : 160, value > 1 ? new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } : null);
            _scaleAnimation = animation;
            animation.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_scaleAnimation, animation)) return;
                _scaleAnimation = null;
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
        internal void ChangeAngle(double value)
        {
            if (_rotation is null) { _rotation = new RotateTransform(); AddTransform(_rotation); }
            var from = _rotation.Angle;
            _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _rotation.Angle = value;
            _angleAnimation = null;
            if (CanAnimate)
            {
                var animation = Animation(from, value, 160, new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut });
                _angleAnimation = animation;
                animation.Completed += (_, _) =>
                {
                    if (!ReferenceEquals(_angleAnimation, animation)) return;
                    _angleAnimation = null;
                    _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
                };
                _rotation.BeginAnimation(RotateTransform.AngleProperty, animation, HandoffBehavior.SnapshotAndReplace);
            }
        }
    }
}

/// <summary>Selection background that glides between the items of a segmented control or a list.</summary>
internal sealed class SelectionPill
{
    private readonly FrameworkElement _host;
    private readonly Border _pill;
    private readonly Func<FrameworkElement?> _selected;
    private readonly TranslateTransform _offset = new();
    private bool _placed;

    internal SelectionPill(FrameworkElement host, Border pill, Func<FrameworkElement?> selected)
    {
        _host = host; _pill = pill; _selected = selected;
        pill.RenderTransform = _offset; pill.IsHitTestVisible = false;
        pill.HorizontalAlignment = HorizontalAlignment.Left; pill.VerticalAlignment = VerticalAlignment.Top;
        host.SizeChanged += (_, _) => Move(false);
        host.IsVisibleChanged += (_, _) => Queue();
        host.Loaded += (_, _) => Queue();
        Queue();
    }
    private void Queue() => _host.Dispatcher.InvokeAsync(() => Move(false), DispatcherPriority.Loaded);
    internal void Move(bool animate = true)
    {
        var item = _selected();
        if (item is null || !_host.IsVisible || !item.IsVisible || item.ActualWidth <= 0 || !item.IsDescendantOf(_host))
        {
            _pill.Visibility = Visibility.Hidden; _placed = false; return;
        }
        var bounds = item.TransformToAncestor(_host).TransformBounds(new Rect(item.RenderSize));
        animate &= _placed && UiMotion.Allowed(_host);
        _pill.Visibility = Visibility.Visible; _placed = true;
        Slide(_offset, TranslateTransform.XProperty, bounds.X, animate);
        Slide(_offset, TranslateTransform.YProperty, bounds.Y, animate);
        Slide(_pill, FrameworkElement.WidthProperty, bounds.Width, animate);
        Slide(_pill, FrameworkElement.HeightProperty, bounds.Height, animate);
    }
    private static void Slide(DependencyObject target, DependencyProperty property, double to, bool animate)
    {
        var from = (double)target.GetValue(property);
        var animatable = (IAnimatable)target;
        animatable.BeginAnimation(property, null);
        target.SetValue(property, to);
        if (!animate || double.IsNaN(from) || Math.Abs(from - to) < 0.5) return;
        animatable.BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
        });
    }
}
