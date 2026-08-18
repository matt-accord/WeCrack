using System.IO;
using System.Text.Json;

namespace WeCrack;

// Tiny JSON-backed user config, lives at %LocalAppData%\WeCrack\config.json.
// Currently holds one setting: an override path for the WeMod install root, used
// when the user browses to a non-standard location. Grows freely as needed.
internal sealed class Config
{
    public string? OverrideInstallRoot { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WeCrack", "config.json");

    private static readonly JsonSerializerOptions Fmt = new() { WriteIndented = true };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<Config>(json) ?? new Config();
            }
        }
        catch { /* corrupt file → treat as empty; user's Save() will overwrite it */ }
        return new Config();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Fmt));
    }
}
