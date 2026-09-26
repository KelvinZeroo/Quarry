using System.IO;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using QuarryApp.Models;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace QuarryApp.Core;

public class MediaStreamExtractor
{
    private readonly HttpClientService _httpClient;
    private readonly YoutubeClient _youtubeClient = new();

    private static readonly Regex YouTubeRegex = new(
        @"(?:https?:\/\/)?(?:www\.)?(?:youtube\.com\/(?:watch\?v=|embed\/|shorts\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public MediaStreamExtractor(HttpClientService httpClient)
    {
        _httpClient = httpClient;
    }

    public bool IsSupportedVideoUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var lower = url.ToLowerInvariant();
        return YouTubeRegex.IsMatch(url) ||
               lower.Contains("youtube.com") ||
               lower.Contains("youtu.be") ||
               lower.Contains("xvideos.com") ||
               lower.Contains("xvideos.red") ||
               lower.Contains("xnxx.com") ||
               lower.Contains("pornhub.com") ||
               lower.Contains("xhamster.com") ||
               lower.Contains("spankbang.com") ||
               lower.Contains("eporner.com") ||
               lower.Contains("redtube.com") ||
               lower.Contains("youporn.com") ||
               lower.Contains("hqporner.com") ||
               lower.Contains("erome.com") ||
               lower.Contains("beeg.com") ||
               lower.Contains("daftsex.com") ||
               lower.Contains("tiktok.com") ||
               lower.Contains("twitter.com") ||
               lower.Contains("x.com") ||
               lower.Contains(".mp4") ||
               lower.Contains(".webm") ||
               lower.Contains(".m3u8");
    }

