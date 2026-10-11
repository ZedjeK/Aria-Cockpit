using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Aria.Core.Updates;

/// <summary>
/// Appels « tardifs » (IDispatch) vers les objets COM de Windows Update, sans bibliothèque d'interopérabilité.
/// </summary>
internal static class Com
{
    public static object Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
                   ?? throw new InvalidOperationException($"Composant Windows introuvable : {progId}");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException($"Composant Windows inutilisable : {progId}");
    }

    public static object? Get(object target, string name, params object[] args) =>
        Invoke(target, name, BindingFlags.GetProperty, args);

    public static object? Call(object target, string name, params object[] args) =>
        Invoke(target, name, BindingFlags.InvokeMethod, args);

    public static string Str(object target, string name) => Get(target, name)?.ToString() ?? "";

    public static void Set(object target, string name, object value) =>
        Invoke(target, name, BindingFlags.SetProperty, [value]);

    /// <summary>Affecte un objet COM à une propriété : « put », puis « putref » si le composant l'exige.</summary>
    public static void SetObject(object target, string name, object value)
    {
        try
        {
            Invoke(target, name, BindingFlags.PutDispProperty, [value]);
        }
        catch (Exception)
        {
            Invoke(target, name, BindingFlags.PutRefDispProperty, [value]);
        }
    }

    /// <summary>Éléments d'une collection COM (propriétés Count et Item).</summary>
    public static IEnumerable<object> Items(object? collection)
    {
        if (collection is null) yield break;
        var count = Convert.ToInt32(Get(collection, "Count"));
        for (var i = 0; i < count; i++)
            if (Get(collection, "Item", i) is { } item) yield return item;
    }

    private static object? Invoke(object target, string name, BindingFlags flags, object[] args)
    {
        try
        {
            return target.GetType().InvokeMember(name, flags, null, target, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw; // jamais atteint
        }
    }
}
