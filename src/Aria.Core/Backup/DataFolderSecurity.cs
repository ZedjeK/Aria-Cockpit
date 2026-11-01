using System.Security.AccessControl;
using System.Security.Principal;

namespace Aria.Core.Backup;

/// <summary>
/// Droits du dossier des données. Aria Cockpit tourne en administrateur et fait confiance à ces fichiers : la page
/// Restauration réécrit le registre d'après les sauvegardes, la tâche de démarrage applique gpu-profile.json. Dans le
/// profil de l'utilisateur, un programme lancé sans droits pourrait les modifier ; seuls les administrateurs et le
/// système y écrivent donc, l'utilisateur garde la lecture (tout s'ouvre dans l'Explorateur).
/// </summary>
public static class DataFolderSecurity
{
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// Crée le dossier s'il manque, pose ses droits, puis aligne ceux des fichiers repris d'un ancien dossier.
    /// Sans effet bloquant : si Windows refuse (Aria lancé sans droits administrateur), le dossier reste tel quel.
    /// </summary>
    public static bool Protect(string root)
    {
        try
        {
            var folder = Directory.CreateDirectory(root);
            folder.SetAccessControl(RootSecurity(WindowsIdentity.GetCurrent().User));

            // Fichiers et sous-dossiers : plus de droits propres, ils héritent de ceux du dossier
            foreach (var entry in folder.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (entry is DirectoryInfo directory)
                {
                    var security = new DirectorySecurity();
                    security.SetOwner(Administrators);
                    security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    directory.SetAccessControl(security);
                }
                else if (entry is FileInfo file)
                {
                    var security = new FileSecurity();
                    security.SetOwner(Administrators);
                    security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    file.SetAccessControl(security);
                }
            }
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PrivilegeNotHeldException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Droits du dossier : système et administrateurs en contrôle total, l'utilisateur en lecture, rien d'hérité du profil.
    /// Le propriétaire est le groupe Administrateurs : un propriétaire peut toujours changer les droits.
    /// </summary>
    internal static DirectorySecurity RootSecurity(SecurityIdentifier? user)
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        if (user is not null && user != System && user != Administrators)
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
