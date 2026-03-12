using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Star alignment system for alt-az mount celestial tracking.
/// Computes a 3x3 transformation matrix that maps sky coordinates to mount coordinates.
/// For 2 stars: orthonormalized rotation matrix (3 DOF).
/// For 3+ stars: affine matrix (9 DOF) that captures gear ratio errors, axis
/// non-orthogonality, and flexure — or falls back to SVD rotation if the
/// mount geometry is close to ideal.
/// </summary>
public static partial class Calibration
{
    public static event Action<AutoProgressInfo>? CalibrationUpdated;

    /// <summary>Fired after each guide-loop check (correction or no-op).</summary>
    public static event Action<GuidedTrackingProgressInfo>? GuidedTrackingProgress;

    /// <summary>Per-check telemetry broadcast to the UI during guided tracking.</summary>
    public class GuidedTrackingProgressInfo
    {
        public int Check { get; set; }
        public int MaxCorrections { get; set; }
        public int Corrections { get; set; }
        public double DriftPx { get; set; }
        public double DriftArcmin { get; set; }
        public bool CorrectionApplied { get; set; }
        public int? CorrXArcsec { get; set; }
        public int? CorrZArcsec { get; set; }
    }

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
    /// Result from an alignment computation, containing quality metrics and per-star diagnostics.
    /// </summary>
    public class AlignmentResult
    {
        public string Quality { get; set; } = "NOT_ALIGNED";
        public double AverageResidualArcmin { get; set; }
        public double AverageResidualPixels { get; set; }
        public double MaxPairErrorDeg { get; set; }
        public double StepLossPercent { get; set; }
        public int ActiveStarCount { get; set; }
        public int RejectedCount { get; set; }
        public List<StarResidualInfo> Stars { get; set; } = new();
    }

    public class StarResidualInfo
    {
        public int Index { get; set; }
        public float RA { get; set; }
        public float Dec { get; set; }
        public double ResidualArcmin { get; set; }
        public bool Excluded { get; set; }
        public string? ExclusionReason { get; set; }
    }

    public class AutoProgressInfo
    {
        public int PointCount { get; set; }
        public string Quality { get; set; } = "UNKNOWN";
        public double AverageResidualArcmin { get; set; }
        public string Message { get; set; } = "";
        public int CurrentPosition { get; set; }
        public int TotalPositions { get; set; }
    }

    // Stored alignment points
    private static readonly List<AlignmentPoint> _points = new();
    
    // Computed alignment matrix (3x3, row-major)
    // May be a rotation (2-star) or general affine (3+ stars).
    private static float[]? _alignmentMatrix = null;
    
    // Whether the current alignment matrix is affine (non-rotation).
    // When true, Pico must normalize the result vector before extracting alt/az.
    private static bool _isAffineMatrix = false;
    
    // Indices of active (non-excluded) calibration stars in the last alignment.
    private static List<int> _activeIndices = new();
    
    // Last alignment computation result
    private static AlignmentResult? _lastResult = null;

    // Mount encoder offset: maps raw encoder values to true sky angles.
    private static double _mountXOffsetArcsec = 0;
    private static double _mountZOffsetArcsec = 0;
    private static bool _mountOffsetKnown = false;

    // Observer location (degrees)
    public static float Latitude { get; set; } = 50.0f;   // Default to ~central Europe
    public static float Longitude { get; set; } = 14.42f;  // Default to ~Prague

    /// <summary>Plate scale in arcsec/pixel — derived from PlateSolver config.</summary>
    public static double PlateScale => PlateSolver.PlateScale;

    /// <summary>
    /// Clear all alignment points and reset matrix.
    /// </summary>
    public static void Reset()
    {
        _points.Clear();
        _alignmentMatrix = null;
        _isAffineMatrix = false;
        _activeIndices.Clear();
        _lastResult = null;
        _mountXOffsetArcsec = 0;
        _mountZOffsetArcsec = 0;
        _mountOffsetKnown = false;
        CurrentTargetRa  = null;
        CurrentTargetDec = null;
        Logger.Notice("Alignment reset - all points cleared");
    }

    public static void SetMountOffset(double trueAltDeg, double trueAzDeg, int mountXArcsec, int mountZArcsec)
    {
        _mountXOffsetArcsec = trueAltDeg * 3600.0 - mountXArcsec;
        _mountZOffsetArcsec = trueAzDeg * 3600.0 - mountZArcsec;
        _mountOffsetKnown = true;
        Logger.Notice($"Mount offset: Δalt={_mountXOffsetArcsec / 3600:F2}°, Δaz={_mountZOffsetArcsec / 3600:F2}°");
    }

