using CommunityToolkit.Mvvm.ComponentModel;
using Aria.Gui.Services;

namespace Aria.Gui;

/// <summary>État partagé par toutes les pages : le résultat de la dernière analyse.</summary>
public sealed partial class AppState : ObservableObject
{
    [ObservableProperty]
    private AnalysisResult? analysis;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string busyText = "";

    [ObservableProperty]
    private string? error;

    /// <summary>Fiche PC : masquer le nom du PC et de l'utilisateur (coché par défaut, la fiche est faite pour être partagée).</summary>
    [ObservableProperty]
    private bool hidePersonalInfo = true;

    /// <summary>Analyse complète (lecture seule) en arrière-plan : la fenêtre reste fluide.</summary>
    public async Task AnalyzeAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        BusyText = "ANALYSE DU MATÉRIEL…";
        Error = null;
        try
        {
            Analysis = await Task.Run(AnalysisService.Run);
        }
        catch (Exception e)
        {
            Error = $"Analyse impossible : {e.Message} (détails dans {ErrorLog.Write(e)})";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
