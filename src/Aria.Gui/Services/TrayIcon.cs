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
///   menu       ouvrir, état, lancer ou arrêter une courbe de ventilation, poser un profil GPU ou revenir à l'usine,
///              aller à une page, quitter
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
    private Forms.ToolStripMenuItem? _fanMenu, _gpuMenu;
    private string? _signature;
    private bool _reading, _acting, _disposed;

    /// <summary>Carte réglable et son réglage au dernier relevé ; null sans carte NVIDIA réglable.</summary>
    private GpuTuner? _tuner;
    private GpuTuningState? _card;
    private bool _fansAvailable;

    /// <summary>Dernier état relevé (5 s au plus).</summary>
    public TrayState State { get; private set; } = TrayState.Empty;

    /// <summary>Faux tant que le pilote graphique n'a pas répondu (lancement à l'ouverture de session) : aucune lecture de la carte.</summary>
    private bool _driverReady;

    /// <summary>« Démarrer avec Windows » est activé ; null tant que le planificateur n'a pas été interrogé.</summary>
    private bool? _autostart;

    /// <param name="open">Rouvre la fenêtre.</param>
    /// <param name="openPage">Rouvre la fenêtre sur une page.</param>
    /// <param name="quit">Arrête Aria.</param>
    /// <param name="waitForDriver">
    /// Lancement à l'ouverture de session : la carte n'est lue qu'après <see cref="DriverReady"/>. Lue trop tôt, elle
    /// passerait pour absente jusqu'à la fermeture d'Aria (l'accès au pilote n'est ouvert qu'une fois).
    /// </param>
    public TrayIcon(Action open, Action<Type> openPage, Action quit, bool waitForDriver = false)
    {
        _open = open;
        _openPage = openPage;
        _quit = quit;
        _driverReady = !waitForDriver;
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

    /// <summary>Le pilote graphique répond (ou l'attente est terminée) : la carte peut être lue.</summary>
    public void DriverReady()
    {
        if (_driverReady) return;
        _driverReady = true;
        _ = RefreshAsync();
    }

    /// <summary>Relit l'état, hors du fil de l'interface, puis met l'icône à jour.</summary>
    public async Task RefreshAsync()
    {
        if (_reading || _disposed || !_driverReady) return;
        _reading = true;
        try
        {
            var previous = State;
            // Le planificateur n'est interrogé qu'une fois, puis après chaque bascule de l'option
            _autostart ??= await Task.Run(TrayStartup.Enabled);
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

    private TrayState Read(TrayState previous)
    {
        var (profile, profileText) = (GpuProfileStatus.Unknown, (string?)null);
        if (GpuProfile.Tuner is { } tuner)
        {
            _tuner = tuner;
            // Pendant que l'assistant teste des paliers, la carte change exprès : on garde le dernier état sûr
            if (MeasurementGate.AssistantRunning) (profile, profileText) = (previous.Profile, previous.ProfileText);
            else
            {
                try
                {
                    var (status, card) = GpuProfile.Status(tuner);
                    (profile, profileText) = (status, card.Describe());
                    _card = card;
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
            _fansAvailable = true;
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
            Renderer = new DarkRenderer(),
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

        // Actions sans ouvrir la fenêtre : leurs listes sont refaites à chaque ouverture du menu (une courbe ou un
        // profil enregistré entre-temps y apparaît tout de suite)
        _fanMenu = Submenu("Ventilateurs");
        _gpuMenu = Submenu("Profil GPU");
        menu.Items.Add(_fanMenu);
        menu.Items.Add(_gpuMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Opening += (_, _) => FillActions();
        FillActions();

        menu.Items.Add(Item("Réglage GPU", () => _openPage(typeof(GpuTuningPage))));
        menu.Items.Add(Item("Mises à jour", () => _openPage(typeof(UpdatesPage))));
        menu.Items.Add(Item("Rapports", () => _openPage(typeof(ReportsPage))));
        menu.Items.Add(new Forms.ToolStripSeparator());

        _autostartItem = Item(AutostartText(), ToggleAutostart);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(Item("Quitter Aria Cockpit", _quit));
        return menu;
    }

    // ---------------------------------------------------------------- Démarrer avec Windows

    private Forms.ToolStripMenuItem? _autostartItem;

    private string AutostartText() => Mark(_autostart == true, "Démarrer avec Windows");

    /// <summary>Active ou désactive la tâche « Aria Cockpit\Zone de notification », après avoir dit ce qu'elle fait.</summary>
    private void ToggleAutostart()
    {
        if (_autostart is not { } enabled) return;
        var exe = Environment.ProcessPath ?? "Aria-Cockpit.exe";
        if (enabled)
        {
            Act("arrêt du démarrage avec Windows", TrayStartup.Disable, "Aria Cockpit ne démarre plus avec Windows.", autostart: false);
            return;
        }
        if (!Confirm(
                "Aria Cockpit se lancera à chaque ouverture de session, avec les droits administrateur et sans fenêtre de " +
                "confirmation, directement rangée dans cette icône.\n\n" +
                "• Elle relancera la courbe de ventilation qui tournait quand elle s'est arrêtée (sauf si tu as rendu la main au pilote).\n" +
                "• Ton profil GPU reste réappliqué par sa propre tâche, « Garder au démarrage ».\n" +
                $"• La tâche planifiée « {TrayStartup.TaskName} » lance une copie d'Aria rangée dans « {Aria.Core.Settings.LogonExe.Folder} », " +
                "que seuls les administrateurs peuvent modifier.\n\nDémarrer Aria Cockpit avec Windows ?"))
            return;
        Act("démarrage avec Windows", () => TrayStartup.Enable(exe),
            "Aria Cockpit démarrera avec Windows, rangée dans la zone de notification.", autostart: true);
    }

    private static Forms.ToolStripMenuItem Submenu(string text)
    {
        var item = new Forms.ToolStripMenuItem(text) { ForeColor = MenuText };
        var list = (Forms.ToolStripDropDownMenu)item.DropDown;
        list.ShowImageMargin = false;
        list.BackColor = MenuBack;
        list.ForeColor = MenuText;
        return item;
    }

    /// <summary>« ● » devant le choix en place.</summary>
    private static string Mark(bool active, string text) => (active ? "●  " : "     ") + text;

    // ---------------------------------------------------------------- Actions du menu

    /// <summary>Remplit « Ventilateurs » et « Profil GPU » d'après les fichiers et l'état du moment.</summary>
    private void FillActions()
    {
        if (_fanMenu is null || _gpuMenu is null) return;
        try
        {
            // Pendant l'assistant, la carte et les ventilateurs font partie de la mesure : on n'y touche pas d'ici
            var free = !MeasurementGate.AssistantRunning && !_acting;
            FillFans(_fanMenu, free);
            FillGpu(_gpuMenu, free);
            if (_autostartItem is not null)
            {
                _autostartItem.Text = AutostartText();
                _autostartItem.Enabled = _autostart is not null && !_acting;
            }
        }
        catch (Exception e)
        {
            // Fichier de profils illisible : le menu garde ses autres entrées
            ErrorLog.Write(e);
        }
    }

    private void FillFans(Forms.ToolStripMenuItem menu, bool free)
    {
        menu.Visible = _fansAvailable;
        menu.Enabled = free;
        menu.DropDownItems.Clear();
        if (!_fansAvailable) return;

        var running = FanCurveService.Running?.Name;
        foreach (var curve in TrayChoices.FanCurves(FanProfiles.All(), FanCurveStore.Load()))
        {
            var active = TrayChoices.SameName(curve.Name, running);
            menu.DropDownItems.Add(Item(Mark(active, curve.Name), () => Act(
                $"courbe « {curve.Name} »",
                () => FanCurveService.Start(curve),
                $"Courbe « {curve.Name} » en place : Aria pilote les ventilateurs.")));
        }
        menu.DropDownItems.Add(new Forms.ToolStripSeparator());
        var stop = Item(Mark(running is null, "Automatique (pilote NVIDIA)"), () => Act(
            "retour au pilote",
            FanCurveService.Stop,
            "Ventilateurs rendus au pilote NVIDIA."));
        stop.Enabled = running is not null;
        menu.DropDownItems.Add(stop);
    }

    private void FillGpu(Forms.ToolStripMenuItem menu, bool free)
    {
        menu.Visible = _tuner is not null;
        menu.Enabled = free;
        menu.DropDownItems.Clear();
        if (_tuner is not { } tuner) return;

        var profiles = GpuProfiles.For(tuner.Identity).Where(p => p.Tuning is not null).ToList();
        foreach (var profile in profiles)
        {
            var active = _card is { } card && card.SameTuning(profile.Tuning!);
            var changed = profile.DriverChanged(tuner.Identity) ? " (pilote changé depuis)" : "";
            menu.DropDownItems.Add(Item(Mark(active, profile.Name + changed), () => ApplyProfile(tuner, profile)));
        }
        if (profiles.Count == 0)
            menu.DropDownItems.Add(new Forms.ToolStripLabel("Aucun profil enregistré (page Réglage GPU)") { ForeColor = MenuMuted });

        menu.DropDownItems.Add(new Forms.ToolStripSeparator());
        var factory = Item(Mark(State.Profile == GpuProfileStatus.Factory, "Revenir au réglage d'usine…"), () => RevertToFactory(tuner));
        factory.Enabled = State.Profile is GpuProfileStatus.Applied or GpuProfileStatus.Lost;
        menu.DropDownItems.Add(factory);
    }

    /// <summary>
    /// Pose un profil enregistré. « Garder au démarrage » reste tel qu'il est : le menu change le réglage, pas la
    /// façon dont il est réappliqué (comme « Charger » sur la page Réglage GPU).
    /// </summary>
    private void ApplyProfile(GpuTuner tuner, SavedGpuProfile profile)
    {
        if (profile.Tuning is not { } tuning) return;
        if (profile.DriverChanged(tuner.Identity) && !Confirm(
                $"Le profil « {profile.Name} » a été enregistré avec le pilote {profile.DriverVersion}, et le pilote actuel est le " +
                $"{tuner.Identity.DriverVersion}. Un réglage stable avec un pilote ne l'est pas toujours avec un autre.\n\n" +
                $"Appliquer quand même : {tuning.Describe()} ?"))
            return;

        var keep = GpuProfile.Load() is { } file && GpuTuningState.TryParse(file.Current, out var current) && current.AtStartup;
        var exe = Environment.ProcessPath ?? "Aria-Cockpit.exe";
        Act($"profil « {profile.Name} »",
            () => GpuProfile.Apply(tuner, tuning with { AtStartup = keep }, exe),
            $"Profil « {profile.Name} » appliqué" + (keep
                ? ", réappliqué à chaque ouverture de session."
                : " jusqu'au prochain redémarrage. Pour le garder : page Réglage GPU, « Garder au démarrage »."));
    }

    private void RevertToFactory(GpuTuner tuner)
    {
        if (!Confirm("La carte graphique retrouve ses valeurs d'usine, et la tâche de démarrage « Aria Cockpit\\Profil GPU » est " +
                     "supprimée : ton profil ne sera plus réappliqué à l'ouverture de session.\n\nRevenir au réglage d'usine ?"))
            return;
        var exe = Environment.ProcessPath ?? "Aria-Cockpit.exe";
        Act("retour au réglage d'usine", () => GpuProfile.RevertToOrigin(tuner, exe), "Carte graphique revenue à ses valeurs d'usine.");
    }

    private static bool Confirm(string text) =>
        System.Windows.MessageBox.Show(text, "Aria Cockpit", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;

    /// <summary>
    /// Lance une action du menu hors du fil de l'interface. Une seule à la fois ; son résultat arrive par
    /// notification, la fenêtre pouvant être fermée.
    /// </summary>
    /// <param name="autostart">État de « Démarrer avec Windows » si l'action réussit ; null si elle n'y touche pas.</param>
    private async void Act(string label, Action action, string done, bool? autostart = null)
    {
        if (_acting || _disposed) return;
        _acting = true;
        try
        {
            await Task.Run(action);
            if (autostart is { } enabled) _autostart = enabled;
            Notify("Aria Cockpit", done);
        }
        catch (Exception e) when (e is GpuTuningException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            Notify($"Aria Cockpit : {label} impossible", e.Message);
        }
        catch (Exception e)
        {
            Notify($"Aria Cockpit : {label} impossible", $"{e.Message} Détails dans {ErrorLog.Write(e)}.");
        }
        finally
        {
            _acting = false;
            await RefreshAsync();
        }
    }

    private static Forms.ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new Forms.ToolStripMenuItem(text) { ForeColor = MenuText };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Ligne d'état : du texte, pas une commande.</summary>
    private static Forms.ToolStripLabel Line(string text) => new(text) { ForeColor = MenuMuted };

    /// <summary>
    /// Dessin du menu, accordé à la fenêtre d'Aria (nuit profonde). Tout est dessiné ici, avec les couleurs d'Aria :
    /// le rendu standard de Windows Forms reprend les couleurs du système dans une application qui n'est pas Windows
    /// Forms, et la surbrillance claire de Windows rendait le texte blanc illisible.
    /// </summary>
    private sealed class DarkRenderer : Forms.ToolStripRenderer
    {
        private static readonly Color Disabled = Color.FromArgb(0x5E, 0x72, 0x90);

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var back = new SolidBrush(MenuBack);
            e.Graphics.FillRectangle(back, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var border = new Pen(MenuBorder);
            e.Graphics.DrawRectangle(border, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        /// <summary>Entrée survolée, ou dont le sous-menu est ouvert : bleu nuit, sur lequel le texte clair reste lisible.</summary>
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            var lit = e.Item.Enabled && (e.Item.Selected || e.Item.Pressed);
            using var back = new SolidBrush(lit ? MenuHover : MenuBack);
            e.Graphics.FillRectangle(back, new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height));
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            var color = e.Item.Enabled ? e.Item.ForeColor : Disabled;
            Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item is { Enabled: false } ? Disabled : MenuText;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var line = new Pen(MenuBorder);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(line, 8, y, e.Item.Width - 8, y);
        }
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
