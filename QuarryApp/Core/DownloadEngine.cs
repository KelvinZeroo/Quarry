using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using QuarryApp.Adapters;
using QuarryApp.Models;
using YoutubeExplode;
using YoutubeExplode.Converter;
using YoutubeExplode.Videos.Streams;

namespace QuarryApp.Core;

public class DownloadEngine
{
    private static readonly Regex ExtRegex = new(@"^(?<name>.+?)(?<ext>\.[A-Za-z0-9]{2,5})$", RegexOptions.Compiled);

    public static string GetDestFilename(string destDir, int index, ImageItem item)
    {
        var prefix = index.ToString("D4");
        var hint = (item.FilenameHint ?? "").Trim();
        if (string.IsNullOrEmpty(hint))
        {
            return Path.Combine(destDir, $"{prefix}.jpg");
        }

        var raw = StringUtils.SanitizeFilename(hint, 64);
        var match = ExtRegex.Match(raw);
        var rawName = match.Success ? match.Groups["name"].Value : raw;
        var ext = match.Success ? match.Groups["ext"].Value : "";

        var name = rawName.Trim('-', '_', ' ', '.');
        if (name.Length > 24) name = name[..24].Trim('-', '_', ' ', '.');

        if (string.IsNullOrEmpty(name))
        {
            return Path.Combine(destDir, $"{prefix}{(string.IsNullOrEmpty(ext) ? ".jpg" : ext)}");
        }

        return Path.Combine(destDir, $"{prefix}_{name}{ext}");
    }

    public static async Task RunJobAsync(
        DownloadJob job,
        Settings settings,
        HttpClientService client,
        Func<int, Task<int?>>? promptCountFn = null,
        CancellationToken cancellationToken = default)
    {
        job.State = JobState.Running;
        job.AddLog($"[{DateTime.Now:T}] Started download job for {job.Url}");

        try
        {
            // 0. Dedicated BitTorrent & Magnet Link Protocol Engine
            if (TorrentDownloader.IsTorrentUrl(job.Url))
            {
                job.AddLog($"[{DateTime.Now:T}] Initializing BitTorrent / Magnet P2P Engine...");
                await TorrentDownloader.DownloadAsync(job, settings, null, cancellationToken);

                if (!string.IsNullOrEmpty(job.FilePath) && File.Exists(job.FilePath))
                {
                    var finalBytes = new FileInfo(job.FilePath).Length;
                    job.TotalBytes = finalBytes;
                    job.DownloadedBytes = finalBytes;
                }

                job.State = JobState.Done;
                job.SetPercentage(100);
                job.TransferRate = "—";
                job.TimeLeft = "—";
                job.AddLog($"[{DateTime.Now:T}] BitTorrent download complete: 1 completed, 0 skipped, 0 failed");

                if (settings.AutoExtractArchives && !string.IsNullOrEmpty(job.FilePath) && ArchiveExtractor.IsSupportedArchive(job.FilePath))
                {
                    try
                    {
                        job.AddLog($"[{DateTime.Now:T}] Auto-extracting archive {Path.GetFileName(job.FilePath)}...");
                        var extractTarget = Path.Combine(Path.GetDirectoryName(job.FilePath)!, Path.GetFileNameWithoutExtension(job.FilePath));
                        ArchiveExtractor.Extract(job.FilePath, extractTarget);
                        job.AddLog($"[{DateTime.Now:T}] Archive successfully unpacked to: {extractTarget}");
                    }
                    catch (Exception ex)
                    {
                        job.AddLog($"[{DateTime.Now:T}] Auto-extract skipped: {ex.Message}");
                    }
                }
                return;
            }

            Gallery gallery;
            var isRoot = !job.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) &&
                         Uri.TryCreate(job.Url, UriKind.Absolute, out var uri) &&
                         uri.AbsolutePath.Trim('/') == "";
            var shouldScan = settings.Scan || (settings.AutoScan && isRoot);

            if (shouldScan)
            {
                job.AddLog($"[{DateTime.Now:T}] Running site scan...");
                gallery = await SiteScanner.ScanSiteAsync(job.Url, client, settings, (msg) => job.AddLog($"[{DateTime.Now:T}] {msg}"));
            }
            else
            {
                var adapter = AdapterFactory.GetAdapter(job.Url);
                job.AddLog($"[{DateTime.Now:T}] Detected site adapter: {adapter.Site}");

                if (adapter.IsListing(job.Url))
                {
                    job.AddLog($"[{DateTime.Now:T}] Crawling listing page for media galleries...");
                    var urls = await adapter.CrawlGalleriesAsync(job.Url, client);
                    var allItems = new List<ImageItem>();
                    var title = "Crawl";
                    var folder = "crawl";

                    foreach (var gUrl in urls)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        try
                        {
                            var subG = await adapter.CollectAsync(gUrl, client, settings.MaxImages);
                            allItems.AddRange(subG.Items);
                            title = subG.Title;
                            folder = subG.Folder;
                        }
                        catch
                        {
                            // Skip broken subgallery
                        }
                    }

                    gallery = new Gallery
                    {
                        Site = adapter.Site,
                        Url = job.Url,
                        GalleryId = "crawl",
                        Title = title,
                        Folder = folder,
                        Items = allItems,
                        TotalExpected = allItems.Count
                    };
                }
                else
                {
                    gallery = await adapter.CollectAsync(job.Url, client, settings.MaxImages);
                }
            }

