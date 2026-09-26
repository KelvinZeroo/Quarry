using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace QuarryApp.Core;

public class DownloadResultMeta
{
    public long WrittenBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string FinalDest { get; set; } = string.Empty;
}

public class HttpClientService
{
    private readonly HttpClient _client;
    private readonly CookieContainer _cookieContainer;

    private static readonly Dictionary<string, string> MimeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/jpg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/avif"] = ".avif",
        ["image/bmp"] = ".bmp",
        ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm",
        ["video/quicktime"] = ".mov",
        ["video/mp2t"] = ".ts",
    };

    public HttpClientService()
    {
        _cookieContainer = new CookieContainer();

        try
        {
            _cookieContainer.Add(new Cookie("age_verified", "1", "/", ".pornhub.com"));
            _cookieContainer.Add(new Cookie("accessAgeDisclaimerPH", "1", "/", ".pornhub.com"));
            _cookieContainer.Add(new Cookie("platform", "pc", "/", ".pornhub.com"));
            _cookieContainer.Add(new Cookie("hasVisited", "1", "/", ".pornhub.com"));
            _cookieContainer.Add(new Cookie("age_verified", "1", "/", ".pornhubpremium.com"));
            _cookieContainer.Add(new Cookie("accessAgeDisclaimerPH", "1", "/", ".pornhubpremium.com"));
            _cookieContainer.Add(new Cookie("age_verified", "1", "/", ".xhamster.com"));
            _cookieContainer.Add(new Cookie("has_visited", "1", "/", ".xhamster.com"));
            _cookieContainer.Add(new Cookie("age_verified", "1", "/", ".xvideos.com"));
            _cookieContainer.Add(new Cookie("age_verified", "1", "/", ".xnxx.com"));
            _cookieContainer.Add(new Cookie("age_verified", "1", "/", ".spankbang.com"));
            _cookieContainer.Add(new Cookie("age_verified", "1", "/", ".eporner.com"));
        }
        catch { }

        var handler = new SocketsHttpHandler
        {
            CookieContainer = _cookieContainer,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }
        };

        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        _client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        _client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        _client.DefaultRequestHeaders.Add("Sec-Ch-Ua", "\"Not(A:Brand\";v=\"99\", \"Google Chrome\";v=\"133\", \"Chromium\";v=\"133\"");
        _client.DefaultRequestHeaders.Add("Sec-Ch-Ua-Mobile", "?0");
        _client.DefaultRequestHeaders.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
        _client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
        _client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
        _client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
        _client.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
        _client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
    }

    public HttpClient GetUnderlyingHttpClient() => _client;

    public async Task<string?> FetchTextAsync(string url, string? referer = null, int retries = 3)
    {
        for (int i = 1; i <= retries; i++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(referer))
                {
                    request.Headers.Referrer = new Uri(referer);
                }

                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound) return null;
                    if (i < retries)
                    {
                        await Task.Delay(1000 * i);
                        continue;
                    }
                    return null;
                }

                return await response.Content.ReadAsStringAsync();
            }
            catch
            {
                if (i >= retries) return null;
                await Task.Delay(1000 * i);
            }
        }
        return null;
    }

    public async Task<DownloadResultMeta> DownloadFileAsync(
        string url,
        string destPath,
        string? referer = null,
        int retries = 3,
        CancellationToken cancellationToken = default)
    {
        for (int i = 1; i <= retries; i++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(referer))
                {
                    request.Headers.Referrer = new Uri(referer);
                }

                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var contentType = response.Content.Headers.ContentType?.MediaType?.ToLower() ?? "";
                var finalDest = destPath;

                if (!string.IsNullOrEmpty(contentType) && MimeMap.TryGetValue(contentType, out var expectedExt))
                {
                    var currentExt = Path.GetExtension(destPath).ToLower();
                    if (currentExt != expectedExt && (string.IsNullOrEmpty(currentExt) || currentExt == ".jpg" || currentExt == ".tmp"))
                    {
                        var dir = Path.GetDirectoryName(destPath) ?? "";
                        var baseName = Path.GetFileNameWithoutExtension(destPath);
                        finalDest = Path.Combine(dir, $"{baseName}{expectedExt}");
                    }
                }

                var dirToCreate = Path.GetDirectoryName(finalDest);
                if (!string.IsNullOrEmpty(dirToCreate) && !Directory.Exists(dirToCreate))
                {
                    Directory.CreateDirectory(dirToCreate);
                }

                var tempDest = $"{finalDest}.tmp.{Guid.NewGuid():N}";
                long writtenBytes = 0;
                string sha256Hash;

                using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                using (var fileStream = new FileStream(tempDest, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[65536];
                    int read;
                    while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        writtenBytes += read;
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    sha256Hash = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLower();
                }

                if (File.Exists(finalDest)) File.Delete(finalDest);
                File.Move(tempDest, finalDest);

                return new DownloadResultMeta
                {
                    WrittenBytes = writtenBytes,
                    Sha256 = sha256Hash,
                    FinalDest = finalDest
                };
            }
            catch (Exception) when (i < retries && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000 * i, cancellationToken);
            }
        }

        throw new Exception($"Failed to download {url} after {retries} attempts");
    }
}
