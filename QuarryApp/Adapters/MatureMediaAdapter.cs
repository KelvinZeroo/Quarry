using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using QuarryApp.Core;
using QuarryApp.Models;

namespace QuarryApp.Adapters;

/// <summary>
/// Unified Mature / 18+ Media Engine for adult video platforms, photo galleries, and image boards.
/// Handles single video stream extractions (XVideos, PornHub, XHamster, SpankBang, Eporner, etc.)
/// and batch photo galleries (PornPics, EroMe, ImageFap, Boorus, Coomer, Kemono, CreateAIAsian).
/// </summary>
public class MatureMediaAdapter : ISiteAdapter
{
    public string Site => "MatureMedia";

    // Known adult video domains
    private static readonly HashSet<string> VideoDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "xvideos.com", "xvideos.red", "xvideos2.com", "xnxx.com", "xnxx.health", "xnxx.tv",
        "pornhub.com", "pornhubpremium.com", "xhamster.com", "xhamster.desi", "xhamster19.com", "xhamster20.com",
        "spankbang.com", "eporner.com", "redtube.com", "youporn.com", "hqporner.com",
        "beeg.com", "daftsex.com", "tnaflix.com", "tube8.com", "thisav.com"
    };

    // Known adult gallery / booru domains
    private static readonly HashSet<string> GalleryDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "pornpics.com", "erome.com", "imagefap.com", "createaiasian.com",
        "coomer.su", "coomer.party", "kemono.su", "kemono.party",
        "cyberdrop.me", "cyberdrop.to", "cyberdrop.cr", "bunkr.si", "bunkr.site", "bunkr.is", "bunkr.ws",
        "simpcity.su", "simpcity.to",
        "rule34.xxx", "danbooru.donmai.us", "gelbooru.com", "yande.re",
        "konachan.com", "konachan.net", "safebooru.org", "sankakucomplex.com", "e-shuushuu.net"
    };

    public bool Matches(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host[4..];

            foreach (var d in VideoDomains)
            {
                if (host == d || host.EndsWith("." + d)) return true;
            }
            foreach (var d in GalleryDomains)
            {
                if (host == d || host.EndsWith("." + d)) return true;
            }

            // Keyword heuristics for adult domains
            if (host.Contains("porn") || host.Contains("booru") || host.Contains("erome") || host.Contains("xvideos") || host.Contains("xhamster"))
            {
                return true;
            }
        }
        catch { }
        return false;
    }

    public bool IsListing(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            var path = new Uri(url).AbsolutePath;

            if (host.Contains("pornpics.com")) return !Regex.IsMatch(path, @"/galleries/[^/]+?-(\d+)/?$");
            if (host.Contains("erome.com")) return !Regex.IsMatch(path, @"/a/([a-zA-Z0-9]+)");
            if (host.Contains("imagefap.com")) return !path.Contains("/gallery/") && !path.Contains("/pictures/");
            if (host.Contains("rule34.xxx") || host.Contains("booru")) return !url.Contains("id=");
        }
        catch { }
        return false;
    }

    public async Task<List<string>> CrawlGalleriesAsync(string url, HttpClientService client, int? maxGalleries = null)
    {
        var limit = maxGalleries ?? 50;
        var list = new List<string>();
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            if (host.Contains("pornpics.com"))
            {
                return await CrawlPornPicsListingAsync(url, client, limit);
            }
            if (host.Contains("erome.com"))
            {
                return await CrawlEromeListingAsync(url, client, limit);
            }
            if (host.Contains("rule34.xxx") || host.Contains("booru"))
            {
                return await CrawlBooruListingAsync(url, client, limit);
            }
        }
        catch { }
        return list;
    }

    public async Task<Gallery> CollectAsync(string url, HttpClientService client, int? limit = null)
    {
        var html = await client.FetchTextAsync(url);
        if (string.IsNullOrWhiteSpace(html))
        {
            throw new Exception($"Failed to fetch page: {url}");
        }

        var host = new Uri(url).Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];

        // 1. XVideos & XNXX
        if (host.Contains("xvideos") || host.Contains("xnxx"))
        {
            return await ExtractXVideosAsync(url, html, client);
        }

        // 2. PornHub
        if (host.Contains("pornhub"))
        {
            return await ExtractPornHubAsync(url, html, client);
        }

        // 3. XHamster
        if (host.Contains("xhamster"))
        {
            return ExtractXHamster(url, html);
        }

        // 4. SpankBang
        if (host.Contains("spankbang"))
        {
            return ExtractSpankBang(url, html);
        }

        // 5. Eporner
        if (host.Contains("eporner"))
        {
            return ExtractEporner(url, html);
        }

        // 6. RedTube & YouPorn
        if (host.Contains("redtube") || host.Contains("youporn"))
        {
            return ExtractRedTubeOrYouPorn(url, html);
        }

        // 7. HQPorner, Beeg, DaftSex, Tube8, ThisAV
        if (host.Contains("hqporner") || host.Contains("beeg") || host.Contains("daftsex") || host.Contains("tube8") || host.Contains("thisav") || host.Contains("tnaflix"))
        {
            var vidGallery = ExtractGenericAdultVideo(url, html, host);
            if (vidGallery != null) return vidGallery;
        }

        // 8. PornPics Galleries
        if (host.Contains("pornpics.com"))
        {
            return ExtractPornPics(url, html);
        }

        // 9. EroMe Albums (Videos + Photos)
        if (host.Contains("erome.com"))
        {
            return ExtractErome(url, html);
        }

        // 10. ImageFap Galleries
        if (host.Contains("imagefap.com"))
        {
            return ExtractImageFap(url, html);
        }

        // 11. Booru Platforms (Rule34, Danbooru, Gelbooru, etc.)
        if (host.Contains("rule34.xxx") || host.Contains("danbooru") || host.Contains("gelbooru") || host.Contains("yande.re") || host.Contains("konachan") || host.Contains("safebooru"))
        {
            return ExtractBooru(url, html);
        }

        // 12. CreateAIAsian
        if (host.Contains("createaiasian.com"))
        {
            return ExtractCreateAIAsian(url, html);
        }

        // 13. Coomer & Kemono
        if (host.Contains("coomer") || host.Contains("kemono"))
        {
            return ExtractCoomerOrKemono(url, html);
        }

        // Universal Check: If the page has an embedded video player stream, extract the video!
        var fallbackVid = ExtractGenericAdultVideo(url, html, host);
        if (fallbackVid != null) return fallbackVid;

        // Otherwise fallback to multi-image extraction
        return ExtractFallbackGallery(url, html);
    }

    #region Video Extractors

    private async Task<Gallery> ExtractXVideosAsync(string url, string html, HttpClientService client)
    {
        var titleMatch = Regex.Match(html, @"html5player\.setVideoTitle\('([^']+)'\)", RegexOptions.IgnoreCase);
        var title = titleMatch.Success ? titleMatch.Groups[1].Value : ExtractDocTitle(html, "xvideos_video");

        var highMatch = Regex.Match(html, @"html5player\.setVideoUrlHigh\('([^']+)'\)", RegexOptions.IgnoreCase);
        var lowMatch = Regex.Match(html, @"html5player\.setVideoUrlLow\('([^']+)'\)", RegexOptions.IgnoreCase);
        var hlsMatch = Regex.Match(html, @"html5player\.setVideoHLS\('([^']+)'\)", RegexOptions.IgnoreCase);

        var videoUrl = highMatch.Success ? highMatch.Groups[1].Value : (lowMatch.Success ? lowMatch.Groups[1].Value : (hlsMatch.Success ? hlsMatch.Groups[1].Value : ""));
        if (string.IsNullOrEmpty(videoUrl))
        {
            var idMatch = Regex.Match(url, @"video(?:_id=)?(\d+)");
            if (idMatch.Success)
            {
                var embedUrl = $"https://www.xvideos.com/embedframe/{idMatch.Groups[1].Value}";
                var embedHtml = await client.FetchTextAsync(embedUrl, referer: url);
                if (!string.IsNullOrWhiteSpace(embedHtml))
                {
                    var emHigh = Regex.Match(embedHtml, @"html5player\.setVideoUrlHigh\('([^']+)'\)", RegexOptions.IgnoreCase);
                    var emHls = Regex.Match(embedHtml, @"html5player\.setVideoHLS\('([^']+)'\)", RegexOptions.IgnoreCase);
                    videoUrl = emHigh.Success ? emHigh.Groups[1].Value : (emHls.Success ? emHls.Groups[1].Value : "");
                }
            }
        }

        if (string.IsNullOrEmpty(videoUrl))
        {
            videoUrl = ExtractVideoTagSrc(html, url) ?? throw new Exception("Could not find XVideos stream URL.");
        }

        return CreateSingleVideoGallery(url, videoUrl, title);
    }

    private async Task<Gallery> ExtractPornHubAsync(string url, string html, HttpClientService client)
    {
        var title = ExtractDocTitle(html, "pornhub_video");
        string? bestVideoUrl = ParsePornHubVideoUrl(html);

        if (string.IsNullOrEmpty(bestVideoUrl))
        {
            var viewkeyMatch = Regex.Match(url, @"viewkey=([a-zA-Z0-9]+)");
            if (viewkeyMatch.Success)
            {
                var viewkey = viewkeyMatch.Groups[1].Value;
                var embedUrl = $"https://www.pornhub.com/embed/{viewkey}";
                var embedHtml = await client.FetchTextAsync(embedUrl, referer: url);
                if (!string.IsNullOrWhiteSpace(embedHtml))
                {
                    bestVideoUrl = ParsePornHubVideoUrl(embedHtml);
                }
            }
        }

        if (!string.IsNullOrEmpty(bestVideoUrl))
        {
            return CreateSingleVideoGallery(url, bestVideoUrl, title);
        }

        throw new Exception("Could not find PornHub video stream. The video may be geo-restricted or private.");
    }

    private string? ParsePornHubVideoUrl(string html)
    {
        string? bestVideoUrl = null;
        int bestScore = 0;

        // 1. Check mediaDefinitions JSON objects with quality and format
        var mediaDefRegex = new Regex(@"\{[^{}]*""videoUrl""\s*:\s*""([^""]+)""[^{}]*\}", RegexOptions.IgnoreCase);
        var defMatches = mediaDefRegex.Matches(html);
        foreach (Match match in defMatches)
        {
            var rawJsonChunk = match.Value;
            var vUrl = match.Groups[1].Value.Replace(@"\/", "/");
            if (string.IsNullOrWhiteSpace(vUrl) || !vUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

            int qVal = 0;
            var qMatch = Regex.Match(rawJsonChunk, @"""quality""\s*:\s*(\d+)");
            if (qMatch.Success && int.TryParse(qMatch.Groups[1].Value, out var parsedQ))
            {
                qVal = parsedQ;
            }
            else
            {
                var qStrMatch = Regex.Match(rawJsonChunk, @"""quality""\s*:\s*""(\d+)p?""");
                if (qStrMatch.Success && int.TryParse(qStrMatch.Groups[1].Value, out var parsedQStr))
                {
                    qVal = parsedQStr;
                }
            }

            var isMp4 = rawJsonChunk.Contains(@"""format"":""mp4""", StringComparison.OrdinalIgnoreCase) ||
                        rawJsonChunk.Contains(@"""format"": ""mp4""", StringComparison.OrdinalIgnoreCase) ||
                        vUrl.Contains(".mp4", StringComparison.OrdinalIgnoreCase);

            int score = qVal * 10 + (isMp4 ? 5 : 0);
            if (score > bestScore || bestVideoUrl == null)
            {
                bestScore = score;
                bestVideoUrl = vUrl;
            }
        }

        // 2. Check quality_1080p, quality_720p, etc. variables
        if (string.IsNullOrEmpty(bestVideoUrl))
        {
            foreach (var q in new[] { "1080p", "720p", "480p", "360p", "240p" })
            {
                var qMatch = Regex.Match(html, $@"""quality_{q}""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                if (qMatch.Success)
                {
                    bestVideoUrl = qMatch.Groups[1].Value.Replace(@"\/", "/");
                    break;
                }
            }
        }

        // 3. Check player_mp4_url
        if (string.IsNullOrEmpty(bestVideoUrl))
        {
            var directMatch = Regex.Match(html, @"var\s+player_mp4_url\s*=\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
            if (directMatch.Success)
            {
                bestVideoUrl = directMatch.Groups[1].Value.Replace(@"\/", "/");
            }
        }

        // 4. Check any videoUrl matches
        if (string.IsNullOrEmpty(bestVideoUrl))
        {
            var mediaMatches = Regex.Matches(html, @"""videoUrl""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
            foreach (Match m in mediaMatches)
            {
                var clean = m.Groups[1].Value.Replace(@"\/", "/");
                if (!string.IsNullOrEmpty(clean) && clean.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    bestVideoUrl = clean;
                    break;
                }
            }
        }

        // 5. Check video tag src
        if (string.IsNullOrEmpty(bestVideoUrl))
        {
            bestVideoUrl = ExtractVideoTagSrc(html, "");
        }

        return bestVideoUrl;
    }

    private Gallery ExtractXHamster(string url, string html)
    {
        var title = ExtractDocTitle(html, "xhamster_video");
        var match720 = Regex.Match(html, @"""720p""\s*:\s*""(https:[^""]+)""", RegexOptions.IgnoreCase);
        var match1080 = Regex.Match(html, @"""1080p""\s*:\s*""(https:[^""]+)""", RegexOptions.IgnoreCase);
        var matchMp4 = Regex.Match(html, @"""mp4""\s*:\s*""(https:[^""]+)""", RegexOptions.IgnoreCase);

        var videoUrl = match1080.Success ? match1080.Groups[1].Value : (match720.Success ? match720.Groups[1].Value : (matchMp4.Success ? matchMp4.Groups[1].Value : ""));
        videoUrl = videoUrl.Replace(@"\/", "/");

        if (string.IsNullOrEmpty(videoUrl))
        {
            videoUrl = ExtractVideoTagSrc(html, url) ?? throw new Exception("Could not find XHamster video stream.");
        }

        return CreateSingleVideoGallery(url, videoUrl, title);
    }

    private Gallery ExtractSpankBang(string url, string html)
    {
        var title = ExtractDocTitle(html, "spankbang_video");
        var m4k = Regex.Match(html, @"'4k'\s*:\s*\[\s*'([^']+)'", RegexOptions.IgnoreCase);
        var m1080 = Regex.Match(html, @"'1080p'\s*:\s*\[\s*'([^']+)'", RegexOptions.IgnoreCase);
        var m720 = Regex.Match(html, @"'720p'\s*:\s*\[\s*'([^']+)'", RegexOptions.IgnoreCase);
        var streamUrl = m4k.Success ? m4k.Groups[1].Value : (m1080.Success ? m1080.Groups[1].Value : (m720.Success ? m720.Groups[1].Value : ""));

        if (string.IsNullOrEmpty(streamUrl))
        {
            streamUrl = ExtractVideoTagSrc(html, url) ?? throw new Exception("Could not find SpankBang video stream.");
        }

        return CreateSingleVideoGallery(url, streamUrl, title);
    }

    private Gallery ExtractEporner(string url, string html)
    {
        var title = ExtractDocTitle(html, "eporner_video");
        var dloadMatch = Regex.Match(html, @"href=""(/dload/[^""]+)""", RegexOptions.IgnoreCase);
        if (dloadMatch.Success)
        {
            var full = new Uri(new Uri(url), dloadMatch.Groups[1].Value).AbsoluteUri;
            return CreateSingleVideoGallery(url, full, title);
        }

        var src = ExtractVideoTagSrc(html, url) ?? throw new Exception("Could not find Eporner video stream.");
        return CreateSingleVideoGallery(url, src, title);
    }

    private Gallery ExtractRedTubeOrYouPorn(string url, string html)
    {
        var title = ExtractDocTitle(html, "adult_video");
        var mediaMatches = Regex.Matches(html, @"""videoUrl""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
        if (mediaMatches.Count > 0)
        {
            var videoUrl = mediaMatches[^1].Groups[1].Value.Replace(@"\/", "/");
            return CreateSingleVideoGallery(url, videoUrl, title);
        }

        var src = ExtractVideoTagSrc(html, url) ?? throw new Exception("Could not find video stream.");
        return CreateSingleVideoGallery(url, src, title);
    }

    private Gallery? ExtractGenericAdultVideo(string url, string html, string host)
    {
        var src = ExtractVideoTagSrc(html, url);
        if (string.IsNullOrEmpty(src))
        {
            // OpenGraph video
            var ogVid = Regex.Match(html, @"<meta\s+property=""og:video(?::url)?""\s+content=""([^""]+)""", RegexOptions.IgnoreCase);
            if (ogVid.Success) src = ogVid.Groups[1].Value;
        }

        if (!string.IsNullOrEmpty(src))
        {
            var title = ExtractDocTitle(html, $"{host}_video");
            return CreateSingleVideoGallery(url, src, title);
        }

        return null;
    }

    private Gallery CreateSingleVideoGallery(string sourceUrl, string videoUrl, string title)
    {
        var cleanTitle = StringUtils.SanitizeFilename(title, 80);
        var ext = Path.GetExtension(videoUrl.Split('?')[0]);
        if (string.IsNullOrEmpty(ext) || ext == ".m3u8") ext = ".mp4";

        var filename = $"{cleanTitle}{ext}";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sourceUrl)))[..8].ToLower();

        return new Gallery
        {
            Site = Site,
            Url = sourceUrl,
            GalleryId = hash,
            Title = title,
            Folder = "videos",
            Items = new List<ImageItem>
            {
                new()
                {
                    Key = $"{hash}/{filename}",
                    Url = videoUrl,
                    FilenameHint = filename,
                    Referer = sourceUrl
                }
            },
            TotalExpected = 1
        };
    }

    private string? ExtractVideoTagSrc(string html, string baseUrl)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var videoNodes = doc.DocumentNode.SelectNodes("//video//source[@src] | //video[@src]");
        if (videoNodes != null)
        {
            foreach (var node in videoNodes)
            {
                var src = node.GetAttributeValue("src", "");
                if (!string.IsNullOrEmpty(src) && !src.StartsWith("blob:") && !src.StartsWith("data:"))
                {
                    if (src.StartsWith("//")) src = "https:" + src;
                    if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        try { src = new Uri(new Uri(baseUrl), src).AbsoluteUri; } catch { continue; }
                    }
                    return src;
                }
            }
        }

        // Direct regex search for MP4 in HTML
        var mp4Rx = new Regex(@"https?://[^\s""'<>]+\.mp4(?:\?[^\s""'<>]*)?", RegexOptions.IgnoreCase);
        var m = mp4Rx.Match(html);
        if (m.Success && !m.Value.Contains("preview") && !m.Value.Contains("trailer"))
        {
            return m.Value;
        }

        return null;
    }

    private string ExtractDocTitle(string html, string defaultTitle)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var titleNode = doc.DocumentNode.SelectSingleNode("//title") ?? doc.DocumentNode.SelectSingleNode("//h1");
        if (titleNode != null && !string.IsNullOrWhiteSpace(titleNode.InnerText))
        {
            var raw = HtmlEntity.DeEntitize(titleNode.InnerText.Trim());
            // Strip website suffixes like "- XVideos.com", "- Pornhub.com"
            raw = Regex.Replace(raw, @"\s*[-|–]\s*(?:XVideos|Pornhub|XHamster|SpankBang|Eporner|RedTube|YouPorn).*$", "", RegexOptions.IgnoreCase);
            return raw.Trim();
        }
        return defaultTitle;
    }

    #endregion

    #region Gallery Extractors

    private Gallery ExtractPornPics(string url, string html)
    {
        var cleanUrl = url.Split('?')[0];
        if (!cleanUrl.EndsWith("/")) cleanUrl += "/";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var h1 = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'gallery-title')]//h1 | //h1");
        var title = h1 != null && !string.IsNullOrWhiteSpace(h1.InnerText) ? HtmlEntity.DeEntitize(h1.InnerText.Trim()) : "untitled";

        var gidMatch = Regex.Match(new Uri(cleanUrl).AbsolutePath, @"/galleries/[^/]+?-(\d+)/?$");
        var gid = gidMatch.Success ? gidMatch.Groups[1].Value : StringUtils.Slugify(title, 20);

        var items = new List<ImageItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var aNodes = doc.DocumentNode.SelectNodes("//ul[@id='tiles']//a[@href] | //div[@id='tiles']//a[@href] | //li[contains(@class, 'thumb')]//a[@href]");
        if (aNodes != null)
        {
            foreach (var a in aNodes)
            {
                var href = a.GetAttributeValue("href", "");
                if (string.IsNullOrWhiteSpace(href)) continue;
                if (href.StartsWith("//")) href = "https:" + href;
                if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    try { href = new Uri(new Uri(cleanUrl), href).AbsoluteUri; } catch { continue; }
                }

                if (!href.Contains("pornpics.com", StringComparison.OrdinalIgnoreCase) && !href.Contains("cdni.", StringComparison.OrdinalIgnoreCase)) continue;
                href = href.Replace("/460/", "/1280/").Replace("/small/", "/1280/");

                if (seen.Add(href))
                {
                    var filename = Path.GetFileName(new Uri(href).AbsolutePath);
                    if (string.IsNullOrWhiteSpace(filename)) filename = $"{items.Count + 1:D3}.jpg";
                    items.Add(new ImageItem
                    {
                        Key = $"pornpics/{gid}/{filename}",
                        Url = href,
                        FilenameHint = filename,
                        Referer = cleanUrl
                    });
                }
            }
        }

        if (items.Count == 0)
        {
            var rx = new Regex(@"https?://(?:cdni|pics)\.pornpics\.com/[^\s""'<>]+\.(?:jpg|jpeg|png|webp)", RegexOptions.IgnoreCase);
            foreach (Match m in rx.Matches(html))
            {
                var src = m.Value.Replace("/460/", "/1280/").Replace("/small/", "/1280/");
                if (seen.Add(src))
                {
                    var filename = Path.GetFileName(new Uri(src).AbsolutePath);
                    if (string.IsNullOrWhiteSpace(filename)) filename = $"{items.Count + 1:D3}.jpg";
                    items.Add(new ImageItem
                    {
                        Key = $"pornpics/{gid}/{filename}",
                        Url = src,
                        FilenameHint = filename,
                        Referer = cleanUrl
                    });
                }
            }
        }

        var folder = $"{gid}_{StringUtils.Slugify(title, 40)}";
        return new Gallery
        {
            Site = "PornPics",
            Url = cleanUrl,
            GalleryId = gid,
            Title = title,
            Folder = folder,
            Items = items,
            TotalExpected = items.Count
        };
    }

    private Gallery ExtractErome(string url, string html)
    {
        var match = Regex.Match(url, @"/a/([a-zA-Z0-9]+)");
        var aid = match.Success ? match.Groups[1].Value : "album";

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var h1 = doc.DocumentNode.SelectSingleNode("//h1");
        var title = h1 != null && !string.IsNullOrWhiteSpace(h1.InnerText) ? HtmlEntity.DeEntitize(h1.InnerText.Trim()) : $"album-{aid}";

        var items = new List<ImageItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Images
        var imgNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'image-wrapper')]//img | //img[@data-src]");
        if (imgNodes != null)
        {
            foreach (var img in imgNodes)
            {
                var src = img.GetAttributeValue("data-src", null) ?? img.GetAttributeValue("src", null);
                if (!string.IsNullOrEmpty(src) && !src.Contains("thumb_") && !src.Contains("avatar"))
                {
                    var full = new Uri(new Uri("https://www.erome.com"), src).AbsoluteUri;
                    if (seen.Add(full))
                    {
                        var hint = Path.GetFileName(new Uri(full).AbsolutePath);
                        items.Add(new ImageItem
                        {
                            Key = $"erome/{aid}/{hint}",
                            Url = full,
                            FilenameHint = hint,
                            Referer = url
                        });
                    }
                }
            }
        }

        // Videos
        var vidNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'video-wrapper')]//video | //video");
        if (vidNodes != null)
        {
            foreach (var vid in vidNodes)
            {
                var srcNode = vid.SelectSingleNode(".//source");
                var src = srcNode?.GetAttributeValue("src", null) ?? vid.GetAttributeValue("src", null);
                if (!string.IsNullOrEmpty(src))
                {
                    var full = new Uri(new Uri("https://www.erome.com"), src).AbsoluteUri;
                    if (seen.Add(full))
                    {
                        var hint = Path.GetFileName(new Uri(full).AbsolutePath);
                        items.Add(new ImageItem
                        {
                            Key = $"erome/{aid}/{hint}",
                            Url = full,
                            FilenameHint = hint,
                            Referer = url
                        });
                    }
                }
            }
        }

        var folder = $"{aid}_{StringUtils.Slugify(title, 40)}";
        return new Gallery
        {
            Site = "EroMe",
            Url = url,
            GalleryId = aid,
            Title = title,
            Folder = folder,
            Items = items,
            TotalExpected = items.Count
        };
    }

    private Gallery ExtractImageFap(string url, string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var title = ExtractDocTitle(html, "imagefap_gallery");
        var gid = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLower();

        var items = new List<ImageItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var aNodes = doc.DocumentNode.SelectNodes("//a[contains(@href, '/photo/')] | //img[contains(@src, 'imagefap.com')]");
        if (aNodes != null)
        {
            foreach (var a in aNodes)
            {
                var src = a.GetAttributeValue("data-src", null) ?? a.GetAttributeValue("src", null);
                if (!string.IsNullOrEmpty(src) && !src.Contains("logo") && seen.Add(src))
                {
                    var hint = Path.GetFileName(new Uri(src).AbsolutePath);
                    items.Add(new ImageItem
                    {
                        Key = $"imagefap/{gid}/{hint}",
                        Url = src,
                        FilenameHint = hint,
                        Referer = url
                    });
                }
            }
        }

        return new Gallery
        {
            Site = "ImageFap",
            Url = url,
            GalleryId = gid,
            Title = title,
            Folder = $"{gid}_{StringUtils.Slugify(title, 40)}",
            Items = items,
            TotalExpected = items.Count
        };
    }

    private Gallery ExtractBooru(string url, string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var img = doc.DocumentNode.SelectSingleNode("//img[@id='image'] | //video[@id='gelcomVideoServer']//source | //video//source");
        if (img != null)
        {
            var src = img.GetAttributeValue("src", "");
            if (!string.IsNullOrEmpty(src))
            {
                var full = new Uri(new Uri(url), src).AbsoluteUri;
                var hint = Path.GetFileName(new Uri(full).AbsolutePath);
                var id = Regex.Match(url, @"id=(\d+)").Groups[1].Value;

                return new Gallery
                {
                    Site = "Booru",
                    Url = url,
                    GalleryId = id,
                    Title = $"booru_{id}",
                    Folder = "booru",
                    Items = new List<ImageItem>
                    {
                        new()
                        {
                            Key = $"booru/{id}/{hint}",
                            Url = full,
                            FilenameHint = hint,
                            Referer = url
                        }
                    },
                    TotalExpected = 1
                };
            }
        }

        return ExtractFallbackGallery(url, html);
    }

    private Gallery ExtractCreateAIAsian(string url, string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var title = ExtractDocTitle(html, "createaiasian_gallery");
        var gid = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLower();

        var items = new List<ImageItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var imgNodes = doc.DocumentNode.SelectNodes("//img[@src]");
        if (imgNodes != null)
        {
            foreach (var img in imgNodes)
            {
                var src = img.GetAttributeValue("src", "");
                if (src.Contains("/images/") || src.Contains("models"))
                {
                    var full = new Uri(new Uri(url), src).AbsoluteUri;
                    if (seen.Add(full))
                    {
                        var hint = Path.GetFileName(new Uri(full).AbsolutePath);
                        items.Add(new ImageItem
                        {
                            Key = $"createai/{gid}/{hint}",
                            Url = full,
                            FilenameHint = hint,
                            Referer = url
                        });
                    }
                }
            }
        }

        return new Gallery
        {
            Site = "CreateAIAsian",
            Url = url,
            GalleryId = gid,
            Title = title,
            Folder = $"{gid}_{StringUtils.Slugify(title, 40)}",
            Items = items,
            TotalExpected = items.Count
        };
    }

    private Gallery ExtractCoomerOrKemono(string url, string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var title = ExtractDocTitle(html, "creator_post");
        var gid = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLower();

        var items = new List<ImageItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var fileNodes = doc.DocumentNode.SelectNodes("//a[contains(@class, 'fileThumb')] | //a[contains(@class, 'post__attachment-link')] | //img[contains(@class, 'post__image')]");
        if (fileNodes != null)
        {
            foreach (var n in fileNodes)
            {
                var href = n.GetAttributeValue("href", n.GetAttributeValue("src", ""));
                if (!string.IsNullOrEmpty(href))
                {
                    var full = new Uri(new Uri(url), href).AbsoluteUri;
                    if (seen.Add(full))
                    {
                        var hint = Path.GetFileName(new Uri(full).AbsolutePath);
                        items.Add(new ImageItem
                        {
                            Key = $"creator/{gid}/{hint}",
                            Url = full,
                            FilenameHint = hint,
                            Referer = url
                        });
                    }
                }
            }
        }

        return new Gallery
        {
            Site = "CreatorMedia",
            Url = url,
            GalleryId = gid,
            Title = title,
            Folder = $"{gid}_{StringUtils.Slugify(title, 40)}",
            Items = items,
            TotalExpected = items.Count
        };
    }

    private Gallery ExtractFallbackGallery(string url, string html)
    {
        var title = ExtractDocTitle(html, "media_gallery");
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url)))[..8].ToLower();
        var media = GenericAdapter.ExtractMediaFromHtml(html, url);

        var items = new List<ImageItem>();
        for (int i = 0; i < media.Count; i++)
        {
            var mUrl = media[i];
            var hint = Path.GetFileName(new Uri(mUrl).AbsolutePath);
            if (string.IsNullOrEmpty(hint)) hint = $"{i + 1:D3}.jpg";

            items.Add(new ImageItem
            {
                Key = $"mature/{hash}/{hint}",
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
            Folder = $"{hash}_{StringUtils.Slugify(title, 40)}",
            Items = items,
            TotalExpected = items.Count
        };
    }

    #endregion

    #region Listing Crawlers

    private async Task<List<string>> CrawlPornPicsListingAsync(string url, HttpClientService client, int limit)
    {
        var seenGids = new HashSet<string>();
        var galleryUrls = new List<string>();
        int page = 1;
        int offset = 0;

        while (galleryUrls.Count < limit && page <= 50)
        {
            var sep = url.Contains('?') ? "&" : "?";
            var targetUrl = page == 1 ? url : $"{url}{sep}page={page}&offset={offset}";
            var html = await client.FetchTextAsync(targetUrl);
            if (string.IsNullOrWhiteSpace(html)) break;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var aNodes = doc.DocumentNode.SelectNodes("//a[contains(@href, '/galleries/')]");
            if (aNodes == null) break;

            int pageFound = 0;
            foreach (var a in aNodes)
            {
                var href = a.GetAttributeValue("href", "");
                var m = Regex.Match(href, @"/galleries/[^/]+?-(\d+)/?");
                if (!m.Success) continue;

                var gid = m.Groups[1].Value;
                if (seenGids.Add(gid))
                {
                    var full = new Uri(new Uri("https://www.pornpics.com"), href).AbsoluteUri;
                    if (!full.EndsWith("/")) full += "/";
                    galleryUrls.Add(full);
                    pageFound++;
                    if (galleryUrls.Count >= limit) break;
                }
            }

            if (pageFound == 0) break;
            offset += pageFound;
            page++;
        }

        return galleryUrls;
    }

    private async Task<List<string>> CrawlEromeListingAsync(string url, HttpClientService client, int limit)
    {
        var seenAids = new HashSet<string>();
        var albumUrls = new List<string>();

        for (int page = 1; page <= 20 && albumUrls.Count < limit; page++)
        {
            var targetUrl = page == 1 ? url : $"{url}&page={page}";
            var html = await client.FetchTextAsync(targetUrl);
            if (string.IsNullOrWhiteSpace(html)) break;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var aNodes = doc.DocumentNode.SelectNodes("//a[contains(@href, '/a/')]");
            if (aNodes == null) break;

            int pageFound = 0;
            foreach (var a in aNodes)
            {
                var href = a.GetAttributeValue("href", "");
                var match = Regex.Match(href, @"/a/([a-zA-Z0-9]+)");
                if (!match.Success) continue;

                var aid = match.Groups[1].Value;
                if (seenAids.Add(aid))
                {
                    albumUrls.Add(new Uri(new Uri("https://www.erome.com"), href).AbsoluteUri);
                    pageFound++;
                    if (albumUrls.Count >= limit) break;
                }
            }

            if (pageFound == 0) break;
        }

        return albumUrls;
    }

    private async Task<List<string>> CrawlBooruListingAsync(string url, HttpClientService client, int limit)
    {
        var postUrls = new List<string>();
        var html = await client.FetchTextAsync(url);
        if (string.IsNullOrWhiteSpace(html)) return postUrls;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var aNodes = doc.DocumentNode.SelectNodes("//a[contains(@href, 'page=post&s=view') or contains(@href, '/posts/')]");
        if (aNodes != null)
        {
            foreach (var a in aNodes)
            {
                var href = a.GetAttributeValue("href", "");
                if (!string.IsNullOrEmpty(href))
                {
                    var full = new Uri(new Uri(url), href).AbsoluteUri;
                    if (!postUrls.Contains(full))
                    {
                        postUrls.Add(full);
                        if (postUrls.Count >= limit) break;
                    }
                }
            }
        }

        return postUrls;
    }

    #endregion
}
