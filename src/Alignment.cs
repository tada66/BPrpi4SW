using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Simple 2-star alignment system for alt-az mount celestial tracking.
/// Computes a 3x3 rotation matrix that transforms sky coordinates to mount coordinates.
/// </summary>
public static class Alignment
{
    /// <summary>
    /// An alignment point: a known star's celestial coords + the mount's encoder readings when pointed at it.
    /// </summary>
    public class AlignmentPoint
    {
        public float RA { get; set; }       // Right Ascension in hours (0-24)
        public float Dec { get; set; }      // Declination in degrees (-90 to +90)
        public int MountX { get; set; }     // Mount X encoder (arcseconds)
        public int MountY { get; set; }     // Mount Y encoder (arcseconds)
        public int MountZ { get; set; }     // Mount Z encoder (arcseconds)
        public DateTime TimeUtc { get; set; } // Time when alignment was taken
    }

    /// <summary>
    /// A catalog star entry for quick selection during alignment.
    /// </summary>
    public class CatalogStar
    {
        public string Name { get; set; } = "";
        public float RA { get; set; }    // hours
        public float Dec { get; set; }   // degrees
        public float Magnitude { get; set; }
    }

    // Bright star catalog for alignment (visible from northern mid-latitudes)
    public static readonly List<CatalogStar> StarCatalog = new()
    {
        // Ursa Major (Big Dipper) — easy to find
        new() { Name = "Dubhe",     RA = 11.062f, Dec = 61.751f,  Magnitude = 1.79f },
        new() { Name = "Merak",     RA = 11.031f, Dec = 56.382f,  Magnitude = 2.37f },
        new() { Name = "Phecda",    RA = 11.897f, Dec = 53.695f,  Magnitude = 2.44f },
        new() { Name = "Megrez",    RA = 12.257f, Dec = 57.033f,  Magnitude = 3.31f },
        new() { Name = "Alioth",    RA = 12.900f, Dec = 55.959f,  Magnitude = 1.77f },
        new() { Name = "Mizar",     RA = 13.399f, Dec = 54.925f,  Magnitude = 2.04f },
        new() { Name = "Alkaid",    RA = 13.792f, Dec = 49.313f,  Magnitude = 1.86f },
        // Other bright stars
        new() { Name = "Polaris",   RA = 2.530f,  Dec = 89.264f,  Magnitude = 1.98f },
        new() { Name = "Capella",   RA = 5.278f,  Dec = 45.998f,  Magnitude = 0.08f },
        new() { Name = "Betelgeuse",RA = 5.919f,  Dec = 7.407f,   Magnitude = 0.42f },
        new() { Name = "Sirius",    RA = 6.752f,  Dec = -16.716f, Magnitude = -1.46f },
        new() { Name = "Procyon",   RA = 7.655f,  Dec = 5.225f,   Magnitude = 0.34f },
        new() { Name = "Regulus",   RA = 10.140f, Dec = 11.967f,  Magnitude = 1.40f },
        new() { Name = "Arcturus",  RA = 14.261f, Dec = 19.182f,  Magnitude = -0.05f },
        new() { Name = "Vega",      RA = 18.616f, Dec = 38.784f,  Magnitude = 0.03f },
        new() { Name = "Deneb",     RA = 20.690f, Dec = 45.280f,  Magnitude = 1.25f },
        new() { Name = "Altair",    RA = 19.846f, Dec = 8.868f,   Magnitude = 0.77f },
    };

    // Stored alignment points
    private static readonly List<AlignmentPoint> _points = new();
    
    // Computed alignment matrix (3x3, row-major)
    private static float[]? _alignmentMatrix = null;
    
    // Observer location (degrees)
    public static float Latitude { get; set; } = 50.0f;   // Default to ~central Europe
    public static float Longitude { get; set; } = 14.42f;  // Default to ~Prague

    /// <summary>
    /// Clear all alignment points and reset matrix.
    /// </summary>
    public static void Reset()
    {
        _points.Clear();
        _alignmentMatrix = null;
        Logger.Notice("Alignment reset - all points cleared");
    }

    /// <summary>
    /// Add an alignment point. Call this when the user has centered a known star.
    /// </summary>
    public static async Task AddAlignmentPointAsync(float starRA, float starDec)
    {
        // Get current mount position
        var pos = await Tracker.GetAxisPositions();
        
        var point = new AlignmentPoint
        {
            RA = starRA,
            Dec = starDec,
            MountX = pos.XArcsecs,
            MountY = pos.YArcsecs,
            MountZ = pos.ZArcsecs,
            TimeUtc = DateTime.UtcNow
        };
        
        _points.Add(point);
        Logger.Notice($"Alignment point {_points.Count} added: RA={starRA:F2}h, Dec={starDec:F2}° => Mount({pos.XArcsecs}, {pos.YArcsecs}, {pos.ZArcsecs})");
        
        // Auto-compute matrix if we have enough points
        if (_points.Count >= 2)
        {
            ComputeAlignmentMatrix();
        }
    }

    /// <summary>
    /// Add alignment point with manual mount position (for testing).
    /// </summary>
    public static void AddAlignmentPoint(float starRA, float starDec, int mountX, int mountY, int mountZ)
    {
        var point = new AlignmentPoint
        {
            RA = starRA,
            Dec = starDec,
            MountX = mountX,
            MountY = mountY,
            MountZ = mountZ,
            TimeUtc = DateTime.UtcNow
        };
        
        _points.Add(point);
        Logger.Notice($"Alignment point {_points.Count} added: RA={starRA:F2}h, Dec={starDec:F2}° => Mount({mountX}, {mountY}, {mountZ})");
        
        if (_points.Count >= 2)
        {
            ComputeAlignmentMatrix();
        }
    }

