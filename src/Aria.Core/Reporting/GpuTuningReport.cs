using System.Globalization;
using Aria.Core.Bench;
using Aria.Core.Nvidia;
using Aria.Core.Performance;

namespace Aria.Core.Reporting;

/// <summary>Partie du parcours de l'assistant à laquelle appartient une étape.</summary>
public enum TuningPhase { Start, Search, Memory, Power, Validation, Check }

/// <summary>Ce que l'assistant a conclu d'une étape.</summary>
public enum StepVerdict { Reference, Stable, Unstable, NotFaster, Kept, Unjudged, Info }

/// <summary>Une étape du parcours : mesure, contrôle ou décision, avec ce qui l'a motivée.</summary>
/// <param name="Label">« Palier 3 », « Validation », « Auto-contrôle »…</param>
/// <param name="Detail">Mesures et raison : « calculs faux à pleine puissance ; 2552 exécutions, 102 écarts ».</param>
public sealed record TuningStep(DateTime At, TuningPhase Phase, string Label, GpuTuningState Setting, StepVerdict Verdict, string Detail = "");

/// <summary>Tout ce qu'un passage de l'assistant laisse au rapport.</summary>
/// <param name="Before">Réglage de la carte avant l'assistant (remis par « Tout annuler »).</param>
/// <param name="Start">Point de départ des mesures (valeurs d'usine pour Équilibre).</param>
/// <param name="Result">Réglage retenu (ou remis) à la fin.</param>
/// <param name="Changed">Le résultat diffère du réglage d'avant l'assistant.</param>
/// <param name="Outcome">Phrase de fin affichée par l'assistant (« Validé : … », « Aucun palier n'a tenu… »).</param>
/// <param name="Previous">Équilibre enregistré avant ce passage : comparaison pour Équilibre, référence pour les deux autres modes.</param>
public sealed record TuningRun(
    TuningGoal Goal,
    string GpuName,
    string DriverVersion,
    DateTime Started,
    DateTime Ended,
    string Source,
    GpuTuningState Before,
    GpuTuningState Start,
    GpuTuningState Result,
    bool Changed,
    bool Aborted,
    string Outcome,
    bool Validated,
    PerformanceStats? Baseline,
    double? BaselineFps,
    PerformanceStats? ResultStats,
    double? ResultFps,
    BenchStability? ResultChecks,
    BalancedRecord? Previous,
    PerformanceVerdict? Verdict,
    IReadOnlyList<TuningStep> Steps,
    IReadOnlyList<string> Notes,
    IReadOnlyList<DriverIncident> Incidents);

/// <summary>
/// Rapport texte d'un passage de l'assistant, dans C:\ProgramData\AriaCockpit\Rapports : conclusion d'Aria, départ et
/// résultat, déroulé palier par palier avec la raison de chaque verdict, contrôles et garde-fous.
/// </summary>
public static class GpuTuningReport
{
    internal const string FilePrefix = "reglage-gpu_";
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string ModeName(TuningGoal goal) => goal switch
    {
        TuningGoal.Economy => "Économie",
        TuningGoal.Performance => "Performance",
        _ => "Équilibre"
    };

    public static string Save(TuningRun run)
    {
        Directory.CreateDirectory(ReportFile.Folder);
        var path = Path.Combine(ReportFile.Folder, $"{FilePrefix}{run.Ended:yyyy-MM-dd_HHmmss}.txt");
        using var w = new StreamWriter(path);
        Write(run, Environment.MachineName, w);
        return path;
    }

