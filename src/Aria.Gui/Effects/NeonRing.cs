using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Aria.Gui.Effects;

/// <summary>
/// Anneau lumineux de progression (0 à 1), avec graduations et halo pulsant.
/// Quand <see cref="TargetValue"/> change, l'anneau se remplit de façon animée.
/// </summary>
public sealed class NeonRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NeonRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TargetValueProperty = DependencyProperty.Register(
        nameof(TargetValue), typeof(double), typeof(NeonRing),
        new PropertyMetadata(0.0, OnTargetValueChanged));

    /// <summary>Couleur de l'arc et du halo. Si absente : dégradé orange → cyan.</summary>
    public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
        nameof(ArcBrush), typeof(Brush), typeof(NeonRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnArcBrushChanged));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(NeonRing),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Valeur affichée (animée).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Valeur à atteindre (liée au ViewModel).</summary>
    public double TargetValue
    {
        get => (double)GetValue(TargetValueProperty);
        set => SetValue(TargetValueProperty, value);
    }

    public Brush? ArcBrush
    {
        get => (Brush?)GetValue(ArcBrushProperty);
        set => SetValue(ArcBrushProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    private readonly DropShadowEffect _glow;

    public NeonRing()
    {
        _glow = new DropShadowEffect
        {
            Color = Color.FromRgb(255, 138, 31),
            ShadowDepth = 0,
            BlurRadius = 16,
            Opacity = 0.6
        };
        Effect = _glow;
        Loaded += (_, _) => StartGlow();
    }

    private void StartGlow()
    {
        if (!Fx.Enabled) return;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        _glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(10, 28, TimeSpan.FromSeconds(1.6))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease
        });
        _glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.35, 0.85, TimeSpan.FromSeconds(1.6))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease
        });
    }

    /// <summary>Le halo prend la couleur de l'arc.</summary>
    private static void OnArcBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is SolidColorBrush brush) ((NeonRing)d)._glow.Color = brush.Color;
    }

    private static void OnTargetValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (NeonRing)d;
        var target = Math.Clamp((double)e.NewValue, 0, 1);
        if (!Fx.Enabled)
        {
            ring.Value = target;
            return;
        }
        ring.BeginAnimation(ValueProperty, new DoubleAnimation(0, target, TimeSpan.FromSeconds(1.4))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness) / 2 - 6; // place pour les graduations

        // Graduations extérieures
        var tickPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 92, 139, 255)), 1.2);
        for (var i = 0; i < 60; i++)
        {
            var a = i * Math.PI / 30;
            var inner = radius + Thickness / 2 + 2;
            var outer = inner + (i % 5 == 0 ? 5 : 2.5);
            dc.DrawLine(tickPen,
                new Point(center.X + inner * Math.Cos(a), center.Y + inner * Math.Sin(a)),
                new Point(center.X + outer * Math.Cos(a), center.Y + outer * Math.Sin(a)));
        }

        // Piste
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(46, 92, 139, 255)), Thickness), center, radius, radius);

        var value = Math.Clamp(Value, 0, 1);
        if (value <= 0.001) return;

        var arcBrush = ArcBrush ?? new LinearGradientBrush(Color.FromRgb(255, 138, 31), Color.FromRgb(125, 249, 255), 45);
        var arcPen = new Pen(arcBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        if (value >= 0.999)
        {
            dc.DrawEllipse(null, arcPen, center, radius, radius);
            return;
        }

        // Arc depuis le haut, dans le sens des aiguilles d'une montre
        var angle = value * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, arcPen, geometry);
    }
}