    /// <summary>
    /// Add an alignment point. Call this when the user has centered a known star.
    /// </summary>
    public static async Task AddAlignmentPointAsync(float starRA, float starDec)
    {
        // Record time immediately — this is when the star is centered on the sensor.
        // Any delay from GetAxisPositions is small, but be precise.
        DateTime captureTime = DateTime.UtcNow;

        // Get current mount position
        var pos = await Tracker.GetAxisPositions();

        // Auto-set mount offset from first alignment point
        if (!_mountOffsetKnown)
        {
            var (alt, az) = ComputeAltAz(starRA, starDec, captureTime, Latitude, Longitude);
            SetMountOffset(alt, az, pos.XArcsecs, pos.ZArcsecs);
        }
        
        var point = new AlignmentPoint
        {
            RA = starRA,
            Dec = starDec,
            MountX = pos.XArcsecs,
            MountY = pos.YArcsecs,
            MountZ = pos.ZArcsecs,
            TimeUtc = captureTime
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
    public static void AddAlignmentPoint(float starRA, float starDec, int mountX, int mountY, int mountZ, DateTime? timeUtc = null)
    {
        if(timeUtc == null)
            timeUtc = DateTime.UtcNow;
        // Auto-set mount offset from first alignment point
        if (!_mountOffsetKnown)
        {
            var (alt, az) = ComputeAltAz(starRA, starDec, (DateTime)timeUtc, Latitude, Longitude);
            SetMountOffset(alt, az, mountX, mountZ);
        }

        var point = new AlignmentPoint
        {
            RA = starRA,
            Dec = starDec,
            MountX = mountX,
            MountY = mountY,
            MountZ = mountZ,
            TimeUtc = (DateTime)timeUtc
        };
        
        _points.Add(point);
        Logger.Notice($"Alignment point {_points.Count} added: RA={starRA:F2}h, Dec={starDec:F2}° => Mount({mountX}, {mountY}, {mountZ}), Time={timeUtc:HH:mm:ss}");
        
        if (_points.Count >= 2)
        {
            ComputeAlignmentMatrix();
        }
    }

    /// <summary>
    /// Compute the alignment matrix from stored points.
    /// For N=2: Uses two-vector orthonormalization (exact for 2 stars).
    /// For N≥3: Uses SVD least-squares (Wahba's problem) with quality-gated
    /// star inclusion, then tries an affine (general 3×3) solve. If the affine
    /// fits significantly better — indicating mount axis distortion — it is used
    /// instead of the rotation, and previously excluded "step loss" stars may be
    /// re-included under the affine model.
    /// When refTime is provided, sky vectors are adjusted for sidereal drift
    /// relative to that reference time (matching the Pico's frame).
    /// </summary>
    private static void ComputeAlignmentMatrix(DateTime? refTime = null)
    {
        if (_points.Count < 2)
        {
            Logger.Error("Need at least 2 alignment points to compute matrix");
            return;
        }

        DateTime tRef = refTime ?? _points[^1].TimeUtc;

        // ============================================================
        // Pre-filter: remove duplicate mount positions and step-loss victims
        // ============================================================
        // 1) Deduplicate: if two points have mount positions < 0.5° apart,
        //    keep only the LATER observation (more recent = more reliable).
        // 2) Step-loss detection: if sky separation is much smaller than mount
        //    separation for a pair, one of them has step loss. Remove the star
        //    that is inconsistent with the majority.
        {
            var keep = new bool[_points.Count];
            for (int i = 0; i < _points.Count; i++) keep[i] = true;

            // Pass 1: Mount-position deduplication (< 0.5° = 1800 arcsec)
            for (int i = 0; i < _points.Count; i++)
            {
                if (!keep[i]) continue;
                for (int j = i + 1; j < _points.Count; j++)
                {
                    if (!keep[j]) continue;
                    double dx = _points[i].MountX - _points[j].MountX;
                    double dz = _points[i].MountZ - _points[j].MountZ;
                    double mountDistArcsec = Math.Sqrt(dx * dx + dz * dz);
                    if (mountDistArcsec < 1800) // < 0.5°
                    {
                        // Keep the later observation, remove the earlier one
                        Logger.Notice($"  Dedup: star{i + 1} and star{j + 1} have mount positions {mountDistArcsec / 3600:F2}° apart — removing star{i + 1} (earlier)");
                        keep[i] = false;
                        break;
                    }
                }
            }

            // Pass 2: Step-loss detection via pairwise sky/mount ratio.
            // For each point, compute median (sky_sep / mount_sep) ratio across
            // all pairs. Points with consistently low ratios have step loss.
            // We look for points where sky barely moved despite mount moving 1°+.
            var skyVecsTemp = new (double x, double y, double z)[_points.Count];
            var mountVecsTemp = new (double x, double y, double z)[_points.Count];
            for (int i = 0; i < _points.Count; i++)
            {
                skyVecsTemp[i] = CelestialToUnitVector(_points[i].RA, _points[i].Dec, tRef, _points[i].TimeUtc);
                mountVecsTemp[i] = MountToUnitVectorCorrected(_points[i].MountX, _points[i].MountY, _points[i].MountZ);
            }

            // Check each kept point against all other kept points
            for (int i = 0; i < _points.Count; i++)
            {
                if (!keep[i]) continue;
                int badPairs = 0, totalPairs = 0;
                for (int j = 0; j < _points.Count; j++)
                {
                    if (j == i || !keep[j]) continue;
                    double skySep = Math.Acos(Math.Max(-1.0, Math.Min(1.0,
                        skyVecsTemp[i].x * skyVecsTemp[j].x + skyVecsTemp[i].y * skyVecsTemp[j].y + skyVecsTemp[i].z * skyVecsTemp[j].z))) * 180.0 / Math.PI;
                    double mountSep = Math.Acos(Math.Max(-1.0, Math.Min(1.0,
                        mountVecsTemp[i].x * mountVecsTemp[j].x + mountVecsTemp[i].y * mountVecsTemp[j].y + mountVecsTemp[i].z * mountVecsTemp[j].z))) * 180.0 / Math.PI;
                    if (mountSep > 1.0) // Only check pairs with significant mount separation
                    {
                        totalPairs++;
                        if (skySep < mountSep * 0.3) // Sky moved < 30% of mount = step loss
                            badPairs++;
                    }
                }
                if (totalPairs >= 2 && badPairs >= totalPairs * 0.5) // Majority of pairs show step loss
                {
                    Logger.Warn($"  Step-loss filter: removing star{i + 1} — sky position inconsistent with mount position ({badPairs}/{totalPairs} pairs show step loss)");
                    keep[i] = false;
                }
            }

            // Rebuild _points list with only kept entries
            int removed = keep.Count(k => !k);
            if (removed > 0)
            {
                var filtered = new List<AlignmentPoint>();
                for (int i = 0; i < _points.Count; i++)
                {
                    if (keep[i])
                        filtered.Add(_points[i]);
                }
                Logger.Notice($"  Pre-filter: removed {removed} points, {filtered.Count} remaining");
                if (filtered.Count < 2)
                {
                    Logger.Error("Pre-filter removed too many points — need at least 2. Keeping all.");
                }
                else
                {
                    _points.Clear();
                    _points.AddRange(filtered);
                }
            }
        }

        // Build sky and mount vectors for ALL (filtered) points.
        // Mount vectors use RAW encoder positions — step loss is self-canceling
        // (the Pico uses the same encoder for tracking as for calibration, so
        // systematic encoder errors cancel out).  The affine matrix absorbs any
        // axis scale/skew differences between encoder space and the celestial sphere.
        var allSkyVecs = new (double x, double y, double z)[_points.Count];
        var allMountVecs = new (double x, double y, double z)[_points.Count];
        for (int i = 0; i < _points.Count; i++)
        {
            allSkyVecs[i] = CelestialToUnitVector(_points[i].RA, _points[i].Dec, tRef, _points[i].TimeUtc);
            allMountVecs[i] = MountToUnitVectorCorrected(_points[i].MountX, _points[i].MountY, _points[i].MountZ);
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
        var rejectionReasons = new Dictionary<int, string>();

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
                rejectionReasons[k] = $"Would increase error from {bestAvg * 60:F1}' to {candidateAvg * 60:F1}'";
                Logger.Warn($"  Auto-excluded star{k + 1} (RA={_points[k].RA:F2}h Dec={_points[k].Dec:F1}°): " +
                    $"would increase error from {bestAvg * 60:F1}' to {candidateAvg * 60:F1}' — likely step loss during slew");
            }
        }

        _alignmentMatrix = bestMatrix;
        _isAffineMatrix = false; // Phase 1-2 always uses rotation
        _activeIndices = new List<int>(bestActive);

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

                    rejectionReasons[rejIdx] = $"Outlier: residual {worstRes * 60:F1}' vs best {bestRes * 60:F1}'";
                    Logger.Warn($"  Outlier-rejected star{rejIdx + 1} (RA={_points[rejIdx].RA:F2}h Dec={_points[rejIdx].Dec:F1}°): " +
                        $"residual {worstRes * 60:F1}' vs best {bestRes * 60:F1}'");
                    changed = true;
                }
            }
        }

        // Update active indices after outlier removal
        _activeIndices = new List<int>(bestActive);

        // ============================================================
        // Phase 4: Affine analysis for 3+ stars (diagnostic + re-evaluation)
        // ============================================================
        // The rotation matrix (SVD) forces det=1 and singular values [1,1,1].
        // Real mounts have gear ratio errors, axis non-orthogonality, and flexure
        // that make the sky→mount mapping non-rotational. A general 3×3 affine
        // matrix captures these effects. For 3 stars it's exact; for N>3 it's
        // least-squares.
        //
        // Currently we use the affine for DIAGNOSTICS and STAR RE-EVALUATION:
        //  - If stars were excluded under rotation but fit under affine, the issue
        //    is mount distortion, not step loss. We re-include them and recompute
        //    the affine for better sky coverage.
        //  - The affine matrix is sent to the Pico for tracking. The Pico MUST
        //    normalize the mount vector before extracting alt/az (2-line change).
        //    This normalization is safe for rotations too (norm≈1, no-op).
        // ============================================================
        if (bestActive.Count >= 3 || (_points.Count >= 3 && rejected.Count > 0))
        {
            // Try affine with ALL stars (including excluded) to detect mount distortion
            var allPoints = new List<int>();
            for (int i = 0; i < _points.Count; i++) allPoints.Add(i);

            var affineSkyAll = new (double x, double y, double z)[_points.Count];
            var affineMountAll = new (double x, double y, double z)[_points.Count];
            for (int i = 0; i < _points.Count; i++)
            {
                affineSkyAll[i] = allSkyVecs[i];
                affineMountAll[i] = allMountVecs[i];
            }

            float[]? affineMatrix = (_points.Count >= 4) ? ComputeAffineMatrix(affineSkyAll, affineMountAll) : null;
            if (affineMatrix != null)
            {
                // Compare affine vs rotation residuals on ALL stars
                double rotAvgAll = 0, affAvgAll = 0;
                for (int i = 0; i < _points.Count; i++)
                {
                    rotAvgAll  += ComputePointResidual(_alignmentMatrix!, allSkyVecs[i], allMountVecs[i]);
                    affAvgAll  += ComputePointResidualAffine(affineMatrix, allSkyVecs[i], allMountVecs[i]);
                }
                rotAvgAll  /= _points.Count;
                affAvgAll  /= _points.Count;

                Logger.Notice($"  Rotation avg residual (all {_points.Count} stars): {rotAvgAll * 60:F1}'");
                Logger.Notice($"  Affine avg residual (all {_points.Count} stars): {affAvgAll * 60:F1}'");

                // If affine fits better → mount has distortion that rotation can't capture.
                // Threshold: affine avg must be at least 15% better than rotation,
                // OR rotation is poor (>10') and affine is acceptable (<0.5°).
                if (affAvgAll < rotAvgAll * 0.85 || (rotAvgAll > 0.167 && affAvgAll < 0.5))
                {
                    Logger.Notice($"  *** AFFINE model fits {rotAvgAll / Math.Max(affAvgAll, 0.001):F0}× better than rotation ***");
                    Logger.Notice($"  *** This means the mount has axis scale/orthogonality errors ***");

                    // Re-evaluate excluded stars under affine.
                    // Strategy depends on whether the system is over-determined:
                    //
                    // N = 3 (exact fit): residuals should be ~0. Use tight 10' threshold
                    //   to catch genuinely bad stars.
                    //
                    // N >= 4 (over-determined): least-squares inherently produces nonzero
                    //   residuals. Accept ALL stars if avg < 0.5° (30', ~40px) — the
                    //   over-determined model gives far better extrapolation than an exact
                    //   3-star fit. Only reject extreme outliers (> 3° or > 5× median).
                    //
                    var newActive = new List<int>();
                    var newRejected = new List<int>();

                    if (_points.Count >= 4 && affAvgAll < 0.5)
                    {
                        // Over-determined system with acceptable average: use generous outlier detection
                        var residuals = new double[_points.Count];
                        for (int i = 0; i < _points.Count; i++)
                            residuals[i] = ComputePointResidualAffine(affineMatrix, allSkyVecs[i], allMountVecs[i]);

                        var sorted = residuals.OrderBy(r => r).ToArray();
                        double medianRes = sorted[sorted.Length / 2];

                        for (int i = 0; i < _points.Count; i++)
                        {
                            // Only reject extreme outliers: > 3° AND > 5× median
                            // (a star with 3° = 180' = 250px error is clearly wrong positioning)
                            if (residuals[i] > 3.0 && residuals[i] > medianRes * 5)
                            {
                                newRejected.Add(i);
                                if (!rejectionReasons.ContainsKey(i))
                                    rejectionReasons[i] = $"Extreme outlier: {residuals[i] * 60:F1}' (> 3° and > 5× median {medianRes * 60:F1}')";
                                Logger.Warn($"  Rejecting star{i + 1}: affine residual {residuals[i] * 60:F1}' is an extreme outlier");
                            }
                            else
                            {
                                newActive.Add(i);
                                if (residuals[i] > 0.167)
                                    Logger.Notice($"  Including star{i + 1} despite {residuals[i] * 60:F1}' affine residual (over-determined model)");
                            }
                        }
                        Logger.Notice($"  Over-determined affine ({_points.Count} stars): avg={affAvgAll * 60:F1}', median={medianRes * 60:F1}', using {newActive.Count} stars");
                    }
                    else
                    {
                        // Exactly-determined (3 stars) or poor fit: use tight threshold
                        for (int i = 0; i < _points.Count; i++)
                        {
                            double res = ComputePointResidualAffine(affineMatrix, allSkyVecs[i], allMountVecs[i]);
                            if (res < 0.167) // < 10 arcmin → accept under affine
                                newActive.Add(i);
                            else
                            {
                                newRejected.Add(i);
                                if (!rejectionReasons.ContainsKey(i))
                                    rejectionReasons[i] = $"Affine residual {res * 60:F1}' > 10'";
                            }
                        }
                    }

                    if (newActive.Count >= 3)
                    {
                        int reincluded = 0;
                        foreach (int idx in newActive)
                            if (rejected.Contains(idx)) reincluded++;

                        if (reincluded > 0)
                            Logger.Notice($"  Re-including {reincluded} star(s) that were wrongly excluded as 'step loss' — actually mount distortion");

                        // Recompute affine with all qualified stars
                        var afSky = new (double x, double y, double z)[newActive.Count];
                        var afMount = new (double x, double y, double z)[newActive.Count];
                        for (int ai = 0; ai < newActive.Count; ai++)
                        {
                            afSky[ai] = allSkyVecs[newActive[ai]];
                            afMount[ai] = allMountVecs[newActive[ai]];
                        }
                        float[]? finalAffine = ComputeAffineMatrix(afSky, afMount);
                        if (finalAffine != null)
                        {
                            _alignmentMatrix = finalAffine;
                            _isAffineMatrix = true;
                            bestActive = newActive;
                            rejected = newRejected;
                            _activeIndices = new List<int>(bestActive);
                            Logger.Notice($"  Using AFFINE alignment ({newActive.Count} stars)");
                        }
                    }
                }
                else
                {
                    Logger.Notice($"  Affine not significantly better — mount geometry is close to ideal");
                }
            }
        }

        // ============================================================
        // Diagnostics and quality assessment
        // ============================================================

        if (rejected.Count > 0)
            Logger.Notice($"  Using {bestActive.Count} of {_points.Count} stars ({rejected.Count} excluded)");
        if (bestActive.Count > 2 && !_isAffineMatrix)
            Logger.Notice($"  {bestActive.Count}-star least-squares alignment (SVD rotation)");
        else if (_isAffineMatrix)
            Logger.Notice($"  {bestActive.Count}-star affine alignment (captures mount distortion)");

        // Zenith proximity detection
        for (int i = 0; i < _points.Count; i++)
        {
            double mountAltDeg = _points[i].MountX / 3600.0;
            if (Math.Abs(Math.Cos(mountAltDeg * Math.PI / 180.0)) < 0.26)
                Logger.Warn($"  Star{i + 1}: mount tilt={mountAltDeg:F1}° (near zenith singularity)");
        }

        // Pairwise separation mismatch — diagnostic for step loss
        double maxPairSepError = 0;
        double maxStepLossPct = 0;
        for (int ai = 0; ai < bestActive.Count; ai++)
        {
            for (int aj = ai + 1; aj < bestActive.Count; aj++)
            {
                int i = bestActive[ai], j = bestActive[aj];
                double skyDot = allSkyVecs[i].x * allSkyVecs[j].x + allSkyVecs[i].y * allSkyVecs[j].y + allSkyVecs[i].z * allSkyVecs[j].z;
                double mountDot = allMountVecs[i].x * allMountVecs[j].x + allMountVecs[i].y * allMountVecs[j].y + allMountVecs[i].z * allMountVecs[j].z;
                double pairSkySep = Math.Acos(Math.Clamp(skyDot, -1.0, 1.0)) * 180.0 / Math.PI;
                double pairMountSep = Math.Acos(Math.Clamp(mountDot, -1.0, 1.0)) * 180.0 / Math.PI;
                double pairSepErr = Math.Abs(pairSkySep - pairMountSep);
                double stepLoss = (pairSkySep > 0.5) ? (1.0 - pairMountSep / pairSkySep) * 100.0 : 0;

                bool nearZenith = Math.Abs(Math.Cos(_points[i].MountX / 3600.0 * Math.PI / 180.0)) < 0.26 ||
                                  Math.Abs(Math.Cos(_points[j].MountX / 3600.0 * Math.PI / 180.0)) < 0.26;

                Logger.Debug($"  Pair star{i + 1}-star{j + 1}: sky={pairSkySep:F3}° mount={pairMountSep:F3}° Δ={pairSepErr:F3}°");

                if (!nearZenith)
                {
                    if (pairSepErr > maxPairSepError) maxPairSepError = pairSepErr;
                    if (Math.Abs(stepLoss) > Math.Abs(maxStepLossPct)) maxStepLossPct = stepLoss;
                }
            }
        }

        // Per-star residual report
        double totalResidual = 0;
        int activeCount = 0;
        var starResiduals = new List<StarResidualInfo>();
        for (int i = 0; i < _points.Count; i++)
        {
            double err = _isAffineMatrix
                ? ComputePointResidualAffine(_alignmentMatrix!, allSkyVecs[i], allMountVecs[i])
                : ComputePointResidual(_alignmentMatrix!, allSkyVecs[i], allMountVecs[i]);

            bool isRejected = rejected.Contains(i);
            Logger.Debug($"  Residual star{i + 1}: {err * 60:F1}' ({err:F3}°){(isRejected ? " [EXCLUDED]" : "")}");

            starResiduals.Add(new StarResidualInfo
            {
                Index = i + 1,
                RA = _points[i].RA,
                Dec = _points[i].Dec,
                ResidualArcmin = Math.Round(err * 60, 2),
                Excluded = isRejected,
                ExclusionReason = isRejected && rejectionReasons.TryGetValue(i, out var reason) ? reason : null
            });

            if (!isRejected)
            {
                totalResidual += err;
                activeCount++;
            }
        }
        double avgResidual = totalResidual / Math.Max(1, activeCount);

        double plateScale = PlateScale;
        double avgPixels = avgResidual * 3600.0 / plateScale;

        Logger.Notice($"  Average residual: {avgResidual * 60:F1}' ≈ {avgPixels:F0}px");

        if (maxPairSepError > 0.3 && !_isAffineMatrix)
            Logger.Warn($"  Step loss detected: worst pair Δ={maxPairSepError:F2}° ({Math.Abs(maxStepLossPct):F1}%)");

        // Quality assessment (EXCELLENT < 0.5° < OK < 1.5° < MARGINAL < 3.0° < REJECTED)
        if (avgResidual > 3.0)
        {
            Logger.Warn($"  ALIGNMENT REJECTED (~{avgPixels:F0}px error). Recalibrate with stars at 30°-70° altitude.");
            _alignmentMatrix = null;
        }
        else if (avgResidual > 1.5)
            Logger.Warn($"  Alignment MARGINAL: ~{avgPixels:F0}px error");
        else if (avgResidual > 0.5)
            Logger.Notice($"  Alignment quality: OK (~{avgPixels:F0}px error)");
        else
            Logger.Notice($"  Alignment quality: EXCELLENT (~{avgPixels:F0}px error)");

        if (_alignmentMatrix != null)
        {
            Logger.Debug($"  Matrix: [{_alignmentMatrix[0]:F4},{_alignmentMatrix[1]:F4},{_alignmentMatrix[2]:F4}] [{_alignmentMatrix[3]:F4},{_alignmentMatrix[4]:F4},{_alignmentMatrix[5]:F4}] [{_alignmentMatrix[6]:F4},{_alignmentMatrix[7]:F4},{_alignmentMatrix[8]:F4}]");
        }

        // Build alignment result for external consumers
        string quality;
        if (_alignmentMatrix == null)
            quality = "REJECTED";
        else if (avgResidual > 1.5)
            quality = "MARGINAL";
        else if (avgResidual > 0.5)
            quality = "OK";
        else
            quality = "EXCELLENT";

        _lastResult = new AlignmentResult
        {
            Quality = quality,
            AverageResidualArcmin = Math.Round(avgResidual * 60, 2),
            AverageResidualPixels = Math.Round(avgPixels, 1),
            MaxPairErrorDeg = Math.Round(maxPairSepError, 3),
            StepLossPercent = Math.Round(Math.Abs(maxStepLossPct), 1),
            ActiveStarCount = bestActive.Count,
            RejectedCount = rejected.Count,
            Stars = starResiduals
        };
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
    /// Convert mount encoder positions to a unit vector with offset correction.
    /// </summary>
    private static (double x, double y, double z) MountToUnitVectorCorrected(int xArcsec, int yArcsec, int zArcsec)
    {
        double alt = (xArcsec + _mountXOffsetArcsec) * Math.PI / (180.0 * 3600.0);
        double az = (zArcsec + _mountZOffsetArcsec) * Math.PI / (180.0 * 3600.0);
        return (Math.Cos(alt) * Math.Cos(az), Math.Cos(alt) * Math.Sin(az), Math.Sin(alt));
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
    /// Compute a general affine (3×3) transformation matrix A such that A * sky_i ≈ mount_i.
    /// For N=3 this is an exact solve (zero residuals); for N>3 it's least-squares.
    /// Unlike ComputeRotationMatrixSVD, this captures axis scale errors, gear ratio
    /// inaccuracies, and non-orthogonality — not just rotation.
    /// The output vector A*sky is NOT guaranteed to be a unit vector, so the caller
    /// (or Pico) must normalize before extracting alt/az.
    /// </summary>
    private static float[]? ComputeAffineMatrix(
        (double x, double y, double z)[] skyVecs,
        (double x, double y, double z)[] mountVecs)
    {
        int n = skyVecs.Length;
        if (n < 3) return null; // Need at least 3 for a 3×3 solve

        // Build S (3×N) and M (3×N) matrices: columns are sky/mount vectors
        // We want A such that A * S = M,  i.e.  A = M * S^T * (S * S^T)^{-1}

        // Compute S * S^T (3×3 Gram matrix of sky vectors)
        double[,] SSt = new double[3, 3];
        for (int k = 0; k < n; k++)
        {
            double[] sv = { skyVecs[k].x, skyVecs[k].y, skyVecs[k].z };
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    SSt[i, j] += sv[i] * sv[j];
        }

        // Invert SSt (3×3) using cofactor/adjugate method
        double det = Det3x3(SSt);
        if (Math.Abs(det) < 1e-12)
        {
            Logger.Warn("Affine matrix: sky vectors are coplanar (degenerate), falling back to SVD rotation");
            return null;
        }

        // Cofactor matrix (transposed = adjugate)
        double[,] adj = new double[3, 3];
        adj[0, 0] =  (SSt[1, 1] * SSt[2, 2] - SSt[1, 2] * SSt[2, 1]);
        adj[0, 1] = -(SSt[0, 1] * SSt[2, 2] - SSt[0, 2] * SSt[2, 1]);
        adj[0, 2] =  (SSt[0, 1] * SSt[1, 2] - SSt[0, 2] * SSt[1, 1]);
        adj[1, 0] = -(SSt[1, 0] * SSt[2, 2] - SSt[1, 2] * SSt[2, 0]);
        adj[1, 1] =  (SSt[0, 0] * SSt[2, 2] - SSt[0, 2] * SSt[2, 0]);
        adj[1, 2] = -(SSt[0, 0] * SSt[1, 2] - SSt[0, 2] * SSt[1, 0]);
        adj[2, 0] =  (SSt[1, 0] * SSt[2, 1] - SSt[1, 1] * SSt[2, 0]);
        adj[2, 1] = -(SSt[0, 0] * SSt[2, 1] - SSt[0, 1] * SSt[2, 0]);
        adj[2, 2] =  (SSt[0, 0] * SSt[1, 1] - SSt[0, 1] * SSt[1, 0]);

        double[,] SStInv = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                SStInv[i, j] = adj[i, j] / det;

        // Compute M * S^T (3×3)
        double[,] MSt = new double[3, 3];
        for (int k = 0; k < n; k++)
        {
            double[] sv = { skyVecs[k].x, skyVecs[k].y, skyVecs[k].z };
            double[] mv = { mountVecs[k].x, mountVecs[k].y, mountVecs[k].z };
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    MSt[i, j] += mv[i] * sv[j];
        }

        // A = M * S^T * (S * S^T)^{-1}
        double[,] A = Multiply(MSt, SStInv);

        // Check singular values for diagnostics.
        // The mapping from equatorial unit vectors to mount spherical unit vectors
        // is NOT a rotation — the cos(alt) factor in the mount's spherical formula
        // creates angular distance distortion. At encoder altitude ~50°, azimuth
        // distances are compressed by cos(50°)≈0.64, giving SVs well outside [0.9,1.1].
        // This is normal mount geometry, NOT degenerate star geometry.
        // Only reject truly degenerate cases (near-zero determinant or extreme SVs).
        var (_, sigma, _) = SVD3x3(A);
        double detA = Det3x3(A);
        double minSV = Math.Min(sigma[0], Math.Min(sigma[1], sigma[2]));
        double maxSV = Math.Max(sigma[0], Math.Max(sigma[1], sigma[2]));

        Logger.Notice($"  Affine matrix: det={detA:F4}, singular values=[{sigma[0]:F3}, {sigma[1]:F3}, {sigma[2]:F3}]");

        if (minSV < 0.05 || maxSV > 20.0 || Math.Abs(detA) < 0.01)
        {
            Logger.Warn($"  Affine matrix is degenerate (SVs [{minSV:F3}, {maxSV:F3}], det={detA:F4}) — falling back to SVD rotation");
            return null;
        }

        // Log non-rotation magnitude for diagnostics
        double nonRotation = Math.Max(Math.Abs(sigma[0] - 1.0), Math.Max(Math.Abs(sigma[1] - 1.0), Math.Abs(sigma[2] - 1.0)));
        if (nonRotation > 0.5)
            Logger.Notice($"  Significant mount coordinate distortion ({nonRotation * 100:F0}%) — this is expected (cos(alt) effect in spherical coordinates)");
        else if (nonRotation > 0.02)
            Logger.Notice($"  Mount has {nonRotation * 100:F1}% axis distortion — affine model corrects this");
        else
            Logger.Notice($"  Mount is well-calibrated (distortion <2%) — affine and rotation give similar results");

        return new float[]
        {
            (float)A[0, 0], (float)A[0, 1], (float)A[0, 2],
            (float)A[1, 0], (float)A[1, 1], (float)A[1, 2],
            (float)A[2, 0], (float)A[2, 1], (float)A[2, 2]
        };
    }

    /// <summary>
    /// Compute the angular residual (degrees) between A*sky (normalized) and mount.
    /// Works for both rotation matrices and affine matrices.
    /// </summary>
    private static double ComputePointResidualAffine(float[] matrix,
        (double x, double y, double z) sky, (double x, double y, double z) mount)
    {
        double rx = matrix[0] * sky.x + matrix[1] * sky.y + matrix[2] * sky.z;
        double ry = matrix[3] * sky.x + matrix[4] * sky.y + matrix[5] * sky.z;
        double rz = matrix[6] * sky.x + matrix[7] * sky.y + matrix[8] * sky.z;
        // Normalize the result (no-op for rotation, essential for affine)
        double norm = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        if (norm > 1e-10) { rx /= norm; ry /= norm; rz /= norm; }
        return Math.Acos(Math.Max(-1.0, Math.Min(1.0, rx * mount.x + ry * mount.y + rz * mount.z))) * 180.0 / Math.PI;
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

        // Step 4: Sort singular values in descending order
        for (int i = 0; i < 2; i++)
        {
            int maxIdx = i;
            for (int j = i + 1; j < 3; j++)
                if (sigma[j] > sigma[maxIdx]) maxIdx = j;
            
            if (maxIdx != i)
            {
                double tempSig = sigma[i];
                sigma[i] = sigma[maxIdx];
                sigma[maxIdx] = tempSig;
                for (int k = 0; k < 3; k++)
                {
                    double tempV = V[k, i];
                    V[k, i] = V[k, maxIdx];
                    V[k, maxIdx] = tempV;
                }
            }
        }

        // Step 5: U = H * V * S^{-1}
        double[,] HV = Multiply(H, V);
        double[,] U = new double[3, 3];
        for (int j = 0; j < 3; j++)
        {
            if (sigma[j] > 1e-10)
            {
                for (int i = 0; i < 3; i++) U[i, j] = HV[i, j] / sigma[j];
            }
            else
            {
                // If singular value is zero (coplanar points), construct orthogonal column
                if (j == 2)
                {
                    U[0, 2] = U[1, 0] * U[2, 1] - U[2, 0] * U[1, 1];
                    U[1, 2] = U[2, 0] * U[0, 1] - U[0, 0] * U[2, 1];
                    U[2, 2] = U[0, 0] * U[1, 1] - U[1, 0] * U[0, 1];
                }
                else
                {
                    for (int i = 0; i < 3; i++) U[i, j] = 0;
                }
            }
        }

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
    /// Currently active tracking target (null when not tracking).
    /// Set by StartTrackingAsync, cleared by Reset.
    /// </summary>
    public static float? CurrentTargetRa  { get; private set; }
    public static float? CurrentTargetDec { get; private set; }

    /// <summary>
    /// Get the last alignment computation result (quality metrics, per-star residuals).
    /// Null if no alignment has been attempted yet.
    /// </summary>
    public static AlignmentResult? LastResult => _lastResult;

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
            Logger.Error("Cannot start tracking - not aligned! Add at least 2 alignment points first.");
            return false;
        }

        // Remember the tracking target for external display (LCD)
        CurrentTargetRa  = targetRA;
        CurrentTargetDec = targetDec;

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
        // Normalize result vector (no-op for rotation, essential for affine)
        double norm = Math.Sqrt(mx * mx + my * my + mz * mz);
        if (norm > 1e-10) { mx /= norm; my /= norm; mz /= norm; }
        double predAlt = Math.Asin(Math.Max(-1.0, Math.Min(1.0, mz))) * 180.0 / Math.PI;
        double predAz  = Math.Atan2(my, mx) * 180.0 / Math.PI;
        long predXArcsec = (long)(predAlt * 3600) - (long)_mountXOffsetArcsec;
        long predZArcsec = (long)(predAz  * 3600) - (long)_mountZOffsetArcsec;
        Logger.Notice($"Predicted initial position: X={predXArcsec}\" ({predAlt:F2}°), Z={predZArcsec}\" ({predAz:F2}°){(_isAffineMatrix ? " [affine]" : "")}");
        if (predAlt > 80.0)
            Logger.Warn($"  Target near zenith (alt={predAlt:F1}°) — accuracy degrades.");

        // Extrapolation warning
        if (_activeIndices.Count >= 2)
        {
            double nearestDist = double.MaxValue;
            int nearestIdx = -1;
            double maxBaseline = 0;
            for (int ai = 0; ai < _activeIndices.Count; ai++)
            {
                int idx = _activeIndices[ai];
                var calSky = CelestialToUnitVector(_points[idx].RA, _points[idx].Dec, refDateTime, _points[idx].TimeUtc);
                double dot = targetSky.x * calSky.x + targetSky.y * calSky.y + targetSky.z * calSky.z;
                double dist = Math.Acos(Math.Clamp(dot, -1.0, 1.0)) * 180.0 / Math.PI;
                if (dist < nearestDist) { nearestDist = dist; nearestIdx = idx; }

                for (int aj = ai + 1; aj < _activeIndices.Count; aj++)
                {
                    int jj = _activeIndices[aj];
                    var sj = CelestialToUnitVector(_points[jj].RA, _points[jj].Dec, refDateTime, _points[jj].TimeUtc);
                    double d = Math.Acos(Math.Clamp(calSky.x*sj.x + calSky.y*sj.y + calSky.z*sj.z, -1.0, 1.0)) * 180.0 / Math.PI;
                    if (d > maxBaseline) maxBaseline = d;
                }
            }

            double extrapRatio = (maxBaseline > 1.0) ? nearestDist / (maxBaseline * 0.5) : nearestDist;
            Logger.Debug($"  Nearest cal star: star{nearestIdx + 1} @{nearestDist:F1}°, baseline={maxBaseline:F1}°, extrap={extrapRatio:F1}×");

            if (extrapRatio > 3.0)
                Logger.Warn($"  Extreme extrapolation ({extrapRatio:F1}×) — add calibration stars near target");
            else if (extrapRatio > 1.5)
                Logger.Warn($"  Extrapolation warning ({extrapRatio:F1}×) — outside calibration zone");
        }

        // Local affine optimization: use 3 nearest stars for better local accuracy
        float[] trackingMatrix = _alignmentMatrix!;

        if (_isAffineMatrix && _activeIndices.Count >= 4)
        {
            var starDistances = new List<(int idx, double dist)>();
            for (int ai = 0; ai < _activeIndices.Count; ai++)
            {
                int idx = _activeIndices[ai];
                var calSky = CelestialToUnitVector(_points[idx].RA, _points[idx].Dec, refDateTime, _points[idx].TimeUtc);
                double dot = targetSky.x * calSky.x + targetSky.y * calSky.y + targetSky.z * calSky.z;
                double dist = Math.Acos(Math.Clamp(dot, -1.0, 1.0)) * 180.0 / Math.PI;
                starDistances.Add((idx, dist));
            }
            starDistances.Sort((a, b) => a.dist.CompareTo(b.dist));

            var localSky = new (double x, double y, double z)[3];
            var localMount = new (double x, double y, double z)[3];
            for (int i = 0; i < 3; i++)
            {
                int idx = starDistances[i].idx;
                localSky[i] = CelestialToUnitVector(_points[idx].RA, _points[idx].Dec, refDateTime, _points[idx].TimeUtc);
                localMount[i] = MountToUnitVectorCorrected(_points[idx].MountX, _points[idx].MountY, _points[idx].MountZ);
            }

            float[]? localMatrix = ComputeAffineMatrix(localSky, localMount);
            if (localMatrix != null)
            {
                trackingMatrix = localMatrix;
                Logger.Notice($"  Using local affine from 3 nearest stars");
            }
        }

        return await UartClient.Client.StartCelestialTracking(
            targetRA,
            targetDec,
            trackingMatrix,
            refTime,
            (float)Latitude,
            (int)_mountXOffsetArcsec,
            (int)_mountZOffsetArcsec
        );
    }

    // Plate-solve assisted operations were moved to Calibration.AutoSolve.cs
    #endregion
}
