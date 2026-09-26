using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using QuarryApp.Models;

namespace QuarryApp.Core;

public class ChunkRangeState
{
    public int Id { get; set; }
    public long InitialStart { get; set; }
    public long Start { get; set; }
    public long Current { get; set; }
    public long End { get; set; }
    public bool IsActive { get; set; }
    public bool IsDone => Current > End;
    public long Remaining => Math.Max(0, End - Current + 1);

    public readonly object Lock = new();
}

public class QPartMetadata
{
    public string Url { get; set; } = string.Empty;
    public long TotalLength { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public List<ChunkRangeState> Chunks { get; set; } = new();
}

public class MultiPartDownloader
{
    private readonly HttpClient _client;
    private const int BufferSize = 64 * 1024; // 64 KB pooled chunk buffers
    private const long MinStealDistance = 2 * 1024 * 1024; // 2 MB minimum remaining to split and steal

    public MultiPartDownloader(HttpClient client)
    {
        _client = client;
    }

    public async Task<DownloadResultMeta> DownloadAsync(
        string url,
        string destPath,
        string? referer = null,
        int numParts = 8,
        long? speedLimitBytesPerSec = null,
        IProgress<(long downloaded, long total, string speed)>? progress = null,
        Action<double[]>? chunkProgressCallback = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // 1. Probe server for Accept-Ranges, Content-Length, and ETag
        long totalLength = -1;
        bool supportsRanges = false;
        string? etag = null;
        string? lastModified = null;

        try
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
            if (!string.IsNullOrEmpty(referer)) headReq.Headers.Referrer = new Uri(referer);
            using var headResp = await _client.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (headResp.IsSuccessStatusCode)
            {
                totalLength = headResp.Content.Headers.ContentLength ?? -1;
                etag = headResp.Headers.ETag?.Tag;
                lastModified = headResp.Content.Headers.LastModified?.ToString();
                supportsRanges = headResp.Headers.AcceptRanges.Contains("bytes") ||
                                 headResp.Content.Headers.ContentRange != null ||
                                 totalLength > 1024 * 1024;
            }
        }
        catch { }

        // If HEAD didn't give content-length, do a quick Range test with 0-0
        if (totalLength <= 0)
        {
            try
            {
                using var rangeTestReq = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(referer)) rangeTestReq.Headers.Referrer = new Uri(referer);
                rangeTestReq.Headers.Range = new RangeHeaderValue(0, 0);

                using var rangeResp = await _client.SendAsync(rangeTestReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (rangeResp.StatusCode == HttpStatusCode.PartialContent && rangeResp.Content.Headers.ContentRange?.Length != null)
                {
                    totalLength = rangeResp.Content.Headers.ContentRange.Length.Value;
                    supportsRanges = true;
                    etag ??= rangeResp.Headers.ETag?.Tag;
                    lastModified ??= rangeResp.Content.Headers.LastModified?.ToString();
                }
            }
            catch { }
        }

        // 2. Decide download strategy: Dynamic Multi-Part with Work-Stealing vs Single Stream
        if (supportsRanges && totalLength > 1024 * 512 && numParts > 1)
        {
            return await DownloadDynamicWorkStealingAsync(
                url, destPath, referer, totalLength, numParts, etag, lastModified,
                speedLimitBytesPerSec, progress, chunkProgressCallback, cancellationToken);
        }
        else
        {
            return await DownloadSingleStreamAsync(url, destPath, referer, speedLimitBytesPerSec, progress, cancellationToken);
        }
    }

