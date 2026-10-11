using System.Text;
using System.Text.RegularExpressions;
using Aria.Core.Diagnostics;
using Aria.Core.Models;
using Aria.Core.Settings;

namespace Aria.Core.Reporting;

/// <summary>Données de la fiche PC.</summary>
/// <param name="History">Dernières actions (rapports), du plus récent au plus ancien.</param>
public sealed record HtmlReportInput(
    DateTime At,
    string Version,
    HardwareProfile Hardware,
    BiosGuide Bios,
    IReadOnlyList<DiagnosticFinding> Findings,
    IReadOnlyList<PlanItem> Plan,
    IReadOnlyList<ReportInfo> History);

/// <summary>
/// Fiche PC en HTML : un seul fichier autonome (styles intégrés, aucun lien externe), à partager ou imprimer.
/// Thème néon à l'écran, version claire à l'impression.
/// </summary>
public static class HtmlReport
{
    public const string FilePrefix = "fiche-pc_";

    /// <summary>Ligne lue par la page Rapports pour afficher le bilan.</summary>
    internal const string SummaryMarker = "<meta name=\"description\" content=\"";

    public static string Save(HtmlReportInput input, bool hidePersonal)
    {
        Directory.CreateDirectory(ReportFile.Folder);
        var path = Path.Combine(ReportFile.Folder, $"{FilePrefix}{input.At:yyyy-MM-dd_HHmmss}.html");
        File.WriteAllText(path, Build(input, hidePersonal), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public static string Build(HtmlReportInput input, bool hidePersonal)
    {
        var privacy = new Privacy(hidePersonal);
        var w = new HtmlWriter(privacy);
        var hw = input.Hardware;

        var toFix = input.Findings.Where(f => f.Severity >= Severity.Warning).OrderByDescending(f => f.Severity).ToList();
        var info = input.Findings.Where(f => f.Severity == Severity.Info).ToList();
        var ok = input.Findings.Where(f => f.Severity == Severity.Ok).ToList();
        var total = input.Findings.Count;
        var ratio = total == 0 ? 0 : (double)ok.Count / total;
        // Mêmes seuils que l'anneau de l'application : un tiers, deux tiers
        var level = ratio <= 1.0 / 3 ? "low" : ratio <= 2.0 / 3 ? "medium" : "high";
        var changes = input.Plan.Count(p => p.NeedsChange);

        var title = hidePersonal ? "Fiche PC" : $"Fiche PC — {Environment.MachineName}";
        var summary = $"Bilan : {ok.Count}/{total} conformes · {toFix.Count} à corriger · " +
                      $"{(changes == 0 ? "réglages à jour" : $"{changes} réglage{(changes > 1 ? "s" : "")} à changer")}";

        w.Raw("<!DOCTYPE html>\n<html lang=\"fr\">\n<head>\n<meta charset=\"utf-8\">\n");
        w.Raw("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        w.Raw($"{SummaryMarker}{Escape(summary)}\">\n");
        w.Raw($"<title>{w.Enc(title)}</title>\n<style>{Css}</style>\n</head>\n<body>\n<main>\n");

        // ---- En-tête ----
        w.Raw("<header class=\"hero\">\n<div class=\"hero-text\">\n");
        w.Raw($"<div class=\"tag\">ARIA · FICHE PC · {input.At:dd/MM/yyyy à HH:mm}</div>\n");
        w.Raw($"<h1>{w.Enc(hidePersonal ? $"{hw.Machine.Manufacturer} {hw.Machine.Model}" : Environment.MachineName)}</h1>\n");
        w.Raw($"<p class=\"muted\">{w.Enc($"{hw.Os.Name} {hw.Os.DisplayVersion} · {hw.Cpu.Name} · {hw.NvidiaGpu?.Name ?? hw.Gpus.FirstOrDefault()?.Name ?? "carte graphique inconnue"}")}</p>\n");
        w.Raw("<div class=\"counters\">");
        Counter(w, toFix.Count, "À CORRIGER", "orange");
        Counter(w, info.Count, "POUR INFO", "cyan");
        Counter(w, ok.Count, "CONFORMES", "teal");
        Counter(w, changes, "RÉGLAGES À CHANGER", changes == 0 ? "teal" : "orange");
        w.Raw("</div>\n</div>\n");
        Ring(w, ok.Count, total, ratio, level);
        w.Raw("</header>\n");

        // ---- Matériel ----
        w.Raw("<h2>Matériel</h2>\n<div class=\"grid\">\n");
        var sink = new HtmlInventorySink(w);
        InventoryPrinter.Write(hw, sink);
        sink.Close();
        w.Raw("</div>\n");

        // ---- Diagnostic ----
        w.Raw("<h2>Diagnostic</h2>\n");
        if (toFix.Count > 0)
        {
            w.Raw("<h3>À corriger</h3>\n");
            foreach (var f in toFix) Finding(w, f);
        }
        if (info.Count > 0)
        {
            w.Raw("<h3>Pour information</h3>\n");
            foreach (var f in info) Finding(w, f);
        }
        if (ok.Count > 0)
        {
            // Liste visible (pas repliée) : elle doit aussi apparaître à l'impression
            w.Raw($"<h3>Conformes ({ok.Count})</h3>\n<div class=\"card\"><ul class=\"ok-list\">\n");
            foreach (var f in ok) w.Raw($"<li><span class=\"dot teal\"></span>{w.Enc(f.Title)}</li>\n");
            w.Raw("</ul></div>\n");
        }
        if (input.Findings.Any(f => f.NeedsBios && f.Severity >= Severity.Info))
        {
            var brand = input.Bios.Brand;
            w.Raw($"<p class=\"note\">Accès au BIOS ({w.Enc(brand.Brand)}) : touche <b>{w.Enc(brand.SetupKey)}</b> au démarrage" +
                  (brand.AdvancedModeKey is { } key ? $", puis <b>{w.Enc(key)}</b> pour le mode avancé" : "") +
                  ". Modifier un réglage à la fois ; « Load Optimized Defaults » remet tout par défaut.</p>\n");
        }

        // ---- Réglages ----
        w.Raw("<h2>Réglages</h2>\n");
        foreach (var group in input.Plan.GroupBy(p => p.Setting.Category))
        {
            w.Raw($"<h3>{SettingCategoryNames.Of(group.Key)}</h3>\n");
            w.Raw("<div class=\"table-wrap\"><table class=\"settings\">\n<colgroup><col class=\"c-name\"><col class=\"c-value\"><col class=\"c-value\"><col class=\"c-state\"></colgroup>\n" +
                  "<thead><tr><th>Réglage</th><th>Actuel</th><th>Recommandé</th><th>État</th></tr></thead>\n<tbody>\n");
            foreach (var p in group) SettingRow(w, p);
            w.Raw("</tbody>\n</table></div>\n");
        }

        // ---- Historique ----
        var history = input.History.Where(r => r.Kind != ReportKind.PcSheet).Take(10).ToList();
        if (history.Count > 0)
        {
            w.Raw("<h2>Dernières actions</h2>\n<div class=\"table-wrap\"><table>\n<tbody>\n");
            foreach (var r in history)
                w.Raw($"<tr><td class=\"nowrap\">{r.At:dd/MM/yyyy HH:mm}</td><td><span class=\"badge {KindClass(r.Kind)}\">{KindLabel(r.Kind)}</span></td>" +
                      $"<td>{w.Enc(r.Summary.Length > 0 ? r.Summary : "—")}</td></tr>\n");
            w.Raw("</tbody>\n</table></div>\n");
        }

        w.Raw($"<footer>Généré par Aria {w.Enc(input.Version)} le {input.At:dd/MM/yyyy à HH:mm:ss}." +
              (hidePersonal ? " Informations personnelles masquées (nom du PC et de l'utilisateur)." : "") +
              " Diagnostic en lecture seule : rien n'est modifié sans validation.</footer>\n");
        w.Raw("</main>\n</body>\n</html>\n");
        return w.ToString();
    }

    // ---------------------------------------------------------------- Blocs

    private static void Counter(HtmlWriter w, int value, string label, string color) =>
        w.Raw($"<div class=\"counter\"><div class=\"value {color}\">{value}</div><div class=\"label\">{label}</div></div>");

    /// <summary>Anneau de score en SVG : cercle de rayon 52, longueur ≈ 326,7.</summary>
    private static void Ring(HtmlWriter w, int ok, int total, double ratio, string level)
    {
        const double circumference = 2 * Math.PI * 52;
        var dash = (ratio * circumference).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        var rest = circumference.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        w.Raw($"""
               <div class="ring {level}">
               <svg viewBox="0 0 120 120" aria-hidden="true">
               <circle class="track" cx="60" cy="60" r="52"/>
               <circle class="arc" cx="60" cy="60" r="52" stroke-dasharray="{dash} {rest}" transform="rotate(-90 60 60)"/>
               </svg>
               <div class="ring-text"><span class="score">{ok}</span><span class="total">/{total}</span><div class="label">CONFORMES</div></div>
               </div>

               """);
    }

    private static void Finding(HtmlWriter w, DiagnosticFinding f)
    {
        // La gravité colore la bordure et le titre ; le texte reste lisible en blanc
        var css = f.Severity switch { Severity.Critical => "sev-critical", Severity.Warning => "sev-warning", _ => "sev-info" };
        w.Raw($"<article class=\"finding {css}\">\n<h4>{w.Enc(f.Title)}</h4>\n<p>{w.Enc(f.Detail)}</p>\n");
        if (f.Advice is { Length: > 0 } advice) w.Raw($"<p class=\"advice\">→ {w.Enc(advice)}</p>\n");
        if (f.Steps is { Count: > 0 } steps)
        {
            w.Raw("<ol>\n");
            foreach (var step in steps) w.Raw($"<li>{w.Enc(step)}</li>\n");
            w.Raw("</ol>\n");
        }
        w.Raw("</article>\n");
    }

    private static void SettingRow(HtmlWriter w, PlanItem p)
    {
        var current = p.Current?.Display ?? "—";
        var (target, state, css) =
            p.ReadError is not null ? ("—", "Illisible", "red")
            : p.NotApplicableReason is not null ? ("—", $"Non applicable : {p.NotApplicableReason}", "muted")
            : p.NeedsChange ? (p.Target?.Value.Display ?? "—", "À changer", "orange")
            : (p.Target?.Value.Display ?? current, "Correct", "teal");
        w.Raw($"<tr><td>{w.Enc(p.Setting.DisplayName)}</td><td class=\"mono\">{w.Enc(current)}</td>" +
              $"<td class=\"mono\">{w.Enc(target)}</td><td class=\"{css}\">{w.Enc(state)}</td></tr>\n");
    }

    internal static string KindLabel(ReportKind kind) => kind switch
    {
        ReportKind.Settings => "RÉGLAGES",
        ReportKind.Restore => "RESTAURATION",
        ReportKind.Updates => "MISES À JOUR",
        ReportKind.PcSheet => "FICHE PC",
        ReportKind.Cleanup => "NETTOYAGE",
        _ => "SIMULATION"
    };

    private static string KindClass(ReportKind kind) => kind switch
    {
        ReportKind.Settings => "orange",
        ReportKind.Restore or ReportKind.Cleanup => "teal",
        _ => "cyan"
    };

    // ---------------------------------------------------------------- Outils

    /// <summary>Échappe uniquement les caractères dangereux : les accents restent lisibles dans le fichier.</summary>
    internal static string Escape(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

    /// <summary>Écrit l'inventaire sous forme de cartes : une par section.</summary>
    private sealed class HtmlInventorySink(HtmlWriter w) : IInventorySink
    {
        private bool _open;
        private bool _inWarnings;

        public void Section(string title)
        {
            Close();
            w.Raw($"<section class=\"card\">\n<h4>{w.Enc(title)}</h4>\n<dl>\n");
            _open = true;
        }

        public void Line(string label, string value)
        {
            var sub = label.StartsWith("  ", StringComparison.Ordinal);
            w.Raw($"<dt{(sub ? " class=\"sub\"" : "")}>{w.Enc(label.Trim())}</dt><dd>{w.Enc(value)}</dd>\n");
        }

        public void Warning(string text)
        {
            if (!_inWarnings)
            {
                w.Raw("</dl>\n<ul class=\"warnings\">\n");
                _inWarnings = true;
            }
            w.Raw($"<li>{w.Enc(text)}</li>\n");
        }

        public void Close()
        {
            if (!_open) return;
            w.Raw(_inWarnings ? "</ul>\n</section>\n" : "</dl>\n</section>\n");
            _open = false;
            _inWarnings = false;
        }
    }

    private sealed class HtmlWriter(Privacy privacy)
    {
        private readonly StringBuilder _html = new();

        public void Raw(string html) => _html.Append(html);

        /// <summary>Texte échappé, après masquage éventuel des informations personnelles.</summary>
        public string Enc(string text) => Escape(privacy.Clean(text));

        public override string ToString() => _html.ToString();
    }

    /// <summary>Masque le nom du PC et celui de l'utilisateur (y compris dans les chemins).</summary>
    internal sealed class Privacy
    {
        private readonly List<(Regex Pattern, string Replacement)> _rules = [];

        public Privacy(bool enabled, string? machineName = null, string? userName = null, string? profileFolder = null)
        {
            if (!enabled) return;
            machineName ??= Environment.MachineName;
            userName ??= Environment.UserName;
            profileFolder ??= Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/'));

            Add(machineName, "[PC]");
            foreach (var name in new[] { profileFolder, userName }.Distinct(StringComparer.OrdinalIgnoreCase))
                Add(name, "[utilisateur]");
        }

        private void Add(string? value, string replacement)
        {
            // Trop court, on masquerait des mots ordinaires
            if (string.IsNullOrWhiteSpace(value) || value.Length < 3) return;
            _rules.Add((new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(value)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase), replacement));
        }

        public string Clean(string text) =>
            _rules.Aggregate(text, (current, rule) => rule.Pattern.Replace(current, rule.Replacement));
    }

    // ---------------------------------------------------------------- Styles

    private const string Css = """

        :root { --bg:#05070d; --panel:#0b111e; --line:#19e3ff33; --text:#e6f1fa; --muted:#8da2b8;
                --orange:#ff8a1f; --cyan:#19e3ff; --teal:#2ef2b8; --red:#ff3d5a; --green:#3cff7a; }
        * { box-sizing:border-box; }
        body { margin:0; background:var(--bg); color:var(--text);
               font:15px/1.5 Bahnschrift, "Segoe UI", system-ui, sans-serif;
               background-image:radial-gradient(circle at 0 0, #ff8a1f22, transparent 40%), radial-gradient(circle at 100% 100%, #19e3ff1a, transparent 45%); }
        main { max-width:1100px; margin:0 auto; padding:32px 16px 48px; }
        h1 { margin:6px 0 2px; font-size:38px; font-weight:600; color:var(--cyan); text-shadow:0 0 18px #ff8a1f8c; overflow-wrap:anywhere; }
        h2 { margin:36px 0 14px; font-size:16px; letter-spacing:.08em; text-transform:uppercase; color:var(--cyan);
             display:flex; align-items:center; gap:14px; }
        h2::after { content:""; flex:1; height:1px; background:linear-gradient(90deg, var(--cyan), transparent); }
        h3 { margin:20px 0 10px; font-size:13px; letter-spacing:.06em; text-transform:uppercase; color:var(--muted); }
        h4 { margin:0 0 8px; font-size:16px; }
        .muted { color:var(--muted); }
        .tag, .label { font-size:11px; letter-spacing:.08em; font-weight:600; color:var(--muted); }
        .tag { color:var(--teal); }
        .hero { display:flex; gap:24px; align-items:center; justify-content:space-between; flex-wrap:wrap;
                padding:24px; border:1px solid var(--line); border-radius:12px; background:linear-gradient(135deg, #ff8a1f1a, #0b111ecc 45%, #19e3ff14); }
        .hero-text { flex:1 1 360px; min-width:0; }
        .counters { display:flex; flex-wrap:wrap; gap:28px; margin-top:18px; }
        .counter .value { font-size:30px; font-weight:600; line-height:1.1; }
        .ring { position:relative; width:170px; height:170px; flex:0 0 auto; margin:0 auto; }
        .ring svg { width:100%; height:100%; }
        .ring circle { fill:none; stroke-width:10; }
        .ring .track { stroke:#19e3ff1f; }
        .ring .arc { stroke-linecap:round; filter:drop-shadow(0 0 6px currentColor); }
        .ring.low { color:var(--red); } .ring.medium { color:var(--orange); } .ring.high { color:var(--green); }
        .ring .arc { stroke:currentColor; }
        .ring-text { position:absolute; inset:0; display:flex; flex-direction:column; align-items:center; justify-content:center; }
        .ring-text .score { font-size:40px; font-weight:600; line-height:1; color:currentColor; }
        .ring-text .total { font-size:20px; color:var(--cyan); }
        .ring-text .label { margin-top:4px; }
        .grid { display:grid; grid-template-columns:repeat(auto-fit, minmax(300px, 1fr)); gap:12px; }
        .card, .finding { background:#0b111ecc; border:1px solid var(--line); border-radius:10px; padding:16px 18px; }
        .card h4 { color:var(--cyan); font-size:13px; letter-spacing:.06em; }
        dl { display:grid; grid-template-columns:max-content 1fr; gap:4px 14px; margin:0; }
        dt { color:var(--muted); } dt.sub { padding-left:12px; }
        dd { margin:0; overflow-wrap:anywhere; }
        .warnings { margin:8px 0 0; padding-left:18px; color:var(--orange); }
        .finding { margin-bottom:10px; border-left:4px solid var(--cyan); }
        .finding.sev-info h4 { color:var(--cyan); }
        .finding.sev-warning { border-left-color:var(--orange); } .finding.sev-warning h4 { color:var(--orange); }
        .finding.sev-critical { border-left-color:var(--red); } .finding.sev-critical h4 { color:var(--red); }
        .finding p { margin:4px 0; }
        .advice { color:var(--teal); }
        .finding ol { margin:6px 0 0; padding-left:22px; color:var(--muted); }
        .ok-list { list-style:none; margin:0; padding:0; columns:2 260px; }
        .ok-list li { margin:3px 0; break-inside:avoid; }
        .dot { display:inline-block; width:8px; height:8px; border-radius:50%; margin-right:8px; background:var(--teal); box-shadow:0 0 6px var(--teal); }
        .note { color:var(--muted); border:1px dashed var(--line); border-radius:10px; padding:10px 14px; }
        .table-wrap { overflow-x:auto; border:1px solid var(--line); border-radius:10px; background:#0b111ecc; }
        table { width:100%; border-collapse:collapse; }
        /* Mêmes colonnes pour Windows et NVIDIA ; sur téléphone, le tableau défile dans son cadre */
        table.settings { table-layout:fixed; min-width:600px; }
        .c-name { width:34%; } .c-value { width:22%; } .c-state { width:22%; }
        th, td { text-align:left; padding:8px 12px; border-bottom:1px solid #19e3ff1a; vertical-align:top; }
        th { font-size:11px; letter-spacing:.08em; color:var(--muted); text-transform:uppercase; }
        tr:last-child td { border-bottom:none; }
        .mono { font-family:Consolas, "Cascadia Mono", monospace; }
        .nowrap { white-space:nowrap; }
        .orange { color:var(--orange); } .cyan { color:var(--cyan); } .teal { color:var(--teal); } .red { color:var(--red); }
        .badge { display:inline-block; font-size:10px; font-weight:600; letter-spacing:.06em; padding:2px 8px; border-radius:9px; border:1px solid currentColor; white-space:nowrap; }
        footer { margin-top:40px; font-size:12px; color:var(--muted); text-align:center; }
        @media print {
          :root { --bg:#fff; --panel:#fff; --line:#ccd; --text:#111; --muted:#555; --cyan:#0a6d80; --teal:#0b7a5c; --orange:#b85c00; }
          body { background:#fff; font-size:12px; }
          h1 { text-shadow:none; }
          .hero, .card, .finding, .table-wrap { background:#fff; }
          .ring .arc, .dot { filter:none; box-shadow:none; }
          .card, .finding, tr { break-inside:avoid; }
        }

        """;
}
