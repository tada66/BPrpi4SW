using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

public class TcpLiveViewSender
{
    private readonly Camera _camera;
    private readonly string _host;
    private readonly int _port;
    private bool _running;
    private Thread? _thread;

    public TcpLiveViewSender(Camera camera, string host, int port)
    {
        _camera = camera;
        _host = host;
        _port = port;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop);
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join();
    }

    private void Loop()
    {
        while (_running)
        {
            try
            {
                using var client = new TcpClient();
                Console.WriteLine($"Connecting to {_host}:{_port}...");
                client.Connect(_host, _port);
                using var stream = client.GetStream();
                Console.WriteLine("Connected. Streaming live view...");

                int frameCount = 0;

                while (_running && client.Connected)
                {
                    // 1. Check for incoming commands
                    if (stream.DataAvailable)
                    {
                        HandleIncomingCommand(stream);
                    }

                    // 2. Send Metadata periodically (every 30 frames ~ 1 sec)
                    if (frameCount % 30 == 0)
                    {
                        SendMetadata(stream);
                    }

                    // 3. Send Live View Frame
                    byte[] frame = _camera.GetLiveViewBytes();
                    if (frame.Length > 0)
                    {
                        SendPacket(stream, 0x02, frame);
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }
                    
                    frameCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Streaming error: {ex.Message}");

                // If camera disconnected (GP_ERROR_IO_USB_FIND) or not found (GP_ERROR_MODEL_NOT_FOUND), 
                // shutdown driver and clear specific port so we can try to auto-detect on re-init
                if (ex.Message.Contains("GP_ERROR_IO_USB_FIND") || ex.Message.Contains("GP_ERROR_MODEL_NOT_FOUND"))
                {
                    Console.WriteLine("Camera lost or not found. Resetting connection state...");
                    try 
                    { 
                        _camera.Shutdown();
                        _camera.ClearSelectedCamera(); 
                    } 
                    catch (Exception shutdownEx) { Console.WriteLine($"Shutdown error: {shutdownEx.Message}"); }
                }

                Thread.Sleep(1000); // Retry delay
            }
        }
    }

    private void HandleIncomingCommand(NetworkStream stream)
    {
        try
        {
            // Read Header: [Type (1)][Length (4)]
            int type = stream.ReadByte();
            if (type == -1) return;

            byte[] lenBytes = new byte[4];
            int read = stream.Read(lenBytes, 0, 4);
            if (read < 4) return;

            if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
            int length = BitConverter.ToInt32(lenBytes, 0);

            byte[] payload = new byte[length];
            int totalRead = 0;
            while (totalRead < length)
            {
                int r = stream.Read(payload, totalRead, length - totalRead);
                if (r == 0) break;
                totalRead += r;
            }

            if (type == 0x03) // Command
            {
                string json = Encoding.UTF8.GetString(payload);
                var cmd = JsonSerializer.Deserialize<CommandPacket>(json);
                if (cmd != null) ExecuteCommand(cmd);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling command: {ex.Message}");
        }
    }

    private void ExecuteCommand(CommandPacket cmd)
    {
        Console.WriteLine($"Executing command: {cmd.Action} {cmd.Value}");
        try
        {
            switch (cmd.Action)
            {
                case "set_iso":
                    if (int.TryParse(cmd.Value, out int iso)) _camera.Iso.value = iso;
                    break;
                case "set_shutter":
                    _camera.shutterSpeed.value = cmd.Value;
                    break;
                case "set_aperture":
                    _camera.aperture.value = cmd.Value;
                    break;
                case "magnify":
                    _camera.Magnify();
                    break;
                case "magnify_off":
                    _camera.MagnifyOff();
                    break;
                case "focus_closer":
                    if (int.TryParse(cmd.Value, out int stepCloser)) _camera.focus.Closer(stepCloser);
                    else _camera.focus.Closer(2); // Default step
                    break;
                case "focus_further":
                    if (int.TryParse(cmd.Value, out int stepFurther)) _camera.focus.Further(stepFurther);
                    else _camera.focus.Further(2); // Default step
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to execute command: {ex.Message}");
        }
    }

    private void SendMetadata(NetworkStream stream)
    {
        try
        {
            var meta = new
            {
                iso = _camera.Iso.value.ToString(),
                shutter = _camera.shutterSpeed.value,
                aperture = _camera.aperture.value
            };
            string json = JsonSerializer.Serialize(meta);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            SendPacket(stream, 0x01, bytes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending metadata: {ex.Message}");
        }
    }

    private void SendPacket(NetworkStream stream, byte type, byte[] payload)
    {
        stream.WriteByte(type);
        
        byte[] lenBytes = BitConverter.GetBytes(payload.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
        stream.Write(lenBytes, 0, 4);
        
        stream.Write(payload, 0, payload.Length);
    }

    private class CommandPacket
    {
        public string Action { get; set; } = "";
        public string Value { get; set; } = "";
    }
}
