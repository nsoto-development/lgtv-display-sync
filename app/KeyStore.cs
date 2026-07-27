namespace LgtvDisplaySync.App;

// Persists the LG webOS client-key. Resolution order on load:
//   1. an explicit --keyfile / config path, if given
//   2. legacy local file (%LocalAppData%\lgtv-display-sync\{ip}_ClientKey.txt) if present
//   3. shared store (%ProgramData%\…\config\{ip}_ClientKey.txt)
//   4. flat ProgramData key (pre-config/ layout) — migrate into config/
//   5. one-time migration: reuse a key already paired by ColorControl, and copy it to config/
// If none exist, Load() returns null and the client performs first-run pairing (TV prompt),
// then calls Save() with the key the TV returns (explicit path if set, else config/).
public sealed class KeyStore(string ip, string? explicitFile)
{
    private string ProgramDataPath => AppPaths.ClientKeyPath(ip);
    private string FlatProgramDataPath => AppPaths.LegacyProgramDataKeyPath(ip);
    private string LegacyPath => Path.Combine(AppPaths.LegacyDataDir, $"{ip}_ClientKey.txt");

    private static string ColorControlPath(string ip) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Maassoft", "ColorControl", $"{ip}_ClientKey.txt");

    public string? Load()
    {
        if (explicitFile is not null)
            return Read(explicitFile);

        // Prefer an existing local file so interactive upgrades keep working without a copy step.
        var legacy = Read(LegacyPath);
        if (legacy is not null)
            return legacy;

        var own = Read(ProgramDataPath);
        if (own is not null)
            return own;

        // Pre-config/ layout: key sat next to log.txt under ProgramData root.
        var flat = Read(FlatProgramDataPath);
        if (flat is not null)
        {
            Save(flat);
            try { File.Delete(FlatProgramDataPath); } catch { /* leave flat copy if delete fails */ }
            return flat;
        }

        // One-time reuse of ColorControl's paired key so existing users don't re-pair.
        var cc = Read(ColorControlPath(ip));
        if (cc is not null)
        {
            Save(cc); // migrate a copy into ProgramData config/
            return cc;
        }
        return null;
    }

    public void Save(string key)
    {
        try
        {
            var path = explicitFile ?? ProgramDataPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, key.Trim());
        }
        catch { /* non-fatal: we can still run this session with the in-memory key */ }
    }

    private static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var t = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }
        catch { return null; }
    }
}
