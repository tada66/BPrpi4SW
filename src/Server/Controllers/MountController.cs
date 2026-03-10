using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Handles mount-related WebSocket commands by wrapping UartClient and Calibration.
/// Subscribes to UART events and forwards them as WebSocket events.
/// </summary>
public class MountController : IDisposable
{
    private readonly WebSocketServer _wsServer;
    private readonly CameraController _cameraController;
    private bool _motorsEnabled;

    /// <summary>Background task for long-running auto operations.</summary>
    private Task? _autoTask;

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
        Calibration.CalibrationUpdated += OnCalibrationUpdated;
    }

    private void OnCalibrationUpdated(Calibration.AutoProgressInfo info)
    {
        if (!_wsServer.HasClient) return;
        _ = _wsServer.SendMessageAsync(Message.Event("mount.calibration.update", new
        {
            pointCount = info.PointCount,
            quality = info.Quality,
            averageResidualArcmin = info.AverageResidualArcmin,
            message = info.Message,
            currentPosition = info.CurrentPosition,
            totalPositions = info.TotalPositions,
            alignmentStatus = BuildAlignmentStatus()
        }));
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
        if (Calibration.IsAligned || Calibration.PointCount > 0)
        {
            Calibration.Reset();
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
        if (!Calibration.IsAligned)
            throw new InvalidOperationException("Cannot start tracking: mount is not calibrated. Add at least 2 alignment stars first.");

        bool ok = await Calibration.StartTrackingAsync(payload.Ra, payload.Dec);
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
        Calibration.Reset();
        return new { 
            message = "Alignment initialized. Center a known star and call mount.Calibration.add_star with its RA/Dec.",
            pointCount = 0 
        };
    }

    public async Task<object> AlignmentAddStarAsync(MountAlignStarPayload payload)
    {
        if (!_motorsEnabled)
            throw new InvalidOperationException("Cannot add alignment star while motors are disabled.");

        await Calibration.AddAlignmentPointAsync(payload.Ra, payload.Dec);
        return BuildAlignmentStatus();
    }

    public object AlignmentStatus()
    {
        return BuildAlignmentStatus();
    }

    private static MountAlignmentStatusPayload BuildAlignmentStatus()
    {
        var result = Calibration.LastResult;
        return new MountAlignmentStatusPayload(
            IsAligned: Calibration.IsAligned,
            PointCount: Calibration.PointCount,
            Latitude: Calibration.Latitude,
            Longitude: Calibration.Longitude,
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
        Calibration.CalibrationUpdated -= OnCalibrationUpdated;
    }

    // ── Plate-solve assisted operations ──

    /// <summary>
    /// Bind the camera instance for plate-solve operations.
    /// Called when a camera connects.
    /// </summary>
    public void SetSolveCamera(Camera? camera)
    {
        Calibration.SolveCamera = camera;
    }

    public Task<object> AutoCenterAsync(AutoCenterPayload payload)
    {
        EnsureSolveCamera();

        // Cancel any previous auto operation
        Calibration.CancelAutoOperation();

        // Run in background so the WebSocket message loop stays responsive
        var cts = new CancellationTokenSource();
        Calibration._autoCts = cts;
        var ct = cts.Token;

        _autoTask = Task.Run(async () =>
        {
            try
            {
                var result = await Calibration.AutoCenterAsync(
                    payload.Ra, payload.Dec,
                    maxIterations: 5,
                    tolerancePx: payload.Tolerance,
                    ct: ct);

                if (_wsServer.HasClient)
                {
                    _ = _wsServer.SendMessageAsync(Message.Event("mount.auto_center.complete", new
                    {
                        success = result?.Success ?? false,
                        iterations = result?.Iterations ?? 0,
                        finalErrorPx = Math.Round(result?.FinalErrorPx ?? 0, 1),
                        finalErrorArcmin = Math.Round(result?.FinalErrorArcmin ?? 0, 2),
                        solvedRa = result?.SolvedRA ?? 0,
                        solvedDec = result?.SolvedDec ?? 0,
                        message = result?.Message ?? "Auto-center failed",
                        alignmentStatus = BuildAlignmentStatus()
                    }));
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Notice("AutoCenter: cancelled by user");
                if (_wsServer.HasClient)
                    _ = _wsServer.SendMessageAsync(Message.Event("mount.auto_center.cancelled"));
            }
            catch (Exception ex)
            {
                Logger.Error($"AutoCenter error: {ex.Message}");
                if (_wsServer.HasClient)
                    _ = _wsServer.SendMessageAsync(Message.Event("mount.auto_center.error", new { message = ex.Message }));
            }
        });

        return Task.FromResult<object>(new { message = "Auto-center started", ra = payload.Ra, dec = payload.Dec });
    }

    public Task<object> AutoCalibrateAsync(AutoCalibratePayload payload)
    {
        EnsureSolveCamera();

        // Cancel any previous auto operation
        Calibration.CancelAutoOperation();

        // Run in background so the WebSocket message loop stays responsive
        var cts = new CancellationTokenSource();
        Calibration._autoCts = cts;
        var ct = cts.Token;

        int totalPositions = payload.AltSteps * payload.AzSteps;

        _autoTask = Task.Run(async () =>
        {
            try
            {
                var result = await Calibration.AutoCalibrateAsync(
                    payload.AltSteps, payload.AzSteps, payload.WideSweep,
                    ct: ct);

                if (_wsServer.HasClient)
                {
                    _ = _wsServer.SendMessageAsync(Message.Event("mount.auto_calibrate.complete", new
                    {
                        solvedCount = result?.SolvedCount ?? 0,
                        failedCount = result?.FailedCount ?? 0,
                        totalPositions = result?.TotalPositions ?? totalPositions,
                        totalPoints = result?.TotalPoints ?? 0,
                        quality = result?.Quality ?? "UNKNOWN",
                        elapsedSeconds = result?.ElapsedSeconds ?? 0,
                        alignmentStatus = BuildAlignmentStatus()
                    }));
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Notice("AutoCalibrate: cancelled by user");
                if (_wsServer.HasClient)
                    _ = _wsServer.SendMessageAsync(Message.Event("mount.auto_calibrate.cancelled", new
                    {
                        alignmentStatus = BuildAlignmentStatus()
                    }));
            }
            catch (Exception ex)
            {
                Logger.Error($"AutoCalibrate error: {ex.Message}");
                if (_wsServer.HasClient)
                    _ = _wsServer.SendMessageAsync(Message.Event("mount.auto_calibrate.error", new { message = ex.Message }));
            }
        });

        return Task.FromResult<object>(new { message = "Auto-calibrate started", totalPositions });
    }

    public Task<object> StartGuidedTrackingAsync(GuidedTrackingPayload payload)
    {
        if (!Calibration.IsAligned)
            throw new InvalidOperationException("Cannot start guided tracking: mount is not calibrated.");

        EnsureSolveCamera();

        // Cancel any previous auto operation
        Calibration.CancelAutoOperation();

        var cts = new CancellationTokenSource();
        Calibration._autoCts = cts;
        var ct = cts.Token;

        _autoTask = Task.Run(async () =>
        {
            try
            {
                var centerResult = await Calibration.StartGuidedTrackingAsync(
                    payload.Ra, payload.Dec, payload.Interval, ct: ct);

                if (_wsServer.HasClient)
                {
                    _ = _wsServer.SendMessageAsync(Message.Event("mount.guided_tracking.complete", new
                    {
                        success = centerResult?.Success ?? false,
                        message = centerResult?.Message ?? "Guided tracking ended"
                    }));
                }
            }
            catch (OperationCanceledException)
            {
                if (_wsServer.HasClient)
                    _ = _wsServer.SendMessageAsync(Message.Event("mount.guided_tracking.stopped"));
            }
            catch (Exception ex)
            {
                Logger.Error($"GuidedTracking error: {ex.Message}");
                if (_wsServer.HasClient)
                    _ = _wsServer.SendMessageAsync(Message.Event("mount.guided_tracking.error", new { message = ex.Message }));
            }
        });

        return Task.FromResult<object>(new { message = "Guided tracking started", ra = payload.Ra, dec = payload.Dec, interval = payload.Interval });
    }

    public object StopGuidedTracking()
    {
        Calibration.CancelAutoOperation();
        return new { message = "Guided tracking stopped" };
    }

    /// <summary>
    /// Cancel any active auto-operation (auto-calibrate, auto-center, guided tracking).
    /// </summary>
    public object CancelAutoOperation()
    {
        Calibration.CancelAutoOperation();
        return new { message = "Auto-operation cancelled" };
    }

    public async Task<object> SolveCurrentAsync()
    {
        EnsureSolveCamera();
        var result = await Calibration.SolveCurrentAsync();
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
        var fl = payload.FocalLengthMm ?? payload.FocalLength;
        var px = payload.PixelSizeUm ?? payload.PixelSize;

        if (fl.HasValue)
            PlateSolver.FocalLengthMm = fl.Value;
        if (px.HasValue)
            PlateSolver.PixelSizeUm = px.Value;

        return new
        {
            focalLengthMm = PlateSolver.FocalLengthMm,
            focalLength = PlateSolver.FocalLengthMm,
            pixelSizeUm = PlateSolver.PixelSizeUm,
            pixelSize = PlateSolver.PixelSizeUm,
            plateScaleArcsecPerPx = Math.Round(PlateSolver.PlateScale, 2)
        };
    }

    /// <summary>
    /// Ensure the plate-solve camera is set from the camera controller.
    /// </summary>
    private void EnsureSolveCamera()
    {
        Calibration.SolveCamera = _cameraController.Camera;
        if (Calibration.SolveCamera == null || !Calibration.SolveCamera.connected)
            throw new InvalidOperationException("No camera connected. Connect a camera first.");
    }
}
