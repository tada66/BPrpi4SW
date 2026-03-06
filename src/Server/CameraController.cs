using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

/// <summary>
/// Handles camera-related WebSocket commands by wrapping the Camera class.
/// </summary>
public class CameraController
{
    private Camera? _camera;
    private readonly UdpLiveViewSender _liveView;
    private readonly WebSocketServer _wsServer;
    private readonly HttpFileServer _httpServer;

    public Camera? Camera => _camera;

    public CameraController(WebSocketServer wsServer, HttpFileServer httpServer)
    {
        _wsServer = wsServer;
        _httpServer = httpServer;
        _liveView = new UdpLiveViewSender(null!); // Will be recreated on connect
        
        _wsServer.ClientDisconnected += OnClientDisconnected;
    }

    private UdpLiveViewSender? _activeLiveView;

    private void OnClientDisconnected()
    {
        // Stop live view when client disconnects
        _activeLiveView?.Stop();
        _activeLiveView?.Dispose();
        _activeLiveView = null;
    }

    private Camera RequireCamera()
    {
        if (_camera == null || !_camera.connected)
            throw new InvalidOperationException("No camera connected.");
        return _camera;
    }

    // ── Actions ──

    public object ListCameras()
    {
        var cam = _camera ?? new Camera();
        if (_camera == null) _camera = cam;
        
        var cameras = cam.GetAvailableCameras().ToList();
        return cameras.Select(c => new CameraListEntry(c.Model, c.Port)).ToArray();
    }

    public object Connect(CameraConnectPayload payload)
    {
        _camera ??= new Camera();

        // Parse "model:port" format
        var cameras = _camera.GetAvailableCameras().ToList();
        
        var match = cameras.FirstOrDefault(c => $"{c.Model}:{c.Port}" == payload.Camera);
        if (match == null)
        {
            // Try matching by model name only
            match = cameras.FirstOrDefault(c => c.Model == payload.Camera);
        }
        if (match == null)
        {
            // Try matching by index
            if (int.TryParse(payload.Camera, out int idx) && idx >= 0 && idx < cameras.Count)
                match = cameras[idx];
        }
        if (match == null)
            throw new InvalidOperationException($"Camera not found: {payload.Camera}. Available: {string.Join(", ", cameras.Select(c => $"{c.Model}:{c.Port}"))}");

        _camera.ConnectCamera(match);
        Logger.Notice($"Camera connected: {_camera.cameramodel}");

        return GetInfo();
    }

    public object? Disconnect()
    {
        _activeLiveView?.Stop();
        _activeLiveView?.Dispose();
        _activeLiveView = null;

        _camera?.Shutdown();
        Logger.Notice("Camera disconnected.");
        return new { disconnected = true };
    }

    public object GetInfo()
    {
        if (_camera == null)
            return new CameraInfoPayload("", "", "", false, null);

        bool connected = _camera.connected;
        return new CameraInfoPayload(
            Model: connected ? _camera.cameramodel : "",
            Manufacturer: connected ? _camera.manufacturer : "",
            Battery: connected ? _camera.batteryLevel : "",
            Connected: connected,
            Capabilities: connected ? GetCapabilities() : null
        );
    }

    private CameraCapabilities? GetCapabilities()
    {
        // The capabilities are determined by the loaded config file via CameraCommandInterpreter.
        // We can check by calling the camera methods and catching NotSupportedException,
        // but for now we'll try a simpler approach using the camera properties.
        try
        {
            return new CameraCapabilities(
                LiveView: TryCapability(() => _camera!.GetLiveViewBytes()),
                ImageCapture: TryCapability(() => { /* check via info */ }),
                TriggerCapture: true,
                Configuration: true
            );
        }
        catch
        {
            return new CameraCapabilities(true, true, true, true);
        }
    }

    private static bool TryCapability(Action action)
    {
        try { action(); return true; }
        catch (NotSupportedException) { return false; }
        catch { return true; } // Other errors mean capability exists but may have failed
    }

    public object GetSettings()
    {
        var cam = RequireCamera();
        return new CameraSettingsPayload(
            Iso: new PropertyInfo(cam.Iso.value.ToString(), cam.Iso.Values.ToArray()),
            ShutterSpeed: new PropertyInfo(cam.shutterSpeed.value, cam.shutterSpeed.Values.ToArray()),
            Aperture: new PropertyInfo(cam.aperture.value, cam.aperture.Values.ToArray()),
            FocusMode: new PropertyInfo(cam.focus.mode, cam.focus.Values.ToArray())
        );
    }

    public object SetIso(SetValuePayload payload)
    {
        var cam = RequireCamera();
        if (int.TryParse(payload.Value, out int iso))
            cam.Iso.value = iso;
        else
            throw new ArgumentException($"Invalid ISO value: {payload.Value}");
        return new { iso = cam.Iso.value };
    }

    public object SetShutter(SetValuePayload payload)
    {
        Logger.Info($"Setting shutter speed to {payload.Value}...");
        var cam = RequireCamera();
        cam.shutterSpeed.value = payload.Value;
        return new { shutter = cam.shutterSpeed.value };
    }

    public object SetAperture(SetValuePayload payload)
    {
        var cam = RequireCamera();
        cam.aperture.value = payload.Value;
        return new { aperture = cam.aperture.value };
    }

    public object SetFocusMode(SetValuePayload payload)
    {
        var cam = RequireCamera();
        cam.focus.mode = payload.Value;
        return new { focusMode = cam.focus.mode, available = cam.focus.Values.ToArray() };
    }

