using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Aria.Gui.Effects;

/// <summary>
/// Cadre à coins coupés des panneaux d'Aria : le coin haut droit et le coin bas gauche sont biseautés, les deux autres
/// restent droits. Un Border ne sait dessiner que des coins arrondis ; celui-ci trace sa forme lui-même, fond puis
/// liseré, et place son contenu à l'intérieur comme le ferait un Border.
/// </summary>
public sealed class CutBorder : Decorator
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(CutBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(CutBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(double), typeof(CutBorder),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeOpacityProperty = DependencyProperty.Register(
        nameof(StrokeOpacity), typeof(double), typeof(CutBorder),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CutProperty = DependencyProperty.Register(
        nameof(Cut), typeof(double), typeof(CutBorder),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(CutBorder),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Fond du panneau.</summary>
    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>Couleur du liseré.</summary>
    public Brush? BorderBrush
    {
        get => (Brush?)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    /// <summary>Épaisseur du liseré.</summary>
    public double Stroke
    {
        get => (double)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Opacité du liseré : la même couleur sert au liseré fin de tous les panneaux et au liseré franc.</summary>
    public double StrokeOpacity
    {
        get => (double)GetValue(StrokeOpacityProperty);
        set => SetValue(StrokeOpacityProperty, value);
    }

    /// <summary>Longueur du biseau, sur chaque côté du coin coupé.</summary>
    public double Cut
    {
        get => (double)GetValue(CutProperty);
        set => SetValue(CutProperty, value);
    }

    /// <summary>Marge entre le liseré et le contenu.</summary>
    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    private double Edge => Math.Max(0, Stroke);

    protected override Size MeasureOverride(Size constraint)
    {
        var extra = new Size(Padding.Left + Padding.Right + 2 * Edge, Padding.Top + Padding.Bottom + 2 * Edge);
        if (Child is not { } child) return extra;
        child.Measure(new Size(Math.Max(0, constraint.Width - extra.Width), Math.Max(0, constraint.Height - extra.Height)));
        return new Size(child.DesiredSize.Width + extra.Width, child.DesiredSize.Height + extra.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(
            Padding.Left + Edge,
            Padding.Top + Edge,
            Math.Max(0, arrangeSize.Width - Padding.Left - Padding.Right - 2 * Edge),
            Math.Max(0, arrangeSize.Height - Padding.Top - Padding.Bottom - 2 * Edge)));
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Le tracé passe au milieu du liseré, pour qu'il ne déborde pas du panneau
        var half = Edge / 2;
        var cut = Math.Max(0, Math.Min(Cut, Math.Min(width, height) / 2));
        var shape = new StreamGeometry();
        using (var context = shape.Open())
        {
            context.BeginFigure(new Point(half, half), isFilled: true, isClosed: true);
            context.LineTo(new Point(width - cut, half), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(width - half, cut), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(width - half, height - half), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(cut, height - half), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(half, height - cut), isStroked: true, isSmoothJoin: false);
        }
        shape.Freeze();

        if (Background is { } background) drawingContext.DrawGeometry(background, null, shape);
        if (BorderBrush is not { } brush || Edge <= 0) return;
        drawingContext.PushOpacity(Math.Clamp(StrokeOpacity, 0, 1));
        drawingContext.DrawGeometry(null, new Pen(brush, Edge), shape);
        drawingContext.Pop();
    }
}
