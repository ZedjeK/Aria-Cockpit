using System.Runtime.InteropServices;
using System.Security;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Aria.Core.Startup;

/// <summary>
/// Tâches de démarrage des applications empaquetées (Microsoft Store, MSIX), que le Gestionnaire des tâches liste à côté
/// des programmes classiques. L'application déclare sa tâche dans son manifeste ; Windows note son état, par utilisateur,
/// dans le registre, avec les valeurs de l'énumération StartupTaskState que Microsoft documente. L'emplacement dans le
/// registre, lui, n'est pas documenté, et aucune API ne règle la tâche d'une autre application : on lit cette clé, et on
/// y écrit ce que les Paramètres de Windows y écrivent (relevé sur Windows 11, build 26300, le 15 novembre 2026).
/// <para>
/// L'état noté ne suffit pas. D'après la documentation de StartupTask, une tâche ne prend effet qu'une fois l'application
/// ouverte au moins une fois, ou activée par l'utilisateur dans Paramètres → Démarrage : une application dont le
/// manifeste active la tâche est notée « activée » dès son installation, mais Windows l'affiche « Désactivé » et ne la
/// lance pas tant qu'elle n'a jamais été ouverte. Windows ne dit pas où il garde cette trace ; on lit celle qu'on a vue
/// apparaître à la première ouverture, la clé d'activité de l'application (HAM\AUI), dans la même famille.
/// </para>
/// </summary>
internal static class StoreStartupTasks
{
    private const string AppModel = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel";
    internal const string DefaultStateKey = AppModel + @"\SystemAppData";
    private const string PackagesKey = AppModel + @"\Repository\Packages";
    private const string StateValue = "State";
    private const string UserEnabledOnceValue = "UserEnabledStartupOnce";
    private const string LastDisabledValue = "LastDisabledTime";
    private const string ActivityKey = @"HAM\AUI";
    private const string ResourcePrefix = "ms-resource:";
    private const long MaxManifestBytes = 4 * 1024 * 1024;

    /// <summary>Valeurs de StartupTaskState (Windows.ApplicationModel).</summary>
    internal const int Disabled = 0, DisabledByUser = 1, Enabled = 2, DisabledByPolicy = 3, EnabledByPolicy = 4;

    /// <summary>Sépare la famille du paquet et la tâche dans le nom d'une entrée : aucun des deux ne peut le contenir.</summary>
    internal const char Separator = '!';

    /// <summary>Ce qu'un manifeste dit d'une tâche de démarrage (textes bruts, parfois des références « ms-resource: »).</summary>
    internal sealed record TaskInfo(string PackageName, string DisplayName, string PackageDisplayName, string Publisher, string? Executable);

    internal static string EntryName(string family, string task) => $"{family}{Separator}{task}";

    /// <summary>Famille et tâche d'un nom d'entrée ; null s'il n'a pas cette forme.</summary>
    internal static (string Family, string Task)? Split(string name)
    {
        var at = name.IndexOf(Separator);
        if (at <= 0 || at == name.Length - 1) return null;
        var (family, task) = (name[..at], name[(at + 1)..]);
        // Un nom de clé du registre ne contient jamais « \ » : on refuse tout ce qui mènerait ailleurs
        return family.Contains('\\') || task.Contains('\\') ? null : (family, task);
    }

    /// <summary>Activée, par l'utilisateur ou par une stratégie.</summary>
    internal static bool IsEnabledState(int state) => state is Enabled or EnabledByPolicy;

    /// <summary>
    /// État que l'utilisateur peut changer. Une tâche imposée ou interdite par une stratégie n'est pas listée : la
    /// désactiver ici n'aurait aucun effet.
    /// </summary>
    internal static bool IsUserControlled(int state) => state is Disabled or DisabledByUser or Enabled;

    /// <summary>
    /// La tâche se lance vraiment à l'ouverture de session, et Windows l'affiche « Activé » : notée activée, et
    /// l'application déjà ouverte une fois ou la tâche activée par l'utilisateur dans les Paramètres. Ce second cas se
    /// déduit du nom de la valeur « UserEnabledStartupOnce » et de la documentation : il n'a pas été observé.
    /// </summary>
    internal static bool TakesEffect(int state, bool opened, bool userEnabledOnce) =>
        state == EnabledByPolicy || (state == Enabled && (opened || userEnabledOnce));

    /// <summary>L'application a déjà été ouverte : Windows a créé sa clé d'activité, avec au moins une application dedans.</summary>
    private static bool HasBeenOpened(RegistryKey familyKey)
    {
        using var activity = OpenOrNull(familyKey, ActivityKey);
        return activity is { SubKeyCount: > 0 };
    }

    private static bool UserEnabledOnce(RegistryKey taskKey) => taskKey.GetValue(UserEnabledOnceValue) is int once && once != 0;

