using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HotCorners;

public sealed class Settings
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HotCorners");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public Dictionary<Corner, HotAction> Bindings { get; set; } = new()
    {
        [Corner.TopLeft] = HotAction.TaskView,
        [Corner.TopRight] = HotAction.None,
        [Corner.BottomLeft] = HotAction.None,
        [Corner.BottomRight] = HotAction.None,
    };

    public int DwellMs { get; set; } = 150;

    public bool SuppressInFullscreen { get; set; } = true;

    public int CooldownMs { get; set; } = 500;

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var defaults = new Settings();
                defaults.Save();
                return defaults;
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<Settings>(json, JsonOpts) ?? new Settings();
            foreach (Corner c in Enum.GetValues<Corner>())
            {
                if (c == Corner.None) continue;
                loaded.Bindings.TryAdd(c, HotAction.None);
            }
            return loaded;
        }
        catch
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
