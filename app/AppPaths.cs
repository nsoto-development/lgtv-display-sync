namespace LgtvDisplaySync.App;

// Shared data root for keys and logs (interactive + service).
// Default: %ProgramData%\nsoto.dev\lg-tv-display-sync
//   config\  — client keys
//   log\     — log.txt
// Legacy flat files under DataDir and %LocalAppData%\lgtv-display-sync are still read on load.
internal static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "nsoto.dev", "lg-tv-display-sync");

    public static string ConfigDir { get; } = Path.Combine(DataDir, "config");

    public static string LogDir { get; } = Path.Combine(DataDir, "log");

    public static string LegacyDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "lgtv-display-sync");

    public static string LogFile => Path.Combine(LogDir, "log.txt");

    public static string ClientKeyPath(string ip) =>
        Path.Combine(ConfigDir, $"{ip}_ClientKey.txt");

    /// <summary>Flat key path used before config/ layout (still checked on load).</summary>
    public static string LegacyProgramDataKeyPath(string ip) =>
        Path.Combine(DataDir, $"{ip}_ClientKey.txt");

    public static string EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);
        return DataDir;
    }

    public static string EnsureConfigDir()
    {
        Directory.CreateDirectory(ConfigDir);
        return ConfigDir;
    }

    public static string EnsureLogDir()
    {
        Directory.CreateDirectory(LogDir);
        TryMigrateFlatLog();
        return LogDir;
    }

    private static void TryMigrateFlatLog()
    {
        try
        {
            var flat = Path.Combine(DataDir, "log.txt");
            if (!File.Exists(flat) || File.Exists(LogFile)) return;
            File.Move(flat, LogFile);
        }
        catch { /* best-effort */ }
    }
}
