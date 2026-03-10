using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;


public static class ImageProcessing
{

        /// <summary>
    /// Fraction of each image dimension to keep from the center before solving.
    /// 0.5 = keep center 50%×50% (25% of pixels) — removes distorted/vignetted borders.
    /// Set to 1.0 to disable cropping.
    /// Example: 24 MP full-frame → center-crop at 0.5 → 6 MP centre-only image.
    /// </summary>
    private static double CropFraction { get; set; } = 0.5;
    
    

    /// <summary>
    /// Crop an image to the centre <see cref="CropFraction"/> of each dimension using
    /// ImageMagick <c>convert</c>.  Pixel scale is unaffected — only the FOV shrinks,
    /// removing lens-distorted / vignetted borders that confuse star detection.
    /// </summary>
    internal static async Task<string?> CropCenterAsync(string imagePath, CancellationToken ct)
    {
        int pct = (int)Math.Round(CropFraction * 100.0);
        string outPath = Path.Combine(PlateSolver.TempDir, $"crop_{Path.GetFileName(imagePath)}");

        // ImageMagick: gravity Centre, then crop pct%×pct% from that anchor.
        // +repage resets the canvas so downstream tools see a clean image size.
        string bashCmd = $"convert '{imagePath}' -gravity Center -crop {pct}%x{pct}%+0+0 +repage '{outPath}'";
        var (exitCode, _, stderr) = await ExternalProcess.RunProcessAsync("bash", new[] { "-c", bashCmd }, 60, ct);
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
    internal static async Task<bool> ConvertRawToJpegAsync(string rawPath, string jpegPath, CancellationToken ct)
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
            var (code, _, err) = await ExternalProcess.RunProcessAsync("dcraw", new[] { "-c", "-w", "-h", "-b", "0.1", rawPath }, 120, ct, jpegPath);
            return code == 0;
        }

        // dcraw -c -w -h RAW | pnmtojpeg > output.jpg
        // Use bash -c with ArgumentList so that the script string is passed as a single
        // unambiguous argument — avoids .NET's Windows-style Arguments parser mangling spaces/quotes.
        // Export TMPDIR to keep any temp files out of /tmp RAMdisk.
        string tmpDir = PlateSolver.TempDir;
        string bashCmd = $"export TMPDIR='{tmpDir}'; dcraw -c -w -b 0.1 '{rawPath}' | {pipeCmd} > '{jpegPath}'";
        var (exitCode, _, stderr) = await ExternalProcess.RunProcessAsync("bash", new[] { "-c", bashCmd }, 120, ct);
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
}