    public async Task<object> CaptureAsync()
    {
        var cam = RequireCamera();

        // Stop live view if running
        bool wasStreaming = _activeLiveView?.IsStreaming ?? false;
        _activeLiveView?.Stop();

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName = $"img_{timestamp}";
        
        string result = await Task.Run(() => cam.CaptureImage(baseName));
        Logger.Notice($"Capture complete: {result}");

        // Move file to captures directory
        string finalPath = MoveToCaptures(result);
        string filename = Path.GetFileName(finalPath);

        // Send capture_complete event with download URL
        string? clientIp = _wsServer.ClientAddress?.ToString();
        if (clientIp != null)
        {
            // Use the server's own IP instead of the client's
            string serverIp = GetServerAddress();
            string url = HttpFileServer.GetDownloadUrl(serverIp, _httpServer.Port, filename);
            await _wsServer.SendMessageAsync(Message.Event("camera.capture_complete", 
                new CaptureCompletePayload(url)));
        }

        return new { path = finalPath, filename };
    }

    public async Task<object> CaptureBulbAsync(CaptureBulbPayload payload)
    {
        var cam = RequireCamera();

        // Stop live view if running
        _activeLiveView?.Stop();

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName = $"bulb_{timestamp}_{payload.Duration}s";

        string result = await Task.Run(() => cam.CaptureImageBulb(payload.Duration, baseName));
        Logger.Notice($"Bulb capture complete: {result}");

        string finalPath = MoveToCaptures(result);
        string filename = Path.GetFileName(finalPath);

        string serverIp = GetServerAddress();
        string url = HttpFileServer.GetDownloadUrl(serverIp, _httpServer.Port, filename);
        await _wsServer.SendMessageAsync(Message.Event("camera.capture_complete",
            new CaptureCompletePayload(url)));

        return new { path = finalPath, filename };
    }

    public object StartLiveView(LiveViewStartPayload payload)
    {
        var cam = RequireCamera();
        var clientIp = _wsServer.ClientAddress
            ?? throw new InvalidOperationException("No client connected.");

        // Stop any existing stream
        _activeLiveView?.Stop();
        _activeLiveView?.Dispose();

        _activeLiveView = new UdpLiveViewSender(cam);
        _activeLiveView.Start(clientIp, payload.Port);

        return new { streaming = true, targetPort = payload.Port };
    }

    public object StopLiveView()
    {
        _activeLiveView?.Stop();
        return new { streaming = false };
    }

    public object Magnify()
    {
        RequireCamera().Magnify();
        return new { magnified = true };
    }

    public object MagnifyOff()
    {
        RequireCamera().MagnifyOff();
        return new { magnified = false };
    }

    public object Focus(FocusPayload payload)
    {
        var cam = RequireCamera();
        if (payload.Direction == "closer")
            cam.focus.Closer(payload.Step);
        else if (payload.Direction == "further")
            cam.focus.Further(payload.Step);
        else
            throw new ArgumentException($"Invalid focus direction: {payload.Direction}. Use 'closer' or 'further'.");
        return new { focused = payload.Direction, step = payload.Step };
    }

    public object GetAllWidgets()
    {
        var cam = RequireCamera();
        // Access the internal driver to get all selectable widgets
        // We use reflection-free approach: the camera exposes this via a public method we add,
        // or we'll use the existing driver. For now, return settings we know about.
        var settings = GetSettings();
        return settings;
    }

    public object SetWidget(SetWidgetPayload payload)
    {
        RequireCamera();
        // This would need access to the internal driver's SetWidgetValueByPath
        // For now, we route through known properties
        throw new NotSupportedException("Generic widget access not yet implemented. Use specific camera.set_* actions instead.");
    }

    // ── Status (for periodic broadcast) ──

    public CameraStatusPayload? GetStatusPayload()
    {
        if (_camera == null) return new CameraStatusPayload(false, null, null, null, null, null, null, null);

        try
        {
            bool connected = _camera.connected;
            if (!connected)
                return new CameraStatusPayload(false, null, null, null, null, null, null, null);

            return new CameraStatusPayload(
                Connected: true,
                Battery: _camera.batteryLevel,
                Model: _camera.cameramodel,
                Manufacturer: _camera.manufacturer,
                Iso: _camera.Iso.value.ToString(),
                ShutterSpeed: _camera.shutterSpeed.value,
                Aperture: _camera.aperture.value,
                FocusMode: _camera.focus.mode
            );
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error getting camera status: {ex.Message}");
            return new CameraStatusPayload(false, null, null, null, null, null, null, null);
        }
    }

    #region Helpers

    private string MoveToCaptures(string sourcePath)
    {
        if (!File.Exists(sourcePath)) return sourcePath;

        string filename = Path.GetFileName(sourcePath);
        string destPath = Path.Combine(_httpServer.CapturesDirectory, filename);

        // Avoid overwriting
        if (File.Exists(destPath))
        {
            string nameWithout = Path.GetFileNameWithoutExtension(filename);
            string ext = Path.GetExtension(filename);
            destPath = Path.Combine(_httpServer.CapturesDirectory, $"{nameWithout}_{Guid.NewGuid():N}{ext}");
        }

        File.Move(sourcePath, destPath);
        return destPath;
    }

    private static string GetServerAddress()
    {
        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);

            foreach (var iface in interfaces)
            {
                var ip = iface.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address)
                    .FirstOrDefault();
                if (ip != null) return ip.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }
}
    #endregion
