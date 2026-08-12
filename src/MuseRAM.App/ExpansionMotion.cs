using System.Windows;
using System.Windows.Media.Animation;

namespace MuseRAM.App;

public static class ExpansionMotion
{
    private static int _activeAnimationCount;
    public static bool IsAnyAnimationActive => _activeAnimationCount > 0;

    private static readonly DependencyProperty ExpandedHeightProperty = DependencyProperty.RegisterAttached(
        "ExpandedHeight",
        typeof(double),
        typeof(ExpansionMotion),
        new PropertyMetadata(0d));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.RegisterAttached(
        "IsExpanded",
        typeof(bool),
        typeof(ExpansionMotion),
        new PropertyMetadata(false, OnIsExpandedChanged));

    public static bool GetIsExpanded(DependencyObject element) =>
        (bool)element.GetValue(IsExpandedProperty);

    public static void SetIsExpanded(DependencyObject element, bool value) =>
        element.SetValue(IsExpandedProperty, value);

    private static void OnIsExpandedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (!element.IsLoaded)
        {
            element.Loaded -= OnLoaded;
            element.Loaded += OnLoaded;
            return;
        }

        Apply(element, (bool)e.NewValue, animate: true);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        element.Loaded -= OnLoaded;
        Apply(element, GetIsExpanded(element), animate: false);
    }

    private static void Apply(FrameworkElement element, bool expanded, bool animate)
    {
        EndAnimation(element);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            element.Opacity = expanded ? 1 : 0;
            element.MaxHeight = expanded ? double.PositiveInfinity : 0;
            element.IsHitTestVisible = expanded;
            return;
        }

        if (expanded)
        {
            element.MaxHeight = double.PositiveInfinity;
            element.Measure(new System.Windows.Size(
                element.ActualWidth > 0 ? element.ActualWidth : double.PositiveInfinity,
                double.PositiveInfinity));
            var targetHeight = Math.Max(0, element.DesiredSize.Height);
            element.SetValue(ExpandedHeightProperty, targetHeight);

            element.MaxHeight = 0;
            element.Opacity = 0;
            element.IsHitTestVisible = true;

            var expandDuration = MotionDuration(targetHeight);
            var heightAnimation = new DoubleAnimation(0, targetHeight, expandDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            heightAnimation.Completed += (_, _) =>
            {
                EndAnimation(element);
                if (!GetIsExpanded(element)) return;
                element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
                element.MaxHeight = double.PositiveInfinity;
            };
            BeginAnimation(element);
            element.BeginAnimation(FrameworkElement.MaxHeightProperty, heightAnimation);
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
                {
                    BeginTime = TimeSpan.FromMilliseconds(35),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            return;
        }

        var cachedHeight = (double)element.GetValue(ExpandedHeightProperty);
        var currentHeight = Math.Max(element.ActualHeight, cachedHeight);
        element.MaxHeight = currentHeight;
        element.Opacity = 1;

        var collapseDuration = MotionDuration(currentHeight);
        var collapseAnimation = new DoubleAnimation(currentHeight, 0, collapseDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        collapseAnimation.Completed += (_, _) =>
        {
            EndAnimation(element);
            if (GetIsExpanded(element)) return;
            element.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.MaxHeight = 0;
            element.Opacity = 0;
            element.IsHitTestVisible = false;
        };
        BeginAnimation(element);
        element.BeginAnimation(FrameworkElement.MaxHeightProperty, collapseAnimation);
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, collapseDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            });
    }

    private static TimeSpan MotionDuration(double height) => TimeSpan.FromMilliseconds(340);

    private static void BeginAnimation(FrameworkElement element)
    {
        if ((bool)element.GetValue(AnimationActiveProperty)) return;
        element.SetValue(AnimationActiveProperty, true);
        _activeAnimationCount++;
    }

    private static void EndAnimation(FrameworkElement element)
    {
        if (!(bool)element.GetValue(AnimationActiveProperty)) return;
        element.SetValue(AnimationActiveProperty, false);
        _activeAnimationCount = Math.Max(0, _activeAnimationCount - 1);
    }

    private static readonly DependencyProperty AnimationActiveProperty = DependencyProperty.RegisterAttached(
        "AnimationActive",
        typeof(bool),
        typeof(ExpansionMotion),
        new PropertyMetadata(false));
}
