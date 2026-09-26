using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using QuarryApp.Models;

namespace QuarryApp.Core;

/// <summary>
/// Embedded zero-dependency HLS (.m3u8) stream downloader and assembler.
/// Extracts highest bandwidth master playlists and streams/assembles fMP4 / TS media fragments into playable MP4.
/// </summary>
public class HlsDownloader
{
    private readonly HttpClient _client;

    public HlsDownloader(HttpClient client)
    {
        _client = client;
    }

    public async Task<DownloadResultMeta> DownloadAsync(
        string playlistUrl,
        string destPath,
        string? referer = null,
        int concurrency = 8,
        long? speedLimitBytesPerSec = null,
        IProgress<(long downloaded, long total, string speed)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // 1. Fetch playlist content
        var playlistContent = await FetchTextWithHeadersAsync(playlistUrl, referer, cancellationToken);
        if (string.IsNullOrWhiteSpace(playlistContent))
        {
            throw new Exception("Failed to download HLS playlist.");
        }

        var currentUrl = playlistUrl;

        // 2. Check if Master Playlist (contains child variant streams)
        if (playlistContent.Contains("#EXT-X-STREAM-INF"))
        {
            var bestVariantUrl = SelectBestVariant(playlistContent, currentUrl);
            if (!string.IsNullOrEmpty(bestVariantUrl))
            {
                currentUrl = bestVariantUrl;
                playlistContent = await FetchTextWithHeadersAsync(currentUrl, referer ?? playlistUrl, cancellationToken);
            }
        }

        // 3. Parse segments and init fragments
        var segments = ParseSegments(playlistContent, currentUrl);
        if (segments.Count == 0)
        {
            throw new Exception("No playable video segments found in HLS playlist.");
        }

        var tempDest = destPath + ".qtmp";
        if (File.Exists(tempDest)) File.Delete(tempDest);

        long totalDownloaded = 0;
        var stopwatch = Stopwatch.StartNew();
        long lastReportedBytes = 0;
        long lastReportTimeMs = 0;

        using (var fileStream = new FileStream(tempDest, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var segUrl = segments[i];
                byte[] segBytes;

                using (var req = new HttpRequestMessage(HttpMethod.Get, segUrl))
                {
                    if (!string.IsNullOrEmpty(referer)) req.Headers.Referrer = new Uri(referer);
                    using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    resp.EnsureSuccessStatusCode();
                    segBytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
                }

                await fileStream.WriteAsync(segBytes.AsMemory(0, segBytes.Length), cancellationToken);
                totalDownloaded += segBytes.Length;

                var now = stopwatch.ElapsedMilliseconds;
                if (now - lastReportTimeMs > 350)
                {
                    lastReportTimeMs = now;
                    var delta = totalDownloaded - lastReportedBytes;
                    lastReportedBytes = totalDownloaded;
                    double speedKb = (delta / 1024.0) / 0.35;
                    var speedStr = speedKb >= 1024 ? $"{speedKb / 1024.0:F2} MB/s" : $"{speedKb:F0} KB/s";

                    // Estimated total based on segment count
                    var estTotal = (totalDownloaded / (i + 1)) * segments.Count;
                    progress?.Report((totalDownloaded, estTotal, speedStr));
                }

                if (speedLimitBytesPerSec.HasValue && speedLimitBytesPerSec.Value > 0)
                {
                    var expectedMs = (segBytes.Length * 1000.0) / speedLimitBytesPerSec.Value;
                    if (expectedMs > 1) await Task.Delay((int)expectedMs, cancellationToken);
                }
            }

            fileStream.Flush();
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tempDest, destPath);

        progress?.Report((totalDownloaded, totalDownloaded, "0 KB/s"));

        return new DownloadResultMeta
        {
            WrittenBytes = totalDownloaded,
            FinalDest = destPath
        };
    }

    private async Task<string> FetchTextWithHeadersAsync(string url, string? referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer)) req.Headers.Referrer = new Uri(referer);
        using var resp = await _client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private string? SelectBestVariant(string masterContent, string baseUrl)
    {
        var lines = masterContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        long maxBandwidth = -1;
        string? bestUri = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF:"))
            {
                long bw = 0;
                var bwMatch = Regex.Match(line, @"BANDWIDTH=(\d+)");
                if (bwMatch.Success && long.TryParse(bwMatch.Groups[1].Value, out var parsedBw))
                {
                    bw = parsedBw;
                }

                if (i + 1 < lines.Length)
                {
                    var nextLine = lines[i + 1].Trim();
                    if (!nextLine.StartsWith("#"))
                    {
                        if (bw > maxBandwidth || bestUri == null)
                        {
                            maxBandwidth = bw;
                            bestUri = nextLine;
                        }
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(bestUri))
        {
            if (!bestUri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try { return new Uri(new Uri(baseUrl), bestUri).AbsoluteUri; } catch { }
            }
            return bestUri;
        }

        return null;
    }

    private List<string> ParseSegments(string playlistContent, string baseUrl)
    {
        var segments = new List<string>();
        var lines = playlistContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        // Check for #EXT-X-MAP (initialization fragment in fMP4 HLS)
        foreach (var line in lines)
        {
            var trim = line.Trim();
            if (trim.StartsWith("#EXT-X-MAP:"))
            {
                var mapMatch = Regex.Match(trim, @"URI=""([^""]+)""");
                if (mapMatch.Success)
                {
                    var mapUri = mapMatch.Groups[1].Value;
                    if (!mapUri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        try { mapUri = new Uri(new Uri(baseUrl), mapUri).AbsoluteUri; } catch { }
                    }
                    segments.Add(mapUri);
                }
            }
        }

        // Add media fragments
        foreach (var line in lines)
        {
            var trim = line.Trim();
            if (string.IsNullOrWhiteSpace(trim) || trim.StartsWith("#")) continue;

            var segUrl = trim;
            if (!segUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try { segUrl = new Uri(new Uri(baseUrl), segUrl).AbsoluteUri; } catch { continue; }
            }
            segments.Add(segUrl);
        }

        return segments;
    }
}
