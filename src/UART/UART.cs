using System;
using System.IO.Ports;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Drawing.Text;

public static class Axis
{
    public const byte X = 0;
    public const byte Y = 1;
    public const byte Z = 2;
}

/// <summary>
/// C# implementation of the UART communication protocol from commFINAL.py
/// </summary>
public sealed class UartClient : IDisposable
{
    private enum UartCommand : byte
    {
        CMD_ACK = 0x01,
        CMD_MOVE_STATIC = 0x10,
        CMD_MOVE_TRACKING = 0x11,
        CMD_PAUSE = 0x12,
        CMD_RESUME = 0x13,
        CMD_STOP = 0x14,
        CMD_GETPOS = 0x20,
        CMD_POSITION = 0x21,
        CMD_STATUS = 0x22,
        CMD_ESTOPTRIG = 0x30
    }

    private readonly SerialPort _port;
    private readonly Thread _rxThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _writeLock = new();
    
    // ACK tracking
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<bool>> _pendingAcks = new();
    private byte _lastSentId = 0x00;

    // Events for received data
    public event Action<int, int, int>? PositionReceived;
    public event Action<float, int, int, int, bool, bool, int>? StatusReceived;
    //public event Action? EstopTriggered;

    public UartClient(string? portName = null, int baudRate = 9600)
    {
        // Auto-detect port if not specified
        portName ??= FindSerialPort();
        if (portName == null)
            throw new InvalidOperationException("No suitable serial port found!");

        Logger.Info($"Using serial port: {portName}");

        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 1000,
            WriteTimeout = 500
        };
        _port.Open();

        // Reset sequence - clear any pending data
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();

        Logger.Debug("Sending reset bytes...");
        lock (_writeLock)
        {
            _port.Write(new byte[] { 0x00, 0x00, 0x00 }, 0, 3);
        }
        Thread.Sleep(100);

        // Clear any junk that came in
        if (_port.BytesToRead > 0)
        {
            var junk = new byte[_port.BytesToRead];
            _port.Read(junk, 0, junk.Length);
            Logger.Notice($"Cleared {junk.Length} bytes of pending data");
        }

        Logger.Info("UART connection established.");

