using System;
using System.Threading.Tasks;

/// <summary>
/// Handles mount-related WebSocket commands by wrapping UartClient and Alignment.
/// Subscribes to UART events and forwards them as WebSocket events.
/// </summary>
public class MountController : IDisposable
{
    private readonly WebSocketServer _wsServer;
    private bool _motorsEnabled;

    public MountController(WebSocketServer wsServer)
    {
        _wsServer = wsServer;
        SubscribeToEvents();
    }

    // ── Event subscriptions ──

    private void SubscribeToEvents()
    {
        UartClient.Client.StatusReceived += OnStatusReceived;
        UartClient.Client.PositionReceived += OnPositionReceived;
        UartClient.Client.ReferenceLost += OnReferenceLost;
    }

    private void OnStatusReceived(float temp, int x, int y, int z, bool enabled, bool paused, bool celestialTracking, int fanPct)
    {
        _motorsEnabled = enabled;

        if (!_wsServer.HasClient) return;

        var payload = new MountStatusPayload(
            X: x, Y: y, Z: z,
            Temperature: temp,
            MotorsEnabled: enabled,
            MotorsPaused: paused,
            CelestialTracking: celestialTracking,
            FanSpeed: fanPct
        );

        _ = _wsServer.SendMessageAsync(Message.Event("mount.status", payload));
    }

    private void OnPositionReceived(int x, int y, int z)
    {
        if (!_wsServer.HasClient) return;

        var payload = new MountPositionPayload(x, y, z);
        _ = _wsServer.SendMessageAsync(Message.Event("mount.position", payload));
    }

    private void OnReferenceLost()
    {
        // Reference lost means motor positions are no longer reliable → discard calibration
        if (Alignment.IsAligned || Alignment.PointCount > 0)
        {
            Alignment.Reset();
            Logger.Warn("Reference lost: alignment discarded (motor positions no longer valid)");
        }

        if (!_wsServer.HasClient) return;
        _ = _wsServer.SendMessageAsync(Message.Event("mount.reference_lost"));
    }

    // ── Command handlers ──

    private static byte ParseAxis(string axis) => axis.ToLower() switch
    {
        "x" => Axis.X,
        "y" => Axis.Y,
        "z" => Axis.Z,
        _ => throw new ArgumentException($"Invalid axis: '{axis}'. Use 'x', 'y', or 'z'.")
    };

    public async Task<object> MoveStaticAsync(MountMovePayload payload)
    {
        byte axis = ParseAxis(payload.Axis);
        bool ok = await UartClient.Client.MoveStatic(axis, payload.Position);
        return new { success = ok, axis = payload.Axis, position = payload.Position };
    }

    public async Task<object> MoveRelativeAsync(MountMoveRelativePayload payload)
    {
        byte axis = ParseAxis(payload.Axis);
        bool ok = await UartClient.Client.MoveRelative(axis, payload.Offset);
        return new { success = ok, axis = payload.Axis, offset = payload.Offset };
    }

    public async Task<object> StartLinearAsync(MountLinearPayload payload)
    {
        bool ok = await UartClient.Client.StartLinearMove(payload.XRate, payload.YRate, payload.ZRate);
        return new { success = ok };
    }

    public async Task<object> StartTrackingAsync(MountTrackingPayload payload)
    {
        if (!Alignment.IsAligned)
            throw new InvalidOperationException("Cannot start tracking: mount is not calibrated. Add at least 2 alignment stars first.");

        bool ok = await Alignment.StartTrackingAsync(payload.Ra, payload.Dec);
        return new { success = ok, ra = payload.Ra, dec = payload.Dec };
    }

    public async Task<object> StopAsync()
    {
        bool ok = await UartClient.Client.StopAll();
        return new { success = ok };
    }

    public async Task<object> PauseAsync()
    {
        bool ok = await UartClient.Client.PauseMotors();
        return new { success = ok };
    }

    public async Task<object> ResumeAsync()
    {
        bool ok = await UartClient.Client.ResumeMotors();
        return new { success = ok };
    }

    public async Task<object> GetPositionAsync()
    {
        bool ok = await UartClient.Client.GetPositions();
        return new { success = ok };
    }

    public object AlignmentInit()
    {
        Alignment.Reset();
        return new { 
            message = "Alignment initialized. Center a known star and call mount.alignment.add_star with its RA/Dec.",
            pointCount = 0 
        };
    }

    public async Task<object> AlignmentAddStarAsync(MountAlignStarPayload payload)
    {
        if (!_motorsEnabled)
            throw new InvalidOperationException("Cannot add alignment star while motors are disabled.");

        await Alignment.AddAlignmentPointAsync(payload.Ra, payload.Dec);
        return BuildAlignmentStatus();
    }

    public object AlignmentStatus()
    {
        return BuildAlignmentStatus();
    }

    private static MountAlignmentStatusPayload BuildAlignmentStatus()
    {
        var result = Alignment.LastResult;
        return new MountAlignmentStatusPayload(
            IsAligned: Alignment.IsAligned,
            PointCount: Alignment.PointCount,
            Latitude: Alignment.Latitude,
            Longitude: Alignment.Longitude,
            Quality: result?.Quality,
            AverageResidualArcmin: result?.AverageResidualArcmin,
            AverageResidualPixels: result?.AverageResidualPixels,
            MaxPairErrorDeg: result?.MaxPairErrorDeg,
            StepLossPercent: result?.StepLossPercent,
            ActiveStarCount: result?.ActiveStarCount,
            RejectedCount: result?.RejectedCount,
            Stars: result?.Stars.Select(s => new AlignmentStarInfo(
                Index: s.Index,
                Ra: s.RA,
                Dec: s.Dec,
                ResidualArcmin: s.ResidualArcmin,
                Excluded: s.Excluded,
                ExclusionReason: s.ExclusionReason
            )).ToArray()
        );
    }

    public void Dispose()
    {
        UartClient.Client.StatusReceived -= OnStatusReceived;
        UartClient.Client.PositionReceived -= OnPositionReceived;
        UartClient.Client.ReferenceLost -= OnReferenceLost;
    }
}
