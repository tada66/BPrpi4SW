// Author: Tadeáš Horák - xhorakt00
// Bachelor's thesis: Motorized star tracker

using System.Device.Gpio;
using System.Device.Spi;
using System.IO;
using System.Linq;
using System.Threading;
using ImageSharpColor = SixLabors.ImageSharp.Color;
using SixLabors.Fonts;
using SixLaborsFont = SixLabors.Fonts.Font;
using FontStyle = SixLabors.Fonts.FontStyle;
using SystemFonts = SixLabors.Fonts.SystemFonts;
using PointF = SixLabors.ImageSharp.PointF;
using Rectangle = SixLabors.ImageSharp.Rectangle;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

/// <summary>
/// Full-screen dashboard controller for the HX8357 SPI LCD.
/// This implementation renders directly in C# and writes pixels to the display over SPI.
/// </summary>
public sealed class LcdController : IDisposable
{
    private const int DisplayWidth = 480;
    private const int DisplayHeight = 320;

    private readonly object _stateLock = new();
    private readonly Hx8357Display _display;
    private readonly Timer _refreshTimer;
    private bool _diagnosticMode = false;

    private readonly SixLaborsFont _fontXl;
    private readonly SixLaborsFont _fontLarge;
    private readonly SixLaborsFont _fontMed;
    private readonly SixLaborsFont _fontSmall;

    private string _connectionType = "Network";
    private string _ipAddress = "0.0.0.0";
    private float _posX;
    private float _posY;
    private float _posZ;
    private float _tempC;
    private bool _motorsEnabled;
    private bool _motorsPaused;

    public LcdController()
    {
        _fontXl = LoadFont(50);
        _fontLarge = LoadFont(35);
        _fontMed = LoadFont(22);
        _fontSmall = LoadFont(18);

        _display = new Hx8357Display(DisplayWidth, DisplayHeight);
        try
        {
            _display.Initialize();
            Logger.Debug("LCD initialized successfully");
        }
        catch (Exception ex)
        {
            Logger.Debug($"LCD init error: {ex}");
            throw;
        }

        _refreshTimer = new Timer(OnRefreshTick, null, 500, 250);  // Start after 500ms, then every 250ms
        Logger.Debug("LCD refresh timer started");
    }

    public void SetConnectionInfo(string type, string ip)
    {
        lock (_stateLock)
        {
            _connectionType = string.IsNullOrWhiteSpace(type) ? "Network" : type;
            _ipAddress = string.IsNullOrWhiteSpace(ip) ? "0.0.0.0" : ip;
        }
    }

    public void UpdatePosition(float x, float y, float z)
    {
        lock (_stateLock)
        {
            _posX = x;
            _posY = y;
            _posZ = z;
        }
    }

    public void UpdateMotorStatus(float temp, bool enabled, bool paused)
    {
        lock (_stateLock)
        {
            _tempC = temp;
            _motorsEnabled = enabled;
            _motorsPaused = paused;
        }
    }

    public void Clear()
    {
        lock (_stateLock)
        {
            _display.Clear();
        }
    }

        /// <summary>
        /// Diagnostic: write a solid color to verify the display is responding.
        /// </summary>
        public void TestColor(byte r, byte g, byte b)
        {
            try
            {
                lock (_stateLock)
                {
                    using var image = new Image<Rgba32>(DisplayWidth, DisplayHeight);
                    var color = ImageSharpColor.FromRgb(r, g, b);
                    image.Mutate(ctx => ctx.Fill(color));
                    _display.Write(image);
                    Logger.Debug($"LCD test color written: RGB({r},{g},{b})");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"LCD test color failed: {ex}");
            }
        }

        /// <summary>
        /// Disable diagnostic mode and return to normal dashboard rendering.
        /// </summary>
        public void DisableDiagnostics()
        {
            _diagnosticMode = false;
            Logger.Debug("LCD diagnostic mode disabled");
        }

