using System;
using System.Device.Gpio;
using System.Threading;
using Iot.Device.CharacterLcd;

public class LcdController : IDisposable
{
    private readonly Lcd2004? _lcd;
    private readonly GpioController? _gpio;

    // Pin configuration
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
    }

    public void WritePos(float x, float y, float z)
    {
        string Fmt(float v) => (v >= 0 ? " " : "") + v.ToString("00000000");
        WriteLine(0, $"X: {Fmt(x)} arcsecs");
        WriteLine(1, $"Y: {Fmt(y)} arcsecs");
        WriteLine(2, $"Z: {Fmt(z)} arcsecs");
    }

    public void WriteStatus(string status){
        WriteLine(3, status);
    }

    private void WriteLine(int line, string text)
    {
        if (_lcd == null) return;
        
        try
        {
            // Pad with spaces to clear the rest of the line
            if (text.Length < 20)
            {
                text = text.PadRight(20);
            }
            else if (text.Length > 20)
            {
                text = text.Substring(0, 20);
            }

            _lcd.SetCursorPosition(0, line);
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
        try
        {
            _lcd.Clear();
        }
        catch { }
    }

    public void Dispose()
    {
        _lcd?.Dispose();
        _gpio?.Dispose();
    }
}