    public static void Write(TuningRun run, string machine, TextWriter w)
    {
        w.WriteLine($"Aria Cockpit — réglage GPU du {run.Ended:dd/MM/yyyy à HH:mm} — {machine}");
        w.WriteLine($"Mode : assistant {ModeName(run.Goal)}");
        w.WriteLine($"Bilan : {Summary(run)}");

        w.WriteLine();
        w.WriteLine("== CARTE ==");
        w.WriteLine($"  {run.GpuName} · pilote {run.DriverVersion}");
        w.WriteLine($"  Mesures : {run.Source}");
        w.WriteLine($"  Durée : {run.Started:HH:mm} → {run.Ended:HH:mm} ({(int)(run.Ended - run.Started).TotalMinutes} min)");

        w.WriteLine();
        w.WriteLine("== CONCLUSION D'ARIA ==");
        foreach (var line in Conclusion(run)) w.WriteLine($"  - {line}");

        w.WriteLine();
        w.WriteLine("== DÉPART ET RÉSULTAT ==");
        w.WriteLine($"  Avant l'assistant : {run.Before.Describe()}");
        w.WriteLine($"  Départ des mesures : {run.Start.Describe()}");
        if (run.Goal != TuningGoal.Balanced && run.Previous?.ResultState is { } balanced)
            w.WriteLine($"  Équilibre : {balanced.Describe()}");
        w.WriteLine($"  Résultat : {run.Result.Describe()}");
        WriteTable(run, w);

        w.WriteLine();
        w.WriteLine("== DÉROULÉ ==");
        if (run.Steps.Count == 0) w.WriteLine("  Aucune mesure terminée.");
        foreach (var s in run.Steps)
        {
            var verdict = VerdictText(s.Verdict);
            w.WriteLine($"  {s.At:HH:mm}  {s.Label} · {s.Setting.Describe()}{(verdict.Length > 0 ? $" → {verdict}" : "")}");
            if (s.Detail.Length > 0) w.WriteLine($"         {s.Detail}");
        }

        if (run.ResultChecks is { } c && (c.ExecutionsCalcul > 0 || c.Passes > 0))
        {
            w.WriteLine();
            w.WriteLine("== CONTRÔLES DU RÉSULTAT ==");
            if (c.ExecutionsCalcul > 0) w.WriteLine($"  Calcul vérifié : {c.ExecutionsCalcul} exécutions à pleine puissance, {c.EcartsCalcul} écart(s)");
            if (c.Passes > 0)
                w.WriteLine(F($"  Mémoire vidéo : {c.Passes} passes, {c.MotsFaux} mot(s) relu(s) faux") +
                            (c.BandePassanteLectureMedianeGoS is { } read ? F($", lecture {read:0} Go/s") : ""));
        }

        w.WriteLine();
        w.WriteLine("== GARDE-FOUS ==");
        w.WriteLine(run.Incidents.Count == 0
            ? "  Aucun incident du pilote NVIDIA noté par Windows pendant l'assistant."
            : $"  Incidents du pilote pendant l'assistant : {Incidents(run.Incidents)}.");
        foreach (var note in run.Notes) w.WriteLine($"  {note}");
    }

    /// <summary>« Équilibre validé · 320 W · plafond 2685 MHz · GPU +145 MHz · mémoire +750 MHz · 104,8 FPS (+6,7 %) · 304 W (-11 W) ».</summary>
    internal static string Summary(TuningRun run)
    {
        var mode = ModeName(run.Goal);
        if (run.Aborted) return $"{mode} interrompu avant la fin";
        if (!run.Changed) return $"{mode} : aucun réglage retenu";
        var parts = new List<string> { $"{mode} {(run.Validated ? "validé" : "non validé")} · {run.Result.Describe()}" };
        if (Fps(run) is { } fps) parts.Add(F($"{fps.Now:0.0} FPS ({fps.Percent:+0.0;-0.0;0} %)"));
        if (run.ResultStats?.AvgPower is { } p && run.Baseline?.AvgPower is { } b) parts.Add(F($"{p:0} W ({TuningPlan.Delta(p, b, "W")})"));
        return string.Join(" · ", parts);
    }