    /// <summary>
    /// Compute the alignment matrix from stored points.
    /// For N=2: Uses two-vector orthonormalization (exact for 2 stars).
    /// For N≥3: Uses SVD least-squares (Wahba's problem) with quality-gated
    /// star inclusion. Stars that worsen the alignment (e.g. due to step loss
    /// during large slews) are automatically excluded.
    /// When refTime is provided, sky vectors are adjusted for sidereal drift
    /// relative to that reference time (matching the Pico's frame).
    /// </summary>
    private static void ComputeAlignmentMatrix(DateTime? refTime = null)
    {
        if (_points.Count < 2)
        {
            Logger.Warn("Need at least 2 alignment points to compute matrix");
            return;
        }

        DateTime tRef = refTime ?? _points[^1].TimeUtc;

        // Build sky and mount vectors for ALL points
        var allSkyVecs = new (double x, double y, double z)[_points.Count];
        var allMountVecs = new (double x, double y, double z)[_points.Count];
        for (int i = 0; i < _points.Count; i++)
        {
            allSkyVecs[i] = CelestialToUnitVector(_points[i].RA, _points[i].Dec, tRef, _points[i].TimeUtc);
            allMountVecs[i] = MountToUnitVector(_points[i].MountX, _points[i].MountY, _points[i].MountZ);
        }

        // ============================================================
        // Quality-gated star selection
        // ============================================================
        // Strategy: Start with the reliable 2-star baseline, then greedily
        // add each subsequent star ONLY if it doesn't degrade the alignment.
        // This prevents step-loss-corrupted stars from polluting the SVD.
        // ============================================================

        // Phase 1: 2-star baseline (always stars 0 and 1 — user manually centered)
        var bestActive = new List<int> { 0, 1 };
        float[] bestMatrix = ComputeRotationMatrix(allSkyVecs[0], allSkyVecs[1], allMountVecs[0], allMountVecs[1]);
        double bestAvg = ComputeAvgResidualForSubset(bestMatrix, allSkyVecs, allMountVecs, bestActive);

        var rejected = new List<int>();

        // Phase 2: Greedily try adding each subsequent star
        for (int k = 2; k < _points.Count; k++)
        {
            var candidateActive = new List<int>(bestActive) { k };

            // Build arrays for this candidate subset
            var candSky = new (double x, double y, double z)[candidateActive.Count];
            var candMount = new (double x, double y, double z)[candidateActive.Count];
            for (int ai = 0; ai < candidateActive.Count; ai++)
            {
                candSky[ai] = allSkyVecs[candidateActive[ai]];
                candMount[ai] = allMountVecs[candidateActive[ai]];
            }

            float[] candidateMatrix = ComputeRotationMatrixSVD(candSky, candMount);
            double candidateAvg = ComputeAvgResidualForSubset(candidateMatrix, allSkyVecs, allMountVecs, candidateActive);

            // Accept the star if:
            //   - It doesn't worsen avg residual by more than 50%, OR
            //   - The resulting avg is still excellent (< 10 arcmin = 0.167°)
            bool accept = candidateAvg <= bestAvg * 1.5 || candidateAvg < 0.167;

            if (accept)
            {
                bestActive = candidateActive;
                bestMatrix = candidateMatrix;
                bestAvg = candidateAvg;
            }
            else
            {
                rejected.Add(k);
                Logger.Warn($"  Auto-excluded star{k + 1} (RA={_points[k].RA:F2}h Dec={_points[k].Dec:F1}°): " +
                    $"would increase error from {bestAvg * 60:F1}' to {candidateAvg * 60:F1}' — likely step loss during slew");
            }
        }

        _alignmentMatrix = bestMatrix;

        // Phase 3: Final outlier check on accepted stars
        // Catches cases where an early star looked OK incrementally but is an outlier overall
        if (bestActive.Count >= 3)
        {
            bool changed = true;
            while (changed && bestActive.Count >= 3)
            {
                changed = false;
                var residuals = ComputeResiduals(_alignmentMatrix, allSkyVecs, allMountVecs, bestActive);

                // Find worst and best
                int worstIdx = 0;
                double bestRes = residuals[0], worstRes = residuals[0];
                for (int ai = 1; ai < residuals.Length; ai++)
                {
                    if (residuals[ai] > worstRes) { worstRes = residuals[ai]; worstIdx = ai; }
                    if (residuals[ai] < bestRes) bestRes = residuals[ai];
                }

                // Reject if worst > 5× best AND > 10 arcmin (0.167°)
                if (worstRes > 5.0 * bestRes && worstRes > 0.167)
                {
                    int rejIdx = bestActive[worstIdx];
                    rejected.Add(rejIdx);
                    bestActive.RemoveAt(worstIdx);

                    // Recompute matrix without the outlier
                    if (bestActive.Count == 2)
                    {
                        _alignmentMatrix = ComputeRotationMatrix(
                            allSkyVecs[bestActive[0]], allSkyVecs[bestActive[1]],
                            allMountVecs[bestActive[0]], allMountVecs[bestActive[1]]);
                    }
                    else
                    {
                        var sk = new (double x, double y, double z)[bestActive.Count];
                        var mk = new (double x, double y, double z)[bestActive.Count];
                        for (int ai = 0; ai < bestActive.Count; ai++)
                        {
                            sk[ai] = allSkyVecs[bestActive[ai]];
                            mk[ai] = allMountVecs[bestActive[ai]];
                        }
                        _alignmentMatrix = ComputeRotationMatrixSVD(sk, mk);
                    }

                    Logger.Warn($"  Outlier-rejected star{rejIdx + 1} (RA={_points[rejIdx].RA:F2}h Dec={_points[rejIdx].Dec:F1}°): " +
                        $"residual {worstRes * 60:F1}' vs best {bestRes * 60:F1}'");
                    changed = true;
                }
            }
        }

        // ============================================================
        // Diagnostics and quality assessment
        // ============================================================

        if (rejected.Count > 0)
            Logger.Notice($"  Using {bestActive.Count} of {_points.Count} stars ({rejected.Count} excluded)");
        if (bestActive.Count > 2)
            Logger.Notice($"  {bestActive.Count}-star least-squares alignment (SVD)");

        // Pairwise separation mismatch — PRIMARY step loss detector.
        // If the mount physically loses steps during large slews, the angular
        // separation between encoder positions will be LESS than the true sky separation.
        double maxPairSepError = 0;
        double maxStepLossPct = 0;
        for (int i = 0; i < _points.Count; i++)
        {
            for (int j = i + 1; j < _points.Count; j++)
            {
                double pairSkySep = Math.Acos(Math.Max(-1.0, Math.Min(1.0,
                    allSkyVecs[i].x * allSkyVecs[j].x + allSkyVecs[i].y * allSkyVecs[j].y + allSkyVecs[i].z * allSkyVecs[j].z))) * 180.0 / Math.PI;
                double pairMountSep = Math.Acos(Math.Max(-1.0, Math.Min(1.0,
                    allMountVecs[i].x * allMountVecs[j].x + allMountVecs[i].y * allMountVecs[j].y + allMountVecs[i].z * allMountVecs[j].z))) * 180.0 / Math.PI;
                double pairSepErr = Math.Abs(pairSkySep - pairMountSep);
                double stepLoss = (pairSkySep > 0.5) ? (1.0 - pairMountSep / pairSkySep) * 100.0 : 0;

                string lossTag = (Math.Abs(stepLoss) > 1.0) ? $", ~{Math.Abs(stepLoss):F1}% step loss" : "";
                Logger.Notice($"  Pair star{i + 1}-star{j + 1}: sky={pairSkySep:F3}° mount={pairMountSep:F3}° (Δ={pairSepErr:F3}°{lossTag})");

                if (pairSepErr > maxPairSepError) maxPairSepError = pairSepErr;
                if (Math.Abs(stepLoss) > Math.Abs(maxStepLossPct)) maxStepLossPct = stepLoss;
            }
        }

        // Per-star residual report
        double totalResidual = 0;
        int activeCount = 0;
        for (int i = 0; i < _points.Count; i++)
        {
            double err = ComputePointResidual(_alignmentMatrix!, allSkyVecs[i], allMountVecs[i]);

            bool isRejected = rejected.Contains(i);
            string tag = isRejected ? " [EXCLUDED]" : "";
            Logger.Notice($"  Residual star{i + 1} (RA={_points[i].RA:F2}h Dec={_points[i].Dec:F1}°): {err * 60:F1}' ({err:F3}°){tag}");

            if (!isRejected)
            {
                totalResidual += err;
                activeCount++;
            }
        }
        double avgResidual = totalResidual / Math.Max(1, activeCount);

        const double PLATE_SCALE = 43.7; // arcsec per pixel
        double avgPixels = avgResidual * 3600.0 / PLATE_SCALE;

        Logger.Notice($"  Average residual: {avgResidual * 60:F1}' ({avgResidual:F3}°) ≈ {avgPixels:F0} pixels @ 28mm");

        // Step loss warning
        if (maxPairSepError > 0.3)
        {
            Logger.Warn($"  *** STEP LOSS DETECTED: worst pair Δ={maxPairSepError:F2}° ({Math.Abs(maxStepLossPct):F1}% loss) ***");
            Logger.Warn($"  *** Mount encoder positions don't match true star separations ***");
            Logger.Warn($"  *** TIP: Use stars closer in AZIMUTH to reduce Z-axis slew distance ***");
            Logger.Warn($"  *** TIP: Warm up the Z bearing by slewing back and forth before calibrating ***");
        }

        if (avgResidual > 0.5 || maxPairSepError > 0.7)
        {
            Logger.Warn($"  *** ALIGNMENT REJECTED: pointing would be ≈{avgPixels:F0}+ pixels off ***");
            if (maxPairSepError > 0.5)
                Logger.Warn($"  *** Cause: {Math.Abs(maxStepLossPct):F1}% step loss on Z bearing ***");
            Logger.Warn($"  *** Please RESET and recalibrate with stars closer together in azimuth ***");
            _alignmentMatrix = null;
        }
        else if (avgResidual > 0.25 || maxPairSepError > 0.3)
        {
            Logger.Warn($"  Alignment MARGINAL: ~{avgPixels:F0}px pointing error (pair Δ={maxPairSepError:F2}°)");
            Logger.Warn($"  Consider resetting and using star pairs closer in azimuth.");
        }
        else if (avgResidual > 0.10)
        {
            Logger.Notice($"  Alignment quality: OK (~{avgPixels:F0}px error, pair Δ={maxPairSepError:F3}°)");
        }
        else
        {
            Logger.Notice($"  Alignment quality: EXCELLENT (~{avgPixels:F0}px error, pair Δ={maxPairSepError:F3}°)");
        }

        if (_alignmentMatrix != null)
        {
            Logger.Notice("Alignment matrix computed:");
            Logger.Notice($"  [{_alignmentMatrix[0]:F4}, {_alignmentMatrix[1]:F4}, {_alignmentMatrix[2]:F4}]");
            Logger.Notice($"  [{_alignmentMatrix[3]:F4}, {_alignmentMatrix[4]:F4}, {_alignmentMatrix[5]:F4}]");
            Logger.Notice($"  [{_alignmentMatrix[6]:F4}, {_alignmentMatrix[7]:F4}, {_alignmentMatrix[8]:F4}]");
        }
    }

