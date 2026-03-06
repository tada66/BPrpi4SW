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
        Logger.Notice("Program started.");
        using var lcd = new LcdController();
        lcd.WriteStatus("Program Starting...");
        
        string ip = GetLocalIPAddress();
        lcd.WriteStatus($"IP: {ip}");

        if (!OperatingSystem.IsLinux())
        {
            Logger.Fatal("This program can only run on Linux.");
            lcd.WriteStatus("Error: Not Linux");
            return;
        }

        // Subscribe to UART events to update LCD
        UartClient.Client.PositionReceived += (x, y, z) => 
        {
            lcd.WritePos((float)x, (float)y, (float)z);
        };

        UartClient.Client.StatusReceived += (temp, x, y, z, enabled, paused, celestialTracking, fanPct) => 
        {
            lcd.WritePos((float)x, (float)y, (float)z);
            string state = enabled ? (paused ? "   PAUSED" : "  RUNNING") : " DISABLED";
            lcd.WriteStatus($"Temp:{temp:F1}C {state} ");
        };

        Logger.Info("UART Client started. LCD listening.");
        Logger.Notice("Please note that the original position of the device (0,0,0) is not necessarily the 'home' position. It is simply the position at which the device was powered on. The device must be calibrated to know it's actual position.");
        Logger.Notice("Also note that in case motors are disabled (not paused, but disabled), any calibration is LOST! Reported position will not match any previous readings and device must be recalibrated.");

        // ── Start device control server ──
        StartServer(lcd);

        // Keep the main thread alive
        Logger.Notice("Server running. Press Ctrl+C to stop.");
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
        await tcs.Task;

        Logger.Notice("Shutting down...");
    }

    private static void StartServer(LcdController lcd)
    {
        try
        {
            // 1. WebSocket server
            var wsServer = new WebSocketServer(WS_PORT);
            wsServer.ClientConnected += clientIp =>
            {
                lcd.WriteStatus($"Client: {clientIp}");
                Logger.Notice($"UI client connected from {clientIp}");
            };
            wsServer.ClientDisconnected += () =>
            {
                lcd.WriteStatus("No client");
            };
            wsServer.Start();

            // 2. HTTP file server (for captured image downloads)
            var httpServer = new HttpFileServer(HTTP_PORT);
            httpServer.Start();

            // 3. Camera controller
            var cameraController = new CameraController(wsServer, httpServer);

            // 4. Mount controller (subscribes to UART events, forwards via WebSocket)
            var mountController = new MountController(wsServer);

            // 5. Message router (connects WebSocket messages to controllers)
            var router = new MessageRouter(wsServer, cameraController, mountController);

            // 6. Status broadcaster (periodic camera status over WebSocket)
            var statusBroadcaster = new StatusBroadcaster(wsServer, cameraController);
            statusBroadcaster.Start();

            // 7. mDNS service advertisement
            var advertiser = new ServiceAdvertiser(WS_PORT, UDP_PORT, HTTP_PORT);
            advertiser.Start();

            string ip = GetLocalIPAddress();
            Logger.Notice($"Device control server started:");
            Logger.Notice($"  WebSocket : ws://{ip}:{WS_PORT}");
            Logger.Notice($"  UDP LV    : {ip}:{UDP_PORT}");
            Logger.Notice($"  HTTP Files: http://{ip}:{HTTP_PORT}/captures/");
            Logger.Notice($"  mDNS      : _bpcontrol._tcp.local");

            lcd.WriteStatus($"Srv:{WS_PORT} {ip}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to start server: {ex.Message}");
            lcd.WriteStatus("Server Error!");
        }
    }

    static string GetLocalIPAddress()
    {
        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);

            foreach (var iface in interfaces)
            {
                var props = iface.GetIPProperties();
                var ip = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address)
                    .FirstOrDefault();
                
                if (ip != null) return ip.ToString();
            }
        }
        catch { }
        return "No IP";
    }
}