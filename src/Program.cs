using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Iot.Device.CharacterLcd;

class Program
{
    // Server ports
    private const int WS_PORT = 4400;
    private const int UDP_PORT = 4401;
    private const int HTTP_PORT = 4402;

    static async Task Main()
    {
        Logger.Notice("StarTracker v1.0.2 started."); 
        using var lcd = new LcdController();

        if (!OperatingSystem.IsLinux())
        {
            Logger.Fatal("This program can only run on Linux.");
            return;
        }

        // Ensure we have a network — create a hotspot if no WiFi/Ethernet is available
        string ip = await HotspotManager.EnsureNetworkAsync();


        // Row 0: permanent connection info
        lcd.SetConnectionInfo(HotspotManager.GetConnectionType(ip), ip);

        // Subscribe to UART events to update LCD
        UartClient.Client.PositionReceived += (x, y, z) =>
        {
            lcd.UpdatePosition((float)x, (float)y, (float)z);
        };

        UartClient.Client.StatusReceived += (temp, x, y, z, enabled, paused, celestialTracking, fanPct) =>
        {
            lcd.UpdatePosition((float)x, (float)y, (float)z);
            lcd.UpdateMotorStatus(temp, enabled, paused);
        };

        Logger.Info("UART Client started. LCD listening.");
        Logger.Notice("Please note that the original position of the device (0,0,0) is not necessarily the 'home' position. It is simply the position at which the device was powered on. The device must be calibrated to know it's actual position.");
        Logger.Notice("Also note that in case motors are disabled (not paused, but disabled), any calibration is LOST! Reported position will not match any previous readings and device must be recalibrated.");

        // ── Start device control server ──
        StartServer(lcd, ip);

        // Keep the main thread alive
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
        await tcs.Task;

        Logger.Notice("Shutting down...");
    }

    private static void StartServer(LcdController lcd, string ip)
    {
        try
        {
            // 1. WebSocket server
            var wsServer = new WebSocketServer(WS_PORT);
            wsServer.ClientConnected += clientIp =>
            {
                Logger.Notice($"UI client connected from {clientIp}");
            };
            wsServer.ClientDisconnected += () =>
            {
                Logger.Notice($"UI client disconnected");
            };
            wsServer.Start();

            // 2. HTTP file server (for captured image downloads)
            var httpServer = new HttpFileServer(HTTP_PORT);
            httpServer.Start();

            // 3. Camera controller
            var cameraController = new CameraController(wsServer, httpServer);

            // 4. Mount controller (subscribes to UART events, forwards via WebSocket)
            var mountController = new MountController(wsServer, cameraController);

            // 5. Message router (connects WebSocket messages to controllers)
            var router = new MessageRouter(wsServer, cameraController, mountController);

            // 6. Status broadcaster (periodic camera status over WebSocket)
            var statusBroadcaster = new StatusBroadcaster(wsServer, cameraController);
            statusBroadcaster.Start();

            // 7. mDNS service advertisement
            var advertiser = new ServiceAdvertiser(WS_PORT, UDP_PORT, HTTP_PORT);
            advertiser.Start();

            Logger.Info($"Device control server started:");
            Logger.Info($"  WebSocket : ws://{ip}:{WS_PORT}");
            Logger.Info($"  UDP LV    : {ip}:{UDP_PORT}");
            Logger.Info($"  HTTP Files: http://{ip}:{HTTP_PORT}/captures/");
            Logger.Info($"  mDNS      : _bpcontrol._tcp.local");
            if (ip == HotspotManager.HotspotIp)
                Logger.Info($"  Hotspot   : SSID={HotspotManager.HotspotSsid} PW={HotspotManager.HotspotPassword}");
        }
        catch (Exception ex)
        {
            Logger.Fatal($"Failed to start server: {ex.Message}");
        }
    }
}