        private void OnRefreshTick(object? state)
        {
            try
            {
                if (_diagnosticMode)
                {
                    // Diagnostic mode: cycle through test colors
                    var elapsed = (DateTime.Now.Millisecond / 1000.0);  // 0-1 over each second
                    int color = (int)(elapsed * 4);  // 0-3 color sequence
                    switch (color)
                    {
                        case 0: TestColor(255, 0, 0); break;    // Red
                        case 1: TestColor(0, 255, 0); break;    // Green
                        case 2: TestColor(0, 0, 255); break;    // Blue
                        default: TestColor(255, 255, 255); break; // White
                    }
                    return;
                }

                string connectionType;
                string ipAddress;
                float posX;
                float posY;
                float posZ;
                float tempC;
                bool motorsEnabled;
                bool motorsPaused;
                float? targetRa;
                float? targetDec;
                string cameraName;

                lock (_stateLock)
                {
                    connectionType = _connectionType;
                    ipAddress = _ipAddress;
                    posX = _posX;
                    posY = _posY;
                    posZ = _posZ;
                    tempC = _tempC;
                    motorsEnabled = _motorsEnabled;
                    motorsPaused = _motorsPaused;
                    targetRa = Calibration.CurrentTargetRa;
                    targetDec = Calibration.CurrentTargetDec;
                    cameraName = Calibration.SolveCamera?.cameramodel ?? "N/A";
                }

                Logger.Debug($"LCD refresh: calibrated={Calibration.IsAligned}, motors={motorsEnabled}, pos=({posX},{posY},{posZ})");

                using var image = new Image<Rgba32>(DisplayWidth, DisplayHeight);
                image.Mutate(ctx => ctx.Fill(ImageSharpColor.Black));

                // Top banner
                var bannerColor = Calibration.IsAligned
                    ? ImageSharpColor.FromRgb(70, 170, 70)
                    : ImageSharpColor.FromRgb(255, 255, 0);
                image.Mutate(ctx => ctx.Fill(bannerColor, new Rectangle(0, 0, DisplayWidth, 30)));
                
                try { DrawCenteredText(image, Calibration.IsAligned ? "CALIBRATED" : "NOT CALIBRATED", _fontMed, ImageSharpColor.Black, 0, 0, DisplayWidth, 30); }
                catch (Exception ex) { Logger.Debug($"Banner text failed: {ex.Message}"); }

                // Dividers only - skip all other text for now
                image.Mutate(ctx => ctx.Fill(ImageSharpColor.FromRgb(120, 120, 120), new Rectangle(10, 185, DisplayWidth - 20, 2)));
                image.Mutate(ctx => ctx.Fill(ImageSharpColor.FromRgb(120, 120, 120), new Rectangle(260, 195, 2, 115)));

                _display.Write(image);
                Logger.Debug("LCD refresh written");
            }
            catch (Exception ex)
            {
                Logger.Debug($"LCD refresh failed: {ex}");
            }
        }

    private static void DrawCenteredText(Image<Rgba32> image, string text, SixLaborsFont font, ImageSharpColor color, int x, int y, int width, int height)
    {
        var size = TextMeasurer.MeasureSize(text, new TextOptions(font));
        float textX = x + (width - size.Width) / 2f;
        float textY = y + (height - size.Height) / 2f - 2f;
        image.Mutate(ctx => ctx.DrawText(text, font, color, new PointF(textX, textY)));
    }

    private static string FormatRa(float? ra) => ra.HasValue ? $"{ra.Value:F3}h" : "--";

    private static string FormatDec(float? dec) => dec.HasValue ? $"{dec.Value:+0.00;-0.00} deg" : "--";

    private static SixLaborsFont LoadFont(float size)
    {
        var fontPath = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf";
        if (File.Exists(fontPath))
        {
            var collection = new FontCollection();
            var family = collection.Add(fontPath);
            return family.CreateFont(size, FontStyle.Bold);
        }

        return SystemFonts.CreateFont("DejaVu Sans", size, FontStyle.Bold);
    }

    public void Dispose()
    {
        _refreshTimer.Dispose();
        _display.Dispose();
    }

    private sealed class Hx8357Display : IDisposable
    {
        private const int SpiChunkSize = 4096;

        private const byte SWRESET = 0x01;
        private const byte SLPOUT = 0x11;
        private const byte NORON = 0x13;
        private const byte INVOFF = 0x20;
        private const byte DISPON = 0x29;
        private const byte CASET = 0x2A;
        private const byte PASET = 0x2B;
        private const byte RAMWR = 0x2C;
        private const byte MADCTL = 0x36;
        private const byte COLMOD = 0x3A;
        private const byte SETC = 0xB9;
        private const byte SETOSC = 0xB0;
        private const byte SETPWR1 = 0xB1;
        private const byte SETRGB = 0xB3;
        private const byte SETCYC = 0xB4;
        private const byte SETCOM = 0xB6;
        private const byte SETSTBA = 0xC0;
        private const byte SETPANEL = 0xCC;
        private const byte SETGAMMA = 0xE0;

        private readonly GpioController _gpio;
        private readonly SpiDevice _spi;
        private readonly int _dcPin = 25;
        private readonly int _resetPin = 24;
        private readonly int _width;
        private readonly int _height;
        private readonly byte[] _pixelBuffer;

