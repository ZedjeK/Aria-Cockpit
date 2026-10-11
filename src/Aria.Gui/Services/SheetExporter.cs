using System.Reflection;
using Aria.Core.Reporting;

namespace Aria.Gui.Services;

/// <summary>Export de la fiche PC en HTML, partagé par les pages Mon PC et Rapports.</summary>
public static class SheetExporter
{
    /// <summary>Crée la fiche à partir de la dernière analyse, l'ouvre dans le navigateur. Renvoie le chemin, ou null en cas d'échec (déjà signalé).</summary>
    public static async Task<string?> ExportAsync(AppState state)
    {
        if (state.Analysis is null) await state.AnalyzeAsync();
        if (state.Analysis is not { } analysis)
        {
            await ShowError(state.Error ?? "L'analyse du PC n'est pas disponible.");
            return null;
        }

        try
        {
            var hide = state.HidePersonalInfo;
            var input = new HtmlReportInput(DateTime.Now, Version, analysis.Hardware, analysis.Bios, analysis.Findings,
                analysis.Plan, ReportIndex.List());
            var path = await Task.Run(() => HtmlReport.Save(input, hide));
            // Navigateur lancé sans les droits administrateur d'Aria
            Shell.OpenUnelevated(path);
            return path;
        }
        catch (Exception e)
        {
            await ShowError($"{e.Message}\n\nDétails : {ErrorLog.Write(e)}");
            return null;
        }
    }

    private static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    private static Task<bool> ShowError(string message) =>
        DialogService.Current.ConfirmAsync(new DialogRequest(
            Title: "Export impossible",
            ConfirmText: "OK",
            Message: message,
            ShowCancel: false));
}