    /// <summary>La conclusion d'Aria, phrase par phrase (au vouvoiement, comme le reste de l'interface).</summary>
    public static IReadOnlyList<string> Conclusion(TuningRun run)
    {
        var lines = new List<string>();
        var mode = ModeName(run.Goal);

        // 1. Ce qui est en place
        if (run.Aborted)
            lines.Add(run.Outcome.Length > 0 ? run.Outcome : $"Assistant abandonné : la carte est revenue à son réglage d'avant ({run.Before.Describe()}).");
        else if (!run.Changed)
            lines.Add(run.Outcome.Length > 0 ? run.Outcome : "Aucun palier n'a tenu : la carte est revenue à son réglage de départ.");
        else
            lines.Add($"{mode} {(run.Validated ? "validé" : "terminé sans validation")} : {run.Result.Describe()}.");

        // 2. Ce que le résultat change, mesures à l'appui
        if (!run.Aborted && run.Changed && run.Baseline is { } b && run.ResultStats is { } r)
        {
            var fps = Fps(run);
            var watts = r.AvgPower is { } rp && b.AvgPower is { } bp ? rp - bp : (double?)null;
            var measured = new List<string>();
            if (fps is { } f) measured.Add(F($"{f.Now:0.0} FPS au banc contre {f.Before:0.0} au départ ({f.Percent:+0.0;-0.0;0} %)"));
            if (watts is { } dw) measured.Add(F($"{r.AvgPower:0} W contre {b.AvgPower:0} W ({dw:+0;-0;0} W)"));
            measured.Add(F($"{r.AvgClock:0} MHz de moyenne contre {b.AvgClock:0} MHz"));
            measured.Add($"{r.MaxTemp} °C au plus contre {b.MaxTemp} °C");
            lines.Add($"Mesuré : {string.Join(", ", measured)}.");
            if (b.PowerCapShare > 0.2 || r.PowerCapShare > 0.2)
                lines.Add(F($"Bridée par sa limite de puissance {r.PowerCapShare * 100:0} % du temps, contre {b.PowerCapShare * 100:0} % au départ."));

            if (run.Goal == TuningGoal.Balanced && fps is { } g && watts is { } w)
                lines.Add(g.Percent >= 1 && w <= 0 ? "Plus rapide et moins gourmande à la fois : c'est le résultat d'un undervolt réussi."
                    : g.Percent > -1 && w < 0 ? "Les mêmes performances pour moins de watts : l'objectif d'Équilibre est atteint."
                    : g.Percent <= -1 ? "Un peu moins de FPS qu'au départ : le plafond retient la carte. Si vous visez les FPS, essayez Performance."
                    : "Plus rapide, mais en consommant plus : le gain vient du décalage plus que de l'économie.");
            if (run.Goal == TuningGoal.Economy && watts is { } ew)
                lines.Add(F($"Économie : {ew:+0;-0;0} W et {(int)r.MaxTemp - (int)b.MaxTemp:+0;-0;0} °C") +
                          (fps is { } ef ? F($" pour {ef.Percent:+0.0;-0.0;0} % de FPS.") : "."));
        }
        if (run.Verdict is { } verdict)
            lines.Add($"Avis sur Performance : {verdict.Title}. {string.Join(" ", verdict.Details)}");

        // 3. Où et pourquoi la recherche s'est arrêtée
        lines.AddRange(Limits(run, TuningPhase.Search));
        lines.AddRange(Limits(run, TuningPhase.Memory));
        lines.AddRange(Limits(run, TuningPhase.Power));

        // 4. Validation
        var validation = run.Steps.LastOrDefault(s => s.Phase == TuningPhase.Validation);
        if (run.Validated)
            lines.Add("Validation réussie : 15 minutes sur le réglage retenu sans plantage ni erreur" +
                      (run.ResultChecks is { ExecutionsCalcul: > 0 } ? ", puis calcul et mémoire vérifiés sur la carte chaude." : "."));
        else if (validation is { Verdict: StepVerdict.Unstable } failed)
            lines.Add($"La validation a échoué ({failed.Detail}) : le réglage a reculé d'un cran. Relancez la validation avant de le garder.");
        else if (!run.Aborted && run.Changed && run.Goal == TuningGoal.Balanced)
            lines.Add("Ce réglage n'a pas passé la validation de 15 minutes : ne le gardez pas au démarrage avant de l'avoir validé.");

        // 5. Comparaison avec l'Équilibre précédent
        if (run.Goal == TuningGoal.Balanced && run.Changed && !run.Aborted && run.Previous?.ResultState is { } old)
        {
            var when = $"l'Équilibre du {run.Previous.At:dd/MM}";
            var driver = run.Previous.DriverVersion.Length > 0 && run.Previous.DriverVersion != run.DriverVersion
                ? $" Le pilote a changé entre-temps ({run.Previous.DriverVersion} → {run.DriverVersion}), ce qui peut déplacer la courbe." : "";
            lines.Add(old.SameTuning(run.Result) ? $"Même réglage que {when} : le résultat se reproduit, bon signe de fiabilité.{driver}"
                : old.CoreOffsetMHz == run.Result.CoreOffsetMHz && old.MaxClockMHz == run.Result.MaxClockMHz
                    ? $"Même décalage GPU que {when} ({Signed(old.CoreOffsetMHz)} MHz) ; seule la mémoire diffère ({Signed(old.MemoryOffsetMHz)} → {Signed(run.Result.MemoryOffsetMHz)} MHz).{driver}"
                    : $"Différent de {when} ({old.Describe()}).{driver}");
        }

        // 6. Ce qui a pu fausser ou interrompre les mesures
        var unjudged = run.Steps.Count(s => s.Verdict == StepVerdict.Unjudged);
        if (unjudged > 0) lines.Add($"{unjudged} mesure(s) non jugée(s) puis refaite(s) ou abandonnée(s) : voir le déroulé.");
        if (run.Incidents.Count > 0)
            lines.Add($"Windows a noté des incidents du pilote pendant l'assistant ({Incidents(run.Incidents)}) : si un palier n'a pas été vu instable, " +
                      "revenez d'origine et refaites l'assistant.");

        // 7. Conseil
        if (!run.Aborted && run.Changed && (run.Validated || run.Goal != TuningGoal.Balanced) && run.Verdict?.RecommendsBalanced != true)
            lines.Add("Conseil : enregistrez-le comme profil, et gardez-le au démarrage s'il vous convient. Refaites l'assistant après une mise à jour " +
                      "du pilote NVIDIA, ou si un jeu ou une IA locale plante avec ce réglage.");
        return lines;
    }

