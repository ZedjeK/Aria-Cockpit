namespace Aria.Core.Models;

public enum RunMode
{
    /// <summary>Diagnostic, état actuel, liste à cocher, puis application des seuls choix validés.</summary>
    Interactive,

    /// <summary>Tout est affiché, rien n'est modifié.</summary>
    Simulate,

    /// <summary>Retour aux valeurs d'une sauvegarde, avec la même liste à cocher.</summary>
    Restore
}
