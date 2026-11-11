using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Aria.Core.Nvidia;
using Aria.Core.Tray;
using Aria.Gui.Views;
using Forms = System.Windows.Forms;

namespace Aria.Gui.Services;

/// <summary>
/// Icône d'Aria Cockpit dans la zone de notification (à côté de l'horloge) : son dessin, son infobulle et son menu
/// suivent ce qu'Aria a d'actif sur la machine, pour tout voir sans ouvrir la fenêtre.
///
///   icône      délavée (rien d'actif), normale (profil GPU en place ou courbe en cours), pastille jaune (le profil
///              enregistré n'est plus sur la carte), pastille cyan (Aria pilote les ventilateurs)
///   infobulle  « Aria Cockpit · profil GPU appliqué · courbe « Ma config » · 52 °C · ventilateurs 60 % »
///   menu       ouvrir, état, aller à une page, quitter
///
/// L'état du réglage GPU est relu sur la carte toutes les 5 s, hors du fil de l'interface : l'icône dit ce que la
/// carte répond, pas ce qu'Aria croit avoir posé.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private static readonly Color MenuBack = Color.FromArgb(0x0A, 0x14, 0x28);
    private static readonly Color MenuHover = Color.FromArgb(0x1C, 0x30, 0x55);
    private static readonly Color MenuBorder = Color.FromArgb(0x2D, 0x4F, 0x78);
    private static readonly Color MenuText = Color.FromArgb(0xDD, 0xEB, 0xFF);
    private static readonly Color MenuMuted = Color.FromArgb(0x93, 0xA8, 0xC8);
    private static readonly Color AlertColor = Color.FromArgb(0xFF, 0xD2, 0x3F);
    private static readonly Color FanColor = Color.FromArgb(0x7D, 0xF9, 0xFF);

    private readonly Forms.NotifyIcon _icon;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private readonly Action _open;
    private readonly Action<Type> _openPage;
    private readonly Action _quit;
    private readonly Dictionary<(TrayIconKind Kind, bool Fan), Icon> _icons = [];
    private readonly List<IntPtr> _iconHandles = [];
    private Forms.ToolStripLabel? _profileLine, _fanLine, _workLine;
    private string? _signature;
    private bool _reading, _disposed;

    /// <summary>Dernier état relevé (5 s au plus).</summary>
    public TrayState State { get; private set; } = TrayState.Empty;

    /// <param name="open">Rouvre la fenêtre.</param>
    /// <param name="openPage">Rouvre la fenêtre sur une page.</param>
    /// <param name="quit">Arrête Aria.</param>
    public TrayIcon(Action open, Action<Type> openPage, Action quit)
    {
        _open = open;
        _openPage = openPage;
        _quit = quit;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _icon = new Forms.NotifyIcon { Text = State.Summary(), Icon = IconFor(TrayIconKind.Idle, fan: false), Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _open();
        };
        Show(State);

        // Un changement connu (courbe lancée ou arrêtée, opération commencée ou finie) n'attend pas le relevé suivant
        FanCurveService.Changed += OnFanCurveChanged;
        BackgroundWork.Changed += RefreshSoon;
        MeasurementGate.Changed += RefreshSoon;

        _timer = new DispatcherTimer(Interval, DispatcherPriority.Background, (_, _) => _ = RefreshAsync(), _dispatcher);
        _ = RefreshAsync();
    }

    /// <summary>
    /// État à l'instant : le dernier relevé de la carte, avec la courbe et les opérations en cours lues tout de suite
    /// (la règle de fermeture ne doit pas se tromper sur une courbe lancée il y a deux secondes).
    /// </summary>
    public TrayState Current() => State with { FanCurve = FanCurveService.Running?.Name, Work = BackgroundWork.Running };

    /// <summary>Notification Windows (la fenêtre peut être fermée).</summary>
    public void Notify(string title, string text)
    {
        if (!_disposed) _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
    }

    /// <summary>Relit l'état, hors du fil de l'interface, puis met l'icône à jour.</summary>
    public async Task RefreshAsync()
    {
        if (_reading || _disposed) return;
        _reading = true;
        try
        {
            var previous = State;
            var state = await Task.Run(() => Read(previous));
            if (!_disposed) Show(state);
        }
        catch (Exception e)
        {
            // L'icône est un témoin : une lecture ratée ne doit jamais arrêter Aria
            ErrorLog.Write(e);
        }
        finally
        {
            _reading = false;
        }
    }

    private void RefreshSoon() => _dispatcher.InvokeAsync(() => _ = RefreshAsync());

    /// <summary>La courbe signale chaque relevé (un par seconde) : seuls son démarrage et son arrêt comptent ici.</summary>
    private void OnFanCurveChanged()
    {
        if (FanCurveService.Running?.Name != State.FanCurve) RefreshSoon();
    }

    private static TrayState Read(TrayState previous)
    {
        var (profile, profileText) = (GpuProfileStatus.Unknown, (string?)null);
        if (GpuProfile.Tuner is { } tuner)
        {
            // Pendant que l'assistant teste des paliers, la carte change exprès : on garde le dernier état sûr
            if (MeasurementGate.AssistantRunning) (profile, profileText) = (previous.Profile, previous.ProfileText);
            else
            {
                try
                {
                    var (status, card) = GpuProfile.Status(tuner);
                    (profile, profileText) = (status, card.Describe());
                }
                catch (GpuTuningException)
                {
                    // Carte illisible à cet instant (pilote en cours de rechargement) : état inconnu jusqu'au relevé suivant
                }
            }
        }

        uint? temperature = null, speed = null;
        if (FanCurveService.Device is { } fans)
        {
            temperature = fans.ReadTemperature();
            speed = fans.ReadSpeeds().Max();
        }
        return new TrayState(profile, profileText, FanCurveService.Running?.Name, temperature, speed, BackgroundWork.Running);
    }

    private void Show(TrayState state)
    {
        State = state;
        _icon.Text = state.Summary();
        if (state.Signature != _signature)
        {
            _signature = state.Signature;
            _icon.Icon = IconFor(state.Icon, state.FanBadge);
            var old = _icon.ContextMenuStrip;
            _icon.ContextMenuStrip = BuildMenu(state);
            old?.Dispose();
        }
        // Température et vitesse changent à chaque relevé : seuls les textes sont repris
        if (_profileLine is not null) _profileLine.Text = ProfileText(state);
        if (_fanLine is not null) _fanLine.Text = Sentence(state.FanLine ?? "");
        if (_workLine is not null) _workLine.Text = Sentence(state.WorkLine ?? "");
    }

    private static string ProfileText(TrayState state) =>
        state.ProfileText is { } tuning && state.Profile is not GpuProfileStatus.Unknown
            ? $"{Sentence(state.ProfileLine!)} : {tuning}"
            : Sentence(state.ProfileLine ?? "carte graphique non réglable");

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // ---------------------------------------------------------------- Menu

    private Forms.ContextMenuStrip BuildMenu(TrayState state)
    {
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false },
            ShowImageMargin = false,
            BackColor = MenuBack,
            ForeColor = MenuText
        };

        var open = Item("Ouvrir Aria Cockpit", _open);
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());

        _profileLine = Line(ProfileText(state));
        menu.Items.Add(_profileLine);
        _fanLine = state.FanLine is null ? null : Line(Sentence(state.FanLine));
        if (_fanLine is not null) menu.Items.Add(_fanLine);
        _workLine = state.WorkLine is null ? null : Line(Sentence(state.WorkLine));
        if (_workLine is not null) menu.Items.Add(_workLine);
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add(Item("Réglage GPU", () => _openPage(typeof(GpuTuningPage))));
        menu.Items.Add(Item("Mises à jour", () => _openPage(typeof(UpdatesPage))));
        menu.Items.Add(Item("Rapports", () => _openPage(typeof(ReportsPage))));
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add(Item("Quitter Aria Cockpit", _quit));
        return menu;
    }

    private static Forms.ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new Forms.ToolStripMenuItem(text) { ForeColor = MenuText };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Ligne d'état : du texte, pas une commande.</summary>
    private static Forms.ToolStripLabel Line(string text) => new(text) { ForeColor = MenuMuted };

    /// <summary>Couleurs du menu, accordées à la fenêtre d'Aria (nuit profonde).</summary>
    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => MenuBack;
        public override Color ImageMarginGradientBegin => MenuBack;
        public override Color ImageMarginGradientMiddle => MenuBack;
        public override Color ImageMarginGradientEnd => MenuBack;
        public override Color MenuBorder => TrayIcon.MenuBorder;
        public override Color MenuItemBorder => MenuHover;
        public override Color MenuItemSelected => MenuHover;
        public override Color MenuItemSelectedGradientBegin => MenuHover;
        public override Color MenuItemSelectedGradientEnd => MenuHover;
        public override Color MenuItemPressedGradientBegin => MenuHover;
        public override Color MenuItemPressedGradientEnd => MenuHover;
        public override Color SeparatorDark => TrayIcon.MenuBorder;
        public override Color SeparatorLight => MenuBack;
    }

    // ---------------------------------------------------------------- Dessin de l'icône

    private const int IconSize = 32, DotSize = 13;

    /// <summary>Icône d'Aria dans l'état demandé, dessinée une fois puis gardée.</summary>
    private Icon IconFor(TrayIconKind kind, bool fan)
    {
        if (_icons.TryGetValue((kind, fan), out var cached)) return cached;

        using var bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var logo = LoadLogo();
            var area = new Rectangle(0, 0, IconSize, IconSize);
            if (kind == TrayIconKind.Idle)
            {
                // Rien d'actif : logo en gris, à moitié effacé
                using var faded = new ImageAttributes();
                faded.SetColorMatrix(new ColorMatrix(
                [
                    [0.30f, 0.30f, 0.30f, 0, 0],
                    [0.59f, 0.59f, 0.59f, 0, 0],
                    [0.11f, 0.11f, 0.11f, 0, 0],
                    [0, 0, 0, 0.60f, 0],
                    [0, 0, 0, 0, 1]
                ]));
                g.DrawImage(logo, area, 0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, faded);
            }
            else g.DrawImage(logo, area);

            if (kind == TrayIconKind.Alert) Dot(g, IconSize - DotSize, 0, AlertColor);
            if (fan) Dot(g, IconSize - DotSize, IconSize - DotSize, FanColor);
        }

        var handle = bitmap.GetHicon();
        _iconHandles.Add(handle);
        var icon = Icon.FromHandle(handle);
        _icons[(kind, fan)] = icon;
        return icon;
    }

    private static void Dot(Graphics g, int x, int y, Color color)
    {
        using var edge = new SolidBrush(Color.FromArgb(0x03, 0x08, 0x12));
        using var fill = new SolidBrush(color);
        g.FillEllipse(edge, x - 1, y - 1, DotSize + 1, DotSize + 1);
        g.FillEllipse(fill, x + 1, y + 1, DotSize - 3, DotSize - 3);
    }

    /// <summary>Logo d'Aria Cockpit : l'icône de l'exe, en 32 px.</summary>
    private static Bitmap LoadLogo()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Aria-Cockpit.ico"))
                       ?? throw new InvalidOperationException("Icône d'Aria introuvable dans l'exe.");
        using var stream = resource.Stream;
        using var icon = new Icon(stream, IconSize, IconSize);
        return icon.ToBitmap();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        FanCurveService.Changed -= OnFanCurveChanged;
        BackgroundWork.Changed -= RefreshSoon;
        MeasurementGate.Changed -= RefreshSoon;
        // Sans ce retrait, l'icône reste affichée jusqu'au passage de la souris
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        foreach (var handle in _iconHandles) DestroyIcon(handle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
