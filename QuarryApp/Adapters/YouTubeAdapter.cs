using System.IO;
using System.Text.RegularExpressions;
using QuarryApp.Core;
using QuarryApp.Models;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace QuarryApp.Adapters;

public class YouTubeAdapter : ISiteAdapter
{
    public string Site => "YouTube";

    public static readonly Regex YouTubeUrlRegex = new(
        @"(?:https?:\/\/)?(?:www\.)?(?:youtube\.com\/(?:watch\?v=|embed\/|shorts\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsYouTubeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return YouTubeUrlRegex.IsMatch(url) ||
               url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
    }

    public static string ExtractVideoId(string url)
    {
        var match = YouTubeUrlRegex.Match(url);
        return match.Success ? match.Groups[1].Value : url;
    }

    private readonly YoutubeClient _client = new();

    public bool Matches(string url) => IsYouTubeUrl(url);

    public bool IsListing(string url) => false;

    public Task<List<string>> CrawlGalleriesAsync(string url, HttpClientService client)
    {
        return Task.FromResult(new List<string> { url });
    }

    public async Task<Gallery> CollectAsync(string url, HttpClientService client, int? maxImages = null)
    {
        var videoId = ExtractVideoId(url);
        var video = await _client.Videos.GetAsync(videoId);
        var streamManifest = await _client.Videos.Streams.GetManifestAsync(videoId);

        var videoStream = streamManifest.GetVideoStreams().GetWithHighestVideoQuality();
        var audioStream = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();

        long totalBytes = (videoStream?.Size.Bytes ?? 0) + (videoStream is MuxedStreamInfo ? 0 : (audioStream?.Size.Bytes ?? 0));

        var cleanTitle = StringUtils.SanitizeFilename(video.Title, 80);
        var filename = $"{cleanTitle}.mp4";

        var item = new ImageItem
        {
            Key = video.Id.Value,
            Url = videoStream?.Url ?? url,
            FilenameHint = filename,
            Referer = "https://www.youtube.com",
            Bytes = totalBytes
        };

        return new Gallery
        {
            Site = "YouTube",
            Url = url,
            GalleryId = video.Id.Value,
            Title = video.Title,
            Folder = "Video",
            Items = new List<ImageItem> { item },
            TotalExpected = 1,
            TotalBytes = totalBytes
        };
    }
}
