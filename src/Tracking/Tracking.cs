internal static class Tracker
{
    internal class AxisPositions
    {
        internal int XArcsecs;
        internal int YArcsecs;
        internal int ZArcsecs;
    }

    internal static async Task<AxisPositions> GetAxisPositions()
    {
        Logger.Debug("Fetching axis positions... this will likely take a few seconds");
        var pos = new AxisPositions();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await UartClient.Client.PauseMotors();
        System.Action<int, int, int> handler = (x, y, z) =>
        {
            pos.XArcsecs = x;
            pos.YArcsecs = y;
            pos.ZArcsecs = z;
            tcs.TrySetResult(true);
        };

        UartClient.Client.PositionReceived += handler;
        try
        {
            await UartClient.Client.GetPositions();
            await tcs.Task;
        }
        finally
        {
            await UartClient.Client.ResumeMotors();
            UartClient.Client.PositionReceived -= handler;
        }
        return pos;
    }

    internal static async Task WaitUntilMotorsStopAsync(CancellationToken ct = default)
    {
        Logger.Debug("Waiting for motors to settle (polling positions without pausing)...");
        AxisPositions? prevPos = null;

        while (!ct.IsCancellationRequested)
        {
            var pos = new AxisPositions();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            System.Action<int, int, int> handler = (x, y, z) =>
            {
                pos.XArcsecs = x;
                pos.YArcsecs = y;
                pos.ZArcsecs = z;
                tcs.TrySetResult(true);
            };

            UartClient.Client.PositionReceived += handler;
            try
            {
                await UartClient.Client.GetPositions();
                var timeoutTask = Task.Delay(1500, ct);
                if (await Task.WhenAny(tcs.Task, timeoutTask) == timeoutTask)
                {
                    Logger.Debug("  Timeout waiting for position update...");
                    continue; // Timeout, try again
                }
            }
            finally
            {
                UartClient.Client.PositionReceived -= handler;
            }

            if (prevPos != null)
            {
                // If the positions haven't changed by more than 2 arcseconds, they have stopped.
                if (Math.Abs(pos.XArcsecs - prevPos.XArcsecs) < 2 &&
                    Math.Abs(pos.YArcsecs - prevPos.YArcsecs) < 2 &&
                    Math.Abs(pos.ZArcsecs - prevPos.ZArcsecs) < 2)
                {
                    Logger.Debug("Motors have arrived at target and settled.");
                    break;
                }
            }

            prevPos = pos;
            await Task.Delay(300, ct); // Poll every 300ms
        }
    }

    /// <summary>
    /// After StartCelestialTracking, the Pico firmware performs an internal slew to the
    /// predicted sky position before engaging the sidereal tracking rate.
    /// During this slew the status reports "Celestial Tracking: INACTIVE".
    /// This method waits until the first status update that shows TRACKING, then adds a
    /// brief stabilisation delay so the mount has fully settled before an exposure begins.
    /// </summary>
    internal static async Task WaitForCelestialTrackingAsync(int timeoutMs = 15000, CancellationToken ct = default)
    {
        Logger.Debug("WaitForCelestialTracking: waiting for mount to complete initial slew...");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Action<float, int, int, int, bool, bool, bool, int> handler =
            (_, _, _, _, _, _, celestialTracking, _) =>
            {
                if (celestialTracking)
                    tcs.TrySetResult(true);
            };

        UartClient.Client.StatusReceived += handler;
        try
        {
            var timeoutTask = Task.Delay(timeoutMs, ct);
            var winner = await Task.WhenAny(tcs.Task, timeoutTask);
            if (winner == timeoutTask)
                Logger.Warn("WaitForCelestialTracking: timed out waiting for TRACKING state — mount may still be slewing");
            else
                Logger.Debug("WaitForCelestialTracking: TRACKING confirmed.");
        }
        finally
        {
            UartClient.Client.StatusReceived -= handler;
        }

        // Wait one additional status period so the mount is fully settled at sidereal rate
        // before we start a camera exposure.
        await Task.Delay(2500, ct);
        Logger.Debug("WaitForCelestialTracking: stabilisation complete.");
    }
}