    /// <summary>
    /// Compute the angular residual (degrees) between R*sky and mount for a single point.
    /// </summary>
    private static double ComputePointResidual(float[] matrix, (double x, double y, double z) sky, (double x, double y, double z) mount)
    {
        double rx = matrix[0] * sky.x + matrix[1] * sky.y + matrix[2] * sky.z;
        double ry = matrix[3] * sky.x + matrix[4] * sky.y + matrix[5] * sky.z;
        double rz = matrix[6] * sky.x + matrix[7] * sky.y + matrix[8] * sky.z;
        return Math.Acos(Math.Max(-1.0, Math.Min(1.0, rx * mount.x + ry * mount.y + rz * mount.z))) * 180.0 / Math.PI;
    }

    /// <summary>
    /// Compute per-star residuals for a subset of stars.
    /// </summary>
    private static double[] ComputeResiduals(float[] matrix,
        (double x, double y, double z)[] allSky, (double x, double y, double z)[] allMount,
        List<int> indices)
    {
        var residuals = new double[indices.Count];
        for (int ai = 0; ai < indices.Count; ai++)
            residuals[ai] = ComputePointResidual(matrix, allSky[indices[ai]], allMount[indices[ai]]);
        return residuals;
    }

    /// <summary>
    /// Compute average residual across a subset of stars.
    /// </summary>
    private static double ComputeAvgResidualForSubset(float[] matrix,
        (double x, double y, double z)[] allSky, (double x, double y, double z)[] allMount,
        List<int> indices)
    {
        double total = 0;
        foreach (int i in indices)
            total += ComputePointResidual(matrix, allSky[i], allMount[i]);
        return total / indices.Count;
    }

