using System.IO;
using System.Text.RegularExpressions;
using QuarryApp.Core;
using QuarryApp.Models;

namespace QuarryApp.Adapters;

/// <summary>
/// Cloud Storage and File Hoster Resolver for Pixeldrain, Google Drive, and Direct pipes.
/// Resolves raw binary endpoints bypassing intermediate download warning pages.
/// </summary>
public class CloudHosterAdapter : ISiteAdapter
{
    public string Site => "CloudHoster";

    private static readonly Regex PixeldrainRegex = new(@"pixeldrain\.com/(?:u|l)/([a-zA-Z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GoogleDriveRegex = new(@"drive\.google\.com/(?:file/d/|open\?id=)([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool Matches(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            return host.Contains("pixeldrain.com") || host.Contains("drive.google.com");
        }
        catch
        {
            return false;
        }
    }

    public bool IsListing(string url) => false;

    public Task<List<string>> CrawlGalleriesAsync(string url, HttpClientService client, int? maxGalleries = null)
    {
        return Task.FromResult(new List<string>());
    }

    public async Task<Gallery> CollectAsync(string url, HttpClientService client, int? limit = null)
    {
        var lower = url.ToLowerInvariant();

        // 1. Pixeldrain
        if (lower.Contains("pixeldrain.com"))
        {
            var match = PixeldrainRegex.Match(url);
            if (match.Success)
            {
                var fileId = match.Groups[1].Value;
                var directUrl = $"https://pixeldrain.com/api/file/{fileId}";
                var infoJson = await client.FetchTextAsync($"https://pixeldrain.com/api/file/{fileId}/info");
                var name = $"pixeldrain_{fileId}";

                if (!string.IsNullOrWhiteSpace(infoJson))
                {
                    var nameMatch = Regex.Match(infoJson, @"""name""\s*:\s*""([^""]+)""");
                    if (nameMatch.Success) name = nameMatch.Groups[1].Value;
                }

                return CreateSingleFileGallery(url, directUrl, name);
            }
        }

        // 2. Google Drive
        if (lower.Contains("drive.google.com"))
        {
            var match = GoogleDriveRegex.Match(url);
            if (match.Success)
            {
                var fileId = match.Groups[1].Value;
                var directUrl = $"https://drive.google.com/uc?export=download&id={fileId}&confirm=t";
                return CreateSingleFileGallery(url, directUrl, $"gdrive_{fileId}");
            }
        }

        throw new Exception($"Could not resolve cloud hoster link: {url}");
    }

    private static Gallery CreateSingleFileGallery(string sourceUrl, string directUrl, string filename)
    {
        return new Gallery
        {
            Site = "CloudHoster",
            Url = sourceUrl,
            GalleryId = filename,
            Title = filename,
            Folder = "downloads",
            Items = new List<ImageItem>
            {
                new()
                {
                    Key = filename,
                    Url = directUrl,
                    FilenameHint = filename,
                    Referer = sourceUrl
                }
            },
            TotalExpected = 1
        };
    }
}
