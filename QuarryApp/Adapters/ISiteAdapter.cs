using System.Text.RegularExpressions;
using QuarryApp.Core;
using QuarryApp.Models;

namespace QuarryApp.Adapters;

public interface ISiteAdapter
{
    string Site { get; }
    bool Matches(string url);
    bool IsListing(string url) => false;
    bool IsSiteScan(string url) => false;
    Task<List<string>> CrawlGalleriesAsync(string url, HttpClientService client, int? maxGalleries = null) => Task.FromResult(new List<string>());
    Task<Gallery> CollectAsync(string url, HttpClientService client, int? limit = null);
    Task<string?> RefreshItemAsync(ImageItem item, HttpClientService client) => Task.FromResult<string?>(null);
}

public static class StringUtils
{
    private static readonly Regex IllegalRegex = new(@"[<>:""/\\|?*\x00-\x1f]", RegexOptions.Compiled);
    private static readonly Regex SlugRegex = new(@"[^a-zA-Z0-9]+", RegexOptions.Compiled);

    public static string SanitizeFilename(string name, int maxLen = 140)
    {
        var clean = IllegalRegex.Replace(name, "_").Trim(' ', '.');
        clean = Regex.Replace(clean, @"\s+", " ");
        if (clean.Length > maxLen) clean = clean[..maxLen];
        return string.IsNullOrWhiteSpace(clean) ? "image" : clean;
    }

    public static string Slugify(string text, int maxLen = 80)
    {
        var slug = SlugRegex.Replace(text, "-").Trim('-').ToLower();
        if (slug.Length > maxLen) slug = slug[..maxLen];
        return string.IsNullOrWhiteSpace(slug) ? "untitled" : slug;
    }
}
