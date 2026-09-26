using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using QuarryApp.Core;
using QuarryApp.Models;

namespace QuarryApp.Adapters;

/// <summary>
/// Universal Social Media Adapter for TikTok, Twitter / X, Reddit, and Instagram.
/// Extracts high-definition video streams and photo sets without requiring external Python/yt-dlp tools.
/// </summary>
public class SocialMediaAdapter : ISiteAdapter
{
    public string Site => "SocialMedia";

    private static readonly Regex TikTokRegex = new(@"tiktok\.com/(?:@[\w.-]+/video/(\d+)|v/(\d+)|(\w+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TwitterRegex = new(@"(?:twitter\.com|x\.com)/[\w.-]+/status/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RedditRegex = new(@"reddit\.com/r/[\w.-]+/comments/(\w+)|v\.redd\.it/(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InstagramRegex = new(@"instagram\.com/(?:p|reel|tv|reels)/([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool Matches(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            return host.Contains("tiktok.com") ||
                   host.Contains("twitter.com") ||
                   host.Contains("x.com") ||
                   host.Contains("reddit.com") ||
                   host.Contains("redd.it") ||
                   host.Contains("instagram.com");
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

        if (lower.Contains("tiktok.com"))
        {
            return await ExtractTikTokAsync(url, client);
        }
        if (lower.Contains("twitter.com") || lower.Contains("x.com"))
        {
            return await ExtractTwitterAsync(url, client);
        }
        if (lower.Contains("reddit.com") || lower.Contains("redd.it"))
        {
            return await ExtractRedditAsync(url, client);
        }
        if (lower.Contains("instagram.com"))
        {
            return await ExtractInstagramAsync(url, client);
        }

        throw new Exception($"Unsupported social media URL: {url}");
    }

    #region Platform Extractors

    private async Task<Gallery> ExtractTikTokAsync(string url, HttpClientService client)
    {
        var html = await client.FetchTextAsync(url);
        if (string.IsNullOrWhiteSpace(html)) throw new Exception("Failed to fetch TikTok page.");

        var title = "TikTok_Video";
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        if (titleNode != null && !string.IsNullOrWhiteSpace(titleNode.InnerText))
        {
            title = StringUtils.SanitizeFilename(HtmlEntity.DeEntitize(titleNode.InnerText.Trim()), 60);
        }

        // 1. Check video URL in Universal Data / JSON hydration
        var playUrlMatch = Regex.Match(html, @"""playAddr""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
        if (playUrlMatch.Success)
        {
            var playUrl = Regex.Unescape(playUrlMatch.Groups[1].Value);
            return CreateSingleVideo(url, playUrl, title);
        }

        var downloadUrlMatch = Regex.Match(html, @"""downloadAddr""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
        if (downloadUrlMatch.Success)
        {
            var downUrl = Regex.Unescape(downloadUrlMatch.Groups[1].Value);
            return CreateSingleVideo(url, downUrl, title);
        }

        // 2. OpenGraph video
        var ogVid = doc.DocumentNode.SelectSingleNode("//meta[@property='og:video']/@content")?.GetAttributeValue("content", null);
        if (!string.IsNullOrEmpty(ogVid))
        {
            return CreateSingleVideo(url, ogVid, title);
        }

        throw new Exception("Could not find TikTok video stream.");
    }

    private async Task<Gallery> ExtractTwitterAsync(string url, HttpClientService client)
    {
        var html = await client.FetchTextAsync(url);
        if (string.IsNullOrWhiteSpace(html)) throw new Exception("Failed to fetch Twitter/X page.");

        var title = "Twitter_Video";
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var ogTitle = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']/@content")?.GetAttributeValue("content", null);
        if (!string.IsNullOrEmpty(ogTitle))
        {
            title = StringUtils.SanitizeFilename(HtmlEntity.DeEntitize(ogTitle.Trim()), 60);
        }

        // Search for twimg video variants
        var matches = Regex.Matches(html, @"https://video\.twimg\.com/[^\s""'<>]+\.mp4(?:\?[^\s""'<>]*)?", RegexOptions.IgnoreCase);
        if (matches.Count > 0)
        {
            // Pick the longest / highest resolution variant
            var bestUrl = matches.OrderByDescending(m => m.Value.Length).First().Value;
            return CreateSingleVideo(url, bestUrl, title);
        }

        var ogVid = doc.DocumentNode.SelectSingleNode("//meta[@property='og:video:url']/@content")?.GetAttributeValue("content", null) ??
                     doc.DocumentNode.SelectSingleNode("//meta[@property='og:video']/@content")?.GetAttributeValue("content", null);
        if (!string.IsNullOrEmpty(ogVid))
        {
            return CreateSingleVideo(url, ogVid, title);
        }

        throw new Exception("Could not find Twitter/X video stream.");
    }

    private async Task<Gallery> ExtractRedditAsync(string url, HttpClientService client)
    {
        // Strip trailing query parameters
        var cleanUrl = url.Split('?')[0];
        if (!cleanUrl.EndsWith(".json"))
        {
            cleanUrl = cleanUrl.TrimEnd('/') + ".json";
        }

        var json = await client.FetchTextAsync(cleanUrl);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new Exception("Failed to fetch Reddit post metadata.");
        }

        var title = "Reddit_Video";
        string? videoUrl = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                var postData = root[0].GetProperty("data").GetProperty("children")[0].GetProperty("data");
                if (postData.TryGetProperty("title", out var titleProp))
                {
                    title = StringUtils.SanitizeFilename(titleProp.GetString() ?? "Reddit_Video", 60);
                }

                if (postData.TryGetProperty("secure_media", out var secMedia) && secMedia.ValueKind == JsonValueKind.Object)
                {
                    if (secMedia.TryGetProperty("reddit_video", out var rv))
                    {
                        if (rv.TryGetProperty("fallback_url", out var fb))
                        {
                            videoUrl = fb.GetString();
                        }
                        else if (rv.TryGetProperty("hls_url", out var hls))
                        {
                            videoUrl = hls.GetString();
                        }
                    }
                }
            }
        }
        catch { }

        if (string.IsNullOrEmpty(videoUrl))
        {
            var match = Regex.Match(json, @"https://v\.redd\.it/[^""\s\\]+/DASH_\d+\.mp4", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                videoUrl = match.Value;
            }
        }

        if (!string.IsNullOrEmpty(videoUrl))
        {
            return CreateSingleVideo(url, videoUrl, title);
        }

        throw new Exception("Could not find Reddit video stream.");
    }

    private async Task<Gallery> ExtractInstagramAsync(string url, HttpClientService client)
    {
        var html = await client.FetchTextAsync(url);
        if (string.IsNullOrWhiteSpace(html)) throw new Exception("Failed to fetch Instagram page.");

        var title = "Instagram_Media";
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var ogVid = doc.DocumentNode.SelectSingleNode("//meta[@property='og:video']/@content")?.GetAttributeValue("content", null);
        if (!string.IsNullOrEmpty(ogVid))
        {
            return CreateSingleVideo(url, ogVid, title);
        }

        var mediaList = GenericAdapter.ExtractMediaFromHtml(html, url);
        if (mediaList.Count > 0)
        {
            var items = mediaList.Select((mUrl, i) => new ImageItem
            {
                Key = $"insta/{i + 1}",
                Url = mUrl,
                FilenameHint = $"insta_{i + 1}{Path.GetExtension(mUrl.Split('?')[0])}",
                Referer = url
            }).ToList();

            return new Gallery
            {
                Site = "Instagram",
                Url = url,
                GalleryId = "insta",
                Title = title,
                Folder = "instagram",
                Items = items,
                TotalExpected = items.Count
            };
        }

        throw new Exception("No media found on Instagram page (may require login or be private).");
    }

    private static Gallery CreateSingleVideo(string sourceUrl, string videoUrl, string title)
    {
        var cleanTitle = StringUtils.SanitizeFilename(title, 80);
        var filename = $"{cleanTitle}.mp4";

        return new Gallery
        {
            Site = "SocialMedia",
            Url = sourceUrl,
            GalleryId = "social_vid",
            Title = title,
            Folder = "videos",
            Items = new List<ImageItem>
            {
                new()
                {
                    Key = $"social/{filename}",
                    Url = videoUrl,
                    FilenameHint = filename,
                    Referer = sourceUrl
                }
            },
            TotalExpected = 1
        };
    }

    #endregion
}
