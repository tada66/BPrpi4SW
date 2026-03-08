using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Handles mount-related WebSocket commands by wrapping UartClient and Alignment.
/// Subscribes to UART events and forwards them as WebSocket events.
/// </summary>
public class MountController : IDisposable
{
    private readonly WebSocketServer _wsServer;
    private readonly CameraController _cameraController;
    private bool _motorsEnabled;

    public MountController(WebSocketServer wsServer, CameraController cameraController)
    {
        _wsServer = wsServer;
        _cameraController = cameraController;
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

    // ── Plate-solve assisted operations ──

    /// <summary>
    /// Bind the camera instance for plate-solve operations.
    /// Called when a camera connects.
    /// </summary>
    public void SetSolveCamera(Camera? camera)
    {
        Alignment.SolveCamera = camera;
    }

    public async Task<object> AutoCenterAsync(AutoCenterPayload payload)
    {
        EnsureSolveCamera();
        var result = await Alignment.AutoCenterAsync(
            payload.Ra, payload.Dec,
            maxIterations: 5,
            tolerancePx: payload.Tolerance,
            ct: CancellationToken.None);

        if (result == null)
            throw new InvalidOperationException("Auto-center failed: plate solve not available or mount not aligned.");

        return new
        {
            success = result.Success,
            iterations = result.Iterations,
            finalErrorPx = Math.Round(result.FinalErrorPx, 1),
            finalErrorArcmin = Math.Round(result.FinalErrorArcmin, 2),
            solvedRa = result.SolvedRA,
            solvedDec = result.SolvedDec,
            message = result.Message,
            alignmentStatus = BuildAlignmentStatus()
        };
    }

    public async Task<object> AutoCalibrateAsync(AutoCalibratePayload payload)
    {
        EnsureSolveCamera();
        var result = await Alignment.AutoCalibrateAsync(
            payload.AltSteps, payload.AzSteps,
            ct: CancellationToken.None);

        if (result == null)
            throw new InvalidOperationException("Auto-calibrate failed: camera not connected or no initial alignment.");

        return new
        {
            solvedCount = result.SolvedCount,
            failedCount = result.FailedCount,
            totalPositions = result.TotalPositions,
            totalPoints = result.TotalPoints,
            quality = result.Quality,
            elapsedSeconds = result.ElapsedSeconds,
            alignmentStatus = BuildAlignmentStatus()
        };
    }

    public async Task<object> StartGuidedTrackingAsync(GuidedTrackingPayload payload)
    {
        if (!Alignment.IsAligned)
            throw new InvalidOperationException("Cannot start guided tracking: mount is not calibrated.");

        EnsureSolveCamera();

        // Run guided tracking in background — returns immediately with initial centering result
        var centerResult = await Alignment.StartGuidedTrackingAsync(
            payload.Ra, payload.Dec, payload.Interval,
            ct: CancellationToken.None);

        return new
        {
            success = centerResult?.Success ?? false,
            message = centerResult?.Message ?? "Guided tracking started",
            initialErrorPx = Math.Round(centerResult?.FinalErrorPx ?? 0, 1)
        };
    }

    public object StopGuidedTracking()
    {
        Alignment.StopGuidedTracking();
        return new { message = "Guided tracking stopped" };
    }

    public async Task<object> SolveCurrentAsync()
    {
        EnsureSolveCamera();
        var result = await Alignment.SolveCurrentAsync();
        if (result == null)
            throw new InvalidOperationException("Plate solve failed or no camera connected.");

        return new
        {
            raCenterHours = Math.Round(result.RaCenterHours, 4),
            decCenterDeg = Math.Round(result.DecCenterDeg, 4),
            pixelScale = Math.Round(result.PixelScaleArcsecPerPx, 2),
            rotationDeg = Math.Round(result.RotationDeg, 2),
            fieldWidthDeg = Math.Round(result.FieldWidthDeg, 2),
            fieldHeightDeg = Math.Round(result.FieldHeightDeg, 2),
            solveTimeSeconds = Math.Round(result.SolveTime.TotalSeconds, 1)
        };
    }

    public object ConfigurePlateSolver(PlateSolveConfigPayload payload)
    {
        if (payload.FocalLengthMm.HasValue)
            PlateSolver.FocalLengthMm = payload.FocalLengthMm.Value;
        if (payload.PixelSizeUm.HasValue)
            PlateSolver.PixelSizeUm = payload.PixelSizeUm.Value;

        return new
        {
            focalLengthMm = PlateSolver.FocalLengthMm,
            pixelSizeUm = PlateSolver.PixelSizeUm,
            plateScaleArcsecPerPx = Math.Round(PlateSolver.PlateScale, 2)
        };
    }

    /// <summary>
    /// Ensure the plate-solve camera is set from the camera controller.
    /// </summary>
    private void EnsureSolveCamera()
    {
        Alignment.SolveCamera = _cameraController.Camera;
        if (Alignment.SolveCamera == null || !Alignment.SolveCamera.connected)
            throw new InvalidOperationException("No camera connected. Connect a camera first.");
    }
}