    /// <summary>
    /// Famille d'un paquet d'après son nom complet « Nom_Version_Architecture_Ressource_Éditeur » : « Nom_Éditeur ».
    /// null si le nom n'a pas ces cinq parties.
    /// </summary>
    internal static string? FamilyOf(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        return parts.Length == 5 && parts[0].Length > 0 && parts[4].Length > 0 ? $"{parts[0]}_{parts[4]}" : null;
    }

    /// <summary>Tâches de démarrage des applications empaquetées de l'utilisateur, activées ou non.</summary>
    public static IReadOnlyList<StartupEntry> Scan()
    {
        var found = new List<(string Family, string Task, bool Enabled)>();
        using (var root = Registry.CurrentUser.OpenSubKey(DefaultStateKey))
        {
            if (root is null) return [];
            foreach (var family in root.GetSubKeyNames())
            {
                using var familyKey = OpenOrNull(root, family);
                if (familyKey is null) continue;
                foreach (var task in familyKey.GetSubKeyNames())
                {
                    using var taskKey = OpenOrNull(familyKey, task);
                    if (taskKey?.GetValue(StateValue) is int state && IsUserControlled(state))
                        found.Add((family, task, TakesEffect(state, HasBeenOpened(familyKey), UserEnabledOnce(taskKey))));
                }
            }
        }
        if (found.Count == 0) return [];

        var packages = InstalledPackages(found.Select(f => f.Family).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var entries = new List<StartupEntry>();
        foreach (var (family, task, enabled) in found)
        {
            // Listée seulement si le manifeste d'un paquet installé déclare bien cette tâche : une autre clé qui
            // porterait une valeur « State » n'est jamais prise pour une tâche de démarrage.
            foreach (var (fullName, folder) in packages[family])
            {
                if (ReadManifest(folder) is not { } manifest || Declared(manifest, task) is not { } info) continue;
                var display = Resolve(fullName, info.PackageName, info.DisplayName)
                              ?? Resolve(fullName, info.PackageName, info.PackageDisplayName)
                              ?? (info.PackageName.Length > 0 ? info.PackageName : family);
                entries.Add(new StartupEntry(StartupSource.StoreTask, EntryName(family, task), ExecutableIn(folder, info.Executable),
                    display, enabled, Resolve(fullName, info.PackageName, info.Publisher)));
                break;
            }
        }
        return entries;
    }

    /// <summary>La tâche se lance à l'ouverture de session, comme Windows l'affiche ; désactivée si elle a disparu.</summary>
    public static bool IsEnabled(string name, string stateKey = DefaultStateKey)
    {
        if (Split(name) is not (var family, var task)) return false;
        using var familyKey = Registry.CurrentUser.OpenSubKey($@"{stateKey}\{family}");
        if (familyKey is null) return false;
        using var taskKey = OpenOrNull(familyKey, task);
        return taskKey?.GetValue(StateValue) is int state && TakesEffect(state, HasBeenOpened(familyKey), UserEnabledOnce(taskKey));
    }

    /// <summary>
    /// Active ou désactive une tâche comme le font les Paramètres de Windows. Désactiver : la date (secondes Unix, QWORD)
    /// puis l'état « désactivée par l'utilisateur ». Activer : l'état « activée ». Rien n'est écrit si l'état noté est déjà
    /// le bon : une tâche que son application n'a jamais activée ne devient pas « désactivée par l'utilisateur ».
    /// Aucune clé n'est créée.
    /// </summary>
    public static void SetEnabled(string name, bool enabled, string stateKey = DefaultStateKey)
    {
        if (Split(name) is not (var family, var task)) throw new FormatException($"Tâche de démarrage inattendue : {name}");
        using var key = Registry.CurrentUser.OpenSubKey($@"{stateKey}\{family}\{task}", writable: true);
        if (key?.GetValue(StateValue) is not int state)
            throw new InvalidOperationException("Tâche de démarrage introuvable : l'application n'est plus installée.");
        if (!IsUserControlled(state))
            throw new InvalidOperationException("Cette tâche de démarrage est imposée par une stratégie : elle ne se change pas ici.");
        if (IsEnabledState(state) == enabled) return;
        if (!enabled) key.SetValue(LastDisabledValue, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RegistryValueKind.QWord);
        key.SetValue(StateValue, enabled ? Enabled : DisabledByUser, RegistryValueKind.DWord);
    }

    /// <summary>Ce que le manifeste déclare pour cette tâche ; null s'il ne la déclare pas.</summary>
    internal static TaskInfo? Declared(XDocument manifest, string taskId)
    {
        if (manifest.Root is not { } package) return null;
        foreach (var extension in package.Descendants().Where(e => e.Name.LocalName == "Extension"
                     && string.Equals((string?)e.Attribute("Category"), "windows.startupTask", StringComparison.OrdinalIgnoreCase)))
        {
            var task = extension.Elements().FirstOrDefault(e => e.Name.LocalName == "StartupTask"
                           && string.Equals((string?)e.Attribute("TaskId"), taskId, StringComparison.OrdinalIgnoreCase));
            if (task is null) continue;

            var properties = package.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
            string Property(string name) =>
                (properties?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "").Trim();
            var identity = package.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity");
            var application = extension.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Application");
            return new TaskInfo(
                ((string?)identity?.Attribute("Name") ?? "").Trim(),
                ((string?)task.Attribute("DisplayName") ?? "").Trim(),
                Property("DisplayName"),
                Property("PublisherDisplayName"),
                (string?)extension.Attribute("Executable") ?? (string?)application?.Attribute("Executable"));
        }
        return null;
    }

    /// <summary>
    /// Références à essayer, dans l'ordre, pour lire un texte « ms-resource: » d'un manifeste dans les ressources du paquet.
    /// Vide si la valeur n'en est pas une.
    /// </summary>
    internal static IReadOnlyList<string> ResourceReferences(string packageFullName, string packageName, string value)
    {
        if (!value.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase)) return [];
        var key = value[ResourcePrefix.Length..];
        if (key.Length == 0) return [];
        string Reference(string path) => $"@{{{packageFullName}?ms-resource:{path}}}";
        if (key.StartsWith("//", StringComparison.Ordinal)) return [Reference(key)];                    // adresse complète
        if (key.StartsWith('/')) return [Reference($"//{packageName}{key}")];                           // depuis la racine des ressources
        return [Reference($"//{packageName}/Resources/{key}"), Reference($"//{packageName}/{key}")];   // nom seul
    }

