using System.IO;
using System.Text.Json;
using QuarryApp.Models;

namespace QuarryApp.Core;

public static class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".quarry.json"
    );

    public static Settings Load()
    {
        var localConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        var path = File.Exists(localConfig) ? localConfig : ConfigPath;

        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (loaded != null) return loaded;
            }
            catch
            {
                // Fallback to defaults
            }
        }

        return new Settings();
    }

    public static void Save(Settings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Ignore
        }
    }
}
