namespace Aria.Core.Install;

/// <summary>
/// Raccourci d'Aria Cockpit sur le Bureau de l'utilisateur (jamais sur le Bureau public). Créé seulement sur sa
/// demande, depuis la fenêtre d'installation ou la page Paramètres.
/// </summary>
public static class DesktopShortcut
{
    public const string FileName = "Aria Cockpit.lnk";

    /// <summary>Bureau de l'utilisateur, redirigé ou non (OneDrive).</summary>
    public static string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string DefaultPath => Path.Combine(DesktopFolder, FileName);

    /// <summary>Cible du raccourci ; null s'il n'existe pas ou n'est pas lisible.</summary>
    public static string? TargetOf(string shortcut)
    {
        try
        {
            if (!File.Exists(shortcut)) return null;
            dynamic link = Open(shortcut);
            var target = (string)link.TargetPath;
            return target.Length == 0 ? null : target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                                      or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    /// <summary>
    /// Crée le raccourci, ou en remplace la cible s'il existe déjà. Lève <see cref="IOException"/> si Windows refuse
    /// (Bureau protégé, accès contrôlé aux dossiers).
    /// </summary>
    public static void Create(string shortcut, string target)
    {
        try
        {
            dynamic link = Open(shortcut);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
            link.IconLocation = target + ",0";
            link.Description = "Aria Cockpit";
            link.Save();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException
                                      or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new IOException($"Le raccourci n'a pas pu être créé : {e.Message}", e);
        }
    }

    /// <summary>Supprime le raccourci s'il pointe vers cet exe : un raccourci du même nom vers autre chose n'est pas le nôtre.</summary>
    public static bool Remove(string shortcut, string expectedTarget)
    {
        if (TargetOf(shortcut) is not { } target || !string.Equals(Path.GetFullPath(target), Path.GetFullPath(expectedTarget), StringComparison.OrdinalIgnoreCase))
            return false;
        File.Delete(shortcut);
        return true;
    }

    private static object Open(string shortcut)
    {
        var shell = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell est indisponible.");
        dynamic wsh = Activator.CreateInstance(shell)!;
        return wsh.CreateShortcut(shortcut);
    }
}
