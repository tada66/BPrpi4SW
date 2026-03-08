using System;
using System.Threading.Tasks;

/// <summary>
/// Routes incoming WebSocket messages to the appropriate controller based on topic and action.
/// </summary>
public class MessageRouter
{
    private readonly WebSocketServer _server;
    private readonly CameraController _cameraController;
    private readonly MountController _mountController;

    public MessageRouter(WebSocketServer server, CameraController cameraController, MountController mountController)
    {
        _server = server;
        _cameraController = cameraController;
        _mountController = mountController;

        _server.MessageReceived += OnMessageReceived;
    }

    private async Task OnMessageReceived(string json)
    {
        var msg = Message.Deserialize(json);
        if (msg == null)
        {
            Logger.Warn($"Failed to deserialize message: {json}");
            await _server.SendMessageAsync(Message.Error(null, "unknown", "Invalid JSON message."));
            return;
        }

        if (msg.Type != "request")
        {
            Logger.Warn($"Unexpected message type: {msg.Type}");
            return;
        }

        Logger.Debug($"Routing: {msg.Action} (id={msg.Id})");

        try
        {
            object? result = msg.Action switch
            {
                // ── Camera ──
                "camera.list"            => _cameraController.ListCameras(),
                "camera.connect"         => _cameraController.Connect(msg.GetPayload<CameraConnectPayload>()!),
                "camera.disconnect"      => _cameraController.Disconnect(),
                "camera.get_info"        => _cameraController.GetInfo(),
                "camera.get_settings"    => _cameraController.GetSettings(),
                "camera.set_iso"         => _cameraController.SetIso(msg.GetPayload<SetValuePayload>()!),
                "camera.set_shutter"     => _cameraController.SetShutter(msg.GetPayload<SetValuePayload>()!),
                "camera.set_aperture"    => _cameraController.SetAperture(msg.GetPayload<SetValuePayload>()!),
                "camera.set_focus_mode"  => _cameraController.SetFocusMode(msg.GetPayload<SetValuePayload>()!),
                "camera.capture"         => await _cameraController.CaptureAsync(),
                "camera.capture_bulb"    => await _cameraController.CaptureBulbAsync(msg.GetPayload<CaptureBulbPayload>()!),
                "camera.liveview.start"  => _cameraController.StartLiveView(msg.GetPayload<LiveViewStartPayload>()!),
                "camera.liveview.stop"   => _cameraController.StopLiveView(),
                "camera.magnify"         => _cameraController.Magnify(),
                "camera.magnify_off"     => _cameraController.MagnifyOff(),
                "camera.focus"           => _cameraController.Focus(msg.GetPayload<FocusPayload>()!),
                "camera.get_all_widgets" => _cameraController.GetAllWidgets(),
                "camera.set_widget"      => _cameraController.SetWidget(msg.GetPayload<SetWidgetPayload>()!),

                // ── Mount ──
                "mount.move_static"      => await _mountController.MoveStaticAsync(msg.GetPayload<MountMovePayload>()!),
                "mount.move_relative"    => await _mountController.MoveRelativeAsync(msg.GetPayload<MountMoveRelativePayload>()!),
                "mount.start_linear"     => await _mountController.StartLinearAsync(msg.GetPayload<MountLinearPayload>()!),
                "mount.start_tracking"   => await _mountController.StartTrackingAsync(msg.GetPayload<MountTrackingPayload>()!),
                "mount.stop"             => await _mountController.StopAsync(),
                "mount.pause"            => await _mountController.PauseAsync(),
                "mount.resume"           => await _mountController.ResumeAsync(),
                "mount.get_position"     => await _mountController.GetPositionAsync(),
                "mount.alignment.init"  => _mountController.AlignmentInit(),
                "mount.alignment.add_star" => await _mountController.AlignmentAddStarAsync(msg.GetPayload<MountAlignStarPayload>()!),
                "mount.alignment.status" => _mountController.AlignmentStatus(),

                // ── Plate-Solve ──
                "mount.auto_center"      => await _mountController.AutoCenterAsync(msg.GetPayload<AutoCenterPayload>()!),
                "mount.auto_calibrate"   => await _mountController.AutoCalibrateAsync(msg.GetPayload<AutoCalibratePayload>() ?? new AutoCalibratePayload()),
                "mount.guided_tracking"  => await _mountController.StartGuidedTrackingAsync(msg.GetPayload<GuidedTrackingPayload>()!),
                "mount.guided_tracking.stop" => _mountController.StopGuidedTracking(),
                "mount.cancel"               => _mountController.CancelAutoOperation(),
                "mount.solve_current"    => await _mountController.SolveCurrentAsync(),
                "mount.solver.configure" => _mountController.ConfigurePlateSolver(msg.GetPayload<PlateSolveConfigPayload>() ?? new PlateSolveConfigPayload()),

                // ── System ──
                "system.info"            => GetSystemInfo(),
                "system.shutdown"        => Shutdown(),

                _ => throw new NotSupportedException($"Unknown action: {msg.Action}")
            };

            await _server.SendMessageAsync(Message.Response(msg.Id!, msg.Action, result));
        }
        catch (NotSupportedException ex)
        {
            Logger.Warn($"Unsupported action: {msg.Action} — {ex.Message}");
            await _server.SendMessageAsync(Message.Error(msg.Id, msg.Action, ex.Message));
        }
        catch (Exception ex)
        {
            Logger.Error($"Error handling {msg.Action}: {ex.Message}");
            await _server.SendMessageAsync(Message.Error(msg.Id, msg.Action, ex.Message));
        }
    }

    private static object GetSystemInfo()
    {
        return new SystemInfoPayload(
            Version: "1.0.0",
            Hostname: Environment.MachineName,
            Uptime: (long)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds
        );
    }

    private static object? Shutdown()
    {
        Logger.Notice("Shutdown requested via WebSocket.");
        // Schedule shutdown on a separate thread to allow the response to be sent first
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            Environment.Exit(0);
        });
        return new { message = "Shutting down..." };
    }
}
