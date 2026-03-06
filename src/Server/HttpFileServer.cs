using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MetadataExtractor;
using Directory = System.IO.Directory;

/// <summary>
/// Minimal HTTP file server for serving captured images.
/// GET /          → HTML gallery of all captures
/// GET /captures  → same HTML gallery
/// GET /captures/{filename} → serve file
/// </summary>
public class HttpFileServer : IDisposable
{
    private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".dng", ".tiff", ".tif", ".raw", ".cr2", ".cr3", ".nef", ".arw" };

    private static readonly HashSet<string> _previewExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png" };

    private readonly HttpListener _listener = new();
    private readonly int _port;
    private readonly string _capturesDirectory;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public int Port => _port;
    public string CapturesDirectory => _capturesDirectory;

    public HttpFileServer(int port = 4402, string? capturesDirectory = null)
    {
        _port = port;
        _capturesDirectory = capturesDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "captures");

        Directory.CreateDirectory(_capturesDirectory);

        _listener.Prefixes.Add($"http://+:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        Logger.Notice($"HTTP file server listening on port {_port}, serving from {_capturesDirectory}");
        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }

    /// <summary>
    /// Build a full download URL for a given filename (used in capture_complete events).
    /// </summary>
    public static string GetDownloadUrl(string hostAddress, int port, string filename)
    {
        return $"http://{hostAddress}:{port}/captures/{Uri.EscapeDataString(filename)}";
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequest(context), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Logger.Error($"HTTP accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var req = context.Request;
        var res = context.Response;

        try
        {
            // Only GET allowed
            if (req.HttpMethod != "GET")
            {
                SendError(res, 405, "Method Not Allowed");
                return;
            }

            string path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";

            // Gallery page: GET / or GET /captures
            if (path == "" || path == "/" || path == "/captures")
            {
                await ServeGalleryAsync(res, req);
                return;
            }

            // Must start with /captures/
            if (!path.StartsWith("/captures/"))
            {
                SendError(res, 404, "Not Found");
                return;
            }

            // Extract and validate filename (reject path traversal)
            string filename = Uri.UnescapeDataString(path["/captures/".Length..]);
            if (filename.Contains("..") || filename.Contains('/') || filename.Contains('\\'))
            {
                SendError(res, 403, "Forbidden");
                return;
            }

            string filePath = Path.Combine(_capturesDirectory, filename);
            if (!File.Exists(filePath))
            {
                SendError(res, 404, "Not Found");
                return;
            }

            // Content type from extension
            res.ContentType = Path.GetExtension(filename).ToLower() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".dng" => "image/x-adobe-dng",
                ".tiff" or ".tif" => "image/tiff",
                _ => "application/octet-stream"
            };

            var fileInfo = new FileInfo(filePath);
            res.ContentLength64 = fileInfo.Length;
            res.StatusCode = 200;
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Content-Disposition", $"inline; filename=\"{filename}\"");

            await using var fileStream = File.OpenRead(filePath);
            await fileStream.CopyToAsync(res.OutputStream);

            Logger.Debug($"HTTP served: {filename} ({fileInfo.Length} bytes)");
        }
        catch (Exception ex)
        {
            Logger.Error($"HTTP request error: {ex.Message}");
            try { SendError(res, 500, "Internal Server Error"); } catch { }
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    private async Task ServeGalleryAsync(HttpListenerResponse res, HttpListenerRequest req)
    {
        string baseUrl = $"{req.Url!.Scheme}://{req.UserHostName}";

        var files = Directory.GetFiles(_capturesDirectory)
            .Select(f => new FileInfo(f))
            .Where(fi => _imageExtensions.Contains(fi.Extension))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>Captures</title>
<style>
  *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: system-ui, -apple-system, sans-serif; background: #0f0f13; color: #d4d4d8; min-height: 100vh; }
  header { display: flex; align-items: center; gap: 1rem; padding: 1.25rem 1.75rem; background: #18181b; border-bottom: 1px solid #27272a; }
  header h1 { font-size: 1.1rem; font-weight: 600; color: #f4f4f5; letter-spacing: .03em; }
  .badge { margin-left: auto; font-size: .75rem; background: #27272a; color: #a1a1aa; padding: .2em .7em; border-radius: 999px; }
  .empty { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: .6rem; padding: 5rem 2rem; color: #52525b; }
  .empty p { font-size: .9rem; }
  .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 1px; background: #27272a; border-top: 1px solid #27272a; }
  .card { background: #18181b; display: flex; flex-direction: column; overflow: hidden; transition: background .15s; }
  .card:hover { background: #1f1f23; }
  .thumb { width: 100%; aspect-ratio: 4/3; object-fit: cover; background: #09090b; display: block; }
  .thumb-ph { width: 100%; aspect-ratio: 4/3; background: #09090b; display: flex; align-items: center; justify-content: center; color: #3f3f46; font-size: 2rem; font-weight: 300; letter-spacing: .05em; }
  .info { padding: .65rem .8rem .35rem; flex: 1; display: flex; flex-direction: column; gap: .25rem; }
  .filename { font-size: .78rem; font-weight: 500; color: #e4e4e7; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .meta { font-size: .7rem; color: #71717a; display: flex; justify-content: space-between; }
  .exif { display: flex; flex-wrap: wrap; gap: .3rem; margin-top: .15rem; }
  .exif span { font-size: .68rem; background: #27272a; color: #a1a1aa; padding: .1em .45em; border-radius: 4px; }
  .actions { padding: .5rem .8rem .75rem; display: flex; gap: .5rem; }
  .btn { flex: 1; padding: .35rem 0; border: 1px solid #3f3f46; border-radius: 5px; background: transparent; color: #a1a1aa; font-size: .72rem; text-align: center; text-decoration: none; transition: background .12s, color .12s; }
  .btn:hover { background: #27272a; color: #f4f4f5; }
  .btn.dl { background: #2563eb; border-color: #2563eb; color: #fff; }
  .btn.dl:hover { background: #1d4ed8; }
</style>
</head>
<body>
<header>
  <h1>Captures</h1>
  <span class="badge">
""");
        sb.Append(files.Count);
        sb.Append(files.Count == 1 ? " image" : " images");
        sb.Append("</span>\n</header>\n");

        if (files.Count == 0)
        {
            sb.Append("""
<div class="empty">
  <svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
    <rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="8.5" cy="8.5" r="1.5"/><polyline points="21 15 16 10 5 21"/>
  </svg>
  <p>No captures yet</p>
</div>
""");
        }
        else
        {
            sb.Append("<div class=\"grid\">\n");
            foreach (var fi in files)
            {
                string enc = Uri.EscapeDataString(fi.Name);
                string url = $"{baseUrl}/captures/{enc}";
                string size = FormatSize(fi.Length);
                string date = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                bool canPreview = _previewExtensions.Contains(fi.Extension);
                string ext = fi.Extension.TrimStart('.').ToUpper();

                sb.Append("<div class=\"card\">\n");
                if (canPreview)
                    sb.Append($"  <a href=\"{url}\" target=\"_blank\"><img class=\"thumb\" src=\"{url}\" alt=\"{fi.Name}\" loading=\"lazy\"/></a>\n");
                else
                    sb.Append($"  <a href=\"{url}\" download=\"{fi.Name}\" style=\"text-decoration:none\"><div class=\"thumb-ph\">{ext}</div></a>\n");

                sb.Append($"  <div class=\"info\"><span class=\"filename\" title=\"{fi.Name}\">{fi.Name}</span><div class=\"meta\"><span>{date}</span><span>{size}</span></div>");

                // EXIF chips
                var exif = ReadExif(fi.FullName);
                if (exif.Count > 0)
                {
                    sb.Append("<div class=\"exif\">");
                    foreach (var tag in exif)
                        sb.Append($"<span>{tag}</span>");
                    sb.Append("</div>");
                }

                sb.Append("</div>\n");
                sb.Append("  <div class=\"actions\">\n");
                if (canPreview)
                    sb.Append($"    <a class=\"btn\" href=\"{url}\" target=\"_blank\">View</a>\n");
                sb.Append($"    <a class=\"btn dl\" href=\"{url}\" download=\"{fi.Name}\">Download</a>\n");
                sb.Append("  </div>\n</div>\n");
            }
            sb.Append("</div>\n");
        }

        sb.Append("</body>\n</html>");

        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        res.ContentType = "text/html; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        res.StatusCode = 200;
        res.AddHeader("Access-Control-Allow-Origin", "*");
        await res.OutputStream.WriteAsync(bytes);
        Logger.Debug($"HTTP gallery: {files.Count} captures");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024) return $"{bytes / 1_024.0:F0} KB";
        return $"{bytes} B";
    }

    // Normalise shutter speed strings so sub-second values always show as 1/x.
    // MetadataExtractor already returns "1/200 sec" for most cameras, but some
    // (Sony ILCE etc.) return "0.005 sec" or just "0.005".
    private static string FormatShutter(string raw)
    {
        // Already fraction-formatted — leave it alone
        if (raw.Contains('/')) return raw;

        // Extract the numeric part (strip " sec" suffix etc.)
        string num = raw.Split(' ')[0];
        if (double.TryParse(num, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double sec))
        {
            if (sec >= 1.0)
                return $"{sec:G4} sec";           // e.g. "2 sec", "1.3 sec"
            if (sec > 0.0)
                return $"1/{(int)Math.Round(1.0 / sec)} sec";  // e.g. "1/200 sec"
        }
        return raw; // fallback: return as-is
    }

    private static List<string> ReadExif(string filePath)
    {
        var tags = new List<string>();
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);

            // Helper: scan all directories for the first non-empty value matching a tag name
            string? FindTag(params string[] names)
            {
                foreach (var dir in directories)
                    foreach (var tag in dir.Tags)
                        foreach (var name in names)
                            if (tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(tag.Description))
                                return tag.Description;
                return null;
            }

            string? iso = FindTag("ISO Speed Ratings", "ISO Speed", "Recommended Exposure Index", "ISO Equivalent");
            if (iso != null) tags.Add($"ISO {iso}");

            string? shutter = FindTag("Exposure Time", "Shutter Speed Value");
            if (shutter != null) tags.Add(FormatShutter(shutter));

            string? aperture = FindTag("F-Number", "Aperture Value");
            if (aperture != null) tags.Add(aperture);

            string? fl = FindTag("Focal Length");
            if (fl != null) tags.Add(fl);
        }
        catch { /* non-EXIF file or unreadable — silently skip */ }
        return tags;
    }

    private static void SendError(HttpListenerResponse res, int statusCode, string reason)
    {
        res.StatusCode = statusCode;
        res.StatusDescription = reason;
        res.ContentType = "text/plain";
        using var writer = new StreamWriter(res.OutputStream);
        writer.Write(reason);
    }
}