        public Hx8357Display(int width, int height)
        {
            _width = width;
            _height = height;
            _pixelBuffer = new byte[_width * _height * 2];

            _gpio = new GpioController(PinNumberingScheme.Logical);
            _gpio.OpenPin(_dcPin, PinMode.Output);
            _gpio.OpenPin(_resetPin, PinMode.Output);
            _gpio.Write(_dcPin, PinValue.High);
            _gpio.Write(_resetPin, PinValue.High);

            var settings = new SpiConnectionSettings(0, 0)
            {
                ClockFrequency = 24_000_000,
                Mode = SpiMode.Mode0,
                DataBitLength = 8
            };

            _spi = SpiDevice.Create(settings);
        }

        public void Initialize()
        {
            HardReset();

            WriteCommand(SWRESET);
            Thread.Sleep(150);

            WriteCommand(SETC, 0xFF, 0x83, 0x57);
            WriteCommand(SETOSC, 0x68);
            WriteCommand(SETPANEL, 0x05);
            WriteCommand(SETPWR1, 0x00, 0x15, 0x1C, 0x1C, 0x83, 0xAA);
            WriteCommand(SETSTBA, 0x50, 0x50, 0x01, 0x3C, 0x1E, 0x08);
            WriteCommand(SETCYC, 0x02, 0x40, 0x00, 0x2A, 0x2A, 0x0D, 0x78);
            WriteCommand(SETRGB, 0x80, 0x00, 0x06, 0x06);
            WriteCommand(SETCOM, 0x25);
            WriteCommand(SETGAMMA,
                0x02, 0x0A, 0x11, 0x1D, 0x23, 0x35, 0x41, 0x4B,
                0x4B, 0x42, 0x3A, 0x27, 0x1B, 0x08, 0x09, 0x03,
                0x02, 0x0A, 0x11, 0x1D, 0x23, 0x35, 0x41, 0x4B,
                0x4B, 0x42, 0x3A, 0x27, 0x1B, 0x08, 0x09, 0x03, 0x00, 0x01);
            WriteCommand(COLMOD, 0x55);
            WriteCommand(MADCTL, 0xC0);
            WriteCommand(INVOFF);
            WriteCommand(SLPOUT);
            Thread.Sleep(150);
            WriteCommand(MADCTL, 0x20);  // Correct orientation
            WriteCommand(DISPON);
            Thread.Sleep(100);
        }

        public void Clear()
        {
            Array.Clear(_pixelBuffer);
            WriteFrameBuffer(_pixelBuffer);
        }

        public void Write(Image<Rgba32> image)
        {
            if (image.Width != _width || image.Height != _height)
                throw new InvalidOperationException($"Unexpected image size {image.Width}x{image.Height}; expected {_width}x{_height}.");

            int offset = 0;
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < _height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < _width; x++)
                    {
                        var pixel = row[x];
                        ushort color = Rgb565(pixel.R, pixel.G, pixel.B);
                        _pixelBuffer[offset++] = (byte)(color >> 8);
                        _pixelBuffer[offset++] = (byte)(color & 0xFF);
                    }
                }
            });

            WriteFrameBuffer(_pixelBuffer);
        }

        private void HardReset()
        {
            _gpio.Write(_resetPin, PinValue.High);
            Thread.Sleep(50);
            _gpio.Write(_resetPin, PinValue.Low);
            Thread.Sleep(200);
            _gpio.Write(_resetPin, PinValue.High);
            Thread.Sleep(200);
        }

        private void WriteCommand(byte command, params byte[] data)
        {
            _gpio.Write(_dcPin, PinValue.Low);
            _spi.Write(new byte[] { command });

            if (data.Length > 0)
            {
                _gpio.Write(_dcPin, PinValue.High);
                _spi.Write(data);
            }
        }

        private void SetWindow(int x0, int y0, int x1, int y1)
        {
            WriteCommand(CASET,
                (byte)(x0 >> 8), (byte)x0,
                (byte)(x1 >> 8), (byte)x1);
            WriteCommand(PASET,
                (byte)(y0 >> 8), (byte)y0,
                (byte)(y1 >> 8), (byte)y1);
            WriteCommand(RAMWR);
        }

        private void WriteFrameBuffer(byte[] buffer)
        {
            SetWindow(0, 0, _width - 1, _height - 1);
            _gpio.Write(_dcPin, PinValue.High);

            for (int offset = 0; offset < buffer.Length; offset += SpiChunkSize)
            {
                int length = Math.Min(SpiChunkSize, buffer.Length - offset);
                _spi.Write(buffer.AsSpan(offset, length));
            }
        }

        private static ushort Rgb565(byte r, byte g, byte b)
        {
            return (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
        }

        public void Dispose()
        {
            _spi.Dispose();
            if (_gpio.IsPinOpen(_dcPin)) _gpio.ClosePin(_dcPin);
            if (_gpio.IsPinOpen(_resetPin)) _gpio.ClosePin(_resetPin);
            _gpio.Dispose();
        }
    }
}
