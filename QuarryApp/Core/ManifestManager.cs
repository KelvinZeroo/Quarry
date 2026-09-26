using System.IO;
using System.Text.Json;
using QuarryApp.Models;

namespace QuarryApp.Core;

public class ManifestEntry
{
    public string Dest { get; set; } = string.Empty;
    public string Status { get; set; } = "ok";
    public long? Bytes { get; set; }
    public string? Sha256 { get; set; }
    public string? Url { get; set; }
    public string? Error { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class ManifestData
{
    public string Version { get; set; } = "1.0";
    public Dictionary<string, string> Gallery { get; set; } = new();
    public Dictionary<string, ManifestEntry> Files { get; set; } = new();
}

public class ManifestManager
{
    private readonly string _destDir;
    private readonly string _manifestPath;
    private readonly ManifestData _data;

    public ManifestManager(string destDir, Gallery? gallery = null)
    {
        _destDir = destDir;
        _manifestPath = Path.Combine(destDir, "manifest.json");
        _data = Load(gallery);
    }

    private ManifestData Load(Gallery? gallery)
    {
        if (File.Exists(_manifestPath))
        {
            try
            {
                var json = File.ReadAllText(_manifestPath);
                var loaded = JsonSerializer.Deserialize<ManifestData>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (loaded != null) return loaded;
            }
            catch
            {
                // Fallback
            }
        }

        return new ManifestData
        {
            Version = "1.0",
            Gallery = new Dictionary<string, string>
            {
                ["site"] = gallery?.Site ?? "",
                ["url"] = gallery?.Url ?? "",
                ["galleryId"] = gallery?.GalleryId ?? "",
                ["title"] = gallery?.Title ?? ""
            },
            Files = new Dictionary<string, ManifestEntry>()
        };
    }

    public bool IsDone(string key)
    {
        if (_data.Files.TryGetValue(key, out var entry) && entry.Status == "ok")
        {
            var fullPath = Path.IsPathRooted(entry.Dest) ? entry.Dest : Path.Combine(_destDir, entry.Dest);
            return File.Exists(fullPath);
        }
        return false;
    }

    public void Record(string key, string destPath, string status, long? bytes = null, string? sha256 = null, string? url = null, string? error = null)
    {
        var relPath = Path.GetRelativePath(_destDir, destPath);
        _data.Files[key] = new ManifestEntry
        {
            Dest = relPath,
            Status = status,
            Bytes = bytes,
            Sha256 = sha256,
            Url = url,
            Error = error,
            Timestamp = DateTime.Now
        };
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(_destDir)) Directory.CreateDirectory(_destDir);
            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_manifestPath, json);
        }
        catch
        {
            // Ignore
        }
    }

    public void WriteInfoTxt(Gallery gallery)
    {
        try
        {
            if (!Directory.Exists(_destDir)) Directory.CreateDirectory(_destDir);
            var infoPath = Path.Combine(_destDir, "info.txt");
            var content = string.Join(Environment.NewLine, new[]
            {
                $"Title: {gallery.Title}",
                $"Site: {gallery.Site}",
                $"URL: {gallery.Url}",
                $"Gallery ID: {gallery.GalleryId}",
                $"Total items: {gallery.Items.Count}",
                $"Saved: {DateTime.Now}"
            });
            File.WriteAllText(infoPath, content);
        }
        catch
        {
            // Ignore
        }
    }
}