    /// <summary>
    /// Convert RA/Dec to a unit vector in equatorial coordinates.
    /// Adjusts RA for sidereal drift between observation time and reference time,
    /// so that vectors from different observation times are expressed in the same
    /// rotating frame that the Pico uses at runtime.
    /// </summary>
    private static (double x, double y, double z) CelestialToUnitVector(float raHours, float decDeg, DateTime refTime, DateTime obsTime)
    {
        const double SIDEREAL_RATE_ARCSEC_PER_SEC = 15.041; // Must match Pico's SIDEREAL_RATE_ARCSEC_PER_SEC

        // RA in arcseconds
        double raArcsec = raHours * 54000.0;

        // Adjust RA for sidereal drift between observation and reference time.
        // The Pico's sky vector formula is: sky = Eq(RA - ω*elapsed_from_Tref).
        // At observation time t_obs, elapsed = t_obs - T_ref (negative since obs is before tracking).
        // So the Pico would compute: RA - ω*(t_obs - T_ref) = RA + ω*(T_ref - t_obs).
        double deltaSec = (refTime - obsTime).TotalSeconds;
        double adjustedRaArcsec = raArcsec + SIDEREAL_RATE_ARCSEC_PER_SEC * deltaSec;

        // Convert to radians
        double raRad = adjustedRaArcsec * Math.PI / (180.0 * 3600.0);
        double decRad = decDeg * Math.PI / 180.0;

        // Equatorial unit vector (Z = celestial pole, X-Y = equatorial plane)
        // This matches the Pico's compute_celestial_targets() convention:
        //   sky = (cos_dec * cos_ra, cos_dec * sin_ra, sin_dec)
        double cosDec = Math.Cos(decRad);
        return (cosDec * Math.Cos(raRad), cosDec * Math.Sin(raRad), Math.Sin(decRad));
    }

    /// <summary>
    /// Convert mount encoder positions (arcseconds) to a unit vector.
    /// Assumes: X=tilt (altitude), Z=pan (azimuth), Y=roll
    /// </summary>
    private static (double x, double y, double z) MountToUnitVector(int xArcsec, int yArcsec, int zArcsec)
    {
        // Convert arcseconds to radians
        double alt = xArcsec * Math.PI / (180.0 * 3600.0);  // X = altitude
        double az = zArcsec * Math.PI / (180.0 * 3600.0);   // Z = azimuth
        // Y (roll) doesn't affect pointing direction, only image orientation

        // Convert to unit vector
        double x = Math.Cos(alt) * Math.Cos(az);
        double y = Math.Cos(alt) * Math.Sin(az);
        double z = Math.Sin(alt);

        return (x, y, z);
    }

    #region Alt-Az / Sidereal Time Computation

    /// <summary>
    /// Compute Julian Date from a UTC DateTime.
    /// </summary>
    private static double ToJulianDate(DateTime utc)
    {
        int y = utc.Year;
        int m = utc.Month;
        double d = utc.Day + utc.Hour / 24.0 + utc.Minute / 1440.0 + utc.Second / 86400.0 + utc.Millisecond / 86400000.0;

        if (m <= 2)
        {
            y -= 1;
            m += 12;
        }

        int A = y / 100;
        int B = 2 - A + A / 4;

        return (int)(365.25 * (y + 4716)) + (int)(30.6001 * (m + 1)) + d + B - 1524.5;
    }

    /// <summary>
    /// Compute Local Sidereal Time in hours (0-24) from UTC time and longitude.
    /// </summary>
    public static double ComputeLocalSiderealTime(DateTime utcTime, double longitudeDeg)
    {
        double jd = ToJulianDate(utcTime);
        double d = jd - 2451545.0; // days since J2000.0

        // Greenwich Mean Sidereal Time in hours
        double gmst = 18.697374558 + 24.06570982441908 * d;
        gmst = gmst % 24.0;
        if (gmst < 0) gmst += 24.0;

        // Local Sidereal Time
        double lst = gmst + longitudeDeg / 15.0;
        lst = lst % 24.0;
        if (lst < 0) lst += 24.0;

        return lst;
    }

