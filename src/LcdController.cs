using System;
using System.Device.Gpio;
using System.Threading;
using Iot.Device.CharacterLcd;

/// <summary>
/// 20×4 LCD layout:
///   Row 0 — connection info:  type + IP  (static, set once)
///   Row 1 — tracking target:  RA / Dec   (cycles every second)
///   Row 2 — motor position:   X / Y / Z  (cycles every second)
///   Row 3 — motor status:     temp + state (updated immediately on status events)
/// </summary>
public class LcdController : IDisposable
{
    private readonly Lcd2004? _lcd;
    private readonly GpioController? _gpio;
    private readonly object _lock = new();
    private readonly Timer _cycleTimer;
    private int _cycleStep = 0;

    // Cycling state
    private float _posX, _posY, _posZ;

    // Pin configuration (matches HotspotManager & lcd_boot.py)
    private const int PinRS = 26;
    private const int PinEN = 19;
    private const int PinD4 = 25;
    private const int PinD5 = 24;
    private const int PinD6 = 22;
    private const int PinD7 = 27;

    public LcdController()
    {
        try
        {
            _gpio = new GpioController();
            _lcd = new Lcd2004(
                registerSelectPin: PinRS,
                enablePin: PinEN,
                dataPins: new int[] { PinD4, PinD5, PinD6, PinD7 },
                controller: _gpio);

            _lcd.Clear();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to initialize LCD: {ex.Message}");
        }

        // Start 1-second cycling for rows 1 and 2
        _cycleTimer = new Timer(OnCycleTick, null, 1000, 1000);
    }

    // ── Public setters ────────────────────────────────────────────────────────

    /// <summary>Row 0: connection type (WiFi/ETH/Hotspot) and IP. Written immediately.</summary>
    public void SetConnectionInfo(string type, string ip)
    {
        lock (_lock) WriteLine(0, $"{type} {ip}");
    }

    /// <summary>Row 2: update last received motor axis positions.</summary>
    public void UpdatePosition(float x, float y, float z)
    {
        _posX = x;
        _posY = y;
        _posZ = z;
    }

    /// <summary>Row 3: motor temperature and enable/pause state. Written immediately.</summary>
    public void UpdateMotorStatus(float temp, bool enabled, bool paused)
    {
        string state = enabled ? (paused ? "   PAUSED" : "  RUNNING") : " DISABLED";
        lock (_lock) WriteLine(3, $"Temp:{temp:F1}C {state}");
    }

    // ── Cycle timer ───────────────────────────────────────────────────────────

    private void OnCycleTick(object? _)
    {
        int step = Interlocked.Increment(ref _cycleStep);

        // Row 1 — Tracking target (RA / Dec, alternating each second)
        float? ra  = Alignment.CurrentTargetRa;
        float? dec = Alignment.CurrentTargetDec;

        string row1 = (ra.HasValue && dec.HasValue)
            ? (step % 2 == 0
                ? $"RA:  {ra.Value,8:F3}h"
                : $"Dec: {dec.Value,7:+0.00;-0.00}\x00DF")   // 0xDF = ° on HD44780
            : "Target: none";

        // Row 2 — Axis positions (X / Y / Z, cycling)
        string row2 = (step % 3) switch
        {
            0 => $"X:  {(int)_posX,9} arcsec",
            1 => $"Y:  {(int)_posY,9} arcsec",
            _ => $"Z:  {(int)_posZ,9} arcsec",
        };

        lock (_lock)
        {
            WriteLine(1, row1);
            WriteLine(2, row2);
        }
    }

    // ── Low-level helpers ─────────────────────────────────────────────────────

    private void WriteLine(int row, string text)
    {
        if (_lcd == null) return;
        try
        {
            text = text.Length > 20 ? text[..20] : text.PadRight(20);
            _lcd.SetCursorPosition(0, row);
            _lcd.Write(text);
        }
        catch (Exception ex)
        {
            Logger.Error($"LCD Write Error: {ex.Message}");
        }
    }

    public void Clear()
    {
        if (_lcd == null) return;
        try { lock (_lock) _lcd.Clear(); } catch { }
    }

    public void Dispose()
    {
        _cycleTimer.Dispose();
        _lcd?.Dispose();
        _gpio?.Dispose();
    }
}