    private async Task<DownloadResultMeta> DownloadDynamicWorkStealingAsync(
        string url,
        string destPath,
        string? referer,
        long totalLength,
        int numParts,
        string? etag,
        string? lastModified,
        long? speedLimitBytesPerSec,
        IProgress<(long downloaded, long total, string speed)>? progress,
        Action<double[]>? chunkProgressCallback,
        CancellationToken cancellationToken)
    {
        var tempDest = destPath + ".qtmp";
        var partJournalPath = destPath + ".qpart";

        List<ChunkRangeState> chunks;

        // Try load existing .qpart journal if present and matches length
        var journal = LoadJournal(partJournalPath, totalLength, etag);
        if (journal != null && journal.Chunks.Count > 0 && File.Exists(tempDest))
        {
            chunks = journal.Chunks;
        }
        else
        {
            if (File.Exists(tempDest)) File.Delete(tempDest);
            if (File.Exists(partJournalPath)) File.Delete(partJournalPath);

            // Sparse pre-allocation on disk
            using (var fs = new FileStream(tempDest, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                fs.SetLength(totalLength);
            }

            chunks = new List<ChunkRangeState>();
            long partSize = totalLength / numParts;
            for (int i = 0; i < numParts; i++)
            {
                long start = i * partSize;
                long end = (i == numParts - 1) ? totalLength - 1 : (start + partSize - 1);
                chunks.Add(new ChunkRangeState
                {
                    Id = i + 1,
                    InitialStart = start,
                    Start = start,
                    Current = start,
                    End = end,
                    IsActive = false
                });
            }
        }

        long totalDownloaded = chunks.Sum(c => c.Current - c.Start);
        var stopwatch = Stopwatch.StartNew();
        long lastReportedBytes = totalDownloaded;
        long lastReportTimeMs = 0;
        long lastJournalSaveMs = 0;

        using var fileStream = new FileStream(tempDest, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, BufferSize, FileOptions.Asynchronous);

        var workerTasks = new List<Task>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var myChunk = chunks[i];
            workerTasks.Add(Task.Run(async () =>
            {
                var pool = ArrayPool<byte>.Shared;
                var buffer = pool.Rent(BufferSize);

                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        long currentStart;
                        long currentEnd;

                        lock (myChunk.Lock)
                        {
                            if (myChunk.IsDone)
                            {
                                // Steal mode: find slowest chunk with largest remaining byte distance
                                ChunkRangeState? bestVictim = null;
                                long maxRemaining = MinStealDistance;

                                foreach (var other in chunks)
                                {
                                    if (other == myChunk) continue;
                                    lock (other.Lock)
                                    {
                                        if (other.IsActive && other.Remaining > maxRemaining)
                                        {
                                            maxRemaining = other.Remaining;
                                            bestVictim = other;
                                        }
                                    }
                                }

                                if (bestVictim != null)
                                {
                                    lock (bestVictim.Lock)
                                    {
                                        var rem = bestVictim.Remaining;
                                        if (rem > MinStealDistance)
                                        {
                                            var half = rem / 2;
                                            var splitPoint = bestVictim.Current + half;
                                            var stolenStart = splitPoint + 1;
                                            var stolenEnd = bestVictim.End;

                                            bestVictim.End = splitPoint; // contract victim

                                            myChunk.Start = stolenStart;
                                            myChunk.Current = stolenStart;
                                            myChunk.End = stolenEnd;
                                            myChunk.IsActive = true;
                                        }
                                        else
                                        {
                                            break; // No chunk big enough to steal
                                        }
                                    }
                                }
                                else
                                {
                                    break; // Nothing left to steal, worker finishes
                                }
                            }

                            currentStart = myChunk.Current;
                            currentEnd = myChunk.End;
                            myChunk.IsActive = true;
                        }

                        if (currentStart > currentEnd) break;

                        // Connect and stream current range
                        try
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get, url);
                            if (!string.IsNullOrEmpty(referer)) req.Headers.Referrer = new Uri(referer);
                            req.Headers.Range = new RangeHeaderValue(currentStart, currentEnd);

                            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                            resp.EnsureSuccessStatusCode();

                            using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);

                            while (true)
                            {
                                int toRead = (int)Math.Min(buffer.Length, Math.Max(0, myChunk.End - myChunk.Current + 1));
                                if (toRead <= 0) break;

                                int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                                if (read <= 0) break;

                                // Write to pre-allocated file position
                                lock (fileStream)
                                {
                                    fileStream.Seek(myChunk.Current, SeekOrigin.Begin);
                                    fileStream.Write(buffer, 0, read);
                                }

                                lock (myChunk.Lock)
                                {
                                    myChunk.Current += read;
                                }

                                var curDownloaded = Interlocked.Add(ref totalDownloaded, read);

                                // Bandwidth speed regulator / throttler
                                if (speedLimitBytesPerSec.HasValue && speedLimitBytesPerSec.Value > 0)
                                {
                                    var expectedMs = (read * 1000.0) / (speedLimitBytesPerSec.Value / (double)chunks.Count);
                                    if (expectedMs > 1) await Task.Delay((int)expectedMs, cancellationToken);
                                }

                                // Progress reporting
                                var now = stopwatch.ElapsedMilliseconds;
                                if (now - lastReportTimeMs > 350)
                                {
                                    lastReportTimeMs = now;
                                    var delta = curDownloaded - lastReportedBytes;
                                    lastReportedBytes = curDownloaded;
                                    var dt = 0.35;
                                    double speedKb = (delta / 1024.0) / dt;
                                    var speedStr = speedKb >= 1024 ? $"{speedKb / 1024.0:F2} MB/s" : $"{speedKb:F0} KB/s";
                                    progress?.Report((curDownloaded, totalLength, speedStr));

                                    // Per-chunk progress percentages
                                    var pcts = chunks.Select(c =>
                                    {
                                        var span = c.End - c.Start + 1;
                                        if (span <= 0) return 100.0;
                                        return Math.Clamp(((double)(c.Current - c.Start) / span) * 100.0, 0, 100);
                                    }).ToArray();
                                    chunkProgressCallback?.Invoke(pcts);
                                }

                                // Periodic .qpart state journaling
                                if (now - lastJournalSaveMs > 2000)
                                {
                                    lastJournalSaveMs = now;
                                    SaveJournal(partJournalPath, url, totalLength, etag, lastModified, chunks);
                                }
                            }
                        }
                        catch (Exception) when (!cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(500, cancellationToken);
                        }
                    }
                }
                finally
                {
                    lock (myChunk.Lock) myChunk.IsActive = false;
                    pool.Return(buffer);
                }
            }, cancellationToken));
        }

        await Task.WhenAll(workerTasks);
        fileStream.Flush();
        fileStream.Close();

        // Download complete: remove .qpart journal and rename to final destination
        if (File.Exists(partJournalPath))
        {
            try { File.Delete(partJournalPath); } catch { }
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tempDest, destPath);

        // Compute SHA-256
        string sha256;
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(destPath))
        {
            sha256 = Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
        }

        progress?.Report((totalLength, totalLength, "0 KB/s"));

        return new DownloadResultMeta
        {
            WrittenBytes = totalLength,
            Sha256 = sha256,
            FinalDest = destPath
        };
    }

    private async Task<DownloadResultMeta> DownloadSingleStreamAsync(
        string url,
        string destPath,
        string? referer,
        long? speedLimitBytesPerSec,
        IProgress<(long downloaded, long total, string speed)>? progress,
        CancellationToken cancellationToken)
    {
        var tempDest = destPath + ".qtmp";
        if (File.Exists(tempDest)) File.Delete(tempDest);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(referer)) req.Headers.Referrer = new Uri(referer);

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;
        var stopwatch = Stopwatch.StartNew();
        long lastReportedBytes = 0;
        long lastReportTimeMs = 0;

        using (var contentStream = await resp.Content.ReadAsStreamAsync(cancellationToken))
        using (var fileStream = new FileStream(tempDest, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
        {
            var pool = ArrayPool<byte>.Shared;
            var buffer = pool.Rent(BufferSize);
            try
            {
                int read;
                while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloaded += read;

                    var now = stopwatch.ElapsedMilliseconds;
                    if (now - lastReportTimeMs > 400)
                    {
                        lastReportTimeMs = now;
                        var deltaBytes = downloaded - lastReportedBytes;
                        lastReportedBytes = downloaded;
                        double speedKb = (deltaBytes / 1024.0) / 0.4;
                        var speedStr = speedKb >= 1024 ? $"{speedKb / 1024.0:F2} MB/s" : $"{speedKb:F0} KB/s";
                        progress?.Report((downloaded, total, speedStr));
                    }

                    if (speedLimitBytesPerSec.HasValue && speedLimitBytesPerSec.Value > 0)
                    {
                        var expectedMs = (read * 1000.0) / speedLimitBytesPerSec.Value;
                        if (expectedMs > 1) await Task.Delay((int)expectedMs, cancellationToken);
                    }
                }
            }
            finally
            {
                pool.Return(buffer);
            }
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tempDest, destPath);

        string sha256;
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(destPath))
        {
            sha256 = Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
        }

        progress?.Report((downloaded, total, "0 KB/s"));

        return new DownloadResultMeta
        {
            WrittenBytes = downloaded,
            Sha256 = sha256,
            FinalDest = destPath
        };
    }

    private static QPartMetadata? LoadJournal(string path, long expectedLength, string? expectedEtag)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var meta = JsonSerializer.Deserialize<QPartMetadata>(json);
            if (meta != null && meta.TotalLength == expectedLength)
            {
                if (string.IsNullOrEmpty(expectedEtag) || meta.ETag == expectedEtag)
                {
                    return meta;
                }
            }
        }
        catch { }
        return null;
    }

    private static void SaveJournal(string path, string url, long totalLength, string? etag, string? lastModified, List<ChunkRangeState> chunks)
    {
        try
        {
            var meta = new QPartMetadata
            {
                Url = url,
                TotalLength = totalLength,
                ETag = etag,
                LastModified = lastModified,
                Chunks = chunks.Select(c => new ChunkRangeState
                {
                    Id = c.Id,
                    InitialStart = c.InitialStart,
                    Start = c.Start,
                    Current = c.Current,
                    End = c.End,
                    IsActive = c.IsActive
                }).ToList()
            };

            var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch { }
    }
}
