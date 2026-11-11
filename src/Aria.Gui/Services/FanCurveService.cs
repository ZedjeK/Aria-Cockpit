using Aria.Core.Nvidia;

namespace Aria.Gui.Services;

/// <summary>
/// Courbe de ventilation de toute l'application : elle tourne tant qu'Aria est ouvert, quelle que soit la page
/// affichée, et rend les ventilateurs au pilote quand on l'arrête ou quand Aria se ferme.
/// </summary>
public static class FanCurveService
{
    private static readonly Lazy<GpuFans?> Fans = new(GpuFans.TryOpen);
    private static FanCurveRunner? _runner;

    /// <summary>Ventilateurs pilotables de la carte, ou null. Le premier accès ouvre NVML : à faire hors du fil de l'interface.</summary>
    public static GpuFans? Device => Fans.Value;

    /// <summary>Courbe en cours, ou null si le pilote a la main.</summary>
    public static FanCurve? Running { get; private set; }

    /// <summary>Dernier relevé de la courbe en cours.</summary>
    public static FanStatus? Last { get; private set; }

    /// <summary>Nouveau relevé, démarrage ou arrêt (appelé depuis n'importe quel fil).</summary>
    public static event Action? Changed;

    /// <summary>La courbe s'est arrêtée sur une erreur ; les ventilateurs ont été rendus au pilote.</summary>
    public static event Action<string>? Failed;

    /// <summary>Ce que <see cref="RecoverAfterCrash"/> a fait, pour la carte Ventilateur ; null s'il n'y avait rien à faire.</summary>
    public static string? RecoveryNote { get; private set; }
    public static bool RecoveryFailed { get; private set; }

    /// <summary>Au lancement d'Aria : s'il s'est arrêté pendant qu'il pilotait les ventilateurs, ils sont rendus au pilote.</summary>
    public static void RecoverAfterCrash()
    {
        if (FanCurveStore.Load() is not { Active: true }) return;
        try
        {
            Device?.RestoreAuto();
            FanCurveStore.SetActive(false);
            RecoveryNote = "Aria s'était arrêté pendant qu'il pilotait les ventilateurs : ils ont été rendus au pilote NVIDIA.";
        }
        catch (GpuTuningException e)
        {
            RecoveryFailed = true;
            RecoveryNote = $"Aria s'était arrêté pendant qu'il pilotait les ventilateurs, et le pilote n'a pas repris la main. {e.Message}";
        }
    }

    /// <summary>Enregistre la courbe et la fait tourner. Lève <see cref="GpuTuningException"/> si la carte refuse.</summary>
    public static void Start(FanCurve curve)
    {
        var fans = Device ?? throw new GpuTuningException("Ventilateurs non pilotables sur cette carte.");
        Stop();
        var runner = new FanCurveRunner(fans, new FanCurveController(curve, fans.MinPercent, fans.MaxPercent));
        runner.Updated += status =>
        {
            Last = status;
            Changed?.Invoke();
        };
        runner.Failed += error =>
        {
            if (!ReferenceEquals(_runner, runner)) return;
            _runner = null;
            Running = null;
            Last = null;
            // Arrêt sur une erreur, pas par choix : la courbe reste à relancer au prochain lancement d'Aria
            FanCurveStore.SetStopped(byUser: false);
            Failed?.Invoke(error);
            Changed?.Invoke();
        };

        // Noté avant de démarrer : si Aria s'arrête brutalement, le prochain lancement rendra la main au pilote
        // (et relancera la courbe, avec « Démarrer avec Windows »)
        FanCurveStore.Save(new FanCurveChoice(curve.Name, curve.Points, Active: true, Resume: true));
        try
        {
            runner.Start();
        }
        catch
        {
            // Refusée d'emblée par la carte : inutile de la retenter à chaque lancement
            FanCurveStore.SetStopped(byUser: true);
            throw;
        }
        _runner = runner;
        Running = curve;
        Changed?.Invoke();
    }

    /// <summary>L'utilisateur rend la main au pilote : la courbe s'arrête, et ne sera pas relancée d'elle-même.</summary>
    public static void Stop() => Stop(byUser: true);

    /// <summary>
    /// Aria se ferme : la courbe s'arrête et le pilote reprend la main, mais elle reste « à relancer » au prochain
    /// lancement (avec « Démarrer avec Windows »). Synchrone : la fermeture attend cet arrêt.
    /// </summary>
    public static void StopForExit() => Stop(byUser: false);

    private static void Stop(bool byUser)
    {
        if (_runner is not { } runner) return;
        _runner = null;
        Running = null;
        Last = null;
        try
        {
            runner.StopAsync().GetAwaiter().GetResult();
        }
        finally
        {
            FanCurveStore.SetStopped(byUser);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Au lancement d'Aria, après <see cref="RecoverAfterCrash"/> : relance la courbe qui tournait quand Aria s'est
    /// arrêtée, si « Démarrer avec Windows » est activé. Renvoie ce qui s'est passé, pour une notification, ou null
    /// s'il n'y avait rien à relancer.
    /// </summary>
    public static (string Text, bool Failed)? ResumeAtStartup()
    {
        // Sans courbe à relancer, on n'interroge même pas le planificateur
        if (FanCurveStore.Load() is not { Resume: true } saved) return null;
        if (Aria.Core.Tray.TrayStartup.CurveToResume(saved, Aria.Core.Tray.TrayStartup.Enabled()) is not { } curve) return null;
        try
        {
            Start(curve);
            return ($"Courbe de ventilation « {curve.Name} » relancée.", false);
        }
        catch (GpuTuningException e)
        {
            return ($"Courbe de ventilation « {curve.Name} » non relancée : {e.Message}", true);
        }
    }
}