    /// <summary>
    /// Compute altitude and azimuth of a celestial object at a given time and location.
    /// Returns (altitude_degrees, azimuth_degrees) where azimuth is 0=N, 90=E, 180=S, 270=W.
    /// </summary>
    public static (double alt, double az) ComputeAltAz(double raHours, double decDeg, DateTime utcTime, double latDeg, double lonDeg)
    {
        double lst = ComputeLocalSiderealTime(utcTime, lonDeg);
        double ha = (lst - raHours) * 15.0; // hour angle in degrees

        double haRad = ha * Math.PI / 180.0;
        double decRad = decDeg * Math.PI / 180.0;
        double latRad = latDeg * Math.PI / 180.0;

        double sinAlt = Math.Sin(decRad) * Math.Sin(latRad) + Math.Cos(decRad) * Math.Cos(latRad) * Math.Cos(haRad);
        sinAlt = Math.Max(-1.0, Math.Min(1.0, sinAlt));
        double alt = Math.Asin(sinAlt);

        double cosAz = (Math.Sin(decRad) - Math.Sin(alt) * Math.Sin(latRad)) / (Math.Cos(alt) * Math.Cos(latRad));
        cosAz = Math.Max(-1.0, Math.Min(1.0, cosAz));
        double az = Math.Acos(cosAz);

        // atan2 convention: azimuth measured from North through East
        if (Math.Sin(haRad) > 0)
            az = 2 * Math.PI - az;

        return (alt * 180.0 / Math.PI, az * 180.0 / Math.PI);
    }

    /// <summary>
    /// After recording alignment star 1, compute and automatically slew to the approximate
    /// mount position of alignment star 2. Uses alt-az computation to estimate the delta.
    /// </summary>
    public static async Task GotoApproximateAsync(float targetRA, float targetDec)
    {
        if (_points.Count < 1)
        {
            Logger.Warn("Need at least 1 alignment point before auto-goto.");
            return;
        }

        var star1 = _points[^1]; // use the most recent alignment point

        // Compute alt-az of star 1 at the time it was recorded
        var (alt1, az1) = ComputeAltAz(star1.RA, star1.Dec, star1.TimeUtc, Latitude, Longitude);

        // Compute alt-az of target star NOW
        var (alt2, az2) = ComputeAltAz(targetRA, targetDec, DateTime.UtcNow, Latitude, Longitude);

        // Compute delta in arcseconds
        double deltaAltDeg = alt2 - alt1;
        double deltaAzDeg = az2 - az1;

        // Handle azimuth wrapping (-180 to +180)
        if (deltaAzDeg > 180.0) deltaAzDeg -= 360.0;
        if (deltaAzDeg < -180.0) deltaAzDeg += 360.0;

        int deltaX = (int)(deltaAltDeg * 3600.0);
        int deltaZ = (int)(deltaAzDeg * 3600.0);

        Logger.Notice($"Auto-goto: star1 alt-az=({alt1:F2}°, {az1:F2}°), target alt-az=({alt2:F2}°, {az2:F2}°)");
        Logger.Notice($"  Moving ΔX={deltaX} arcsec ({deltaAltDeg:F2}°), ΔZ={deltaZ} arcsec ({deltaAzDeg:F2}°)");

        if (alt2 < 5.0)
        {
            Logger.Warn($"  Target is very low (alt={alt2:F1}°) — may not be visible.");
        }
        if (alt2 > 80.0)
        {
            Logger.Warn($"  Target is near zenith (alt={alt2:F1}°) — tracking accuracy will be limited.");
        }

        // Resume motors and move
        await UartClient.Client.ResumeMotors();
        await UartClient.Client.MoveRelative(Axis.X, deltaX);
        await UartClient.Client.MoveRelative(Axis.Z, deltaZ);

        Console.WriteLine($"Mount moving to approximate position of target. Fine-tune when star is in view.");
    }

