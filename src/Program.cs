using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Iot.Device.CharacterLcd;

class Program
{
    static async Task Main()
    {
        Logger.Debug("Program started");
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
        CameraTest();
        while(true)
        {
            try 
            {
                await UartClient.Client.RunInteractiveAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                await Task.Delay(1000);
            }
        }
    }



    private static void CameraTest()
    {
        Camera? cam = null;
        try
        {
            cam = new Camera();
            Logger.Info("Detecting cameras...");
            var cameras = cam.GetAvailableCameras().ToList();
            
            if (cameras.Count == 0)
            {
                Logger.Warn("No cameras found.");
                return;
            }

            string foundCams = "Cameras found:";
            for (int i = 0; i < cameras.Count; i++)
            {
                foundCams += $"\n[{i}] {cameras[i].Model} on {cameras[i].Port}";
            }
            Logger.Debug(foundCams);

            // 2. Select the first one (or add logic to pick specific one)
            var selected = cameras[0]; 
            Logger.Info($"Selecting: {selected.Model}");
            
            cam.ConnectCamera(selected);
            Logger.Notice($"Camera {cam.cameramodel} connected.");
            Logger.Debug($"Camera Model: {cam.cameramodel}");
            Logger.Debug($"Camera Manufacturer: {cam.manufacturer}");
            Logger.Debug($"Camera Battery Level: {cam.batteryLevel}");
            Logger.Debug($"Camera Focus Mode: {cam.focus.mode}");
            Logger.Debug($"Camera Focus range: {string.Join(", ", cam.focus.GetManualFocusDriveRange() ?? (0,0,0))}");
            
            /*try {
                cam.Iso.value = 1250;
                Logger.Debug($"ISO set to: {cam.Iso.value}, index: {cam.Iso.index}");
                cam.shutterSpeed.value = "1/60";
                Logger.Debug($"Shutter Speed set to: {cam.shutterSpeed.value}, index: {cam.shutterSpeed.index}");
                cam.aperture.value = "8";
                Logger.Debug($"Aperture set to: {cam.aperture.value}, index: {cam.aperture.index}");
            } catch (Exception ex) {
                Logger.Warn($"Config warning: {ex.Message}");
            }*/
            


            Logger.Info("Supported ISO values: " + string.Join(", ", cam.Iso.Values));
            Logger.Info("Supported Shutter Speed values: " + string.Join(", ", cam.shutterSpeed.Values));
            Logger.Info("Supported Aperture values: " + string.Join(", ", cam.aperture.Values));
            Logger.Info("Camera configuration done.");
            //Console.WriteLine("Capturing image...");
            //cam.CaptureImage("NIKON_captured_image");
            //Console.WriteLine("Image captured.");
            //Console.WriteLine("Capturing bulb exposure (2s)...");
            //cam.shutterSpeed.value = "Bulb";
            //cam.CaptureImageBulb(2, "NIKONB");
            //Console.WriteLine("Bulb exposure captured.");

            // Start Live View Streaming
            string targetHost = "10.0.0.20"; // PC IP
            int targetPort = 5000;
            Logger.Notice($"Starting Live View Stream to {targetHost}:{targetPort}...");
            
            var sender = new TcpLiveViewSender(cam, targetHost, targetPort);
            sender.Start();

            //Console.WriteLine("Press Enter to exit...");
            //Console.ReadLine();
            
            //sender.Stop();
        }
        catch (Exception ex)
        {
            Logger.Error($"Error: {ex.Message}");
        }
        finally
        {
            cam?.Shutdown();
        }
    }
    static string GetLocalIPAddress()
    {
        try
        {
            // Iterate over network interfaces to find a real IP, avoiding 127.0.1.1 (common on Linux)
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .Where(i => i.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);

            foreach (var iface in interfaces)
            {
                var props = iface.GetIPProperties();
                // Prefer WLAN or Ethernet
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