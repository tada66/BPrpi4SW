using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Periodically broadcasts camera status over WebSocket.
/// Mount status is already event-driven (forwarded by MountController from EVT_STATUS),
/// so this only handles camera status polling.
/// </summary>
public class StatusBroadcaster : IDisposable
{
    private readonly WebSocketServer _wsServer;
    private readonly CameraController _cameraController;
    private Timer? _timer;

    /// <summary>
    /// Broadcast interval in milliseconds.
    /// </summary>
    public int IntervalMs { get; set; } = 2000;

    public StatusBroadcaster(WebSocketServer wsServer, CameraController cameraController)
    {
        _wsServer = wsServer;
        _cameraController = cameraController;
    }

    public void Start()
    {
        _timer = new Timer(OnTick, null, IntervalMs, IntervalMs);
        Logger.Info($"Status broadcaster started (interval: {IntervalMs}ms).");
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    private void OnTick(object? state)
    {
        if (!_wsServer.HasClient) return;

        try
        {
            var status = _cameraController.GetStatusPayload();
            if (status != null)
            {
                _ = _wsServer.SendMessageAsync(Message.Event("camera.status", status));
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Status broadcast error: {ex.Message}");
        }
    }
}