    /// <summary>Texte affichable : la valeur elle-même, ou le texte de la ressource qu'elle désigne. null si rien de lisible.</summary>
    private static string? Resolve(string packageFullName, string packageName, string value)
    {
        if (value.Length == 0) return null;
        if (!value.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase)) return value;
        foreach (var reference in ResourceReferences(packageFullName, packageName, value))
        {
            var buffer = new char[512];
            if (SHLoadIndirectString(reference, buffer, (uint)buffer.Length, IntPtr.Zero) != 0) continue;
            var end = Array.IndexOf(buffer, '\0');
            var text = new string(buffer, 0, end < 0 ? buffer.Length : end).Trim();
            if (text.Length > 0) return text;
        }
        return null;
    }

    /// <summary>Paquets installés pour l'utilisateur, parmi les familles demandées : nom complet et dossier, le paquet principal d'abord.</summary>
    private static ILookup<string, (string FullName, string Folder)> InstalledPackages(HashSet<string> families)
    {
        var installed = new List<(string Family, string FullName, string Folder)>();
        using (var packages = Registry.CurrentUser.OpenSubKey(PackagesKey))
        {
            foreach (var fullName in packages?.GetSubKeyNames() ?? [])
            {
                if (FamilyOf(fullName) is not { } family || !families.Contains(family)) continue;
                using var key = OpenOrNull(packages!, fullName);
                if (key?.GetValue("PackageRootFolder") is string { Length: > 0 } folder) installed.Add((family, fullName, folder));
            }
        }
        // Le paquet principal n'a pas de partie « ressource » (« __ » dans son nom) : c'est lui qui porte le manifeste utile
        return installed.OrderByDescending(p => p.FullName.Contains("__", StringComparison.Ordinal))
            .ToLookup(p => p.Family, p => (p.FullName, p.Folder), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Manifeste d'un paquet. Lu comme une donnée non fiable (un paquet peut être enregistré depuis un dossier de
    /// l'utilisateur) : taille bornée, aucune DTD, aucune ressource externe.
    /// </summary>
    private static XDocument? ReadManifest(string packageFolder)
    {
        try
        {
            var file = new FileInfo(Path.Combine(packageFolder, "AppxManifest.xml"));
            if (!file.Exists || file.Length > MaxManifestBytes) return null;
            using var stream = file.OpenRead();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException or ArgumentException
                                      or NotSupportedException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>Chemin de l'exécutable de la tâche, s'il reste dans le dossier du paquet ; vide sinon.</summary>
    private static string ExecutableIn(string packageFolder, string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return "";
        try
        {
            var root = Path.GetFullPath(packageFolder);
            var full = Path.GetFullPath(Path.Combine(root, executable));
            return full.StartsWith(root.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase) ? full : "";
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    private static RegistryKey? OpenOrNull(RegistryKey parent, string name)
    {
        try
        {
            return parent.OpenSubKey(name);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;   // une clé illisible n'empêche pas de lire les autres
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string source, [Out] char[] output, uint capacity, IntPtr reserved);
}
