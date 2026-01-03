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


        using var uart = new UartClient();
        
        // Subscribe to UART events to update LCD
        uart.PositionReceived += (x, y, z) => 
        {
            lcd.WritePos((float)x, (float)y, (float)z);
        };

        uart.StatusReceived += (temp, x, y, z, enabled, paused, celestialTracking, fanPct) => 
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
                await uart.RunInteractiveAsync();
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
            Console.WriteLine("Detecting cameras...");
            var cameras = cam.GetAvailableCameras().ToList();
            
            if (cameras.Count == 0)
            {
                Console.WriteLine("No cameras found.");
                // lcd.WriteStatus("No Camera Found");
                return;
            }

            for (int i = 0; i < cameras.Count; i++)
            {
                Logger.Debug($"[{i}] {cameras[i].Model} on {cameras[i].Port}");
            }

            // 2. Select the first one (or add logic to pick specific one)
            var selected = cameras[0]; 
            Logger.Info($"Selecting: {selected.Model}");
            
            cam.ConnectCamera(selected);
            Logger.Notice($"Camera {cam.cameramodel} connected.");
            // lcd.WriteStatus("Connected");

            Logger.Debug("--- CAMERA INFO ---");
            Logger.Debug($"Model: {cam.cameramodel}");
            Logger.Debug($"Manufacturer: {cam.manufacturer}");
            Logger.Debug($"Battery Level: {cam.batteryLevel}");
            Logger.Debug($"Focus Mode: {cam.focus.mode}");
            Logger.Debug($"Focus range: {string.Join(", ", cam.focus.GetManualFocusDriveRange() ?? (0,0,0))}");
            Logger.Debug("-------------------");
            // lcd.WriteStatus(cam.cameramodel + " Ready");
            
            try {
                cam.Iso.value = 1250;
                Console.WriteLine($"ISO set to: {cam.Iso.value}, index: {cam.Iso.index}");
                cam.shutterSpeed.value = "1/60";
                Console.WriteLine($"Shutter Speed set to: {cam.shutterSpeed.value}, index: {cam.shutterSpeed.index}");
                cam.aperture.value = "8";
                Console.WriteLine($"Aperture set to: {cam.aperture.value}, index: {cam.aperture.index}");
            } catch (Exception ex) {
                Console.WriteLine($"Config warning: {ex.Message}");
            }
            


            Console.WriteLine("Supported ISO values: " + string.Join(", ", cam.Iso.Values));
            Console.WriteLine("Supported Shutter Speed values: " + string.Join(", ", cam.shutterSpeed.Values));
            Console.WriteLine("Supported Aperture values: " + string.Join(", ", cam.aperture.Values));
            Console.WriteLine("Camera configuration done.");
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
            // lcd.WriteStatus("Streaming...");
            
            var sender = new TcpLiveViewSender(cam, targetHost, targetPort);
            sender.Start();

            //Console.WriteLine("Press Enter to exit...");
            //Console.ReadLine();
            
            //sender.Stop();
        }
        catch (Exception ex)
        {
            Logger.Error($"Error: {ex.Message}");
            // lcd.WriteStatus("Error!");
            // lcd.WriteStatus(ex.Message);
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