    /// <summary>« Décalage GPU : +160 MHz est le plus haut palier passé ; +175 MHz a échoué (…). Retenu : +145 MHz, un cran de marge. »</summary>
    private static IEnumerable<string> Limits(TuningRun run, TuningPhase phase)
    {
        var steps = run.Steps.Where(s => s.Phase == phase).ToList();
        if (steps.Count == 0) yield break;
        var watts = phase == TuningPhase.Power || (run.Goal == TuningGoal.Economy && run.Start.MaxClockMHz == 0);
        Func<GpuTuningState, double> value = phase == TuningPhase.Memory ? s => s.MemoryOffsetMHz
            : watts ? s => -s.PowerLimitWatts
            : run.Goal != TuningGoal.Economy ? s => s.CoreOffsetMHz : s => -s.MaxClockMHz;
        var name = phase == TuningPhase.Memory ? "Mémoire" : watts ? "Limite de puissance" : run.Goal != TuningGoal.Economy ? "Décalage GPU" : "Plafond";
        // Recherche sur un autre réglage que prévu (carte sans plafond : limite de puissance) : réglages complets
        Func<GpuTuningState, string> label = s => Varying(run, phase, s);
        if (steps.Count > 1 && steps.Select(s => value(s.Setting)).Distinct().Count() == 1)
        {
            name = "Paliers";
            label = s => s.Describe();
            value = s => s.PowerLimitWatts + s.CoreOffsetMHz - s.MaxClockMHz;
        }

        var passed = steps.Where(s => s.Verdict is StepVerdict.Stable or StepVerdict.Kept).MaxBy(s => value(s.Setting));
        var refused = steps.Where(s => s.Verdict is StepVerdict.Unstable or StepVerdict.NotFaster).MinBy(s => value(s.Setting));
        var parts = new List<string>();
        if (passed is not null) parts.Add($"{label(passed.Setting)} est le {(watts || (run.Goal == TuningGoal.Economy && phase == TuningPhase.Search) ? "plus bas palier accepté" : "plus haut palier passé")}");
        if (refused is not null)
            parts.Add($"{label(refused.Setting)} {(refused.Verdict == StepVerdict.NotFaster ? "n'allait pas plus vite" : "a échoué")}" +
                      // La raison seule : les mesures qui la suivent (« ; 2700 MHz, … ») restent dans le déroulé
                      (refused.Detail.Length > 0 ? $" ({refused.Detail.Split(" ; ")[0]})" : ""));
        if (parts.Count == 0) yield break;
        var result = label(run.Result);
        var margin = passed is not null && value(run.Result) < value(passed.Setting) ? $"Retenu : {result}, un cran de marge sous le dernier palier passé."
            : passed is not null ? $"Retenu : {result}." : "";
        yield return $"{name} : {string.Join(" ; ", parts)}. {margin}".TrimEnd();
    }

