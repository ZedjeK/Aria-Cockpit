using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Aria.Core.Settings;
using Aria.Gui.Services;

namespace Aria.Gui.Effects;

/// <summary>
/// Fond animé d'Aria Cockpit : un faisceau balaie la zone des pages de gauche à droite, et des points orange,
/// tirés au hasard à chaque passage, s'allument quand il les croise. Il dit ce que fait l'appli : elle analyse.
///
/// Réglé par la page Paramètres (affiché ou non, vitesse, nombre de points de X à Y). En pause quand la fenêtre est
/// cachée ou réduite, et absent si Windows demande moins d'animations.
/// </summary>
public sealed class SweepBackdrop : Canvas
{
    private const double BeamWidth = 160, DotSize = 10, Edge = 30;

    private static readonly Color Cyan = Color.FromRgb(0x7D, 0xF9, 0xFF);
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x8A, 0x1F);

    private readonly Random _random = new();
    private readonly Border _beam;
    private readonly TranslateTransform _beamMove = new();
    private readonly List<Ellipse> _dots = [];
    private Window? _window;
    private int _pass;

    public SweepBackdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Focusable = false;

        _beam = new Border
        {
            Width = BeamWidth,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xD9, Cyan.R, Cyan.G, Cyan.B)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Background = new LinearGradientBrush(Color.FromArgb(0, Cyan.R, Cyan.G, Cyan.B), Color.FromArgb(0x21, Cyan.R, Cyan.G, Cyan.B), 0),
            RenderTransform = _beamMove,
            Visibility = Visibility.Collapsed
        };
        Children.Add(_beam);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Restart();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.IsVisibleChanged += OnWindowChanged;
            _window.StateChanged += OnWindowStateChanged;
        }
        BackdropService.Changed += OnSettingsChanged;
        SystemParameters.StaticPropertyChanged += OnSystemChanged;
        Restart();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.IsVisibleChanged -= OnWindowChanged;
            _window.StateChanged -= OnWindowStateChanged;
            _window = null;
        }
        BackdropService.Changed -= OnSettingsChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemChanged;
        Stop();
    }

    private void OnWindowChanged(object sender, DependencyPropertyChangedEventArgs e) => Restart();
    private void OnWindowStateChanged(object? sender, EventArgs e) => Restart();
    private void OnSettingsChanged() => Dispatcher.InvokeAsync(Restart);

    private void OnSystemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.InvokeAsync(Restart);
    }

    /// <summary>Le fond tourne seulement s'il est activé, si Windows autorise les animations et si la fenêtre se voit.</summary>
    private bool ShouldRun =>
        BackdropService.Current.Active
        && Fx.Enabled
        && _window is { IsVisible: true, WindowState: not WindowState.Minimized }
        && ActualWidth > BeamWidth && ActualHeight > 2 * Edge;

    private void Stop()
    {
        _pass++;
        _beamMove.BeginAnimation(TranslateTransform.XProperty, null);
        _beam.Visibility = Visibility.Collapsed;
        foreach (var dot in _dots)
        {
            dot.BeginAnimation(OpacityProperty, null);
            Children.Remove(dot);
        }
        _dots.Clear();
    }

    private void Restart()
    {
        Stop();
        if (ShouldRun) StartPass();
    }

    /// <summary>Un passage : de nouveaux points tirés au hasard, puis le faisceau traverse la zone et les allume.</summary>
    private void StartPass()
    {
        var settings = BackdropService.Current;
        var duration = settings.PassDuration;
        var width = ActualWidth;
        var height = ActualHeight;
        var travel = width + BeamWidth;
        var pass = _pass;

        _beam.Height = height;
        _beam.Visibility = Visibility.Visible;

        var count = _random.Next(settings.Minimum, settings.Maximum + 1);
        for (var i = 0; i < count; i++)
        {
            var x = Edge + _random.NextDouble() * (width - 2 * Edge);
            var y = Edge + _random.NextDouble() * (height - 2 * Edge);
            var dot = NewDot();
            SetLeft(dot, x - DotSize / 2);
            SetTop(dot, y - DotSize / 2);
            Children.Add(dot);
            _dots.Add(dot);

            // Le point s'allume au moment où le bord droit du faisceau passe sur lui, puis s'éteint doucement
            var reach = TimeSpan.FromTicks((long)(duration.Ticks * (x / travel)));
            var glow = new DoubleAnimationUsingKeyFrames { BeginTime = reach, FillBehavior = FillBehavior.Stop };
            glow.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            glow.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromTicks(duration.Ticks / 60))));
            glow.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromTicks(duration.Ticks / 5))));
            dot.BeginAnimation(OpacityProperty, glow);
        }

        var move = new DoubleAnimation(-BeamWidth, width, new Duration(duration)) { FillBehavior = FillBehavior.Stop };
        move.Completed += (_, _) =>
        {
            // Un arrêt ou une relance entre-temps a déjà pris la main : ce passage-ci n'enchaîne pas
            if (pass == _pass) Restart();
        };
        _beamMove.BeginAnimation(TranslateTransform.XProperty, move);
    }

    private static Ellipse NewDot() => new()
    {
        Width = DotSize,
        Height = DotSize,
        Opacity = 0,
        Fill = new SolidColorBrush(Orange),
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = 0.75, Color = Orange }
    };
}
