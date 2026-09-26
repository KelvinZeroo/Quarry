using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using QuarryApp.Core;
using QuarryApp.Models;

namespace QuarryApp.Adapters;

public class GenericAdapter : ISiteAdapter
{
    public string Site => "generic";

    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".bmp", ".tiff", ".mp4", ".m4v", ".webm"
    };

    private static readonly HashSet<string> BlockExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".css", ".js", ".mjs", ".json", ".xml", ".svg", ".ico", ".woff", ".woff2", ".ttf"
    };

    private static readonly Regex JunkRegex = new(@"avatar|logo|sprite|placeholder|spinner|1x1|pixel|loading|favicon|tracking", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StyleUrlRegex = new(@"url\(\s*['""]?([^'"")]+)['""]?\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool Matches(string url) => true;

    public static List<string> ExtractMediaFromHtml(string html, string baseUrl)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var sink = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? rawUrl, bool requireMediaExt = false)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return;
            var trimmed = rawUrl.Trim();
            if (trimmed.StartsWith("data:") || trimmed.StartsWith("blob:") || trimmed.StartsWith("javascript:") || trimmed.StartsWith("#")) return;

            try
            {
                var uri = new Uri(new Uri(baseUrl), trimmed);
                if (uri.Scheme != "http" && uri.Scheme != "https") return;

                var clean = uri.GetLeftPart(UriPartial.Path);
                var ext = Path.GetExtension(uri.AbsolutePath).ToLower();

                if (BlockExts.Contains(ext)) return;
                if (!string.IsNullOrEmpty(ext) && JunkRegex.IsMatch(uri.AbsolutePath)) return;
                if (requireMediaExt && !MediaExts.Contains(ext)) return;

                if (seen.Add(clean))
                {
                    sink.Add(uri.AbsoluteUri);
                }
            }
            catch
            {
                // Ignore invalid URLs
            }
        }

        var nodes = doc.DocumentNode.SelectNodes("//img | //source | //video | //picture");
        if (nodes != null)
        {
            foreach (var node in nodes)
            {
                foreach (var attr in new[] { "src", "data-src", "data-original", "data-lazy", "data-highres", "data-full-url", "poster" })
                {
                    Add(node.GetAttributeValue(attr, null));
                }
            }
        }

        // Meta tags
        var metaNodes = doc.DocumentNode.SelectNodes("//meta[@property or @name]");
        if (metaNodes != null)
        {
            foreach (var meta in metaNodes)
            {
                var prop = meta.GetAttributeValue("property", "") ?? meta.GetAttributeValue("name", "");
                if (prop.StartsWith("og:image") || prop.StartsWith("og:video") || prop.StartsWith("twitter:image"))
                {
                    Add(meta.GetAttributeValue("content", null));
                }
            }
        }

        // Links to media
        var aNodes = doc.DocumentNode.SelectNodes("//a[@href]");
        if (aNodes != null)
        {
            foreach (var a in aNodes)
            {
                Add(a.GetAttributeValue("href", null), true);
            }
        }

        return sink;
    }

    public static string ExtractTitleFromHtml(string html, string url)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        if (titleNode != null && !string.IsNullOrWhiteSpace(titleNode.InnerText))
        {
            return HtmlEntity.DeEntitize(titleNode.InnerText.Trim());
        }

        var h1 = doc.DocumentNode.SelectSingleNode("//h1");
        if (h1 != null && !string.IsNullOrWhiteSpace(h1.InnerText))
        {
            return HtmlEntity.DeEntitize(h1.InnerText.Trim());
        }

        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch
        {
            return "page";
        }
    }

    public async Task<Gallery> CollectAsync(string url, HttpClientService client, int? limit = null)
    {
        var html = await client.FetchTextAsync(url);
        if (string.IsNullOrWhiteSpace(html))
        {
            throw new Exception($"Failed to fetch page: {url}");
        }

        var title = ExtractTitleFromHtml(html, url);
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLower();

        // Check if page contains a primary video player (Avoid scraping 100+ thumbnails on video pages)
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var videoNode = doc.DocumentNode.SelectSingleNode("//video//source[@src] | //video[@src]");
        var ogVideo = doc.DocumentNode.SelectSingleNode("//meta[@property='og:video' or @property='og:video:url']");

        var videoSrc = videoNode?.GetAttributeValue("src", null) ?? ogVideo?.GetAttributeValue("content", null);
        if (!string.IsNullOrWhiteSpace(videoSrc) && !videoSrc.StartsWith("blob:") && !videoSrc.StartsWith("data:"))
        {
            if (videoSrc.StartsWith("//")) videoSrc = "https:" + videoSrc;
            if (!videoSrc.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try { videoSrc = new Uri(new Uri(url), videoSrc).AbsoluteUri; } catch { }
            }

            if (videoSrc.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var cleanTitle = StringUtils.SanitizeFilename(title, 80);
                var ext = Path.GetExtension(videoSrc.Split('?')[0]);
                if (string.IsNullOrEmpty(ext)) ext = ".mp4";

                var filename = $"{cleanTitle}{ext}";
                return new Gallery
                {
                    Site = Site,
                    Url = url,
                    GalleryId = hash,
                    Title = title,
                    Folder = "videos",
                    Items = new List<ImageItem>
                    {
                        new()
                        {
                            Key = $"video/{hash}/{filename}",
                            Url = videoSrc,
                            FilenameHint = filename,
                            Referer = url
                        }
                    },
                    TotalExpected = 1
                };
            }
        }

        var mediaUrls = ExtractMediaFromHtml(html, url);
        if (mediaUrls.Count == 0)
        {
            throw new Exception("No downloadable media found on this page");
        }

        var folder = $"{hash}_{StringUtils.Slugify(title, 30)}";
        var items = new List<ImageItem>();
        for (int i = 0; i < mediaUrls.Count; i++)
        {
            var mUrl = mediaUrls[i];
            var hint = Path.GetFileName(new Uri(mUrl).AbsolutePath);
            if (string.IsNullOrEmpty(hint)) hint = $"file_{i + 1}.jpg";

            items.Add(new ImageItem
            {
                Key = $"generic/{hash}/{i + 1}",
                Url = mUrl,
                FilenameHint = hint,
                Referer = url
            });
        }

        return new Gallery
        {
            Site = Site,
            Url = url,
            GalleryId = hash,
            Title = title,
            Folder = folder,
            Items = items,
            TotalExpected = items.Count
        };
    }
}
