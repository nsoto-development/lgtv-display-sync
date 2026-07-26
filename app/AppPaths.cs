namespace LgtvDisplaySync.App;

// Shared data root for keys and logs (interactive + service).
// Default: %ProgramData%\nsoto.dev\lg-tv-display-sync
// Legacy local override: %LocalAppData%\lgtv-display-sync (used on load if present)
internal static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "nsoto.dev", "lg-tv-display-sync");

    public static string LegacyDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "lgtv-display-sync");

    public static string LogFile => Path.Combine(DataDir, "log.txt");

    public static string EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);
        return DataDir;
    }
}
