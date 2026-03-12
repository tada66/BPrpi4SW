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

    /// <summary>Temporary directory for solve-field output files.</summary>
    internal static readonly string TempDir = Path.Combine(Directory.GetCurrentDirectory(), "platesolve_tmp");

    static PlateSolver()
    {
        try
        {
            Directory.CreateDirectory(TempDir);
            // Force astrometry.net and its Python scripts (via tempfile module) to use our TempDir
            // instead of the RAMdisk `/tmp`, which easily runs out of space.
            Environment.SetEnvironmentVariable("TMPDIR", TempDir);

            // Clean up old files left over from previous crashes in TempDir.
            foreach (var file in Directory.GetFiles(TempDir))
            {
                try { File.Delete(file); } catch { }
            }

            // Also sweep /tmp for stale astrometry.net files left by killed solve attempts.
            // These are safe to delete: solve-field writes *.wcs / *.rdls / *.axy / *.xyls /
            // *.solved / *.match / *.corr and tmp.solve-field.* scratch dirs.
            // Each set can consume 50-150 MB; a full /tmp (tmpfs) causes dcraw to fail
            // with "No space left on device" even when the main filesystem has plenty of room.
            CleanupAstrometryTmp("/tmp");
        }
        catch { }
    }

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
        
        // Pre-solve cleanup: ensure adequate disk space by removing ALL old temp files.
        // Each solve-field run can generate 50-200 MB of auxiliary files.
        CleanupTempFiles(TempDir);
        
        Logger.Debug($"PlateSolver: temp directory is {TempDir}");

        string solveInput = imagePath;
        string? convertedFile = null;
        string? croppedFile = null;

        try
        {
            // Convert RAW files to JPEG via dcraw
            string ext = Path.GetExtension(imagePath).ToLowerInvariant();
            if (ext is ".arw" or ".cr2" or ".nef" or ".dng" or ".raf")
            {
                Logger.Notice($"converting RAW ({ext}) to JPEG via dcraw...");
                convertedFile = Path.Combine(TempDir, Path.GetFileNameWithoutExtension(imagePath) + ".jpg");
                bool converted = await ImageProcessing.ConvertRawToJpegAsync(imagePath, convertedFile, ct);
                if (!converted)
                {
                    Logger.Warn("RAW conversion failed");
                    return null;
                }
                solveInput = convertedFile;
            }

            // Center-crop to strip distorted/vignetted borders before solving.
            // Pixel scale (arcsec/px) is unchanged by cropping — only FOV shrinks.
            croppedFile = await ImageProcessing.CropCenterAsync(solveInput, ct);
            if (croppedFile != null)
                solveInput = croppedFile;

            // Build solve-field command
            // dcraw -h halves the image resolution, so effective plate scale doubles.
            // solve-field scale hints must match the INPUT image, not the original sensor.
            double scale = PlateScale;
            double scaleLow = scale * 0.7;
            double scaleHigh = scale * 1.3;

            // When dcraw already halved the image, don't downsample again in solve-field
            // (dcraw -h already halves resolution, so effective plate scale is doubled)

            // Use a unique prefix per solve to avoid file collisions
            string prefix = $"solve_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
            string wcsFile = Path.Combine(TempDir, prefix + ".wcs");

            var args = $"--scale-units arcsecperpix --scale-low {scaleLow:F1} --scale-high {scaleHigh:F1} " +
                       $"--overwrite " +
                       $"--cpulimit {CpuLimitSeconds} --no-plots " +
                       $"--dir \"{TempDir}\" --out \"{prefix}\" ";
            Logger.Info("PlateSolver: configured solve-field with args: " + args);

            // RA/Dec hints dramatically speed up solving (typically 5-15s vs 30-60s)
            if (hintRA.HasValue && hintDec.HasValue)
            {
                double hintRaDeg = hintRA.Value * 15.0; // hours → degrees
                args += $"--ra {hintRaDeg:F4} --dec {hintDec.Value:F4} --radius {hintRadiusDeg:F1} ";
            }

            args += $"\"{solveInput}\"";

            Logger.Notice($"PlateSolver: running solve-field (scale hint {scaleLow:F1}-{scaleHigh:F1}\"/px)");
            Logger.Debug($"PlateSolver: solve-field {args}");

            var sw = Stopwatch.StartNew();
            var (exitCode, stdout, stderr) = await ExternalProcess.RunProcessAsync("solve-field", args, CpuLimitSeconds + 60, ct);
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
        }
    }

    // ── WCS parsing ──

    /// <summary>
    /// Parse a FITS WCS header file (.wcs) emitted by solve-field.
    /// Extracts CRVAL1 (RA), CRVAL2 (Dec), CRPIX, CD matrix, and image dimensions.
    /// Computes the actual sky coordinate at the image center pixel using the
    /// full TAN (gnomonic) projection — CRVAL is at the reference pixel (CRPIX),
    /// which may NOT coincide with the image center after WCS refinement/tweaking.
    /// </summary>
    private static SolveResult? ParseWcsFile(string wcsPath)
    {
        if (!File.Exists(wcsPath)) return null;

        try
        {
            // FITS headers are 80-char fixed-width records
            string text = File.ReadAllText(wcsPath);

            double? crval1 = ExtractFitsValue(text, "CRVAL1");  // RA at reference pixel (degrees)
            double? crval2 = ExtractFitsValue(text, "CRVAL2");  // Dec at reference pixel (degrees)
            double? crpix1 = ExtractFitsValue(text, "CRPIX1");  // Reference pixel X (1-based)
            double? crpix2 = ExtractFitsValue(text, "CRPIX2");  // Reference pixel Y (1-based)
            double? cd11   = ExtractFitsValue(text, "CD1_1");
            double? cd12   = ExtractFitsValue(text, "CD1_2");
            double? cd21   = ExtractFitsValue(text, "CD2_1");
            double? cd22   = ExtractFitsValue(text, "CD2_2");
            double? naxis1 = ExtractFitsValue(text, "IMAGEW") ?? ExtractFitsValue(text, "NAXIS1");
            double? naxis2 = ExtractFitsValue(text, "IMAGEH") ?? ExtractFitsValue(text, "NAXIS2");

            if (crval1 == null || crval2 == null)
                return null;

            double raDeg  = crval1.Value;
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

            // ── Compute true image-center sky coordinate via TAN projection ──
            // CRVAL is the sky coord at CRPIX, which may NOT be the image center.
            // We project the actual center pixel through the WCS to get the true field center.
            bool hasCrpix = crpix1 != null && crpix2 != null;
            bool hasSize  = naxis1 != null && naxis2 != null;
            bool hasCd    = cd11 != null && cd12 != null && cd21 != null && cd22 != null;

            if (hasCrpix && hasSize && hasCd)
            {
                double cx = naxis1!.Value / 2.0;   // image center pixel (0.5-based)
                double cy = naxis2!.Value / 2.0;
                double dx = cx - crpix1!.Value;     // offset from reference pixel
                double dy = cy - crpix2!.Value;

                Logger.Debug($"PlateSolver WCS: CRPIX=({crpix1.Value:F1},{crpix2.Value:F1}), " +
                             $"ImageCenter=({cx:F1},{cy:F1}), offset=({dx:F1},{dy:F1})px");

                if (Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5)
                {
                    // Intermediate world coordinates (degrees) via CD matrix
                    double xIwc = cd11!.Value * dx + cd12!.Value * dy;
                    double yIwc = cd21!.Value * dx + cd22!.Value * dy;

                    // TAN inverse projection → native spherical coordinates
                    double r = Math.Sqrt(xIwc * xIwc + yIwc * yIwc);   // degrees
                    double phi, theta;
                    const double Rad2Deg = 180.0 / Math.PI;
                    if (r < 1e-12)
                    {
                        phi   = 0;
                        theta = Math.PI / 2.0;
                    }
                    else
                    {
                        phi   = Math.Atan2(xIwc, -yIwc);          // radians
                        theta = Math.Atan2(Rad2Deg, r);            // radians (= atan(57.296/r°))
                    }

                    // Native → celestial  (φ_p = π for zenithal projections)
                    double ra0  = crval1.Value / Rad2Deg;           // radians
                    double dec0 = crval2.Value / Rad2Deg;           // radians
                    double sinT = Math.Sin(theta), cosT = Math.Cos(theta);
                    double sinD = Math.Sin(dec0),  cosD = Math.Cos(dec0);
                    double sinP = Math.Sin(phi),   cosP = Math.Cos(phi);

                    double sinDec = sinT * sinD - cosT * cosD * cosP;
                    decDeg = Math.Asin(Math.Clamp(sinDec, -1.0, 1.0)) * Rad2Deg;

                    double aNum = cosT * sinP;
                    double aDen = sinT * cosD + cosT * sinD * cosP;
                    raDeg = (ra0 + Math.Atan2(aNum, aDen)) * Rad2Deg;

                    // Normalize RA to [0, 360)
                    raDeg = ((raDeg % 360.0) + 360.0) % 360.0;

                    Logger.Notice($"PlateSolver WCS: CRPIX offset ({dx:F1},{dy:F1})px → " +
                                  $"center shifted from CRVAL({crval1.Value:F4}°,{crval2.Value:F4}°) " +
                                  $"to imgCenter({raDeg:F4}°,{decDeg:F4}°)");
                }
                else
                {
                    Logger.Debug("PlateSolver WCS: CRPIX is at image center — using CRVAL directly");
                }
            }
            else
            {
                Logger.Debug($"PlateSolver WCS: missing CRPIX/NAXIS/CD — using CRVAL as center " +
                             $"(hasCrpix={hasCrpix}, hasSize={hasSize}, hasCd={hasCd})");
            }

            double fieldW = 0, fieldH = 0;
            if (hasSize && pixScale > 0)
            {
                fieldW = naxis1!.Value * pixScale / 3600.0;
                fieldH = naxis2!.Value * pixScale / 3600.0;
            }

            return new SolveResult
            {
                RaCenterHours = raDeg / 15.0,
                DecCenterDeg  = decDeg,
                PixelScaleArcsecPerPx = pixScale,
                RotationDeg   = rotation,
                FieldWidthDeg = fieldW,
                FieldHeightDeg = fieldH
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"PlateSolver: WCS parse error: {ex.Message}");
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

    // ── Temp file cleanup ──

    private static void CleanupTempFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;

        try
        {
            // Remove ALL solve-field artifacts to prevent disk exhaustion.
            // Each solve can generate 50-200 MB of auxiliary files.
            foreach (string file in Directory.GetFiles(dir))
            {
                try
                {
                    long kb = new FileInfo(file).Length / 1024;
                    File.Delete(file);
                    Logger.Debug($"Deleted temp file {Path.GetFileName(file)} ({kb} KB)");
                }
                catch { }
            }
            // Also clean subdirectories that solve-field may create
            foreach (string subDir in Directory.GetDirectories(dir))
            {try { Directory.Delete(subDir, true); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Deletes stale astrometry.net files left in <paramref name="tmpPath"/> (usually /tmp)
    /// by crashed or killed solve-field runs.  Each aborted run can leave 50-150 MB of
    /// .wcs / .rdls / .axy / .xyls / .solved / .match / .corr files plus tmp.solve-field.*
    /// scratch directories.  On a 454 MB tmpfs this quickly fills /tmp and causes dcraw to
    /// fail with "No space left on device" even when the main filesystem is fine.
    /// Only patterns known to be produced by astrometry.net are touched.
    /// </summary>
    private static void CleanupAstrometryTmp(string tmpPath)
    {
        if (!Directory.Exists(tmpPath)) return;
        try
        {
            long freedBytes = 0;
            // astrometry.net output file extensions
            var astroExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".wcs", ".rdls", ".axy", ".xyls", ".solved", ".match", ".corr", ".new" };

            foreach (string file in Directory.GetFiles(tmpPath))
            {
                string ext = Path.GetExtension(file);
                if (!astroExts.Contains(ext)) continue;
                try
                {
                    long sz = new FileInfo(file).Length;
                    File.Delete(file);
                    freedBytes += sz;
                }
                catch { }
            }

            // solve-field also creates tmp.solve-field.XXXXX scratch directories
            foreach (string dir in Directory.GetDirectories(tmpPath, "tmp.solve-field.*"))
            {
                try
                {
                    long sz = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                        .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } });
                    Directory.Delete(dir, true);
                    freedBytes += sz;
                }
                catch { }
            }

            if (freedBytes > 0)
                Logger.Notice($"PlateSolver: cleaned up {freedBytes / (1024 * 1024)} MB of stale astrometry files from {tmpPath}");
        }
        catch (Exception ex)
        {
            Logger.Debug($"CleanupAstrometryTmp({tmpPath}): {ex.Message}");
        }
    }
}
