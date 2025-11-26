// See https://aka.ms/new-console-template for more information
using System;
using System.Linq;

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
            
            cam.ConnectCamera(selected.Port);
            cam.Iso.value = 1600;
            Console.WriteLine("ISO set to: " + cam.Iso.value);
            Console.WriteLine("ISO index: " + cam.Iso.index);
            cam.shutterSpeed.value = "1/60";
            Console.WriteLine("Shutter Speed set to: " + cam.shutterSpeed.value);
            Console.WriteLine("Shutter Speed index: " + cam.shutterSpeed.index);
            cam.aperture.value = "2";
            Console.WriteLine("Aperture set to: " + cam.aperture.value);
            Console.WriteLine("Aperture index: " + cam.aperture.index);
            Console.WriteLine("Supported ISO values: " + string.Join(", ", cam.Iso.Values));
            Console.WriteLine("Supported Shutter Speed values: " + string.Join(", ", cam.shutterSpeed.Values));
            Console.WriteLine("Supported Aperture values: " + string.Join(", ", cam.aperture.Values));
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