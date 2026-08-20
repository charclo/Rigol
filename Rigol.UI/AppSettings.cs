using System.Text.Json;
using System.Text.Json.Serialization;

namespace RigolUI;

/// <summary>
/// Start-up configuration, read from settings.json next to the executable and written with
/// defaults on first run, so a deployed copy can be pointed at another scope or another mask
/// without rebuilding.
/// </summary>
public sealed class AppSettings
{
    public string IpAddress { get; set; } = "192.168.1.12";

    public int Channel { get; set; } = 1;

    /// <summary>
    /// Mask loaded at start-up. A relative path resolves next to the executable, where the
    /// project's own mask.json is copied by the build.
    /// </summary>
    public string MaskPath { get; set; } = "mask.json";

    /// <summary>Connect, load the mask and start running as soon as the window opens.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>
    /// Upper bound on how often a frame is fetched from the scope, judged and drawn, in hertz.
    /// Lower it to keep traffic off the link and leave the machine free for other work, raise it
    /// to test as many frames as the instrument will hand over.
    /// </summary>
    public int MaxRedrawHz { get; set; } = 20;

    public static string Directory => AppContext.BaseDirectory;

    public static string FilePath => Path.Combine(Directory, "settings.json");

    [JsonIgnore]
    public string ResolvedMaskPath =>
        Path.IsPathRooted(MaskPath) ? MaskPath : Path.Combine(Directory, MaskPath);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Read the settings file, falling back to defaults. A missing or unreadable file is not an
    /// error: the defaults are written back so the file is there to edit next time.
    /// </summary>
    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception)
        {
            settings = new AppSettings();
        }

        if (!File.Exists(FilePath))
            settings.Save();

        return settings;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // Read-only install directory; running with defaults is still fine.
        }
    }
}