    /// <summary>
    /// Print list of currently visible catalog stars (above horizon).
    /// </summary>
    public static void PrintVisibleStars()
    {
        DateTime now = DateTime.UtcNow;
        double lst = ComputeLocalSiderealTime(now, Longitude);
        Console.WriteLine($"\nVisible alignment stars (LST={lst:F2}h, Lat={Latitude:F1}°, Lon={Longitude:F1}°):");
        Console.WriteLine($"{"#",-4} {"Name",-12} {"RA(h)",-8} {"Dec(°)",-8} {"Alt(°)",-8} {"Az(°)",-8} {"Mag",-5}");
        Console.WriteLine(new string('-', 56));

        int idx = 0;
        foreach (var star in StarCatalog)
        {
            var (alt, az) = ComputeAltAz(star.RA, star.Dec, now, Latitude, Longitude);
            if (alt > 10.0) // Only show stars above 10° altitude
            {
                Console.WriteLine($"{idx,-4} {star.Name,-12} {star.RA,-8:F3} {star.Dec,-8:F2} {alt,-8:F1} {az,-8:F1} {star.Magnitude,-5:F2}");
            }
            idx++;
        }
        Console.WriteLine();
    }

    /// <summary>
    /// Look up a star by name or index from the catalog.
    /// Returns null if not found.
    /// </summary>
    public static CatalogStar? LookupStar(string input)
    {
        // Try parsing as catalog index
        if (int.TryParse(input, out int idx) && idx >= 0 && idx < StarCatalog.Count)
            return StarCatalog[idx];

        // Try matching by name (case-insensitive, partial match)
        input = input.Trim();
        foreach (var star in StarCatalog)
        {
            if (star.Name.Equals(input, StringComparison.OrdinalIgnoreCase))
                return star;
        }
        // Partial match
        foreach (var star in StarCatalog)
        {
            if (star.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                return star;
        }
        return null;
    }

    #endregion

    /// <summary>
    /// Compute rotation matrix from two pairs of corresponding vectors.
    /// Uses a simple orthonormalization approach.
    /// </summary>
    private static float[] ComputeRotationMatrix(
        (double x, double y, double z) sky1, (double x, double y, double z) sky2,
        (double x, double y, double z) mount1, (double x, double y, double z) mount2)
    {
        // Build orthonormal bases for both frames
        var (sx1, sy1, sz1) = Normalize(sky1);
        var skyPerp = Cross(sky1, sky2);
        var (sx2, sy2, sz2) = Normalize(skyPerp);
        var (sx3, sy3, sz3) = Cross((sx1, sy1, sz1), (sx2, sy2, sz2));

        var (mx1, my1, mz1) = Normalize(mount1);
        var mountPerp = Cross(mount1, mount2);
        var (mx2, my2, mz2) = Normalize(mountPerp);
        var (mx3, my3, mz3) = Cross((mx1, my1, mz1), (mx2, my2, mz2));

        // Sky basis matrix (columns are basis vectors)
        // S = [s1 | s2 | s3]
        // Mount basis matrix
        // M = [m1 | m2 | m3]
        // Rotation R = M * S^T  (transforms sky to mount)

        // R[i,j] = sum_k M[i,k] * S[j,k] = dot(M_row_i, S_row_j) but with column vectors...
        // Actually: R = M * S^(-1) = M * S^T (since S is orthonormal)

        // M as rows of column vectors transposed, S^T as columns transposed
        // Let's just compute directly: R * s1 = m1, R * s2 = m2, R * s3 = m3

        // Build S matrix (sky basis as columns)
        double[,] S = {
            { sx1, sx2, sx3 },
            { sy1, sy2, sy3 },
            { sz1, sz2, sz3 }
        };

        // Build M matrix (mount basis as columns)
        double[,] M = {
            { mx1, mx2, mx3 },
            { my1, my2, my3 },
            { mz1, mz2, mz3 }
        };

        // R = M * S^T
        double[,] ST = Transpose(S);
        double[,] R = Multiply(M, ST);

        // Convert to float array (row-major)
        return new float[]
        {
            (float)R[0, 0], (float)R[0, 1], (float)R[0, 2],
            (float)R[1, 0], (float)R[1, 1], (float)R[1, 2],
            (float)R[2, 0], (float)R[2, 1], (float)R[2, 2]
        };
    }

    /// <summary>
    /// Compute optimal rotation matrix from N pairs of corresponding vectors
    /// using SVD (Wahba's problem / Kabsch algorithm).
    /// Minimizes Σ ||R*sky_i - mount_i||² across all point pairs.
    /// </summary>
    private static float[] ComputeRotationMatrixSVD(
        (double x, double y, double z)[] skyVecs,
        (double x, double y, double z)[] mountVecs)
    {
        int n = skyVecs.Length;

        // Build cross-covariance matrix H = Σ mount_i * sky_i^T (3x3)
        double[,] H = new double[3, 3];
        for (int k = 0; k < n; k++)
        {
            var s = skyVecs[k];
            var m = mountVecs[k];
            double[] sv = { s.x, s.y, s.z };
            double[] mv = { m.x, m.y, m.z };
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    H[i, j] += mv[i] * sv[j];
        }

        // SVD: H = U S V^T
        var (U, sigma, V) = SVD3x3(H);

        // Ensure proper rotation (det = +1, not reflection)
        double detU = Det3x3(U);
        double detV = Det3x3(V);
        double d = (detU * detV < 0) ? -1.0 : 1.0;

        // R = U * diag(1, 1, d) * V^T
        // Apply d to last column of U
        double[,] Ud = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            Ud[i, 0] = U[i, 0];
            Ud[i, 1] = U[i, 1];
            Ud[i, 2] = U[i, 2] * d;
        }

        double[,] Vt = Transpose(V);
        double[,] R = Multiply(Ud, Vt);

        return new float[]
        {
            (float)R[0, 0], (float)R[0, 1], (float)R[0, 2],
            (float)R[1, 0], (float)R[1, 1], (float)R[1, 2],
            (float)R[2, 0], (float)R[2, 1], (float)R[2, 2]
        };
    }

    /// <summary>
    /// Compute SVD of a 3x3 matrix using Jacobi eigenvalue iterations on H^T*H.
    /// Returns (U, sigma, V) where H = U * diag(sigma) * V^T.
    /// </summary>
    private static (double[,] U, double[] sigma, double[,] V) SVD3x3(double[,] H)
    {
        // Step 1: Compute symmetric M = H^T * H
        double[,] M = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                for (int k = 0; k < 3; k++)
                    M[i, j] += H[k, i] * H[k, j];

        // Step 2: Eigendecompose M using Jacobi rotations
        // M = V * diag(eigenvalues) * V^T
        double[,] V = { {1,0,0}, {0,1,0}, {0,0,1} };
        double[,] D = (double[,])M.Clone();

        for (int iter = 0; iter < 100; iter++)
        {
            // Find largest off-diagonal element
            int p = 0, q = 1;
            double maxOff = Math.Abs(D[0, 1]);
            if (Math.Abs(D[0, 2]) > maxOff) { p = 0; q = 2; maxOff = Math.Abs(D[0, 2]); }
            if (Math.Abs(D[1, 2]) > maxOff) { p = 1; q = 2; maxOff = Math.Abs(D[1, 2]); }

            if (maxOff < 1e-15) break; // Converged

            // Compute Jacobi rotation angle
            double diff = D[q, q] - D[p, p];
            double t; // tan(theta)
            if (Math.Abs(D[p, q]) < 1e-30 * Math.Abs(diff))
            {
                t = D[p, q] / diff;
            }
            else
            {
                double phi = diff / (2.0 * D[p, q]);
                t = 1.0 / (Math.Abs(phi) + Math.Sqrt(1.0 + phi * phi));
                if (phi < 0) t = -t;
            }

            double c = 1.0 / Math.Sqrt(1.0 + t * t); // cos(theta)
            double s = t * c;                          // sin(theta)
            double tau = s / (1.0 + c);

            // Update D (symmetric, only need to update relevant entries)
            double dpq = D[p, q];
            D[p, q] = 0;
            D[q, p] = 0;
            D[p, p] -= t * dpq;
            D[q, q] += t * dpq;

            // Update off-diagonal elements
            for (int r = 0; r < 3; r++)
            {
                if (r == p || r == q) continue;
                double drp = D[r, p];
                double drq = D[r, q];
                D[r, p] = D[p, r] = drp - s * (drq + tau * drp);
                D[r, q] = D[q, r] = drq + s * (drp - tau * drq);
            }

            // Accumulate eigenvectors: V = V * G
            for (int r = 0; r < 3; r++)
            {
                double vrp = V[r, p];
                double vrq = V[r, q];
                V[r, p] = vrp - s * (vrq + tau * vrp);
                V[r, q] = vrq + s * (vrp - tau * vrq);
            }
        }

        // Step 3: Singular values = sqrt(eigenvalues of H^T*H)
        double[] sigma = new double[3];
        for (int i = 0; i < 3; i++)
            sigma[i] = Math.Sqrt(Math.Max(0.0, D[i, i]));

        // Step 4: U = H * V * S^{-1}
        double[,] HV = Multiply(H, V);
        double[,] U = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                U[i, j] = sigma[j] > 1e-10 ? HV[i, j] / sigma[j] : 0.0;

        return (U, sigma, V);
    }

    /// <summary>
    /// Compute determinant of a 3x3 matrix.
    /// </summary>
    private static double Det3x3(double[,] m)
    {
        return m[0,0] * (m[1,1]*m[2,2] - m[1,2]*m[2,1])
             - m[0,1] * (m[1,0]*m[2,2] - m[1,2]*m[2,0])
             + m[0,2] * (m[1,0]*m[2,1] - m[1,1]*m[2,0]);
    }

    #region Vector/Matrix Math Helpers

    private static (double, double, double) Normalize((double x, double y, double z) v)
    {
        double len = Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        if (len < 1e-10) return (1, 0, 0); // Fallback
        return (v.x / len, v.y / len, v.z / len);
    }

    private static (double, double, double) Cross((double x, double y, double z) a, (double x, double y, double z) b)
    {
        return (
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x
        );
    }

    private static double[,] Transpose(double[,] m)
    {
        return new double[,]
        {
            { m[0, 0], m[1, 0], m[2, 0] },
            { m[0, 1], m[1, 1], m[2, 1] },
            { m[0, 2], m[1, 2], m[2, 2] }
        };
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        double[,] r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                for (int k = 0; k < 3; k++)
                    r[i, j] += a[i, k] * b[k, j];
        return r;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Check if alignment is complete (matrix computed).
    /// </summary>
    public static bool IsAligned => _alignmentMatrix != null;

    /// <summary>
    /// Get the computed alignment matrix (or identity if not aligned).
    /// </summary>
    public static float[] GetAlignmentMatrix()
    {
        return _alignmentMatrix ?? new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
    }

    /// <summary>
    /// Get number of alignment points collected.
    /// </summary>
    public static int PointCount => _points.Count;

    /// <summary>
    /// Start tracking a target using the computed alignment.
    /// Recomputes the alignment matrix relative to the current time (which becomes
    /// the Pico's reference time) so that the sidereal drift frames match.
    /// </summary>
    public static async Task<bool> StartTrackingAsync(float targetRA, float targetDec)
    {
        if (!IsAligned)
        {
            Logger.Warn("Cannot start tracking - not aligned! Add at least 2 alignment points first.");
            return false;
        }

        // Recompute alignment matrix with current time as the Pico's reference frame.
        // This ensures sky vectors are adjusted for sidereal drift since alignment.
        long refTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        DateTime refDateTime = DateTimeOffset.FromUnixTimeSeconds(refTime).UtcDateTime;
        ComputeAlignmentMatrix(refDateTime);

        if (_alignmentMatrix == null)
        {
            Logger.Warn("Cannot start tracking — alignment was rejected (too much step loss).");
            Logger.Warn("Please RESET and recalibrate with stars closer together in azimuth.");
            return false;
        }

        // Predict initial mount position for diagnostic purposes
        var targetSky = CelestialToUnitVector(targetRA, targetDec, refDateTime, refDateTime); // elapsed=0 at refTime
        var m = _alignmentMatrix!;
        double mx = m[0]*targetSky.x + m[1]*targetSky.y + m[2]*targetSky.z;
        double my = m[3]*targetSky.x + m[4]*targetSky.y + m[5]*targetSky.z;
        double mz = m[6]*targetSky.x + m[7]*targetSky.y + m[8]*targetSky.z;
        double predAlt = Math.Asin(Math.Max(-1.0, Math.Min(1.0, mz))) * 180.0 / Math.PI;
        double predAz  = Math.Atan2(my, mx) * 180.0 / Math.PI;
        long predXArcsec = (long)(predAlt * 3600);
        long predZArcsec = (long)(predAz  * 3600);
        Logger.Notice($"Predicted initial mount position: X={predXArcsec} arcsec ({predAlt:F2}°), Z={predZArcsec} arcsec ({predAz:F2}°)");
        if (predAlt > 80.0)
            Logger.Warn($"  *** Target is near ZENITH (alt={predAlt:F1}°) — tracking accuracy degrades significantly above 80°. Choose a lower target if possible. ***");

        return await UartClient.Client.StartCelestialTracking(
            targetRA, 
            targetDec, 
            _alignmentMatrix!, 
            refTime, 
            Latitude
        );
    }

    /// <summary>
    /// Interactive alignment test from console.
    /// Improved workflow with star catalog and auto-goto.
    /// </summary>
    public static async Task InteractiveAlignmentTest()
    {
        Console.WriteLine("\n=== Star Alignment ===");
        Console.WriteLine($"Observer: Lat={Latitude:F2}°, Lon={Longitude:F2}°");
        PrintAlignHelp();

        while (true)
        {
            Console.Write("Align> ");
            string? input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input)) continue;
            string lower = input.ToLower();

            switch (lower)
            {
                case "1":
                case "add":
                    await HandleAddStar();
                    break;

                case "2":
                case "status":
                    PrintStatus();
                    break;

                case "3":
                case "track":
                    await HandleTrack();
                    break;

                case "g":
                case "goto":
                    await HandleGoto();
                    break;

                case "s":
                case "stars":
                    PrintVisibleStars();
                    break;

                case "r":
                case "reset":
                    Reset();
                    break;

                case "h":
                case "help":
                    PrintAlignHelp();
                    break;

                case "q":
                case "quit":
                    return;

                default:
                    Console.WriteLine("Unknown command. Type 'h' for help.");
                    break;
            }
        }
    }

