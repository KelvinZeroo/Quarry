using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using QuarryApp.Models;

namespace QuarryApp.Server;

public class LocalApiServer
{
    private HttpListener? _listener;
    private bool _running;
    private readonly Settings _settings;
    private readonly Action<string, Dictionary<string, object>?> _onDownloadRequest;
    private readonly Func<Dictionary<string, object>> _getStatusCallback;

    public LocalApiServer(
        Settings settings,
        Action<string, Dictionary<string, object>?> onDownloadRequest,
        Func<Dictionary<string, object>> getStatusCallback)
    {
        _settings = settings;
        _onDownloadRequest = onDownloadRequest;
        _getStatusCallback = getStatusCallback;
    }

    public void Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_settings.Port}/");
            _listener.Start();
            _running = true;

            Task.Run(ListenLoop);
        }
        catch
        {
            // Port might be in use or permission restricted
        }
    }

    public void Stop()
    {
        _running = false;
        _listener?.Stop();
    }

    private async Task ListenLoop()
    {
        while (_running && _listener != null && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch
            {
                if (!_running) break;
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;

        // CORS headers for extension
        res.Headers.Add("Access-Control-Allow-Origin", "*");
        res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 200;
            res.Close();
            return;
        }

        try
        {
            var path = req.Url?.AbsolutePath.ToLower() ?? "/";

            if (path == "/health")
            {
                var json = JsonSerializer.Serialize(new
                {
                    ok = true,
                    version = "1.0.0",
                    downloadDir = _settings.OutDir,
                    workers = _settings.Workers,
                    port = _settings.Port
                });
                SendJson(res, json);
            }
            else if (path == "/download" && req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = reader.ReadToEnd();
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(body);

                if (parsed != null && parsed.TryGetValue("url", out var urlObj) && urlObj != null)
                {
                    var url = urlObj.ToString()!;
                    _onDownloadRequest(url, parsed);
                    SendJson(res, JsonSerializer.Serialize(new { ok = true, jobId = Guid.NewGuid().ToString("N")[..8] }));
                }
                else
                {
                    res.StatusCode = 400;
                    SendJson(res, JsonSerializer.Serialize(new { error = "Missing url" }));
                }
            }
            else if (path == "/status")
            {
                var status = _getStatusCallback();
                SendJson(res, JsonSerializer.Serialize(status));
            }
            else
            {
                res.StatusCode = 404;
                SendJson(res, "{\"error\": \"Not Found\"}");
            }
        }
        catch (Exception ex)
        {
            res.StatusCode = 500;
            SendJson(res, JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    private static void SendJson(HttpListenerResponse res, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes, 0, bytes.Length);
        res.OutputStream.Close();
    }
}
