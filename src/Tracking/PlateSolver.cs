using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Wraps the astrometry.net solve-field command to plate-solve captured images.
/// Determines the exact RA/Dec of the image center, pixel scale, and field rotation.
/// Works with JPEG, TIFF, FITS, and RAW (ARW/CR2) via dcraw conversion.
/// </summary>
public static class PlateSolver
{
    /// <summary>
    /// Result of a successful plate solve.
    /// </summary>
    public class SolveResult
    {
        /// <summary>RA of the image center in hours (0–24).</summary>
        public double RaCenterHours { get; set; }
        /// <summary>Dec of the image center in degrees (−90 to +90).</summary>
        public double DecCenterDeg { get; set; }
        /// <summary>Measured pixel scale in arcsec/pixel.</summary>
        public double PixelScaleArcsecPerPx { get; set; }
        /// <summary>Field rotation angle in degrees (position angle of "up" on sensor).</summary>
        public double RotationDeg { get; set; }
        /// <summary>Field width in degrees.</summary>
        public double FieldWidthDeg { get; set; }
        /// <summary>Field height in degrees.</summary>
        public double FieldHeightDeg { get; set; }
        /// <summary>How long the solve took.</summary>
        public TimeSpan SolveTime { get; set; }
    }

    // ── Configuration ──

    /// <summary>Focal length of the imaging lens in mm.  Used to estimate pixel scale for solve-field hints.</summary>
    public static float FocalLengthMm { get; set; } = 28f;

    /// <summary>Camera pixel size in microns.  Sony A7III = 5.93 µm.</summary>
    public static float PixelSizeUm { get; set; } = 5.93f;

    /// <summary>Computed plate scale in arcsec/pixel based on FocalLengthMm and PixelSizeUm.</summary>
    public static double PlateScale => 206265.0 * PixelSizeUm / (FocalLengthMm * 1000.0);

    /// <summary>Maximum CPU time in seconds for a single solve attempt.</summary>
    public static int CpuLimitSeconds { get; set; } = 60;

    /// <summary>Downsample factor for source extraction (2 = half-res, faster).</summary>
    public static int Downsample { get; set; } = 2;

    /// <summary>
    /// Fraction of each image dimension to keep from the center before solving.
    /// 0.5 = keep center 50%×50% (25% of pixels) — removes distorted/vignetted borders.
    /// Set to 1.0 to disable cropping.
    /// Example: 24 MP full-frame → center-crop at 0.5 → 6 MP centre-only image.
    /// </summary>
    public static double CropFraction { get; set; } = 0.5;