        // Start receiver thread
        _rxThread = new Thread(ReceiverThread) { IsBackground = true };
        _rxThread.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _rxThread.Join(1000); } catch { }
        if (_port.IsOpen) _port.Close();
        _port.Dispose();
        _cts.Dispose();
    }

    private static string? FindSerialPort()
    {
        // Common Linux serial ports
        string[] potentialPorts = { "/dev/ttyS0", "/dev/serial0", "/dev/ttyAMA0", "/dev/ttyUSB0" };
        foreach (var port in potentialPorts)
        {
            if (System.IO.File.Exists(port))
                return port;
        }
        return null;
    }

    #region CRC8 Calculation
    
    /// <summary>
    /// Calculate CRC8 checksum (polynomial 0x07, init 0xFF)
    /// </summary>
    private static byte CalculateCrc8(byte[] data, int offset, int count)
    {
        int crc = 0xFF;
        const int polynomial = 0x07;

        for (int i = 0; i < count; i++)
        {
            crc ^= data[offset + i];
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x80) != 0)
                    crc = (crc << 1) ^ polynomial;
                else
                    crc <<= 1;
                crc &= 0xFF;
            }
        }
        return (byte)crc;
    }

    #endregion

    #region Message ID Generation

    private byte GenerateMsgId()
    {
        byte newId;
        do
        {
            newId = (byte)Random.Shared.Next(1, 256);
        } while (newId == _lastSentId || newId == 0x00);
        
        _lastSentId = newId;
        return newId;
    }

    #endregion

    #region Send Commands

    /// <summary>
    /// Send a command and wait for ACK
    /// </summary>
    private async Task<bool> SendCommandAsync(UartCommand cmd, byte[]? data = null, int timeoutMs = 2000, uint maxAttempts = 3)
    {
        data ??= Array.Empty<byte>();
        byte msgId = GenerateMsgId();

        // Build raw packet: CMD, ID, LEN, DATA..., CRC
        byte[] raw = new byte[3 + data.Length + 1];
        raw[0] = (byte)cmd;
        raw[1] = msgId;
        raw[2] = (byte)data.Length;
        if (data.Length > 0)
            Array.Copy(data, 0, raw, 3, data.Length);
        raw[^1] = CalculateCrc8(raw, 0, raw.Length - 1);

        // COBS encode and add delimiter
        byte[] encoded = Cobs.Encode(raw);
        byte[] packet = new byte[encoded.Length + 1];
        Array.Copy(encoded, packet, encoded.Length);
        packet[^1] = 0x00;


        // For ACK commands, don't wait for ACK (avoid infinite loop)
        if (cmd == UartCommand.CMD_ACK)
        {
            Logger.Debug($"Sending ACK command: {Convert.ToHexString(packet)}  CMD  : 0x{(byte)cmd:X2}  ID   : {msgId} (0x{msgId:X2})  LEN  : {data.Length}  DATA : {(data.Length > 0 ? Convert.ToHexString(data) : "N/A")}  CRC8 : 0x{raw[^1]:X2}");
            lock (_writeLock)
            {
                _port.Write(packet, 0, packet.Length);
            }
            return true;
        }

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Logger.Debug($"Sent command: {Convert.ToHexString(packet)}  CMD  : 0x{(byte)cmd:X2}  ID   : {msgId} (0x{msgId:X2})  LEN  : {data.Length}  DATA : {(data.Length > 0 ? Convert.ToHexString(data) : "N/A")}  CRC8 : 0x{raw[^1]:X2} Attempt {attempt}/{maxAttempts}");

            // Track pending ACK
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[msgId] = tcs;

            lock (_writeLock)
            {
                _port.Write(packet, 0, packet.Length);
            }

            // Wait for ACK with timeout
            using var cts = new CancellationTokenSource(timeoutMs);
            using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                try
                {
                    bool result = await tcs.Task;
                    Logger.Info($"Command ACKed");
                    return result;
                }
                catch (TaskCanceledException)
                {
                    _pendingAcks.TryRemove(msgId, out _);
                    Logger.Warn($"No ACK received for ID={msgId}, CMD=0x{(byte)cmd:X2}");
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(50); // Small delay before retry
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Send ACK for a received message
    /// </summary>
    private void SendAck(byte msgIdToAck)
    {
        Logger.Debug($"  Sending ACK for message ID {msgIdToAck}");
        _ = SendCommandAsync(UartCommand.CMD_ACK, new byte[] { msgIdToAck });
    }

    #endregion

    #region Receiver Thread

    private void ReceiverThread()
    {
        var buffer = new List<byte>();

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                int b = _port.ReadByte();
                if (b == -1) continue;

                if (b == 0x00)
                {
                    // Frame complete
                    if (buffer.Count > 0)
                    {
                        byte[] frame = buffer.ToArray();
                        buffer.Clear();

                        string hexStr = BitConverter.ToString(frame).Replace("-", "");
                        Logger.Debug($"Received data: {hexStr}");

                        // Reasonable frame size check
                        if (frame.Length > 50)
                        {
                            Logger.Warn($"Oversized frame ({frame.Length} bytes) - likely corrupted");
                            continue;
                        }

                        try
                        {
                            Logger.Debug($"COBS frame received ({frame.Length} bytes): {hexStr}");
                            byte[] decoded = Cobs.Decode(frame);
                            Logger.Debug($"Decoded ({decoded.Length} bytes): {BitConverter.ToString(decoded).Replace("-", "")}");
                            ProcessBinaryMessage(decoded);
                        }
                        catch (Exception e)
                        {
                            Logger.Error($"COBS decoding error: {e.Message}");
                        }
                    }
                }
                else
                {
                    buffer.Add((byte)b);
                    // Safety cap
                    if (buffer.Count > 256) buffer.Clear();
                }
            }
            catch (TimeoutException) { }
            catch (Exception)
            {
                if (_cts.Token.IsCancellationRequested) break;
                Thread.Sleep(50);
            }
        }
    }

    private void ProcessBinaryMessage(byte[] decoded)
    {
        if (decoded.Length < 4)
        {
            Logger.Notice($"Decoded message too short: {decoded.Length} bytes");
            return;
        }

        byte cmdType = decoded[0];
        byte msgId = decoded[1];
        byte dataLength = decoded[2];

        // Verify message length
        if (decoded.Length != dataLength + 4)
        {
            Logger.Warn($"Invalid message length: expected {dataLength + 4}, got {decoded.Length}, recovering");
            return;
        }

        // Verify CRC
        byte receivedCrc = decoded[^1];
        byte calculatedCrc = CalculateCrc8(decoded, 0, decoded.Length - 1);
        bool crcValid = receivedCrc == calculatedCrc;

        // Check for invalid message ID
        if (msgId == 0x00)
        {
            Logger.Notice("Received message with invalid ID 0x00, ignoring");
            return;
        }

        Logger.Debug($"Received binary message:  CMD  : 0x{cmdType:X2}  ID   : {msgId} (0x{msgId:X2})  LEN  : {dataLength}");


        // Extract data
        byte[] data = new byte[dataLength];
        if (dataLength > 0)
            Array.Copy(decoded, 3, data, 0, dataLength);

        if (dataLength > 0)
            Logger.Debug($"  DATA : {BitConverter.ToString(data).Replace("-", "")}");

        // Process specific message types
        var cmd = (UartCommand)cmdType;

        switch (cmd)
        {
            case UartCommand.CMD_ACK:
                if (dataLength >= 1)
                {
                    byte ackedId = data[0];
                    Logger.Debug($"  Received ACK for message ID: {ackedId} (0x{ackedId:X2})");
                    if (_pendingAcks.TryRemove(ackedId, out var tcs))
                    {
                        tcs.TrySetResult(true);
                        Logger.Debug($"  Message with ID={ackedId} marked as acknowledged");
                    }
                    else
                    {
                        Logger.Info($"  Received ACK for unknown message ID: {ackedId}");
                    }
                }
                break;
            case UartCommand.CMD_STATUS:
                if (dataLength >= 19)
                {
                    try
                    {
                        float temp = BitConverter.ToSingle(data, 0);
                        int x = BitConverter.ToInt32(data, 4);
                        int y = BitConverter.ToInt32(data, 8);
                        int z = BitConverter.ToInt32(data, 12);
                        bool enabled = data[16] != 0;
                        bool paused = data[17] != 0;
                        int fanPct = data[18];

                        string stateStr = $"{(enabled ? "ENABLED" : "DISABLED")}, {(paused ? "PAUSED" : "RUNNING")}";
                        Console.WriteLine($"Status: Temp={temp:F2}°C, Positions: X={x}, Y={y}, Z={z} arcseconds, Motors: {stateStr}, Fan={fanPct}%");
                        
                        StatusReceived?.Invoke(temp, x, y, z, enabled, paused, fanPct);
                    }
                    catch (Exception e)
                    {
                        Logger.Error($"Error parsing telemetry: {e.Message}");
                    }
                }
                break;
            case UartCommand.CMD_POSITION:
                if (dataLength >= 12)
                {
                    try
                    {
                        int x = BitConverter.ToInt32(data, 0);
                        int y = BitConverter.ToInt32(data, 4);
                        int z = BitConverter.ToInt32(data, 8);

                        Console.WriteLine($"Positions: X={x}, Y={y}, Z={z} arcseconds");
                        PositionReceived?.Invoke(x, y, z);
                    }
                    catch (Exception e)
                    {
                        Logger.Error($"Error parsing positions: {e.Message}");
                    }
                }
                break;
            // ESTOP is broken on the pcb so it cannot be activated and thus tested, deactivated for now
            // TODO: reactivate estop if a new pcb is made
            /*case UartCommand.CMD_ESTOPTRIG:
                Console.WriteLine("ESTOP TRIGGERED!");
                EstopTriggered?.Invoke();
                break;
            */
            default:
                Logger.Warn($"[Warning] Received unknown command type: 0x{cmdType:X2}");
                break;
        }

        Logger.Debug($"  CRC8 : 0x{receivedCrc:X2} ({(crcValid ? "Valid" : "INVALID")})");

        // Report CRC errors
        if (!crcValid)
        {
            Logger.Warn($"CRC error in message ID {msgId}");
            return;
        }

        // Send ACK for valid non-ACK messages
        if (cmd != UartCommand.CMD_ACK && crcValid)
        {
            SendAck(msgId);
        }
    }

    #endregion

    #region High-Level Command Methods

    public Task<bool> Ping() => SendCommandAsync(UartCommand.CMD_ACK);
    
    public Task<bool> PauseMotors()
    {
        Logger.Notice("Pausing motors...");
        return SendCommandAsync(UartCommand.CMD_PAUSE);
    }

    public Task<bool> ResumeMotors()
    {
        Logger.Notice("Resuming motors...");
        return SendCommandAsync(UartCommand.CMD_RESUME);
    }

    public Task<bool> StopAll()
    {
        Logger.Notice("Stopping all movement...");
        return SendCommandAsync(UartCommand.CMD_STOP);
    }

    public Task<bool> GetPositions()
    {
        return SendCommandAsync(UartCommand.CMD_GETPOS);
    }

    public Task<bool> MoveStatic(byte axis, int positionArcsec)
    {
        string axisName = axis switch { 0 => "X", 1 => "Y", 2 => "Z", _ => "Unknown" };
        Logger.Notice($"Moving {axisName} axis to {positionArcsec} arcseconds...");

        var data = new byte[5];
        data[0] = axis;
        Array.Copy(BitConverter.GetBytes(positionArcsec), 0, data, 1, 4);
        return SendCommandAsync(UartCommand.CMD_MOVE_STATIC, data);
    }

    public Task<bool> StartTracking(float xRate, float yRate, float zRate)
    {
        Logger.Notice($"Starting tracking: X={xRate}, Y={yRate}, Z={zRate} arcsec/sec");

        var data = new byte[12];
        Array.Copy(BitConverter.GetBytes(xRate), 0, data, 0, 4);
        Array.Copy(BitConverter.GetBytes(yRate), 0, data, 4, 4);
        Array.Copy(BitConverter.GetBytes(zRate), 0, data, 8, 4);
        return SendCommandAsync(UartCommand.CMD_MOVE_TRACKING, data);
    }

    #endregion

    #region Interactive Mode

    public static void PrintHelp()
    {
        Console.WriteLine("\nAvailable commands:");
        Console.WriteLine("1 - Send ping");
        Console.WriteLine("2 - Pause motors");
        Console.WriteLine("3 - Resume motors");
        Console.WriteLine("5 - Stop all movement");
        Console.WriteLine("x - Move X axis (absolute)");
        Console.WriteLine("y - Move Y axis (absolute)");
        Console.WriteLine("z - Move Z axis (absolute)");
        Console.WriteLine("p - Get current positions (all axes)");
        Console.WriteLine("t - Start tracking mode");
        Console.WriteLine("h - Show this help");
        Console.WriteLine("q - Quit");
    }

    public async Task RunInteractiveAsync()
    {
        PrintHelp();

        while (true)
        {
            Console.Write("> ");
            string? input = Console.ReadLine()?.Trim().ToLower();
            if (string.IsNullOrEmpty(input)) continue;

            switch (input)
            {
                case "q":
                case "quit":
                    Console.WriteLine("Exiting...");
                    return;

                case "h":
                case "help":
                    PrintHelp();
                    break;

                case "1":
                    Console.WriteLine("Sending ping...");
                    await Ping();
                    break;

                case "2":
                    await PauseMotors();
                    break;

                case "3":
                    await ResumeMotors();
                    break;

                case "5":
                    await StopAll();
                    break;

                case "x":
                    Console.Write("Enter X position (arcseconds): ");
                    if (int.TryParse(Console.ReadLine(), out int xPos))
                        await MoveStatic(Axis.X, xPos);
                    else
                        Console.WriteLine("Invalid position.");
                    break;

                case "y":
                    Console.Write("Enter Y position (arcseconds): ");
                    if (int.TryParse(Console.ReadLine(), out int yPos))
                        await MoveStatic(Axis.Y, yPos);
                    else
                        Console.WriteLine("Invalid position.");
                    break;

                case "z":
                    Console.Write("Enter Z position (arcseconds): ");
                    if (int.TryParse(Console.ReadLine(), out int zPos))
                        await MoveStatic(Axis.Z, zPos);
                    else
                        Console.WriteLine("Invalid position.");
                    break;

                case "p":
                    await GetPositions();
                    break;

                case "t":
                    Console.Write("Enter X rate (arcsec/sec): ");
                    if (!float.TryParse(Console.ReadLine(), out float xRate)) { Console.WriteLine("Invalid."); break; }
                    Console.Write("Enter Y rate (arcsec/sec): ");
                    if (!float.TryParse(Console.ReadLine(), out float yRate)) { Console.WriteLine("Invalid."); break; }
                    Console.Write("Enter Z rate (arcsec/sec): ");
                    if (!float.TryParse(Console.ReadLine(), out float zRate)) { Console.WriteLine("Invalid."); break; }
                    await StartTracking(xRate, yRate, zRate);
                    break;

                default:
                    Console.WriteLine($"Unknown command: '{input}'. Type 'h' for help.");
                    break;
            }
        }
    }

    #endregion
}