    /// <summary>Le réglage que la phase fait varier : « +145 MHz » (GPU ou mémoire), « 2550 MHz » (plafond), « 250 W ».</summary>
    private static string Varying(TuningRun run, TuningPhase phase, GpuTuningState s) =>
        phase == TuningPhase.Memory ? $"{Signed(s.MemoryOffsetMHz)} MHz"
        : phase == TuningPhase.Power ? F($"{s.PowerLimitWatts:0} W")
        : run.Goal != TuningGoal.Economy ? $"{Signed(s.CoreOffsetMHz)} MHz"
        : run.Start.MaxClockMHz != 0 ? $"{s.MaxClockMHz} MHz" : F($"{s.PowerLimitWatts:0} W");

    private static void WriteTable(TuningRun run, TextWriter w)
    {
        var columns = new List<(string Title, PerformanceStats? Stats, double? Fps)> { ("Départ", run.Baseline, run.BaselineFps) };
        if (run.Goal != TuningGoal.Balanced && run.Previous is { } p) columns.Add(("Équilibre", p.ResultStats, p.ResultFps));
        columns.Add(("Résultat", run.ResultStats, run.ResultFps));
        if (columns.All(c => c.Stats is null)) return;

        var rows = new List<(string Name, Func<PerformanceStats, double?, string?> Cell)>
        {
            ("FPS moyens (banc)", (_, fps) => fps is { } f ? F($"{f:0.0}") : null),
            ("Fréquence moyenne", (s, _) => F($"{s.AvgClock:0} MHz")),
            ("Consommation moyenne", (s, _) => s.AvgPower is { } pw ? F($"{pw:0} W") : null),
            ("Bridée en puissance", (s, _) => F($"{s.PowerCapShare * 100:0} % du temps")),
            ("Température max", (s, _) => $"{s.MaxTemp} °C"),
            ("Charge GPU moyenne", (s, _) => F($"{s.AvgLoad:0} %"))
        };
        w.WriteLine();
        w.WriteLine($"  {"",-24}{string.Concat(columns.Select(c => $"{c.Title,-18}"))}".TrimEnd());
        foreach (var (name, cell) in rows)
        {
            var cells = columns.Select(c => c.Stats is null ? null : cell(c.Stats, c.Fps)).ToList();
            if (cells.All(x => x is null)) continue;
            w.WriteLine($"  {name,-24}{string.Concat(cells.Select(x => $"{x ?? "–",-18}"))}".TrimEnd());
        }
    }

    private static (double Now, double Before, double Percent)? Fps(TuningRun run) =>
        run.ResultFps is { } now && run.BaselineFps is { } before && before > 0 ? (now, before, (now - before) / before * 100) : null;

    private static string Incidents(IReadOnlyList<DriverIncident> incidents) =>
        string.Join(", ", incidents.GroupBy(i => i.Kind).Select(g => $"{g.Count()} × {DriverIncidents.Describe(g.Key)}"));

    private static string VerdictText(StepVerdict v) => v switch
    {
        StepVerdict.Reference => "référence",
        StepVerdict.Stable => "stable",
        StepVerdict.Unstable => "instable",
        StepVerdict.NotFaster => "pas plus rapide",
        StepVerdict.Kept => "gardé à votre demande",
        StepVerdict.Unjudged => "non jugé",
        _ => ""
    };

    private static string Signed(int value) => value.ToString("+0;-0;+0", CultureInfo.InvariantCulture);

    private static string F(FormattableString s) => s.ToString(Fr);
}
