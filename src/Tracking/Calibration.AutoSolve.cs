using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public static partial class Calibration
{
    // ============================================================
    //  Plate-Solve Assisted Operations
    // ============================================================

    /// <summary>
    /// Reference to the camera instance used for plate-solve captures.
    /// Must be set before calling AutoCenter / AutoCalibrate / GuidedTracking.
    /// </summary>
    public static Camera? SolveCamera { get; set; }

    /// <summary>
    /// Active guided-tracking cancellation source — allows stopping guided tracking.
    /// </summary>
    private static CancellationTokenSource? _guideCts;
    public static CancellationTokenSource? _autoCts;

    public static void CancelAutoOperation()
    {
        _autoCts?.Cancel();
        _guideCts?.Cancel();
    }

    /// <summary>
    /// Auto-center on a target using plate solving.
    /// Goto → capture → solve → measure error → correct → repeat until centered.
    /// Each solved frame also becomes a calibration point, continuously improving the model.
    /// </summary>
    /// <param name="targetRA">Target RA in hours.</param>
    /// <param name="targetDec">Target Dec in degrees.</param>
    /// <param name="maxIterations">Maximum correction iterations (default 5).</param>
    /// <param name="tolerancePx">Target centering tolerance in pixels (default 15).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result with final centering error, or null if solve failed.</returns>
    public static async Task<AutoCenterResult?> AutoCenterAsync(
        float targetRA, float targetDec,
        int maxIterations = 5, double tolerancePx = 15.0,
        CancellationToken ct = default)
    {
        if (SolveCamera == null || !SolveCamera.connected)
        {
            Logger.Warn("AutoCenter: no camera connected. Set Alignment.SolveCamera first.");
            return null;
        }

        if (!IsAligned)
        {
            Logger.Warn("AutoCenter: mount not aligned. Need at least 2 alignment stars.");
            return null;
        }

        double plateScale = PlateScale;
        Logger.Notice($"AutoCenter: target RA={targetRA:F4}h, Dec={targetDec:F4}°, tolerance={tolerancePx:F0}px ({tolerancePx * plateScale:F0}\")");

        Logger.Notice("AutoCenter: starting initial tracking goto...");
        bool trackOk = await StartTrackingAsync(targetRA, targetDec);
        if (!trackOk)
        {
            Logger.Warn("AutoCenter: failed to start tracking");
            return null;
        }

        // Wait for the Pico's initial slew to complete.
        // StartTrackingAsync sends CMD_TRACK_CELESTIAL which makes the Pico slew to the
        // predicted sky position and then engage sidereal tracking.  We CANNOT use
        // WaitForMoveCompleteAsync here — that polls until motor positions stop changing,
        // but during celestial tracking the motors move continuously at sidereal rate
        // (~15"/s on X) and NEVER stop.  On 2026-03-11 this caused AutoCenter to hang
        // forever at this point, blocking all guided tracking corrections.
        // Instead, wait for the Pico to report celestialTracking==true (end of initial slew).
        await Tracker.WaitForCelestialTrackingAsync(15000, ct);

        double lastErrorPx = double.MaxValue;
        int iteration = 0;

        try
        {
            for (iteration = 1; iteration <= maxIterations; iteration++)
            {
                ct.ThrowIfCancellationRequested();

                Logger.Notice($"AutoCenter: iteration {iteration}/{maxIterations}");

                DateTime acCaptureTime = DateTime.UtcNow;
                string baseName = $"solve_{acCaptureTime:yyyyMMdd_HHmmss}";
                string imagePath;
                try
                {
                    imagePath = await Task.Run(() => SolveCamera.CaptureImage(baseName), ct);
                    Logger.Notice($"AutoCenter: captured {Path.GetFileName(imagePath)}");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"AutoCenter: capture failed: {ex.Message}");
                    return null;
                }

                var mountPos = await Tracker.GetAxisPositions();

                var solve = await PlateSolver.SolveAsync(imagePath, targetRA, targetDec, 30.0, ct);
                if (solve == null)
                {
                    Logger.Warn("AutoCenter: plate solve failed — cannot determine pointing");
                    TryDeleteFile(imagePath);
                    return new AutoCenterResult
                    {
                        Success = false,
                        Iterations = iteration,
                        FinalErrorPx = lastErrorPx,
                        Message = "Plate solve failed"
                    };
                }

                double dRaDeg = (targetRA - solve.RaCenterHours) * 15.0 * Math.Cos(targetDec * Math.PI / 180.0);
                double dDecDeg = targetDec - solve.DecCenterDeg;
                double errorDeg = Math.Sqrt(dRaDeg * dRaDeg + dDecDeg * dDecDeg);
                double errorPx = errorDeg * 3600.0 / plateScale;
                lastErrorPx = errorPx;

                Logger.Notice($"AutoCenter: pointing error = {errorDeg * 60:F1}' ({errorPx:F0}px) [ΔRA={dRaDeg * 60:F1}', ΔDec={dDecDeg * 60:F1}']");

                AddAlignmentPoint(
                    (float)solve.RaCenterHours, (float)solve.DecCenterDeg,
                    mountPos.XArcsecs, mountPos.YArcsecs, mountPos.ZArcsecs, acCaptureTime);
                Logger.Notice($"AutoCenter: added plate-solve calibration point (now {PointCount} total)");

                if (errorPx < tolerancePx)
                {
                    Logger.Notice($"AutoCenter: CENTERED in {iteration} iteration(s)! Error={errorPx:F1}px < {tolerancePx:F0}px");

                    await StartTrackingAsync(targetRA, targetDec);

                    TryDeleteFile(imagePath);
                    return new AutoCenterResult
                    {
                        Success = true,
                        Iterations = iteration,
                        FinalErrorPx = errorPx,
                        FinalErrorArcmin = errorDeg * 60,
                        SolvedRA = solve.RaCenterHours,
                        SolvedDec = solve.DecCenterDeg,
                        Message = $"Centered in {iteration} iteration(s)"
                    };
                }

                Logger.Notice($"AutoCenter: correcting — ΔAlt={dDecDeg * 3600:F0}\", ΔAz={dRaDeg * 3600:F0}\"");
                // Note: MoveRelative already stops celestial tracking on the Pico internally
                // (stepper_queue_static_move sets celestial_state.active = false).
                // DO NOT call StopAll() here — it sends CMD_STOP which disables the motors
                // entirely, losing the position reference and invalidating calibration.
                await Task.Delay(200, ct);

                var targetAltAz = ComputeAltAz(targetRA, targetDec, DateTime.UtcNow, Latitude, Longitude);
                var actualAltAz = ComputeAltAz(solve.RaCenterHours, solve.DecCenterDeg, DateTime.UtcNow, Latitude, Longitude);

                double corrAlt = targetAltAz.alt - actualAltAz.alt;
                double corrAz = targetAltAz.az - actualAltAz.az;
                if (corrAz > 180.0) corrAz -= 360.0;
                if (corrAz < -180.0) corrAz += 360.0;

                int corrXArcsec = (int)(corrAlt * 3600.0);
                int corrZArcsec = (int)(corrAz * 3600.0);

                Logger.Notice($"AutoCenter: applying mount correction X={corrXArcsec}\", Z={corrZArcsec}\"");
                await UartClient.Client.MoveRelative(Axis.X, corrXArcsec);
                await UartClient.Client.MoveRelative(Axis.Z, corrZArcsec);

                int maxCorr = Math.Max(Math.Abs(corrXArcsec), Math.Abs(corrZArcsec));
                await WaitForMoveCompleteAsync(maxCorr, ct);

                await StartTrackingAsync(targetRA, targetDec);
                // Wait until the Pico's initial slew (after CMD_TRACK_CELESTIAL) finishes
                // and the mount reports TRACKING — then add 2.5s stabilisation.
                // Task.Delay(2000) was not enough: during the test on 2026-03-11 the mount
                // was still INACTIVE at 2 s and only reached TRACKING 1 s later, causing
                // the iteration-2 exposure to begin mid-slew and produce a blurred image that
                // astrometry.net instantly rejected (exit code 255).
                await Tracker.WaitForCelestialTrackingAsync(15000, ct);

                TryDeleteFile(imagePath);
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Notice("AutoCenter: cancelled");
            return null;
        }

        Logger.Warn($"AutoCenter: did not converge after {maxIterations} iterations (last error={lastErrorPx:F1}px)");
        return new AutoCenterResult
        {
            Success = false,
            Iterations = maxIterations,
            FinalErrorPx = lastErrorPx,
            Message = $"Did not converge after {maxIterations} iterations"
        };
    }

    /// <summary>
    /// Automatic calibration: slew to a grid of positions AROUND the current pointing,
    /// plate-solve each, and build a dense calibration map.
    /// Requires at least 1 manual alignment point so we know where we are.
    /// The grid uses small relative offsets (±spanDeg) from the current alt-az position
    /// to keep movements safe and localized.
    /// </summary>
    public static async Task<AutoCalibrateResult?> AutoCalibrateAsync(
        int altSteps = 3, int azSteps = 3, bool wideSweep = true,
        CancellationToken ct = default)
    {
        if (SolveCamera == null || !SolveCamera.connected)
        {
            Logger.Error("AutoCalibrate: no camera connected.");
            return null;
        }

        int totalPositions = altSteps * azSteps + (wideSweep ? 4 : 0);

        CalibrationUpdated?.Invoke(new AutoProgressInfo { 
            Message = "Solving current position for initial reference...", 
            PointCount = 0, TotalPositions = totalPositions 
        });

        var startMountPos = await Tracker.GetAxisPositions();
        
        DateTime captureTime = DateTime.UtcNow;
        string initialBase = $"solve_init_{captureTime:yyyyMMdd_HHmmss}";
        string initialImagePath = await Task.Run(() => SolveCamera.CaptureImage(initialBase), ct);
        
        var initSolve = await PlateSolver.SolveAsync(initialImagePath, null, null, 30.0, ct);
        TryDeleteFile(initialImagePath);

        if (initSolve == null)
        {
            Logger.Error("AutoCalibrate: Failed to plate-solve initial position. Aborting.");
            CalibrationUpdated?.Invoke(new AutoProgressInfo { Message = "Failed to solve initial position. Aborting." });
            return null;
        }

        Reset(); // Clear old points
        int startX = startMountPos.XArcsecs;
        int startZ = startMountPos.ZArcsecs;

        AddAlignmentPoint((float)initSolve.RaCenterHours, (float)initSolve.DecCenterDeg, 
                          startX, startMountPos.YArcsecs, startZ, captureTime);

        var (refAlt, refAz) = ComputeAltAz(initSolve.RaCenterHours, initSolve.DecCenterDeg, captureTime, Latitude, Longitude);
        Logger.Notice($"AutoCalibrate: initial reference alt={refAlt:F1}°, az={refAz:F1}°");

        CalibrationUpdated?.Invoke(new AutoProgressInfo { 
            Message = $"Initial reference: RA={initSolve.RaCenterHours:F2}h, Dec={initSolve.DecCenterDeg:F2}° (Alt={refAlt:F1}°, Az={refAz:F1}°)",
            PointCount = PointCount, Quality = LastResult?.Quality ?? "UNKNOWN", AverageResidualArcmin = LastResult?.AverageResidualArcmin ?? 0,
            TotalPositions = totalPositions
        });

        List<(double targetAlt, double targetAz)> targetPoints = new();

        double wideSpan = 25.0; // WIDE span for global matrix!
        if (wideSweep) 
        {
            targetPoints.Add((refAlt + wideSpan, refAz));
            targetPoints.Add((refAlt, refAz + wideSpan));
            targetPoints.Add((refAlt - wideSpan, refAz));
            targetPoints.Add((refAlt, refAz - wideSpan));
        }

        const double StepSpanDeg = 5.0;

        double[] altOffsets = new double[altSteps];
        for (int i = 0; i < altSteps; i++)
            altOffsets[i] = altSteps == 1 ? 0.0
                : -StepSpanDeg * (altSteps - 1) / 2.0 + StepSpanDeg * i;

        double[] azOffsets = new double[azSteps];
        for (int i = 0; i < azSteps; i++)
            azOffsets[i] = azSteps == 1 ? 0.0
                : -StepSpanDeg * (azSteps - 1) / 2.0 + StepSpanDeg * i;

        for (int ai = 0; ai < altSteps; ai++)
        {
            for (int azi = 0; azi < azSteps; azi++)
            {
                // Skip the center grid position — it duplicates the initial reference point
                if (Math.Abs(altOffsets[ai]) < 0.1 && Math.Abs(azOffsets[azi]) < 0.1)
                {
                    Logger.Debug($"AutoCalibrate: skipping center grid position (duplicate of reference)");
                    continue;
                }
                targetPoints.Add((refAlt + altOffsets[ai], refAz + azOffsets[azi]));
            }
        }

        // Just to be sure, update totalPositions to actual count
        totalPositions = targetPoints.Count;
        Logger.Notice($"AutoCalibrate: scanning {totalPositions} positions (wideSweep={wideSweep}, grid={altSteps}x{azSteps})");

        int solved = 1; // Start at 1 (the initial reference point we just took)
        int failed = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            int posNum = 0;
            foreach (var tp in targetPoints)
            {
                ct.ThrowIfCancellationRequested();
                posNum++;

                double targetAlt = tp.targetAlt;
                double targetAz = tp.targetAz;

                if (targetAlt < 15.0 || targetAlt > 80.0)
                {
                    Logger.Notice($"AutoCalibrate: skipping position {posNum}/{totalPositions} — alt={targetAlt:F1}° outside safe range [15°,80°]");
                    failed++;
                    continue;
                }

                if (targetAz < 0) targetAz += 360.0;
                if (targetAz >= 360) targetAz -= 360.0;

                CalibrationUpdated?.Invoke(new AutoProgressInfo { 
                    Message = $"Moving to position {posNum}/{totalPositions}...",
                    CurrentPosition = posNum, TotalPositions = totalPositions,
                    PointCount = PointCount, Quality = LastResult?.Quality ?? "UNKNOWN", AverageResidualArcmin = LastResult?.AverageResidualArcmin ?? 0
                });

                double altOffsetDeg = targetAlt - refAlt;
                double azOffsetDeg = targetAz - refAz;
                if (azOffsetDeg > 180.0) azOffsetDeg -= 360.0;
                if (azOffsetDeg < -180.0) azOffsetDeg += 360.0;

                int targetMotorX = startX + (int)(altOffsetDeg * 3600.0);
                int targetMotorZ = startZ + (int)(azOffsetDeg * 3600.0);

                var currentPos = await Tracker.GetAxisPositions();
                int moveX = targetMotorX - currentPos.XArcsecs;
                int moveZ = targetMotorZ - currentPos.ZArcsecs;

                Logger.Notice($"AutoCalibrate: position {posNum}/{totalPositions} — alt={targetAlt:F1}°, az={targetAz:F1}° (moveX: {moveX}, moveZ: {moveZ})");

                await UartClient.Client.ResumeMotors();
                await UartClient.Client.MoveRelative(Axis.X, moveX);
                await UartClient.Client.MoveRelative(Axis.Z, moveZ);

                double maxMoveDeg = Math.Max(Math.Abs(moveX / 3600.0), Math.Abs(moveZ / 3600.0));
                await WaitForMoveCompleteAsync((int)(maxMoveDeg * 3600), ct);

                string baseName = $"cal_{captureTime:yyyyMMdd_HHmmss}";
                string imagePath;
                captureTime = DateTime.UtcNow;
                try
                {
                    imagePath = await Task.Run(() => SolveCamera.CaptureImage(baseName), ct);
                }
                catch (Exception ex)
                {
                    Logger.Error($"AutoCalibrate: capture failed at pos {posNum}: {ex.Message}");
                    failed++;
                    continue;
                }

                var mountPos = await Tracker.GetAxisPositions();

                var solve = await PlateSolver.SolveAsync(imagePath, null, null, 30.0, ct);
                TryDeleteFile(imagePath);
                
                if (solve == null)
                {
                    Logger.Error($"AutoCalibrate: solve failed at pos {posNum}");
                    CalibrationUpdated?.Invoke(new AutoProgressInfo { 
                        Message = $"Position {posNum}/{totalPositions}: solve failed",
                        CurrentPosition = posNum, TotalPositions = totalPositions,
                        PointCount = PointCount, Quality = LastResult?.Quality ?? "UNKNOWN", AverageResidualArcmin = LastResult?.AverageResidualArcmin ?? 0
                    });
                    failed++;
                    continue;
                }

                AddAlignmentPoint(
                    (float)solve.RaCenterHours, (float)solve.DecCenterDeg,
                    mountPos.XArcsecs, mountPos.YArcsecs, mountPos.ZArcsecs, captureTime);
                solved++;
                
                string solvedMsg = $"Position {posNum}/{totalPositions}: solved RA={solve.RaCenterHours:F4}h Dec={solve.DecCenterDeg:F4}°";
                Logger.Notice(solvedMsg);
                
                CalibrationUpdated?.Invoke(new AutoProgressInfo { 
                    Message = solvedMsg,
                    CurrentPosition = posNum, TotalPositions = totalPositions,
                    PointCount = PointCount, Quality = LastResult?.Quality ?? "UNKNOWN", AverageResidualArcmin = LastResult?.AverageResidualArcmin ?? 0
                });
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Notice("AutoCalibrate: cancelled");
            // The MountController listens for this exception to fire the cancelled event
            throw; 
        }
        finally
        {
            try
            {
                Logger.Notice("AutoCalibrate: returning to starting position...");
                CalibrationUpdated?.Invoke(new AutoProgressInfo { 
                    Message = "Returning to starting position...",
                    PointCount = PointCount, Quality = LastResult?.Quality ?? "UNKNOWN", AverageResidualArcmin = LastResult?.AverageResidualArcmin ?? 0,
                    TotalPositions = totalPositions
                });
                
                var currentPos = await Tracker.GetAxisPositions();
                int moveX = startX - currentPos.XArcsecs;
                int moveZ = startZ - currentPos.ZArcsecs;
                
                await UartClient.Client.ResumeMotors();
                await UartClient.Client.MoveRelative(Axis.X, moveX);
                await UartClient.Client.MoveRelative(Axis.Z, moveZ);
                
                double returnMoveDeg = Math.Max(Math.Abs(moveX / 3600.0), Math.Abs(moveZ / 3600.0));
                await WaitForMoveCompleteAsync((int)(returnMoveDeg * 3600), CancellationToken.None);
            }
            catch { }
        }

        sw.Stop();
        string quality = LastResult?.Quality ?? "UNKNOWN";
        Logger.Notice($"AutoCalibrate: complete — {solved}/{totalPositions} positions solved, {failed} failed, alignment quality: {quality}, took {sw.Elapsed.TotalMinutes:F1} min");

        CalibrationUpdated?.Invoke(new AutoProgressInfo { 
            Message = $"Complete: {solved} solved, quality: {quality}",
            PointCount = PointCount, Quality = quality, AverageResidualArcmin = LastResult?.AverageResidualArcmin ?? 0,
            TotalPositions = totalPositions
        });

        return new AutoCalibrateResult
        {
            SolvedCount = solved,
            FailedCount = failed,
            TotalPositions = totalPositions,
            TotalPoints = PointCount,
            Quality = quality,
            ElapsedSeconds = (int)sw.Elapsed.TotalSeconds
        };
    }

    /// <summary>
    /// Start tracked observation with periodic plate-solve guiding.
    /// Combinesx auto-centering and periodic drift correction.
    /// </summary>
    public static async Task<AutoCenterResult?> StartGuidedTrackingAsync(
        float targetRA, float targetDec,
        int guideIntervalSeconds = 60,
        int maxCorrections = 3,          // 0 = unlimited (run until manually stopped)
        CancellationToken ct = default)
    {
        StopGuidedTracking();

        _guideCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var guideCt = _guideCts.Token;

        Logger.Notice($"GuidedTracking: target RA={targetRA:F4}h, Dec={targetDec:F4}°, guide interval={guideIntervalSeconds}s, maxCorrections={maxCorrections}");

        var centerResult = await AutoCenterAsync(targetRA, targetDec, maxIterations: 5, tolerancePx: 15.0, ct: guideCt);
        if (centerResult == null || !centerResult.Success)
        {
            Logger.Error("GuidedTracking: initial centering failed");
            return centerResult;
        }

        Logger.Notice($"GuidedTracking: centered ({centerResult.FinalErrorPx:F1}px). Starting guide loop every {guideIntervalSeconds}s...");

        double plateScale = PlateScale;
        int corrections = 0;
        int checks = 0;

        string exitReason = "stopped";
        try
        {
            while (!guideCt.IsCancellationRequested)
            {
                await Task.Delay(guideIntervalSeconds * 1000, guideCt);
                checks++;

                guideCt.ThrowIfCancellationRequested();

                DateTime guideCaptureTime = DateTime.UtcNow;
                string baseName = $"guide_{guideCaptureTime:yyyyMMdd_HHmmss}";
                string imagePath;
                try
                {
                    imagePath = await Task.Run(() => SolveCamera!.CaptureImage(baseName), guideCt);
                }
                catch (Exception ex)
                {
                    Logger.Error($"GuidedTracking: guide capture failed: {ex.Message}");
                    continue;
                }

                var mountPos = await Tracker.GetAxisPositions();

                var solve = await PlateSolver.SolveAsync(imagePath, targetRA, targetDec, 10.0, guideCt);
                if (solve == null)
                {
                    Logger.Error("GuidedTracking: guide solve failed, skipping");
                    TryDeleteFile(imagePath);
                    continue;
                }

                double dRaDeg = (targetRA - solve.RaCenterHours) * 15.0 * Math.Cos(targetDec * Math.PI / 180.0);
                double dDecDeg = targetDec - solve.DecCenterDeg;
                double errorDeg = Math.Sqrt(dRaDeg * dRaDeg + dDecDeg * dDecDeg);
                double errorPx = errorDeg * 3600.0 / plateScale;

                Logger.Notice($"GuidedTracking: check #{checks} — drift={errorPx:F1}px ({errorDeg * 60:F1}')");

                bool correctionApplied = false;
                int? corrXArcsec = null;
                int? corrZArcsec = null;

                if (errorPx > 3.0)
                {
                    AddAlignmentPoint(
                        (float)solve.RaCenterHours, (float)solve.DecCenterDeg,
                        mountPos.XArcsecs, mountPos.YArcsecs, mountPos.ZArcsecs, guideCaptureTime);

                    // Note: MoveRelative already stops celestial tracking on the Pico internally.
                    // DO NOT call StopAll() — it sends CMD_STOP which disables motors and loses calibration.
                    await Task.Delay(200, guideCt);

                    var targetAltAz = ComputeAltAz(targetRA, targetDec, guideCaptureTime, Latitude, Longitude);
                    var actualAltAz = ComputeAltAz(solve.RaCenterHours, solve.DecCenterDeg, guideCaptureTime, Latitude, Longitude);

                    double corrAlt = targetAltAz.alt - actualAltAz.alt;
                    double corrAz = targetAltAz.az - actualAltAz.az;
                    if (corrAz > 180.0) corrAz -= 360.0;
                    if (corrAz < -180.0) corrAz += 360.0;

                    corrXArcsec = (int)(corrAlt * 3600.0);
                    corrZArcsec = (int)(corrAz * 3600.0);

                    Logger.Notice($"GuidedTracking: correction X={corrXArcsec}\", Z={corrZArcsec}\"");
                    await UartClient.Client.MoveRelative(Axis.X, corrXArcsec.Value);
                    await UartClient.Client.MoveRelative(Axis.Z, corrZArcsec.Value);

                    int maxCorr = Math.Max(Math.Abs(corrXArcsec.Value), Math.Abs(corrZArcsec.Value));
                    await WaitForMoveCompleteAsync(maxCorr, guideCt);
                    await StartTrackingAsync(targetRA, targetDec);

                    corrections++;
                    correctionApplied = true;
                    Logger.Notice($"GuidedTracking: correction #{corrections} applied ({errorPx:F1}px → recentered)");
                }
                else
                {
                    Logger.Notice($"GuidedTracking: on target ({errorPx:F1}px ≤ 3px threshold)");
                }

                // Broadcast per-check progress to UI
                GuidedTrackingProgress?.Invoke(new GuidedTrackingProgressInfo
                {
                    Check = checks,
                    MaxCorrections = maxCorrections,
                    Corrections = corrections,
                    DriftPx = Math.Round(errorPx, 1),
                    DriftArcmin = Math.Round(errorDeg * 60.0, 2),
                    CorrectionApplied = correctionApplied,
                    CorrXArcsec = corrXArcsec,
                    CorrZArcsec = corrZArcsec
                });

                TryDeleteFile(imagePath);

                // Stop automatically when the requested number of guide cycles has been reached
                if (maxCorrections > 0 && checks >= maxCorrections)
                {
                    Logger.Notice($"GuidedTracking: maxCorrections ({maxCorrections}) reached after {checks} checks, {corrections} corrections — stopping");
                    exitReason = "maxCorrectionsReached";
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Notice($"GuidedTracking: stopped after {checks} checks, {corrections} corrections");
        }

        return new AutoCenterResult
        {
            Success = true,
            Iterations = corrections,
            CheckCount = checks,
            FinalErrorPx = 0,
            Message = $"Guided tracking ended ({exitReason}): {corrections} corrections over {checks} checks",
            ExitReason = exitReason
        };
    }

    /// <summary>
    /// Stop any active guided tracking loop.
    /// </summary>
    public static void StopGuidedTracking()
    {
        if (_guideCts != null)
        {
            Logger.Notice("Stopping guided tracking...");
            _guideCts.Cancel();
            _guideCts.Dispose();
            _guideCts = null;
        }
    }

    /// <summary>
    /// Plate-solve the current pointing (diagnostic tool).
    /// Captures an image and returns the solved position without moving the mount.
    /// </summary>
    public static async Task<PlateSolver.SolveResult?> SolveCurrentAsync(CancellationToken ct = default)
    {
        if (SolveCamera == null || !SolveCamera.connected)
        {
            Logger.Warn("SolveCurrent: no camera connected.");
            return null;
        }

        double? hintRA = CurrentTargetRa.HasValue ? (double)CurrentTargetRa.Value : null;
        double? hintDec = CurrentTargetDec.HasValue ? (double)CurrentTargetDec.Value : null;

        Logger.Notice("SolveCurrent: capturing for plate solve...");

        string baseName = $"solve_{DateTime.Now:yyyyMMdd_HHmmss}";
        string imagePath = await Task.Run(() => SolveCamera.CaptureImage(baseName), ct);

        var result = await PlateSolver.SolveAsync(imagePath, hintRA, hintDec, 30.0, ct);
        TryDeleteFile(imagePath);

        if (result != null)
        {
            Logger.Notice($"SolveCurrent: pointing at RA={result.RaCenterHours:F4}h, Dec={result.DecCenterDeg:F4}° (scale={result.PixelScaleArcsecPerPx:F2}\"/px)");
        }

        return result;
    }

    // ── Helper methods for plate-solve operations ──

    /// <summary>
    /// Wait for mount movement to complete by continuously polling
    /// its reported physical coordinates until they stop changing.
    /// then adds a small settling delay to allow vibrations to dampen.
    /// </summary>
    private static async Task WaitForMoveCompleteAsync(int maxDeltaArcsec, CancellationToken ct)
    {
        double expectedSec = Math.Abs(maxDeltaArcsec) / 4500.0;
        Logger.Debug($"WaitForMove: {maxDeltaArcsec}\" → max expected ~{expectedSec:F1}s move");
        
        await Tracker.WaitUntilMotorsStopAsync(ct);
        
        // Add a single second of dampening delay before continuing to capturing pictures
        await Task.Delay(1000, ct);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Result of an auto-centering operation.</summary>
    public class AutoCenterResult
    {
        public bool Success { get; set; }
        public int Iterations { get; set; }
        public double FinalErrorPx { get; set; }
        public double FinalErrorArcmin { get; set; }
        public double SolvedRA { get; set; }
        public double SolvedDec { get; set; }
        public string Message { get; set; } = "";
        /// <summary>"maxCorrectionsReached" | "stopped" | "error" | null for regular auto-center</summary>
        public string? ExitReason { get; set; }
        /// <summary>Total number of plate-solve checks performed (guided tracking only).</summary>
        public int CheckCount { get; set; }
    }

    /// <summary>Result of an auto-calibration operation.</summary>
    public class AutoCalibrateResult
    {
        public int SolvedCount { get; set; }
        public int FailedCount { get; set; }
        public int TotalPositions { get; set; }
        public int TotalPoints { get; set; }
        public string Quality { get; set; } = "";
        public int ElapsedSeconds { get; set; }
    }
}
