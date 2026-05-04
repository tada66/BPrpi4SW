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
    private readonly object _displayLock = new();
    private readonly Hx8357Display _display;
    private readonly Timer _refreshTimer;
    private int _refreshInProgress;
    private bool _diagnosticMode = false;

    private readonly SixLaborsFont _fontMed;
    private readonly SixLaborsFont _fontSmall;
    private readonly SixLaborsFont _fontTarget;
    private readonly SixLaborsFont _fontMountAxis;

    private string _connectionType = "Network";
    private string _ipAddress = "0.0.0.0";
    private float _posX;
    private float _posY;
    private float _posZ;
    private float _tempC;
    private bool _motorsEnabled;
    private bool _motorsPaused;
    private bool _cameraConnected;
    private string _cameraModel = "N/A";
    private string _cameraBattery = "--";
    private string _cameraIso = "--";
    private string _cameraAperture = "--";
    private string _cameraShutter = "--";

    public LcdController()
    {
        _fontMed = LoadFont(22);
        _fontSmall = LoadFont(18);
        _fontTarget = LoadFont(34);
        _fontMountAxis = LoadFont(17);

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

        // Keep refresh period comfortably above worst-case render+SPI transfer time.
        _refreshTimer = new Timer(OnRefreshTick, null, 500, 800);
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

    public void UpdateCameraStatus(CameraStatusPayload status)
    {
        lock (_stateLock)
        {
            _cameraConnected = status.Connected;
            _cameraModel = NormalizeText(status.Model, "N/A");
            _cameraBattery = NormalizeText(status.Battery, "--");
            _cameraIso = NormalizeText(status.Iso, "--");
            _cameraAperture = NormalizeText(status.Aperture, "--");
            _cameraShutter = NormalizeText(status.ShutterSpeed, "--");
        }
    }

    public void Clear()
    {
        lock (_stateLock)
        {
            lock (_displayLock)
            {
                _display.Clear();
            }
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
                    lock (_displayLock)
                    {
                        _display.Write(image);
                    }
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
            if (Interlocked.Exchange(ref _refreshInProgress, 1) == 1)
                return;

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
                bool cameraConnected;
                string cameraModel;
                string cameraBattery;
                string cameraIso;
                string cameraAperture;
                string cameraShutter;
                bool isAligned;
                string calibrationQuality;
                double? calibrationResidualArcmin;
                int alignmentPoints;

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
                    cameraConnected = _cameraConnected;
                    cameraModel = _cameraModel;
                    cameraBattery = _cameraBattery;
                    cameraIso = _cameraIso;
                    cameraAperture = _cameraAperture;
                    cameraShutter = _cameraShutter;
                    isAligned = Calibration.IsAligned;
                    calibrationQuality = NormalizeText(Calibration.LastResult?.Quality, "N/A");
                    calibrationResidualArcmin = Calibration.LastResult?.AverageResidualArcmin;
                    alignmentPoints = Calibration.PointCount;
                }

                using var image = new Image<Rgba32>(DisplayWidth, DisplayHeight);
                var bg = ImageSharpColor.FromRgb(12, 14, 18);
                var panel = ImageSharpColor.FromRgb(26, 30, 38);
                var lineDim = ImageSharpColor.FromRgb(52, 58, 72);
                var accent = ImageSharpColor.FromRgb(90, 200, 220);
                var muted = ImageSharpColor.FromRgb(140, 148, 160);
                var okColor = ImageSharpColor.FromRgb(120, 220, 140);
                var warnColor = ImageSharpColor.FromRgb(235, 185, 70);
                var badColor = ImageSharpColor.FromRgb(235, 95, 95);
                var raColor = ImageSharpColor.FromRgb(130, 230, 255);
                var decColor = ImageSharpColor.FromRgb(200, 185, 255);
                var xColor = ImageSharpColor.FromRgb(255, 170, 170);
                var yColor = ImageSharpColor.FromRgb(170, 255, 190);
                var zColor = ImageSharpColor.FromRgb(170, 210, 255);

                var motorStateText = FormatMotorState(motorsEnabled, motorsPaused);
                var motorStateColor = !motorsEnabled ? badColor : motorsPaused ? warnColor : okColor;
                var calibrationStateColor = isAligned ? okColor : warnColor;
                var calibrationScoreColor =
                    calibrationQuality.Contains("EXCELLENT", StringComparison.OrdinalIgnoreCase) ? okColor :
                    calibrationQuality.Contains("OK", StringComparison.OrdinalIgnoreCase) ? ImageSharpColor.FromRgb(170, 220, 255) :
                    calibrationQuality.Contains("MARGINAL", StringComparison.OrdinalIgnoreCase) ? warnColor :
                    calibrationQuality.Contains("REJECT", StringComparison.OrdinalIgnoreCase) ? badColor :
                    ImageSharpColor.White;

                image.Mutate(ctx => ctx.Fill(bg));

                const int topY = 6;
                const int topH = 132;
                const int topSplitX = 240;
                const int targetBandY0 = 142;
                const int targetBandY1 = 238;
                const int calibY0 = 242;

                image.Mutate(ctx => ctx.Fill(panel, new Rectangle(6, topY, topSplitX - 12, topH - 3)));
                image.Mutate(ctx => ctx.Fill(panel, new Rectangle(topSplitX + 6, topY, DisplayWidth - topSplitX - 12, topH - 3)));
                //image.Mutate(ctx => ctx.Fill(ImageSharpColor.FromRgb(18, 20, 26), new Rectangle(10, targetBandY0, DisplayWidth - 20, targetBandY1 - targetBandY0)));
                image.Mutate(ctx => ctx.Fill(lineDim, new Rectangle(10, topY + topH + 2, DisplayWidth - 20, 2)));
                image.Mutate(ctx => ctx.Fill(lineDim, new Rectangle(topSplitX-1, topY+3, 2, topH)));
                image.Mutate(ctx => ctx.Fill(lineDim, new Rectangle(10, targetBandY1 + 2, DisplayWidth - 20, 2)));

                float camX = 16;
                float camPanelInnerRight = topSplitX - 8;
                float mountX = topSplitX + 16;
                float mountPanelInnerRight = DisplayWidth - 8;
                float camLine = 18f;
                float y = topY + 8;

                DrawText(image, "CAMERA", _fontMed, accent, camX, y);
                int? batteryPct = cameraConnected ? TryParseBatteryPercent(cameraBattery) : null;
                string pctLabel = !cameraConnected ? "--"
                    : batteryPct.HasValue ? $"{batteryPct.Value}%"
                    : TrimToLength(cameraBattery, 8);
                var pctOpts = new TextOptions(_fontSmall);
                float pctW = TextMeasurer.MeasureSize(pctLabel, pctOpts).Width;
                const int batBodyW = 18;
                const int batNubW = 2;
                const int batGap = 6;
                int batGfxW = batBodyW + batNubW;
                float batGroupW = batGfxW + batGap + pctW;
                const float headerRightInset = 10f;
                float batX = camPanelInnerRight - batGroupW - headerRightInset;
                DrawBatteryGlyph(image, (int)batX, (int)y + 5, cameraConnected ? batteryPct : null, lineDim);
                DrawText(image, pctLabel, _fontSmall, cameraConnected ? muted : ImageSharpColor.FromRgb(110, 115, 125), batX + batGfxW + batGap, y);
                image.Mutate(ctx => ctx.Fill(accent, new Rectangle((int)camX, (int)(y + 22), (int)(camPanelInnerRight - camX - 8), 2)));
                y += 28;
                DrawText(image, cameraConnected ? "Connected" : "Disconnected", _fontSmall, cameraConnected ? okColor : badColor, camX, y);
                y += camLine;
                DrawText(image, TrimToLength(cameraModel, 26), _fontSmall, ImageSharpColor.White, camX, y);
                y += camLine;
                if (cameraConnected)
                {
                    DrawText(image, TrimToLength($"ISO {cameraIso}", 28), _fontSmall, ImageSharpColor.FromRgb(155, 210, 255), camX, y);
                    y += camLine;
                    DrawText(image, TrimToLength($"{cameraAperture}", 28), _fontSmall, ImageSharpColor.FromRgb(220, 195, 255), camX, y);
                    y += camLine;
                    DrawText(image, TrimToLength($"{cameraShutter}", 28), _fontSmall, ImageSharpColor.FromRgb(255, 205, 145), camX, y);
                }
                else
                {
                    DrawText(image, TrimToLength("ISO --", 28), _fontSmall, muted, camX, y);
                    y += camLine;
                    DrawText(image, TrimToLength("Aperture --", 28), _fontSmall, muted, camX, y);
                    y += camLine;
                    DrawText(image, TrimToLength("Shutter --", 28), _fontSmall, muted, camX, y);
                }

                y = topY + 8;
                DrawText(image, "MOUNT", _fontMed, accent, mountX, y);
                string tempLabel = $"{tempC:F1}C";
                var tempOpts = new TextOptions(_fontSmall);
                float tempW = TextMeasurer.MeasureSize(tempLabel, tempOpts).Width;
                const int thermBulb = 8;
                const int thermGap = 6;
                int thermGfxW = thermBulb;
                float thermGroupW = thermGfxW + thermGap + tempW;
                float thermX = mountPanelInnerRight - thermGroupW - headerRightInset;
                DrawThermometerGlyph(image, (int)thermX, (int)y + 2, tempC, lineDim);
                DrawText(image, tempLabel, _fontSmall, muted, thermX + thermGfxW + thermGap, y);
                image.Mutate(ctx => ctx.Fill(accent, new Rectangle((int)mountX, (int)(y + 22), (int)(mountPanelInnerRight - mountX - 8), 2)));
                y += 28;
                DrawText(image, $"Motors {motorStateText}", _fontSmall, motorStateColor, mountX, y);
                y += camLine;
                const float axisLine = 18f;
                DrawText(image, $"X {posX:F0}", _fontMountAxis, xColor, mountX, y);
                y += axisLine;
                DrawText(image, $"Y {posY:F0}", _fontMountAxis, yColor, mountX, y);
                y += axisLine;
                DrawText(image, $"Z {posZ:F0}", _fontMountAxis, zColor, mountX, y);
                y += camLine;
                DrawText(image, TrimToLength($"{connectionType}  {ipAddress}", 32), _fontSmall, muted, mountX, y);

                DrawCenteredText(image, "TARGET", _fontMed, accent, 0, targetBandY0 + 4, DisplayWidth, 20);
                try
                {
                    const int targetLineH = 34;
                    DrawCenteredText(image, FormatRa(targetRa), _fontTarget, raColor, 0, targetBandY0 + 24, DisplayWidth, targetLineH);
                    DrawCenteredText(image, FormatDec(targetDec), _fontTarget, decColor, 0, targetBandY0 + 58, DisplayWidth, targetLineH);
                }
                catch (Exception ex) { Logger.Debug($"Target text layout: {ex.Message}"); }

                float calibLeftX = 16;
                float calibRightX = 252;
                float cy = calibY0 + 4;
                const float calibRow = 15f;
                DrawText(image, "CALIBRATION", _fontMed, accent, calibLeftX, cy);
                cy += 22;
                DrawText(image, $"Status: {(isAligned ? "Calibrated" : "Not calibrated")}", _fontSmall, calibrationStateColor, calibLeftX, cy);
                DrawText(image, $"Score: {TrimToLength(calibrationQuality, 22)}", _fontSmall, calibrationScoreColor, calibRightX, cy);
                cy += calibRow;
                DrawText(image, $"Error: {FormatError(calibrationResidualArcmin)}", _fontSmall, ImageSharpColor.White, calibLeftX, cy);
                DrawText(image, $"Points: {alignmentPoints}", _fontSmall, ImageSharpColor.White, calibRightX, cy);

                lock (_displayLock)
                {
                    _display.Write(image);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"LCD refresh failed: {ex}");
            }
            finally
            {
                Volatile.Write(ref _refreshInProgress, 0);
            }
        }

    private static void DrawCenteredText(Image<Rgba32> image, string text, SixLaborsFont font, ImageSharpColor color, int x, int y, int width, int height)
    {
        var size = TextMeasurer.MeasureSize(text, new TextOptions(font));
        float textX = x + (width - size.Width) / 2f;
        float textY = y + (height - size.Height) / 2f - 2f;
        image.Mutate(ctx => ctx.DrawText(text, font, color, new PointF(textX, textY)));
    }

    private static void DrawText(Image<Rgba32> image, string text, SixLaborsFont font, ImageSharpColor color, float x, float y)
    {
        image.Mutate(ctx => ctx.DrawText(text, font, color, new PointF(x, y)));
    }

    /// <summary>Small battery pictogram (body + nub); fill reflects 0–100% when known.</summary>
    private static void DrawBatteryGlyph(Image<Rgba32> image, int x, int y, int? percent, ImageSharpColor frameColor)
    {
        const int bodyW = 18;
        const int nubW = 2;
        const int h = 10;
        var innerBg = ImageSharpColor.FromRgb(28, 31, 38);
        image.Mutate(ctx =>
        {
            ctx.Fill(frameColor, new Rectangle(x, y, bodyW, h));
            ctx.Fill(frameColor, new Rectangle(x + bodyW, y + 2, nubW, h - 4));
            ctx.Fill(innerBg, new Rectangle(x + 1, y + 1, bodyW - 2, h - 2));
            if (!percent.HasValue)
                return;
            int p = Math.Clamp(percent.Value, 0, 100);
            int innerMax = bodyW - 4;
            int fw = p <= 0 ? 0 : Math.Max(1, (innerMax * p + 50) / 100);
            if (fw <= 0)
                return;
            ImageSharpColor fill = p <= 10 ? ImageSharpColor.FromRgb(220, 75, 75)
                : p <= 25 ? ImageSharpColor.FromRgb(235, 175, 55)
                : ImageSharpColor.FromRgb(75, 185, 115);
            ctx.Fill(fill, new Rectangle(x + 2, y + 2, Math.Min(fw, innerMax), h - 4));
        });
    }

    /// <summary>Small thermometer pictogram with mercury fill mapped to mount temperature.</summary>
    private static void DrawThermometerGlyph(Image<Rgba32> image, int x, int y, float tempC, ImageSharpColor frameColor)
    {
        const int bodyW = 6;
        const int bodyH = 12;
        const int bulb = 8;
        var innerBg = ImageSharpColor.FromRgb(28, 31, 38);
        int bodyX = x + 1;
        int bodyY = y;
        int bulbX = x;
        int bulbY = y + bodyH - 2;
        float norm = Math.Clamp((tempC + 10f) / 60f, 0f, 1f); // -10C..50C display range
        int fillH = (int)MathF.Round((bodyH - 2) * norm);
        ImageSharpColor mercury = tempC >= 35f ? ImageSharpColor.FromRgb(220, 75, 75)
            : tempC >= 20f ? ImageSharpColor.FromRgb(235, 175, 55)
            : ImageSharpColor.FromRgb(75, 170, 235);

        image.Mutate(ctx =>
        {
            ctx.Fill(frameColor, new Rectangle(bodyX, bodyY, bodyW, bodyH));
            ctx.Fill(frameColor, new Rectangle(bulbX, bulbY, bulb, bulb));
            ctx.Fill(innerBg, new Rectangle(bodyX + 1, bodyY + 1, bodyW - 2, bodyH - 2));
            ctx.Fill(innerBg, new Rectangle(bulbX + 1, bulbY + 1, bulb - 2, bulb - 2));
            if (fillH > 0)
                ctx.Fill(mercury, new Rectangle(bodyX + 2, bodyY + bodyH - 1 - fillH, bodyW - 4, fillH));
            ctx.Fill(mercury, new Rectangle(bulbX + 2, bulbY + 2, bulb - 4, bulb - 4));
        });
    }

    private static int? TryParseBatteryPercent(string? battery)
    {
        if (string.IsNullOrWhiteSpace(battery) || battery == "--")
            return null;
        ReadOnlySpan<char> s = battery.AsSpan().Trim();
        int i = s.IndexOf('%');
        if (i > 0)
        {
            int j = i - 1;
            while (j >= 0 && char.IsDigit(s[j])) j--;
            ReadOnlySpan<char> digits = s.Slice(j + 1, i - j - 1);
            if (digits.Length > 0 && int.TryParse(digits, out int v))
                return Math.Clamp(v, 0, 100);
        }
        if (s.Contains("Full", StringComparison.OrdinalIgnoreCase))
            return 100;
        if (s.Contains("Empty", StringComparison.OrdinalIgnoreCase) || s.Contains("Critical", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (s.Contains("Low", StringComparison.OrdinalIgnoreCase))
            return 20;
        return null;
    }

    private static string NormalizeText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string TrimToLength(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }

    private static string FormatMotorState(bool motorsEnabled, bool motorsPaused)
    {
        if (!motorsEnabled) return "OFF";
        return motorsPaused ? "PAUSED" : "ON";
    }

    private static string FormatError(double? errorArcmin)
    {
        return errorArcmin.HasValue ? $"{errorArcmin.Value:F2}'" : "--";
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
        private readonly bool _mirrorX = false;
        private readonly bool _swapRedBlue = false;
        private readonly bool _invertColors = false;

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
                        int sourceX = _mirrorX ? (_width - 1 - x) : x;
                        var pixel = row[sourceX];

                        byte r = pixel.R;
                        byte g = pixel.G;
                        byte b = pixel.B;

                        if (_swapRedBlue)
                        {
                            (r, b) = (b, r);
                        }

                        if (_invertColors)
                        {
                            r = (byte)(255 - r);
                            g = (byte)(255 - g);
                            b = (byte)(255 - b);
                        }

                        ushort color = Rgb565(r, g, b);
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
