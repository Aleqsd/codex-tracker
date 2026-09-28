using System.Windows.Media.Animation;

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
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(MotionState), typeof(UiMotion));

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

    internal static bool Allowed(DependencyObject element) => !Suppressed && GetEnabled(element)
        && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    internal static void FadeIn(FrameworkElement element)
    {
        var state = State(element);
        state.StopFade();
        if (!element.IsLoaded || !element.IsVisible || !Allowed(element)) return;
        // Preserve opacity bindings and disabled-state triggers; animate only the presentation.
        var opacity = element.Opacity;
        var animation = Animation(opacity * 0.55, opacity, 180);
        state.Fade = animation;
        animation.Completed += (_, _) => { if (ReferenceEquals(state.Fade, animation)) state.StopFade(); };
        element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimation Animation(double from, double to, int milliseconds) => new(
        from, to, TimeSpan.FromMilliseconds(milliseconds))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop
    };

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
    private static void ScaleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element) State(element).ChangeScale((double)e.NewValue);
    }
    private static void AngleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element) State(element).ChangeAngle((double)e.NewValue);
    }
    private static void AnimateTabsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TabControl tabs) return;
        if ((bool)e.NewValue) tabs.SelectionChanged += TabChanged;
        else tabs.SelectionChanged -= TabChanged;
    }
    private static void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is TabControl tabs && tabs.IsLoaded && ReferenceEquals(e.OriginalSource, tabs)
            && tabs.Template?.FindName("PART_SelectedContentHost", tabs) is FrameworkElement content)
            FadeIn(content);
    }

    private sealed class MotionState
    {
        private readonly FrameworkElement _element;
        private ScaleTransform? _scale;
        private RotateTransform? _rotation;
        private DoubleAnimation? _scaleAnimation, _angleAnimation;
        internal DoubleAnimation? Fade;

        internal MotionState(FrameworkElement element)
        {
            _element = element;
            element.Loaded += (_, _) => { if (GetFadeOnShow(element)) FadeIn(element); };
            element.Unloaded += (_, _) => Reset();
            element.IsVisibleChanged += (_, _) =>
            {
                if (!element.IsVisible) Reset();
                else if (GetFadeOnShow(element)) FadeIn(element);
            };
        }
        internal void StopFade()
        {
            if (Fade is null) return;
            Fade = null;
            _element.BeginAnimation(UIElement.OpacityProperty, null);
        }
        internal void Reset()
        {
            StopFade();
            _scaleAnimation = _angleAnimation = null;
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
        }
        private void AddTransform(Transform transform)
        {
            var group = new TransformGroup();
            group.Children.Add(_element.RenderTransform);
            group.Children.Add(transform);
            _element.SetCurrentValue(UIElement.RenderTransformProperty, group);
        }
        internal void ChangeScale(double value)
        {
            if (_scale is null) { _scale = new ScaleTransform(); AddTransform(_scale); }
            var from = _scale.ScaleX;
            var animate = Allowed(_element) && _element.IsVisible && _element.IsLoaded;
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _scale.ScaleX = _scale.ScaleY = animate ? value : 1;
            _scaleAnimation = null;
            if (!animate) return;
            var animation = Animation(from, value, value < 1 ? 80 : 140);
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
            if (Allowed(_element) && _element.IsVisible && _element.IsLoaded)
            {
                var animation = Animation(from, value, 140);
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
