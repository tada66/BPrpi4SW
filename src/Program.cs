using System;
using System.Linq;
using System.Net;

class Program
{
    static void Main()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("This program can only run on Linux.");
            return;
        }

        Camera? cam = null;
        try
        {
            // 1. List cameras
            cam = new Camera();
            Console.WriteLine("Detecting cameras...");
            var cameras = cam.GetAvailableCameras().ToList();
            
            if (cameras.Count == 0)
            {
                Console.WriteLine("No cameras found.");
                return;
            }

            Console.WriteLine("Found cameras:");
            for (int i = 0; i < cameras.Count; i++)
            {
                Console.WriteLine($"[{i}] {cameras[i].Model} on {cameras[i].Port}");
            }

            // 2. Select the first one (or add logic to pick specific one)
            var selected = cameras[0]; 
            Console.WriteLine($"Selecting: {selected.Model}");
            
            cam.ConnectCamera(selected);
            Console.WriteLine($"Camera {cam.cameramodel} connected.");
            Console.WriteLine("--- CAMERA INFO ---");
            Console.WriteLine($"Model: {cam.cameramodel}");
            Console.WriteLine($"Manufacturer: {cam.manufacturer}");
            Console.WriteLine($"Battery Level: {cam.batteryLevel}");
            Console.WriteLine($"Focus Mode: {cam.focus.mode}");
            Console.WriteLine("-------------------");
            cam.Iso.value = 1250;
            Console.WriteLine($"ISO set to: {cam.Iso.value}, index: {cam.Iso.index}");
            cam.shutterSpeed.value = "1/60";
            Console.WriteLine($"Shutter Speed set to: {cam.shutterSpeed.value}, index: {cam.shutterSpeed.index}");
            cam.aperture.value = "2.8";
            Console.WriteLine($"Aperture set to: {cam.aperture.value}, index: {cam.aperture.index}");
            //Console.WriteLine("Supported ISO values: " + string.Join(", ", cam.Iso.Values));
            //Console.WriteLine("Supported Shutter Speed values: " + string.Join(", ", cam.shutterSpeed.Values));
            //Console.WriteLine("Supported Aperture values: " + string.Join(", ", cam.aperture.Values));
            Console.WriteLine("Camera configuration done.");
            cam.CaptureImage("img");
            Console.WriteLine("Captured image");

            // Start Live View Streaming
            string targetHost = "10.0.0.20"; // PC IP
            int targetPort = 5000;
            Console.WriteLine($"Starting Live View Stream to {targetHost}:{targetPort}...");
            var sender = new TcpLiveViewSender(cam, targetHost, targetPort);
            sender.Start();

            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
            
            sender.Stop();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            cam?.Shutdown();
        }
    }
}