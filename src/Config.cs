using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitStateWidget;

/// <summary>
/// Everything machine-specific lives here, in %LOCALAPPDATA%\GitStateWidget\config.json, never
/// in source - the same binary runs on the work box and the home box with different repo lists.
/// </summary>
public sealed class Config {
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitStateWidget");
    public static readonly string FilePath = Path.Combine(Dir, "config.json");

    public int IntervalMinutes { get; set; } = 5;
    public bool Fetch { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = false;
    public bool StartHidden { get; set; } = false;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public List<RepoConfig> Repos { get; set; } = new();

    /// <summary>True when the file existed but could not be parsed. Saving is then refused so the broken file is not replaced with an empty one.</summary>
    [JsonIgnore]
    public bool LoadFailed { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static Config Load() {
        try {
            if (File.Exists(FilePath)) {
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), JsonOptions);
                if (cfg != null) {
                    if (cfg.IntervalMinutes < 1) {
                        cfg.IntervalMinutes = 1;
                    }
                    return cfg;
                }
            }
        } catch (Exception ex) {
            // A broken config must not stop the widget, and must not be overwritten either - the
            // panel shows no repos, the error sits beside the file, and the tray menu opens it for repair.
            try {
                File.WriteAllText(Path.Combine(Dir, "config-error.txt"), DateTimeOffset.Now.ToString("u") + "\n" + ex);
            } catch {
            }
            return new Config { LoadFailed = true };
        }
        var fresh = new Config();
        fresh.Save();
        return fresh;
    }

    public void Save() {
        if (LoadFailed) {
            return;
        }
        try {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        } catch {
            // Best effort; position/pin state is a convenience.
        }
    }
}
