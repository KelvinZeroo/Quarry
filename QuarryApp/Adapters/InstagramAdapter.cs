using System.IO;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using QuarryApp.Core;
using QuarryApp.Models;

namespace QuarryApp.Adapters;

public class InstagramAdapter : ISiteAdapter
{
    public string Site => "instagram";
    private static readonly Regex InstagramHostRegex = new(@"^(?:[a-zA-Z0-9_-]+\.)?instagram\.com$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PostRegex = new(@"^/(?:p|reel|tv|reels)/([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool Matches(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            return InstagramHostRegex.IsMatch(host);
        }
        catch
        {
            return false;
        }
    }

    public async Task<Gallery> CollectAsync(string url, HttpClientService client, int? limit = null)
    {
        var html = await client.FetchTextAsync(url);
        var mediaUrls = new List<string>();

        if (!string.IsNullOrWhiteSpace(html))
        {
            mediaUrls = GenericAdapter.ExtractMediaFromHtml(html, url);
        }

        if (mediaUrls.Count == 0)
        {
            throw new Exception("No media found for Instagram URL (login wall or private content)");
        }

        var match = PostRegex.Match(new Uri(url).AbsolutePath);
        var id = match.Success ? match.Groups[1].Value : "instagram";

        var items = new List<ImageItem>();
        for (int i = 0; i < mediaUrls.Count; i++)
        {
            var mUrl = mediaUrls[i];
            var ext = Path.GetExtension(new Uri(mUrl).AbsolutePath);
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";

            items.Add(new ImageItem
            {
                Key = $"instagram/{id}/{i + 1}",
                Url = mUrl,
                FilenameHint = $"{id}_{i + 1}{ext}",
                Referer = url
            });
        }

        return new Gallery
        {
            Site = Site,
            Url = url,
            GalleryId = id,
            Title = $"Instagram {id}",
            Folder = $"instagram_{id}",
            Items = items,
            TotalExpected = items.Count
        };
    }
}
