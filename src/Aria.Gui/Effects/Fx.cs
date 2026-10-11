using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Aria.Gui.Effects;

/// <summary>
/// Animations réutilisables, à poser directement dans le XAML :
///   fx:Fx.Reveal="True"  apparition en glissant (en cascade dans une liste)
///   fx:Fx.Pulse="True"   pulsation de l'opacité
///   fx:Fx.Spin="True"    rotation continue
///   fx:Fx.Sweep="True"   balayage lumineux (sur un Grid)
/// Respecte le réglage Windows « Effets d'animation » : s'il est désactivé, tout reste statique.
/// </summary>
public static class Fx
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    // ---------- Apparition ----------

    public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached(
        "Reveal", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnRevealChanged));

    public static bool GetReveal(DependencyObject o) => (bool)o.GetValue(RevealProperty);
    public static void SetReveal(DependencyObject o, bool value) => o.SetValue(RevealProperty, value);

    /// <summary>Délai supplémentaire avant l'apparition (ms).</summary>
    public static readonly DependencyProperty RevealDelayProperty = DependencyProperty.RegisterAttached(
        "RevealDelay", typeof(int), typeof(Fx), new PropertyMetadata(0));

    public static int GetRevealDelay(DependencyObject o) => (int)o.GetValue(RevealDelayProperty);
    public static void SetRevealDelay(DependencyObject o, int value) => o.SetValue(RevealDelayProperty, value);

    private static void OnRevealChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.Loaded -= RevealOnLoaded;
        if ((bool)e.NewValue) element.Loaded += RevealOnLoaded;
    }

    private static void RevealOnLoaded(object sender, RoutedEventArgs e)
    {
        if (!Enabled || sender is not FrameworkElement element) return;

        // Dans une liste, chaque élément arrive un peu après le précédent
        var delay = TimeSpan.FromMilliseconds(GetRevealDelay(element) + Math.Min(IndexInList(element), 12) * 70);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shift = new TranslateTransform(0, 24);

        element.RenderTransform = shift;
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { BeginTime = delay, EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(650)) { BeginTime = delay, EasingFunction = ease });
    }

    private static int IndexInList(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ItemsControl.ItemsControlFromItemContainer(current) is { } owner)
                return Math.Max(0, owner.ItemContainerGenerator.IndexFromContainer(current));
        }
        return 0;
    }

    // ---------- Pulsation ----------

    public static readonly DependencyProperty PulseProperty = DependencyProperty.RegisterAttached(
        "Pulse", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnPulseChanged));

    public static bool GetPulse(DependencyObject o) => (bool)o.GetValue(PulseProperty);
    public static void SetPulse(DependencyObject o, bool value) => o.SetValue(PulseProperty, value);

    private static void OnPulseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        if ((bool)e.NewValue && Enabled)
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        else
            element.BeginAnimation(UIElement.OpacityProperty, null);
    }

    // ---------- Rotation ----------

    public static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached(
        "Spin", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnSpinChanged));

    public static bool GetSpin(DependencyObject o) => (bool)o.GetValue(SpinProperty);
    public static void SetSpin(DependencyObject o, bool value) => o.SetValue(SpinProperty, value);

    private static void OnSpinChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || !(bool)e.NewValue || !Enabled) return;

        var rotation = new RotateTransform();
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = rotation;
        rotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(16)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    // ---------- Balayage lumineux ----------

    public static readonly DependencyProperty SweepProperty = DependencyProperty.RegisterAttached(
        "Sweep", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnSweepChanged));

    public static bool GetSweep(DependencyObject o) => (bool)o.GetValue(SweepProperty);
    public static void SetSweep(DependencyObject o, bool value) => o.SetValue(SweepProperty, value);

    private static void OnSweepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid || !(bool)e.NewValue || !Enabled) return;

        const double bandWidth = 160;
        var move = new TranslateTransform(-bandWidth * 2, 0);
        var band = new Rectangle
        {
            Width = bandWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            Fill = new LinearGradientBrush(
                [
                    new GradientStop(Color.FromArgb(0, 125, 249, 255), 0),
                    new GradientStop(Color.FromArgb(46, 125, 249, 255), 0.5),
                    new GradientStop(Color.FromArgb(0, 125, 249, 255), 1)
                ],
                new Point(0, 0), new Point(1, 0)),
            RenderTransform = new TransformGroup { Children = [new SkewTransform(-20, 0), move] }
        };
        Grid.SetRowSpan(band, 100);
        Grid.SetColumnSpan(band, 100);
        Panel.SetZIndex(band, 100);
        grid.Children.Add(band);
        grid.ClipToBounds = true;

        void Start()
        {
            var end = grid.ActualWidth + bandWidth;
            var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(-bandWidth * 2, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(end, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.2)),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(end, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(5))));
            move.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        grid.Loaded += (_, _) => Start();
        grid.SizeChanged += (_, args) => { if (args.WidthChanged) Start(); };
    }
}
