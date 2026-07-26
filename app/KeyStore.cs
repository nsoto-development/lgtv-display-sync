namespace LgtvDisplaySync.App;

// Persists the LG webOS client-key in the tool's OWN folder
// (%LocalAppData%\lgtv-display-sync\{ip}_ClientKey.txt). Resolution order on load:
//   1. an explicit --keyfile / config path, if given
//   2. our own saved key
//   3. one-time migration: reuse a key already paired by ColorControl, and copy it to ours
// If none exist, Load() returns null and the client performs first-run pairing (TV prompt),
// then calls Save() with the key the TV returns.
public sealed class KeyStore(string ip, string? explicitFile)
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "lgtv-display-sync");

    private string OwnPath => Path.Combine(Dir, $"{ip}_ClientKey.txt");

    private static string ColorControlPath(string ip) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Maassoft", "ColorControl", $"{ip}_ClientKey.txt");

    public string? Load()
    {
        if (explicitFile is not null)
            return Read(explicitFile);

        var own = Read(OwnPath);
        if (own is not null)
            return own;

        // One-time reuse of ColorControl's paired key so existing users don't re-pair.
        var cc = Read(ColorControlPath(ip));
        if (cc is not null)
        {
            Save(cc); // migrate a copy into our own store
            return cc;
        }
        return null;
    }

    public void Save(string key)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(OwnPath, key.Trim());
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