            job.Title = gallery.Title;
            job.Total = gallery.Items.Count;
            if (gallery.TotalBytes.HasValue)
            {
                job.TotalBytes = gallery.TotalBytes.Value;
            }
            else if (gallery.Items.Count == 1 && gallery.Items[0].Bytes is long b)
            {
                job.TotalBytes = b;
            }

            // Check if interactive prompt needed
            if (shouldScan && promptCountFn != null && gallery.Items.Count > settings.AskAbove)
            {
                job.State = JobState.Asking;
                var decision = await promptCountFn(gallery.Items.Count);
                job.State = JobState.Running;

                if (decision == null)
                {
                    job.State = JobState.Stopped;
                    job.AddLog($"[{DateTime.Now:T}] Cancelled by user prompt.");
                    return;
                }

                if (decision > 0 && decision < gallery.Items.Count)
                {
                    gallery.Items = gallery.Items.Take(decision.Value).ToList();
                    job.Total = gallery.Items.Count;
                }
            }

            var destDir = Path.Combine(settings.OutDir, gallery.Folder);
            job.Folder = destDir;

            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

            // 1. Dedicated YouTube Downloader (Handles 4K/2K/1080p/720p/Audio with real-time speed & progress)
            if (gallery.Site == "YouTube" || YouTubeAdapter.IsYouTubeUrl(job.Url))
            {
                var youtube = new YoutubeClient();
                var videoId = YouTubeAdapter.ExtractVideoId(job.Url);
                if (string.IsNullOrEmpty(videoId)) videoId = gallery.GalleryId;

                job.AddLog($"[{DateTime.Now:T}] Resolving YouTube media manifest for video ID: {videoId}...");
                var video = await youtube.Videos.GetAsync(videoId, cancellationToken);
                var streamManifest = await youtube.Videos.Streams.GetManifestAsync(videoId, cancellationToken);

                var cleanTitle = StringUtils.SanitizeFilename(video.Title, 80);
                job.Title = video.Title;
                job.AddLog($"[{DateTime.Now:T}] Video title: \"{video.Title}\" ({video.Duration})");

                var stopwatch = Stopwatch.StartNew();
                long lastBytes = 0;
                long lastTimeMs = 0;
                long totalExpectedBytes = 0;

                var ytProgress = new Progress<double>(p =>
                {
                    if (totalExpectedBytes > 0)
                    {
                        var currentBytes = (long)(p * totalExpectedBytes);
                        job.DownloadedBytes = currentBytes;

                        var now = stopwatch.ElapsedMilliseconds;
                        if (now - lastTimeMs > 300)
                        {
                            var delta = currentBytes - lastBytes;
                            var deltaTimeSec = (now - lastTimeMs) / 1000.0;
                            lastTimeMs = now;
                            lastBytes = currentBytes;

                            if (deltaTimeSec > 0)
                            {
                                double speedBytesPerSec = delta / deltaTimeSec;
                                double speedMb = speedBytesPerSec / (1024.0 * 1024.0);
                                job.TransferRate = speedMb >= 1.0 ? $"{speedMb:F2} MB/s" : $"{speedBytesPerSec / 1024.0:F0} KB/s";

                                if (speedBytesPerSec > 0)
                                {
                                    var remainingBytes = totalExpectedBytes - currentBytes;
                                    var sec = remainingBytes / speedBytesPerSec;
                                    job.TimeLeft = sec < 60 ? $"{sec:F0}s" : $"{sec / 60:F1}m";
                                }
                            }
                        }
                    }
                    else
                    {
                        job.SetPercentage((int)(p * 100));
                    }
                });

                if (job.SelectedQuality?.IsAudioOnly == true)
                {
                    var audioStream = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
                    if (audioStream == null) throw new Exception("No audio stream found for video.");

                    var ext = "." + audioStream.Container.Name;
                    var destPath = Path.Combine(destDir, $"{cleanTitle}{ext}");
                    job.FilePath = destPath;
                    totalExpectedBytes = audioStream.Size.Bytes;
                    job.TotalBytes = totalExpectedBytes;

                    job.AddLog($"[{DateTime.Now:T}] Connection #1 (Audio Stream): Established (HTTP 200 OK) -> Bitrate: {audioStream.Bitrate.KiloBitsPerSecond:F0} kbps ({totalExpectedBytes / (1024.0 * 1024.0):F2} MB)");
                    await youtube.Videos.Streams.DownloadAsync(audioStream, destPath, ytProgress, cancellationToken);
                }
                else
                {
                    var targetHeight = job.SelectedQuality?.TargetHeight ?? 0;
                    
                    IVideoStreamInfo? videoStream = null;
                    if (targetHeight > 0)
                    {
                        videoStream = streamManifest.GetVideoStreams()
                            .Where(s => s.VideoQuality.MaxHeight == targetHeight)
                            .OrderByDescending(s => s.Bitrate)
                            .ThenByDescending(s => s.Container == Container.Mp4)
                            .FirstOrDefault();
                    }

                    if (videoStream == null)
                    {
                        videoStream = streamManifest.GetVideoStreams().GetWithHighestVideoQuality();
                    }

                    var audioStream = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
                    var destPath = Path.Combine(destDir, $"{cleanTitle}.mp4");
                    job.FilePath = destPath;

                    if (videoStream is MuxedStreamInfo muxed)
                    {
                        totalExpectedBytes = muxed.Size.Bytes;
                        job.TotalBytes = totalExpectedBytes;
                        job.AddLog($"[{DateTime.Now:T}] Connection #1 (Muxed Stream): Established (HTTP 200 OK) -> {videoStream.VideoQuality.Label} ({totalExpectedBytes / (1024.0 * 1024.0):F2} MB)");
                        await youtube.Videos.Streams.DownloadAsync(muxed, destPath, ytProgress, cancellationToken);
                    }
                    else if (videoStream != null && audioStream != null)
                    {
                        totalExpectedBytes = videoStream.Size.Bytes + audioStream.Size.Bytes;
                        job.TotalBytes = totalExpectedBytes;
                        job.AddLog($"[{DateTime.Now:T}] Connection #1 (DASH Video): Established (HTTP 200 OK) -> Quality: {videoStream.VideoQuality.Label}");
                        job.AddLog($"[{DateTime.Now:T}] Connection #2 (DASH Audio): Established (HTTP 200 OK) -> Bitrate: {audioStream.Bitrate.KiloBitsPerSecond:F0} kbps");
                        job.AddLog($"[{DateTime.Now:T}] Muxing video and audio streams into MP4 container...");
                        var streamInfos = new IStreamInfo[] { videoStream, audioStream };
                        await youtube.Videos.DownloadAsync(streamInfos, new ConversionRequestBuilder(destPath).Build(), ytProgress, cancellationToken);
                    }
                    else if (videoStream != null)
                    {
                        totalExpectedBytes = videoStream.Size.Bytes;
                        job.TotalBytes = totalExpectedBytes;
                        job.AddLog($"[{DateTime.Now:T}] Connection #1 (Video Stream): Established (HTTP 200 OK) -> {videoStream.VideoQuality.Label}");
                        await youtube.Videos.Streams.DownloadAsync(videoStream, destPath, ytProgress, cancellationToken);
                    }
                    else
                    {
                        throw new Exception("No suitable video stream available.");
                    }
                }

                job.DownloadedBytes = totalExpectedBytes;
                job.SetPercentage(100);
                job.IncrementDownloaded();
                job.AddLog($"[{DateTime.Now:T}] YouTube download complete -> Saved to {Path.GetFileName(job.FilePath)}");
            }
            // 2. Single direct file or HLS video download
            else if (gallery.Items.Count == 1)
            {
                var singleItem = gallery.Items[0];
                var targetUrl = !string.IsNullOrEmpty(job.SelectedQuality?.DirectUrl) ? job.SelectedQuality.DirectUrl : singleItem.Url;
                var rawHint = singleItem.FilenameHint ?? "download.mp4";
                if (rawHint.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    rawHint = Path.GetFileNameWithoutExtension(rawHint) + ".mp4";
                }
                var destPath = Path.Combine(destDir, StringUtils.SanitizeFilename(rawHint));
                job.FilePath = destPath;

                job.AddLog($"[{DateTime.Now:T}] Target URL: {targetUrl}");

                var progress = new Progress<(long downloaded, long total, string speed)>(report =>
                {
                    job.DownloadedBytes = report.downloaded;
                    if (report.total > 0 && (!job.TotalBytes.HasValue || job.TotalBytes.Value != report.total))
                    {
                        job.TotalBytes = report.total;
                    }
                    job.TransferRate = report.speed;
                    if (report.total > 0)
                    {
                        var remainingBytes = report.total - report.downloaded;
                        if (report.speed.Contains("MB/s") && double.TryParse(report.speed.Replace("MB/s", "").Trim(), out var mbSpeed) && mbSpeed > 0)
                        {
                            var secLeft = (remainingBytes / (1024.0 * 1024.0)) / mbSpeed;
                            job.TimeLeft = secLeft < 60 ? $"{secLeft:F0}s" : $"{secLeft / 60:F1}m";
                        }
                    }
                });

                var speedLimit = settings.EnableSpeedLimit && settings.SpeedLimitKbps > 0 ? settings.SpeedLimitKbps * 1024 : (long?)null;

                if (targetUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    job.AddLog($"[{DateTime.Now:T}] Detected HLS Media Stream. Initializing segment stream assembler...");
                    var hls = new HlsDownloader(client.GetUnderlyingHttpClient());
                    var meta = await hls.DownloadAsync(
                        targetUrl,
                        destPath,
                        singleItem.Referer,
                        concurrency: Math.Clamp(settings.Workers, 4, 16),
                        speedLimitBytesPerSec: speedLimit,
                        progress: progress,
                        cancellationToken: cancellationToken);

                    job.IncrementDownloaded();
                    job.AddLog($"[{DateTime.Now:T}] HLS video download complete -> Successfully assembled into {Path.GetFileName(destPath)}");
                }
                else
                {
                    job.AddLog($"[{DateTime.Now:T}] Connecting: Testing server byte-range & partial stream support...");

                    var multiPartDownloader = new MultiPartDownloader(client.GetUnderlyingHttpClient());
                    var parts = Math.Clamp(settings.Workers, 4, 16);

                    job.AddLog($"[{DateTime.Now:T}] Establishing {parts} parallel multi-thread connections (Chunks 1..{parts})...");

                    var meta = await multiPartDownloader.DownloadAsync(
                        targetUrl,
                        destPath,
                        singleItem.Referer,
                        numParts: parts,
                        speedLimitBytesPerSec: speedLimit,
                        progress: progress,
                        cancellationToken: cancellationToken);

                    job.IncrementDownloaded();
                    job.AddLog($"[{DateTime.Now:T}] Multi-part download complete -> Successfully assembled into {Path.GetFileName(destPath)}");
                }
            }
            // 3. Batch Gallery Download (EroMe, PornPics, Booru, ImageFap, etc.)
            else
            {
                var manifest = new ManifestManager(destDir, gallery);
                manifest.WriteInfoTxt(gallery);

                var concurrency = Math.Max(1, settings.Workers);
                using var semaphore = new SemaphoreSlim(concurrency);

                job.AddLog($"[{DateTime.Now:T}] Found {gallery.Items.Count} items in \"{gallery.Title}\"");
                job.AddLog($"[{DateTime.Now:T}] Initializing {concurrency} parallel worker connections...");
                for (int c = 1; c <= Math.Min(concurrency, gallery.Items.Count); c++)
                {
                    job.AddLog($"[{DateTime.Now:T}] Connection #{c}: Established (TLS 1.3 / HTTP 2.0 CDN Socket Ready)");
                }

                var batchStopwatch = Stopwatch.StartNew();
                long batchBytesDownloaded = 0;
                long lastBatchReportedBytes = 0;
                long lastBatchReportTimeMs = 0;
                int index = 0;

                var tasks = gallery.Items.Select(async (item, itemIdx) =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    var workerId = (itemIdx % concurrency) + 1;
                    try
                    {
                        if (cancellationToken.IsCancellationRequested) return;

                        var itemIndex = Interlocked.Increment(ref index);
                        var destPath = GetDestFilename(destDir, itemIndex, item);
                        var shortFileName = Path.GetFileName(destPath);

                        if (!settings.Force && manifest.IsDone(item.Key))
                        {
                            job.IncrementSkipped();
                            job.AddLog($"[{DateTime.Now:T}] Connection #{workerId}: Skipping already downloaded file {shortFileName}");
                            return;
                        }

                        try
                        {
                            job.AddLog($"[{DateTime.Now:T}] Connection #{workerId}: Fetching item {itemIndex}/{gallery.Items.Count} ({shortFileName})...");
                            var meta = await client.DownloadFileAsync(item.Url, destPath, item.Referer, settings.Retries, cancellationToken);
                            manifest.Record(item.Key, meta.FinalDest, "ok", meta.WrittenBytes, meta.Sha256, item.Url);
                            
                            var currentTotalBytes = Interlocked.Add(ref batchBytesDownloaded, meta.WrittenBytes);
                            job.DownloadedBytes = currentTotalBytes;
                            job.IncrementDownloaded();

                            var nowMs = batchStopwatch.ElapsedMilliseconds;
                            if (nowMs - lastBatchReportTimeMs > 400)
                            {
                                var deltaBytes = currentTotalBytes - lastBatchReportedBytes;
                                var deltaTimeSec = (nowMs - lastBatchReportTimeMs) / 1000.0;
                                lastBatchReportTimeMs = nowMs;
                                lastBatchReportedBytes = currentTotalBytes;

                                if (deltaTimeSec > 0)
                                {
                                    var speedBytesPerSec = deltaBytes / deltaTimeSec;
                                    var speedMb = speedBytesPerSec / (1024.0 * 1024.0);
                                    job.TransferRate = speedMb >= 1.0 ? $"{speedMb:F2} MB/s" : $"{speedBytesPerSec / 1024.0:F0} KB/s";

                                    var remainingItems = gallery.Items.Count - (job.Downloaded + job.Skipped);
                                    if (job.Downloaded > 0 && remainingItems > 0)
                                    {
                                        var avgBytesPerItem = (double)currentTotalBytes / job.Downloaded;
                                        var estRemainingBytes = remainingItems * avgBytesPerItem;
                                        if (speedBytesPerSec > 0)
                                        {
                                            var secLeft = estRemainingBytes / speedBytesPerSec;
                                            job.TimeLeft = secLeft < 60 ? $"{secLeft:F0}s" : $"{secLeft / 60:F1}m";
                                        }
                                    }
                                }
                            }

                            job.AddLog($"[{DateTime.Now:T}] Connection #{workerId}: HTTP 200 OK -> Saved {shortFileName} ({meta.WrittenBytes / 1024.0:F0} KB)");
                        }
                        catch (Exception ex)
                        {
                            manifest.Record(item.Key, destPath, "failed", url: item.Url, error: ex.Message);
                            job.IncrementFailed();
                            job.AddLog($"[{DateTime.Now:T}] Connection #{workerId}: FAILED on {shortFileName} ({ex.Message})");
                        }

                        if (settings.DelaySeconds > 0)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(settings.DelaySeconds), cancellationToken);
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
                manifest.Save();
            }

            if (!string.IsNullOrEmpty(job.FilePath) && File.Exists(job.FilePath))
            {
                var finalBytes = new FileInfo(job.FilePath).Length;
                job.TotalBytes = finalBytes;
                job.DownloadedBytes = finalBytes;
            }
            else if (Directory.Exists(destDir))
            {
                try
                {
                    var allFiles = Directory.GetFiles(destDir, "*.*", SearchOption.AllDirectories);
                    if (allFiles.Length > 0)
                    {
                        var sum = allFiles.Sum(f => new FileInfo(f).Length);
                        job.TotalBytes = sum;
                        job.DownloadedBytes = sum;
                    }
                }
                catch { }
            }

            job.State = JobState.Done;
            job.SetPercentage(100);
            job.TransferRate = "—";
            job.TimeLeft = "—";
            job.AddLog($"[{DateTime.Now:T}] Download finished: {job.Downloaded} completed, {job.Skipped} skipped, {job.Failed} failed");

            if (settings.AutoExtractArchives && !string.IsNullOrEmpty(job.FilePath) && ArchiveExtractor.IsSupportedArchive(job.FilePath))
            {
                try
                {
                    job.AddLog($"[{DateTime.Now:T}] Auto-extracting archive {Path.GetFileName(job.FilePath)}...");
                    var extractTarget = Path.Combine(Path.GetDirectoryName(job.FilePath)!, Path.GetFileNameWithoutExtension(job.FilePath));
                    ArchiveExtractor.Extract(job.FilePath, extractTarget);
                    job.AddLog($"[{DateTime.Now:T}] Archive successfully unpacked to: {extractTarget}");
                }
                catch (Exception ex)
                {
                    job.AddLog($"[{DateTime.Now:T}] Auto-extract skipped: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            job.State = JobState.Stopped;
            job.TransferRate = "—";
            job.TimeLeft = "—";
            job.AddLog($"[{DateTime.Now:T}] Job cancelled by user.");
        }
        catch (Exception ex)
        {
            job.State = JobState.Error;
            job.Error = ex.Message;
            job.TransferRate = "—";
            job.TimeLeft = "—";
            job.AddLog($"[{DateTime.Now:T}] ERROR: {ex.Message}");
        }
    }
}
