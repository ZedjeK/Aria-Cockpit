using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aria.Core.Nvidia;

/// <summary>Overclock de NVIDIA App, tel qu'Aria le lit dans sa base de réglages (lecture seule).</summary>
/// <param name="Installed">Base de réglages de NVIDIA App présente.</param>
/// <param name="AutoOc">Overclock automatique activé (« Réglage automatique »).</param>
/// <param name="BackgroundScan">Scan d'overclock en arrière-plan activé.</param>
/// <param name="SavedCurveMHz">Gain moyen de la courbe trouvée par le scan et enregistrée pour être réappliquée ; null s'il n'y en a pas.</param>
/// <param name="Unreadable">Raison si la base n'a pas pu être lue (format changé, accès refusé) ; null sinon.</param>
public sealed record NvidiaAppTuningState(bool Installed, bool AutoOc, bool BackgroundScan, int? SavedCurveMHz, string? Unreadable)
{
    public static NvidiaAppTuningState NotInstalled { get; } = new(false, false, false, null, null);

    /// <summary>Un overclock de NVIDIA App peut se superposer aux réglages d'Aria.</summary>
    public bool Active => AutoOc || BackgroundScan || SavedCurveMHz is not null;

    /// <summary>« Overclock automatique activé, courbe enregistrée par le scan (+95 MHz en moyenne) ».</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (AutoOc) parts.Add("overclock automatique activé");
        if (BackgroundScan) parts.Add("scan en arrière-plan activé");
        if (SavedCurveMHz is { } mhz) parts.Add($"courbe enregistrée par le scan ({mhz:+0;-0;0} MHz en moyenne)");
        if (parts.Count == 0) return "Aucun overclock de NVIDIA App";
        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}

/// <summary>
/// Overclock de NVIDIA App : NVIDIA n'en publie aucune interface, Aria se contente de lire sa base de réglages
/// (nvtopps.db3, SQLite) pour prévenir avant de mesurer. Rien n'y est jamais écrit : c'est l'utilisateur qui désactive.
/// </summary>
public static class NvidiaAppTuning
{
    public static string DefaultDatabase =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NVIDIA Corporation", "nvtopps", "nvtopps.db3");

    /// <summary>NVIDIA App.exe s'il est installé à son emplacement habituel ; null sinon.</summary>
    public static string? AppPath
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVIDIA App", "CEF", "NVIDIA App.exe");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// Lit l'état de l'overclock. La base est copiée dans le dossier temporaire puis ouverte en lecture seule :
    /// le fichier de NVIDIA n'est jamais touché et son service n'est pas gêné.
    /// </summary>
    /// <param name="database">Autre base à lire (tests) ; par défaut celle de NVIDIA App.</param>
    public static NvidiaAppTuningState Read(string? database = null)
    {
        database ??= DefaultDatabase;
        if (!OperatingSystem.IsWindows() || !File.Exists(database)) return NvidiaAppTuningState.NotInstalled;

        // Nom unique : l'assistant et le diagnostic peuvent lire en même temps
        var copy = Path.Combine(Path.GetTempPath(), $"aria-nvtopps-{Guid.NewGuid():N}.db3");
        try
        {
            File.Copy(database, copy, overwrite: true);
            using var db = new Sqlite(copy);
            // Tables vides : max() rend NULL, compté comme désactivé
            var settings = db.Row("SELECT max(backgroundScanEnabled), max(grdOcEnabled) FROM ocScannerSettings");
            var curve = db.Row("SELECT averageOverlockMhz FROM ocScannerResults WHERE autoPersist = 1 AND stable = 1 ORDER BY time DESC LIMIT 1");
            return new(true, settings?[1] > 0, settings?[0] > 0, curve?[0] is { } mhz ? (int)mhz : null, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or DllNotFoundException or EntryPointNotFoundException)
        {
            return new(true, false, false, null, e.Message);
        }
        finally
        {
            try { File.Delete(copy); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* fichier temporaire, sans conséquence */ }
        }
    }

    /// <summary>Ouvre NVIDIA App ; false s'il est introuvable ou refuse de démarrer.</summary>
    public static bool Open()
    {
        if (AppPath is not { } path) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Accès minimal en lecture à une base SQLite par winsqlite3.dll, le SQLite livré avec Windows (10 et plus) :
    /// aucune bibliothèque à ajouter.
    /// </summary>
    private sealed class Sqlite : IDisposable
    {
        private const int Ok = 0, RowReady = 100, Done = 101, OpenReadOnly = 0x1, ColumnNull = 5;
        private IntPtr _db;

        public Sqlite(string path)
        {
            if (sqlite3_open_v2(path, out _db, OpenReadOnly, IntPtr.Zero) == Ok) return;
            var error = Error();
            Dispose();   // SQLite rend une connexion même en cas d'échec : elle doit être fermée
            throw new InvalidOperationException($"Base illisible : {error}");
        }

        /// <summary>Première ligne d'une requête (null par colonne vide) ; null s'il n'y a aucune ligne.</summary>
        public long?[]? Row(string sql)
        {
            if (sqlite3_prepare_v2(_db, sql, -1, out var statement, IntPtr.Zero) != Ok)
                throw new InvalidOperationException($"Format inattendu : {Error()}");
            try
            {
                var step = sqlite3_step(statement);
                if (step == Done) return null;
                if (step != RowReady) throw new InvalidOperationException($"Lecture interrompue : {Error()}");
                var row = new long?[sqlite3_column_count(statement)];
                for (var i = 0; i < row.Length; i++)
                    row[i] = sqlite3_column_type(statement, i) == ColumnNull ? null : sqlite3_column_int64(statement, i);
                return row;
            }
            finally
            {
                sqlite3_finalize(statement);
            }
        }

        private string Error() => Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)) ?? "erreur inconnue";

        public void Dispose()
        {
            if (_db == IntPtr.Zero) return;
            sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }

        [DllImport("winsqlite3.dll", ExactSpelling = true)]
        private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport("winsqlite3.dll", ExactSpelling = true)]
        private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);

        [DllImport("winsqlite3.dll", ExactSpelling = true)] private static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", ExactSpelling = true)] private static extern int sqlite3_column_count(IntPtr statement);
        [DllImport("winsqlite3.dll", ExactSpelling = true)] private static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", ExactSpelling = true)] private static extern long sqlite3_column_int64(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", ExactSpelling = true)] private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", ExactSpelling = true)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport("winsqlite3.dll", ExactSpelling = true)] private static extern int sqlite3_close_v2(IntPtr db);
    }
}