    public async Task<List<VideoStreamQuality>> ExtractQualitiesAsync(string url)
    {
        var qualities = new List<VideoStreamQuality>();

        // 1. YouTube Extraction (Supports 8K, 4K, 2K, 1080p, 720p, 480p, 360p, and Audio)
        if (YouTubeRegex.IsMatch(url) || url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var match = YouTubeRegex.Match(url);
                var videoId = match.Success ? match.Groups[1].Value : url;

                var streamManifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);

                // Best Audio Stream for DASH multiplexing (highest bitrate)
                var audioStreams = streamManifest.GetAudioOnlyStreams().OrderByDescending(s => s.Bitrate).ToList();
                var bestAudio = audioStreams.FirstOrDefault();
                var audioBytes = bestAudio?.Size.Bytes ?? 0;

                // Group all video streams by resolution height (4320, 2160, 1440, 1080, 720, 480, 360, 240, 144)
                var allVideoStreams = streamManifest.GetVideoStreams().ToList();
                var heightGroups = allVideoStreams
                    .GroupBy(s => s.VideoQuality.MaxHeight)
                    .OrderByDescending(g => g.Key);

                foreach (var group in heightGroups)
                {
                    var height = group.Key;
                    // ALWAYS prioritize highest bitrate for maximum quality and true file size
                    var bestVideoStream = group
                        .OrderByDescending(s => s.Bitrate)
                        .ThenByDescending(s => s.Container == Container.Mp4)
                        .First();

                    var isMuxed = bestVideoStream is MuxedStreamInfo;
                    var totalBytes = isMuxed ? bestVideoStream.Size.Bytes : (bestVideoStream.Size.Bytes + audioBytes);

                    var labelPrefix = height switch
                    {
                        >= 4320 => $"8K UHD ({height}p)",
                        >= 2160 => $"4K UHD ({height}p)",
                        >= 1440 => $"2K QHD ({height}p)",
                        >= 1080 => $"1080p Full HD",
                        >= 720 => $"720p HD",
                        >= 480 => $"480p SD",
                        _ => $"{height}p"
                    };

                    qualities.Add(new VideoStreamQuality
                    {
                        QualityLabel = $"{labelPrefix} - MP4 (Video+Audio)",
                        Resolution = $"{bestVideoStream.VideoResolution.Width}x{bestVideoStream.VideoResolution.Height}",
                        TargetHeight = height,
                        DirectUrl = bestVideoStream.Url,
                        Extension = ".mp4",
                        ContentLength = totalBytes,
                        IsAudioOnly = false,
                        IsMuxed = isMuxed
                    });
                }

                // Add Audio Only option
                if (bestAudio != null)
                {
                    qualities.Add(new VideoStreamQuality
                    {
                        QualityLabel = $"Audio Only ({bestAudio.Bitrate.KiloBitsPerSecond:F0} kbps) - M4A",
                        Resolution = "Audio",
                        TargetHeight = 0,
                        DirectUrl = bestAudio.Url,
                        Extension = "." + bestAudio.Container.Name,
                        ContentLength = bestAudio.Size.Bytes,
                        IsAudioOnly = true,
                        IsMuxed = false
                    });
                }

                if (qualities.Count > 0) return qualities;
            }
            catch { }
        }

        // 2. Adult / Mature Video Platforms (XVideos, PornHub, XHamster, SpankBang, Eporner, etc.)
        var matureAdapter = new Adapters.MatureMediaAdapter();
        if (matureAdapter.Matches(url))
        {
            try
            {
                var gallery = await matureAdapter.CollectAsync(url, _httpClient);
                if (gallery.Items.Count == 1 && (gallery.Folder == "videos" || gallery.Items[0].Url.Contains(".mp4") || gallery.Items[0].Url.Contains(".m3u8") || gallery.Items[0].FilenameHint?.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) == true))
                {
                    var item = gallery.Items[0];
                    var ext = Path.GetExtension((item.FilenameHint ?? "download.mp4").Split('?')[0]);
                    if (string.IsNullOrEmpty(ext) || ext == ".m3u8") ext = ".mp4";

                    qualities.Add(new VideoStreamQuality
                    {
                        QualityLabel = $"{gallery.Title} (HD Video Stream)",
                        Resolution = "HD",
                        DirectUrl = item.Url,
                        Extension = ext
                    });
                    return qualities;
                }
            }
            catch { }
        }

        // 3. Generic HTML5 video tag parsing
        try
        {
            var html = await _httpClient.FetchTextAsync(url);
            if (!string.IsNullOrEmpty(html))
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var videoNodes = doc.DocumentNode.SelectNodes("//video//source[@src] | //video[@src]");
                if (videoNodes != null)
                {
                    foreach (var node in videoNodes)
                    {
                        var src = node.GetAttributeValue("src", "");
                        if (!string.IsNullOrEmpty(src))
                        {
                            var fullUrl = new Uri(new Uri(url), src).AbsoluteUri;
                            var label = node.GetAttributeValue("label", node.GetAttributeValue("title", "Standard Quality"));
                            var res = node.GetAttributeValue("res", "720p");

                            qualities.Add(new VideoStreamQuality
                            {
                                QualityLabel = label,
                                Resolution = res,
                                DirectUrl = fullUrl,
                                Extension = Path.GetExtension(fullUrl.Split('?')[0])
                            });
                        }
                    }
                }
            }
        }
        catch { }

        // Fallback default stream
        if (qualities.Count == 0)
        {
            qualities.Add(new VideoStreamQuality
            {
                QualityLabel = "Best Quality (Direct)",
                Resolution = "Original",
                DirectUrl = url,
                Extension = ".mp4"
            });
        }

        return qualities;
    }

    private async Task<List<VideoStreamQuality>> ExtractEromeVideoQualitiesAsync(string url)
    {
        var list = new List<VideoStreamQuality>();
        try
        {
            var html = await _httpClient.FetchTextAsync(url);
            if (string.IsNullOrEmpty(html)) return list;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var videoNodes = doc.DocumentNode.SelectNodes("//video//source[@src] | //video[@src]");
            if (videoNodes != null)
            {
                foreach (var node in videoNodes)
                {
                    var src = node.GetAttributeValue("src", "");
                    if (!string.IsNullOrEmpty(src))
                    {
                        var fullUrl = src.StartsWith("http") ? src : "https:" + src;
                        var label = node.GetAttributeValue("label", "1080p HD Video");
                        var res = node.GetAttributeValue("res", "1080p");

                        list.Add(new VideoStreamQuality
                        {
                            QualityLabel = label,
                            Resolution = res,
                            DirectUrl = fullUrl,
                            Extension = ".mp4"
                        });
                    }
                }
            }
        }
        catch { }

        return list;
    }
}
