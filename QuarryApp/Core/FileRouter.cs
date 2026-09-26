using System.IO;

namespace QuarryApp.Core;

public static class FileRouter
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".flv", ".wmv", ".m4v", ".ts", ".3gp"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".bmp", ".svg", ".ico", ".tiff"
    };

    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".tgz"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".txt", ".epub", ".rtf", ".odt", ".xls", ".xlsx", ".ppt", ".pptx", ".csv"
    };

    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".wma", ".opus", ".alac"
    };

    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".apk", ".dmg", ".pkg", ".deb", ".rpm", ".bin"
    };

    public static string GetCategoryName(string filenameOrUrl)
    {
        if (string.IsNullOrWhiteSpace(filenameOrUrl)) return "General";

        var lower = filenameOrUrl.ToLowerInvariant();

        // 1. Check known video streaming platforms
        if (lower.Contains("youtube.com") || lower.Contains("youtu.be") ||
            lower.Contains("pornhub.") || lower.Contains("xvideos.") || lower.Contains("xnxx.") ||
            lower.Contains("xhamster.") || lower.Contains("spankbang.") || lower.Contains("eporner.") ||
            lower.Contains("redtube.") || lower.Contains("youporn.") || lower.Contains("hqporner.") ||
            lower.Contains("beeg.") || lower.Contains("tiktok.com") || lower.Contains("vimeo.com") ||
            lower.Contains("dailymotion.com"))
        {
            return "Video";
        }

        // 2. Check known photo gallery & image platforms
        if (lower.Contains("pornpics.") || lower.Contains("erome.com") || lower.Contains("imagefap.com") ||
            lower.Contains("rule34.") || lower.Contains("danbooru.") || lower.Contains("gelbooru.") ||
            lower.Contains("yande.re") || lower.Contains("konachan.") || lower.Contains("createaiasian.com") ||
            lower.Contains("coomer.") || lower.Contains("kemono.") || lower.Contains("imgur.com") ||
            lower.Contains("flickr.com") || lower.Contains("pinterest.com"))
        {
            return "Images";
        }

        // 3. Check file extension
        var ext = Path.GetExtension(filenameOrUrl.Split('?')[0]);
        if (string.IsNullOrEmpty(ext)) return "General";

        if (VideoExtensions.Contains(ext)) return "Video";
        if (ImageExtensions.Contains(ext)) return "Images";
        if (CompressedExtensions.Contains(ext)) return "Compressed";
        if (DocumentExtensions.Contains(ext)) return "Documents";
        if (MusicExtensions.Contains(ext)) return "Music";
        if (ProgramExtensions.Contains(ext)) return "Programs";

        return "General";
    }

    public static string RouteDestinationPath(string baseFolder, string filenameOrUrl, bool createCategorySubfolder = true)
    {
        if (!createCategorySubfolder) return baseFolder;

        var category = GetCategoryName(filenameOrUrl);
        var subDir = Path.Combine(baseFolder, category);
        Directory.CreateDirectory(subDir);
        return subDir;
    }
}