    /// <summary>Temporary directory for solve-field output files.</summary>
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "platesolve");

    // ── Public API ──

    /// <summary>
    /// Plate-solve an image file.  Supports JPEG, TIFF, FITS, and RAW formats
    /// (ARW, CR2 — converted via dcraw automatically).
    /// </summary>
    /// <param name="imagePath">Path to the captured image file.</param>
    /// <param name="hintRA">Optional RA hint in hours — massively speeds up solving.</param>
    /// <param name="hintDec">Optional Dec hint in degrees.</param>
    /// <param name="hintRadiusDeg">Search radius around the hint position (default 30°).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>SolveResult on success, null on failure.</returns>
    public static async Task<SolveResult?> SolveAsync(
        string imagePath,
        double? hintRA = null,
        double? hintDec = null,
        double hintRadiusDeg = 30.0,
        CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
        {
            Logger.Warn($"Image not found: {imagePath}");
            return null;
        }

        Directory.CreateDirectory(TempDir);

        string solveInput = imagePath;
        string? convertedFile = null;
        string? croppedFile = null;
        bool dcrawHalved = false;

        try
        {
            // Convert RAW files to JPEG via dcraw
            string ext = Path.GetExtension(imagePath).ToLowerInvariant();
            if (ext is ".arw" or ".cr2" or ".nef" or ".dng" or ".raf")
            {
                Logger.Notice($"converting RAW ({ext}) to JPEG via dcraw...");
                convertedFile = Path.Combine(TempDir, Path.GetFileNameWithoutExtension(imagePath) + ".jpg");
                bool converted = await ConvertRawToJpegAsync(imagePath, convertedFile, ct);
                if (!converted)
                {
                    Logger.Warn("RAW conversion failed");
                    return null;
                }
                solveInput = convertedFile;
                dcrawHalved = true; // dcraw -h halves resolution → doubles plate scale
            }

            // Center-crop to strip distorted/vignetted borders before solving.
            // Pixel scale (arcsec/px) is unchanged by cropping — only FOV shrinks.
            if (CropFraction < 1.0)
            {
                croppedFile = await CropCenterAsync(solveInput, ct);
                if (croppedFile != null)
                    solveInput = croppedFile;
            }

            // Build solve-field command
            // dcraw -h halves the image resolution, so effective plate scale doubles.
            // solve-field scale hints must match the INPUT image, not the original sensor.
            double scale = PlateScale;
            if (dcrawHalved) scale *= 2.0;
            double scaleLow = scale * 0.7;
            double scaleHigh = scale * 1.3;

            // When dcraw already halved the image, don't downsample again in solve-field
            int effectiveDownsample = dcrawHalved ? 1 : Downsample;

            // Use a unique prefix per solve to avoid file collisions
            string prefix = $"solve_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
            string wcsFile = Path.Combine(TempDir, prefix + ".wcs");

            var args = $"--scale-units arcsecperpix --scale-low {scaleLow:F1} --scale-high {scaleHigh:F1} " +
                       $"--downsample {effectiveDownsample} --no-plots --overwrite " +
                       $"--cpulimit {CpuLimitSeconds} " +
                       $"--dir \"{TempDir}\" --out \"{prefix}\" ";

            // RA/Dec hints dramatically speed up solving (typically 5-15s vs 30-60s)
            if (hintRA.HasValue && hintDec.HasValue)
            {
                double hintRaDeg = hintRA.Value * 15.0; // hours → degrees
                args += $"--ra {hintRaDeg:F4} --dec {hintDec.Value:F4} --radius {hintRadiusDeg:F1} ";
            }

            args += $"\"{solveInput}\"";

            Logger.Notice($"PlateSolver: running solve-field (scale hint {scaleLow:F1}-{scaleHigh:F1}\"/px, ds={effectiveDownsample})...");
            Logger.Debug($"PlateSolver: solve-field {args}");

            var sw = Stopwatch.StartNew();
            var (exitCode, stdout, stderr) = await RunProcessAsync("solve-field", args, CpuLimitSeconds + 60, ct);
            sw.Stop();

            if (ct.IsCancellationRequested)
            {
                Logger.Notice("PlateSolver: cancelled");
                return null;
            }

            if (exitCode != 0)
            {
                Logger.Warn($"PlateSolver: solve-field exited with code {exitCode}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    Logger.Debug($"PlateSolver stderr: {stderr}");
                if (!string.IsNullOrWhiteSpace(stdout))
                    Logger.Debug($"PlateSolver stdout (last 500): {(stdout.Length > 500 ? stdout[^500..] : stdout)}");
                return null;
            }

            // Check if solve succeeded
            string solvedFile = Path.Combine(TempDir, prefix + ".solved");
            if (!File.Exists(solvedFile))
            {
                Logger.Warn($"PlateSolver: no solution found (took {sw.Elapsed.TotalSeconds:F1}s)");
                // Log last part of stdout for diagnostics (star count, index files tried, etc.)
                if (!string.IsNullOrWhiteSpace(stdout))
                    Logger.Debug($"PlateSolver stdout (last 500): {(stdout.Length > 500 ? stdout[^500..] : stdout)}");
                return null;
            }

            // Parse the WCS result
            var result = ParseWcsFile(wcsFile);
            if (result == null)
            {
                // Fallback: parse stdout for the solution
                result = ParseStdout(stdout);
            }

            if (result != null)
            {
                result.SolveTime = sw.Elapsed;
                Logger.Notice($"PlateSolver: SOLVED in {sw.Elapsed.TotalSeconds:F1}s — " +
                              $"RA={result.RaCenterHours:F4}h, Dec={result.DecCenterDeg:F4}°, " +
                              $"scale={result.PixelScaleArcsecPerPx:F2}\"/px, " +
                              $"field={result.FieldWidthDeg:F2}°×{result.FieldHeightDeg:F2}°");
            }
            else
            {
                Logger.Warn("PlateSolver: solved marker found but could not parse WCS output");
            }

            return result;
        }
        finally
        {
            // Cleanup temp files (keep input image, clean solve artifacts)
            CleanupTempFiles(TempDir);
            if (convertedFile != null && File.Exists(convertedFile))
                try { File.Delete(convertedFile); } catch { }
            if (croppedFile != null && File.Exists(croppedFile))
                try { File.Delete(croppedFile); } catch { }
        }
    }

    // ── Center crop ──

    /// <summary>
    /// Crop an image to the centre <see cref="CropFraction"/> of each dimension using
    /// ImageMagick <c>convert</c>.  Pixel scale is unaffected — only the FOV shrinks,
    /// removing lens-distorted / vignetted borders that confuse star detection.
    /// </summary>
    private static async Task<string?> CropCenterAsync(string imagePath, CancellationToken ct)
    {
        int pct = (int)Math.Round(CropFraction * 100.0);
        string outPath = Path.Combine(TempDir, $"crop_{Path.GetFileName(imagePath)}");

        // ImageMagick: gravity Centre, then crop pct%×pct% from that anchor.
        // +repage resets the canvas so downstream tools see a clean image size.
        string bashCmd = $"convert '{imagePath}' -gravity Center -crop {pct}%x{pct}%+0+0 +repage '{outPath}'";
        var (exitCode, _, stderr) = await RunProcessAsync("bash", new[] { "-c", bashCmd }, 60, ct);
        if (exitCode != 0 || !File.Exists(outPath) || new FileInfo(outPath).Length == 0)
        {
            Logger.Warn($"PlateSolver: center crop failed (crop {pct}%): {stderr}");
            return null;
        }
        Logger.Notice($"PlateSolver: center-cropped to {pct}%×{pct}% → {Path.GetFileName(outPath)}");
        return outPath;
    }

    // ── RAW conversion ──

    /// <summary>
    /// Convert a RAW camera file to JPEG using dcraw + pnmtojpeg (or cjpeg).
    /// Uses half-resolution (-h) for speed — still plenty of stars for solving.
    /// </summary>
    private static async Task<bool> ConvertRawToJpegAsync(string rawPath, string jpegPath, CancellationToken ct)
    {
        // dcraw -c -w -h outputs PPM to stdout; pipe through cjpeg or pnmtojpeg
        // Try pnmtojpeg first (netpbm), fall back to cjpeg (libjpeg-turbo)
        string pipeCmd;
        if (CommandExists("pnmtojpeg"))
            pipeCmd = "pnmtojpeg";
        else if (CommandExists("cjpeg"))
            pipeCmd = "cjpeg";
        else
        {
            // No JPEG encoder available — output PPM directly (solve-field handles it)
            jpegPath = Path.ChangeExtension(jpegPath, ".ppm");
            var (code, _, err) = await RunProcessAsync("dcraw", new[] { "-c", "-w", "-h", "-b", "0.1", rawPath }, 120, ct, jpegPath);
            return code == 0;
        }

        // dcraw -c -w -h RAW | pnmtojpeg > output.jpg
        // Use bash -c with ArgumentList so that the script string is passed as a single
        // unambiguous argument — avoids .NET's Windows-style Arguments parser mangling spaces/quotes.
        string bashCmd = $"dcraw -c -w -h -b 0.1 '{rawPath}' | {pipeCmd} > '{jpegPath}'";
        var (exitCode, _, stderr) = await RunProcessAsync("bash", new[] { "-c", bashCmd }, 120, ct);
        if (exitCode != 0)
        {
            Logger.Warn($"PlateSolver: dcraw conversion failed: {stderr}");
            return false;
        }
        return File.Exists(jpegPath) && new FileInfo(jpegPath).Length > 0;
    }

    private static bool CommandExists(string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo("which", cmd)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    // ── WCS parsing ──

    /// <summary>
    /// Parse a FITS WCS header file (.wcs) emitted by solve-field.
    /// Extracts CRVAL1 (RA), CRVAL2 (Dec), CD matrix (pixel scale + rotation).
    /// </summary>
    private static SolveResult? ParseWcsFile(string wcsPath)
    {
        if (!File.Exists(wcsPath)) return null;

        try
        {
            // FITS headers are 80-char fixed-width records
            string text = File.ReadAllText(wcsPath);

            double? crval1 = ExtractFitsValue(text, "CRVAL1");  // RA center (degrees)
            double? crval2 = ExtractFitsValue(text, "CRVAL2");  // Dec center (degrees)
            double? cd11 = ExtractFitsValue(text, "CD1_1");
            double? cd12 = ExtractFitsValue(text, "CD1_2");
            double? cd21 = ExtractFitsValue(text, "CD2_1");
            double? cd22 = ExtractFitsValue(text, "CD2_2");
            double? naxis1 = ExtractFitsValue(text, "IMAGEW") ?? ExtractFitsValue(text, "NAXIS1");
            double? naxis2 = ExtractFitsValue(text, "IMAGEH") ?? ExtractFitsValue(text, "NAXIS2");

            if (crval1 == null || crval2 == null)
                return null;

            double raDeg = crval1.Value;
            double decDeg = crval2.Value;

            // Pixel scale from CD matrix
            double pixScale = 0;
            double rotation = 0;
            if (cd11 != null && cd12 != null && cd21 != null && cd22 != null)
            {
                // Pixel scale = sqrt(|det(CD)|) in degrees/pixel → convert to arcsec
                double det = cd11.Value * cd22.Value - cd12.Value * cd21.Value;
                pixScale = Math.Sqrt(Math.Abs(det)) * 3600.0;
                // Rotation angle from CD matrix
                rotation = Math.Atan2(cd21.Value, cd11.Value) * 180.0 / Math.PI;
            }

            double fieldW = 0, fieldH = 0;
            if (naxis1 != null && naxis2 != null && pixScale > 0)
            {
                fieldW = naxis1.Value * pixScale / 3600.0;
                fieldH = naxis2.Value * pixScale / 3600.0;
            }

            return new SolveResult
            {
                RaCenterHours = raDeg / 15.0,
                DecCenterDeg = decDeg,
                PixelScaleArcsecPerPx = pixScale,
                RotationDeg = rotation,
                FieldWidthDeg = fieldW,
                FieldHeightDeg = fieldH
            };
        }
        catch (Exception ex)
        {
            Logger.Warn($"PlateSolver: WCS parse error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extract a numeric value from a FITS header keyword.
    /// FITS format: "KEYWORD = value / comment"
    /// </summary>
    private static double? ExtractFitsValue(string text, string keyword)
    {
        // Match "KEYWORD = value" with flexible spacing
        var match = Regex.Match(text, $@"{Regex.Escape(keyword)}\s*=\s*([+-]?\d+\.?\d*(?:[eE][+-]?\d+)?)",
            RegexOptions.IgnoreCase);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            return val;
        return null;
    }

    /// <summary>
    /// Fallback: parse solve-field stdout for the solution when WCS parsing fails.
    /// Looks for lines like "Field center: (RA,Dec) = (123.456, 45.678) deg."
    /// </summary>
    private static SolveResult? ParseStdout(string stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return null;

        // "Field center: (RA,Dec) = (251.234, 38.784) deg."
        var centerMatch = Regex.Match(stdout,
            @"Field center:\s*\(RA,Dec\)\s*=\s*\(([+-]?\d+\.?\d*),\s*([+-]?\d+\.?\d*)\)\s*deg",
            RegexOptions.IgnoreCase);

        if (!centerMatch.Success) return null;

        if (!double.TryParse(centerMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double raDeg) ||
            !double.TryParse(centerMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double decDeg))
            return null;

        var result = new SolveResult
        {
            RaCenterHours = raDeg / 15.0,
            DecCenterDeg = decDeg
        };

        // "Field size: 73.1234 x 48.7654 degrees"
        var sizeMatch = Regex.Match(stdout,
            @"Field size:\s*([+-]?\d+\.?\d*)\s*x\s*([+-]?\d+\.?\d*)\s*(deg|arcmin)",
            RegexOptions.IgnoreCase);
        if (sizeMatch.Success)
        {
            double w = double.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            double h = double.Parse(sizeMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            string unit = sizeMatch.Groups[3].Value.ToLower();
            if (unit == "arcmin") { w /= 60.0; h /= 60.0; }
            result.FieldWidthDeg = w;
            result.FieldHeightDeg = h;
        }

        // "pixel scale 43.70 arcsec/pix"
        var scaleMatch = Regex.Match(stdout,
            @"pixel scale\s+([+-]?\d+\.?\d*)\s*arcsec/pix",
            RegexOptions.IgnoreCase);
        if (scaleMatch.Success)
            result.PixelScaleArcsecPerPx = double.Parse(scaleMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        return result;
    }

    // ── Process execution ──

    /// <summary>
    /// Run an external process and capture stdout/stderr.
    /// Accepts a pre-split argument list — avoids shell quoting issues on Linux.
    /// </summary>
    private static Task<(int exitCode, string stdout, string stderr)> RunProcessAsync(
        string command, string[] argumentList, int timeoutSeconds, CancellationToken ct, string? stdoutFile = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in argumentList)
            psi.ArgumentList.Add(arg);
        return RunProcessCoreAsync(psi, timeoutSeconds, ct, stdoutFile);
    }

    /// <summary>
    /// Run an external process and capture stdout/stderr.
    /// </summary>
    private static Task<(int exitCode, string stdout, string stderr)> RunProcessAsync(
        string command, string arguments, int timeoutSeconds, CancellationToken ct, string? stdoutFile = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        return RunProcessCoreAsync(psi, timeoutSeconds, ct, stdoutFile);
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunProcessCoreAsync(
        ProcessStartInfo psi, int timeoutSeconds, CancellationToken ct, string? stdoutFile = null)
    {

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (-1, "", $"Failed to start {psi.FileName}: {ex.Message}");
        }

        // If redirecting stdout to a file (for RAW conversion)
        if (stdoutFile != null)
        {
            using var fs = File.Create(stdoutFile);
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(fs, ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await process.WaitForExitAsync(cts.Token);
                await copyTask;
                string stderr = await stderrTask;
                return (process.ExitCode, "(redirected to file)", stderr);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                return (-1, "", "Process timed out or was cancelled");
            }
        }
        else
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await process.WaitForExitAsync(cts.Token);
                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                return (process.ExitCode, stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                return (-1, "", "Process timed out or was cancelled");
            }
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    // ── Temp file cleanup ──

    private static void CleanupTempFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;

        try
        {
            // Remove solve-field artifacts but keep the directory
            foreach (string file in Directory.GetFiles(dir, "solve_*"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }
}