    private static void PrintAlignHelp()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  1/add   - Add alignment star (record current mount position)");
        Console.WriteLine("  2/status- Show alignment status");
        Console.WriteLine("  3/track - Start tracking a target");
        Console.WriteLine("  g/goto  - Auto-goto approximate position of next star (after star 1)");
        Console.WriteLine("  s/stars - Show visible alignment stars from catalog");
        Console.WriteLine("  r/reset - Reset alignment");
        Console.WriteLine("  h/help  - Show this help");
        Console.WriteLine("  q/quit  - Exit alignment mode");
        Console.WriteLine();
    }

    /// <summary>
    /// Prompt user for star RA/Dec (with catalog lookup) and add alignment point.
    /// After star 1, offers auto-goto to star 2.
    /// </summary>
    private static async Task HandleAddStar()
    {
        var (ra, dec, starName) = PromptForStar("Enter star name/index (or RA in hours for manual): ");
        if (ra < 0) return; // user cancelled

        Console.WriteLine($"Centering on {starName} (RA={ra:F3}h, Dec={dec:F3}°)...");
        Console.WriteLine("Make sure the star is centered in your view, then press Enter.");
        Console.ReadLine();

        await AddAlignmentPointAsync(ra, dec);

        if (_points.Count == 1 && _alignmentMatrix == null)
        {
            Console.WriteLine();
            Console.WriteLine("Star 1 recorded! Now you need to center star 2.");
            Console.WriteLine("TIP: Use 'g' (goto) to auto-slew to the approximate position of star 2.");
            Console.WriteLine("     Use 's' (stars) to see visible stars, then 'g' to goto one.");
            Console.WriteLine("     Then fine-tune centering and use '1' to record star 2.");
        }
    }

    /// <summary>
    /// Prompt user for a star (from catalog or manual RA/Dec entry).
    /// Returns (ra, dec, displayName). Returns (-1, -1, "") if cancelled.
    /// </summary>
    private static (float ra, float dec, string name) PromptForStar(string prompt)
    {
        Console.Write(prompt);
        string? input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input)) return (-1, -1, "");

        // Try catalog lookup first
        var star = LookupStar(input);
        if (star != null)
        {
            Console.WriteLine($"  → {star.Name}: RA={star.RA:F3}h, Dec={star.Dec:F3}°");
            return (star.RA, star.Dec, star.Name);
        }

        // Try parsing as RA (manual entry)
        if (float.TryParse(input, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ra))
        {
            Console.Write("Enter Dec (degrees): ");
            string? decInput = Console.ReadLine()?.Trim();
            if (float.TryParse(decInput, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dec))
            {
                return (ra, dec, $"RA={ra:F3}h Dec={dec:F2}°");
            }
            Console.WriteLine("Invalid Dec value.");
            return (-1, -1, "");
        }

        Console.WriteLine($"Star '{input}' not found in catalog. Enter RA in hours for manual entry.");
        return (-1, -1, "");
    }

    /// <summary>
    /// Handle the goto command — auto-slew to approximate position of a star.
    /// </summary>
    private static async Task HandleGoto()
    {
        if (_points.Count < 1)
        {
            Console.WriteLine("Add at least 1 alignment star first (command '1').");
            return;
        }

        var (ra, dec, name) = PromptForStar("Goto star name/index (or RA): ");
        if (ra < 0) return;

        Console.WriteLine($"Auto-slewing to approximate position of {name}...");
        await GotoApproximateAsync(ra, dec);
        Console.WriteLine("Done. Fine-tune position with xr/zr, then use '1' to record alignment point.");
    }

    /// <summary>
    /// Handle tracking command.
    /// </summary>
    private static async Task HandleTrack()
    {
        if (!IsAligned)
        {
            Console.WriteLine("Not aligned yet! Add at least 2 stars first.");
            return;
        }

        var (ra, dec, name) = PromptForStar("Track target name/index (or RA): ");
        if (ra < 0) return;

        Console.WriteLine($"Starting tracking on {name}...");
        await StartTrackingAsync(ra, dec);
    }

    private static void PrintStatus()
    {
        Console.WriteLine($"Alignment points: {PointCount}");
        for (int i = 0; i < _points.Count; i++)
        {
            var p = _points[i];
            var (alt, az) = ComputeAltAz(p.RA, p.Dec, DateTime.UtcNow, Latitude, Longitude);
            Console.WriteLine($"  Star {i + 1}: RA={p.RA:F3}h, Dec={p.Dec:F2}° (current alt={alt:F1}°, az={az:F1}°) Mount({p.MountX}, {p.MountY}, {p.MountZ})");
        }
        Console.WriteLine($"Aligned: {IsAligned}");
        if (IsAligned)
        {
            var m = _alignmentMatrix!;
            Console.WriteLine("Matrix:");
            Console.WriteLine($"  [{m[0]:F4}, {m[1]:F4}, {m[2]:F4}]");
            Console.WriteLine($"  [{m[3]:F4}, {m[4]:F4}, {m[5]:F4}]");
            Console.WriteLine($"  [{m[6]:F4}, {m[7]:F4}, {m[8]:F4}]");
        }
    }

    #endregion
}
