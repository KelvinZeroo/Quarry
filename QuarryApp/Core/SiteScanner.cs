using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using QuarryApp.Adapters;
using QuarryApp.Models;

namespace QuarryApp.Core;

public class SiteScanner
{
    private static readonly Regex SitemapLocRegex = new(@"<loc>\s*([^<\s]+)\s*</loc>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SitemapRobotsRegex = new(@"^\s*Sitemap:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public static async Task<Gallery> ScanSiteAsync(string seedUrl, HttpClientService client, Settings settings, Action<string>? onLog = null)
    {
        var seedUri = new Uri(seedUrl);
        var targetHost = seedUri.Host.ToLower();
        var maxScan = settings.MaxScan <= 0 ? 2000 : settings.MaxScan;

        onLog?.Invoke($"Scanning whole-site media for {seedUrl} (cap: {maxScan})");

        var mediaItems = new List<ImageItem>();
        var seenMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { seedUrl };
        var pageQueue = new Queue<string>();
        pageQueue.Enqueue(seedUrl);

        // Check robots.txt for sitemaps
        var robotsUrl = $"{seedUri.Scheme}://{seedUri.Host}/robots.txt";
        var robots = await client.FetchTextAsync(robotsUrl);
        if (!string.IsNullOrWhiteSpace(robots))
        {
            foreach (Match m in SitemapRobotsRegex.Matches(robots))
            {
                var loc = m.Groups[1].Value.Trim();
                var sitemapXml = await client.FetchTextAsync(loc);
                if (!string.IsNullOrWhiteSpace(sitemapXml))
                {
                    foreach (Match sm in SitemapLocRegex.Matches(sitemapXml))
                    {
                        var sLoc = sm.Groups[1].Value.Trim();
                        if (Uri.TryCreate(sLoc, UriKind.Absolute, out var p) && p.Host.ToLower() == targetHost)
                        {
                            if (seenPages.Add(sLoc)) pageQueue.Enqueue(sLoc);
                            if (pageQueue.Count >= 300) break;
                        }
                    }
                }
            }
        }

        int pagesProcessed = 0;
        while (pageQueue.Count > 0 && pagesProcessed < 100 && mediaItems.Count < maxScan)
        {
            var curUrl = pageQueue.Dequeue();
            pagesProcessed++;

            var html = await client.FetchTextAsync(curUrl);
            if (string.IsNullOrWhiteSpace(html)) continue;

            var foundMedia = GenericAdapter.ExtractMediaFromHtml(html, curUrl);
            foreach (var mUrl in foundMedia)
            {
                if (seenMedia.Add(mUrl))
                {
                    var itemHash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(mUrl)))[..8].ToLower();
                    var hint = Path.GetFileName(new Uri(mUrl).AbsolutePath);
                    if (string.IsNullOrEmpty(hint)) hint = $"file_{mediaItems.Count + 1}.jpg";

                    mediaItems.Add(new ImageItem
                    {
                        Key = $"scan/{targetHost}/{itemHash}",
                        Url = mUrl,
                        FilenameHint = hint,
                        Referer = curUrl
                    });

                    if (mediaItems.Count >= maxScan) break;
                }
            }

            if (mediaItems.Count >= maxScan) break;

            // Enqueue more links
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var aNodes = doc.DocumentNode.SelectNodes("//a[@href]");
            if (aNodes != null)
            {
                foreach (var a in aNodes)
                {
                    var href = a.GetAttributeValue("href", null);
                    if (!string.IsNullOrEmpty(href) && Uri.TryCreate(new Uri(curUrl), href, out var resolved) && resolved.Host.ToLower() == targetHost)
                    {
                        var clean = resolved.GetLeftPart(UriPartial.Path);
                        var ext = Path.GetExtension(resolved.AbsolutePath).ToLower();
                        if (!new[] { ".jpg", ".png", ".gif", ".zip", ".mp4" }.Contains(ext))
                        {
                            if (seenPages.Add(clean)) pageQueue.Enqueue(clean);
                        }
                    }
                }
            }
        }

        if (mediaItems.Count == 0)
        {
            throw new Exception($"Site scan found no media on {seedUrl}");
        }

        var siteTitle = seedUri.Host.Replace("www.", "");
        return new Gallery
        {
            Site = "site-scan",
            Url = seedUrl,
            GalleryId = StringUtils.Slugify(siteTitle, 20),
            Title = $"Site Scan: {siteTitle}",
            Folder = $"site_{StringUtils.Slugify(siteTitle, 30)}",
            Items = mediaItems,
            TotalExpected = mediaItems.Count
        };
    }
}
